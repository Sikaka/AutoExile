using System.Net;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Collections.Concurrent;

namespace AutoExile.WebServer
{
    /// <summary>
    /// Embedded HTTP server using HttpListener. Zero external dependencies.
    /// Serves a web dashboard, REST API, and WebSocket for live updates.
    /// </summary>
    public class BotWebServer : IDisposable
    {
        private HttpListener? _listener;
        private CancellationTokenSource? _cts;
        private Task? _listenTask;
        private Task? _broadcastTask;
        private int _port;
        private readonly bool _networkAccess;
        private readonly Action<string> _log;

        // Thread-safe state bridge: bot thread writes, web thread reads
        private volatile BotStatusSnapshot _currentStatus = new();
        private readonly ConcurrentQueue<WebCommand> _commandQueue = new();

        // WebSocket clients
        private readonly List<WebSocket> _wsClients = new();
        private readonly object _wsLock = new();

        // Settings + data store — set by BotCore after construction
        public BotSettings? Settings { get; set; }
        public DataStore? DataStore { get; set; }
        public Systems.MapDatabase? MapDatabase { get; set; }
        public ConfigManager? ConfigManager { get; set; }
        public Systems.NinjaPriceService? NinjaPrice { get; set; }
        public Systems.LootTracker? LootTracker { get; set; }
        public Systems.GemValuationService? GemValuation { get; set; }

        /// <summary>Delegate to scan nearby hostile monsters. Returns (RenderName, Rarity, GridDistance) tuples. Set by BotCore.</summary>
        public Func<List<(string Name, string Rarity, float Distance)>>? ScanNearbyMonsters { get; set; }

        /// <summary>Delegate to read current player buff names. Set by BotCore.</summary>
        public Func<List<string>>? GetPlayerBuffs { get; set; }

        // Cached terrain data — pushed from BotCore on area change
        private volatile MapTerrainData? _cachedTerrain;
        private long _cachedTerrainAreaHash;

        /// <summary>Update cached terrain data (called from tick thread on area change).</summary>
        public void UpdateTerrain(MapTerrainData terrain, long areaHash)
        {
            _cachedTerrain = terrain;
            _cachedTerrainAreaHash = areaHash;
        }

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        };

        private static readonly JsonSerializerOptions JsonOptsPretty = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        };

        public BotWebServer(int port, bool networkAccess, Action<string> log)
        {
            _port = port;
            _networkAccess = networkAccess;
            _log = log;
        }

        public string Url => $"http://localhost:{_port}/";
        public bool IsRunning => _listener?.IsListening == true;

        public void Start()
        {
            if (IsRunning) return;

            // Try primary port, then fallback ports if in use
            var portsToTry = new[] { _port, _port + 1, _port + 2 };

            foreach (var port in portsToTry)
            {
                try
                {
                    _cts = new CancellationTokenSource();
                    _listener = new HttpListener();

                    // Always start with localhost only — works without admin/netsh
                    _listener.Prefixes.Add($"http://localhost:{port}/");
                    _listener.Prefixes.Add($"http://127.0.0.1:{port}/");

                    // Add network prefix BEFORE Start() if requested — adding after Start()
                    // can crash the listener on some Windows configurations
                    if (_networkAccess)
                    {
                        try
                        {
                            _listener.Prefixes.Add($"http://+:{port}/");
                        }
                        catch (Exception ex)
                        {
                            _log($"Network access prefix failed (need admin or netsh urlacl): {ex.Message}");
                        }
                    }

                    _listener.Start();
                    _port = port; // update in case we fell back to an alternate port
                    _log($"Web server started at http://localhost:{port}/");

                    _listenTask = Task.Run(() => ListenLoop(_cts.Token));
                    _broadcastTask = Task.Run(() => BroadcastLoop(_cts.Token));
                    LastError = null;
                    return;
                }
                catch (HttpListenerException hlex)
                {
                    _log($"Web server failed on port {port}: {hlex.Message} (ErrorCode={hlex.ErrorCode})");
                    _listener?.Close();
                    _listener = null;
                    _cts?.Dispose();
                    _cts = null;

                    // If network access caused the failure, retry localhost-only
                    if (_networkAccess && hlex.ErrorCode == 5) // Access denied
                    {
                        _log("Retrying without network access...");
                        try
                        {
                            _cts = new CancellationTokenSource();
                            _listener = new HttpListener();
                            _listener.Prefixes.Add($"http://localhost:{port}/");
                            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                            _listener.Start();
                            _port = port;
                            _log($"Web server started at http://localhost:{port}/ (localhost only)");
                            _listenTask = Task.Run(() => ListenLoop(_cts.Token));
                            _broadcastTask = Task.Run(() => BroadcastLoop(_cts.Token));
                            LastError = null;
                            return;
                        }
                        catch
                        {
                            _listener?.Close();
                            _listener = null;
                            _cts?.Dispose();
                            _cts = null;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _log($"Web server failed on port {port}: {ex.GetType().Name}: {ex.Message}");
                    _listener?.Close();
                    _listener = null;
                    _cts?.Dispose();
                    _cts = null;
                }
            }

            LastError = $"All ports failed ({string.Join(", ", portsToTry)})";
            _log($"Web server: {LastError}. Check if another process uses these ports.");
        }

        /// <summary>Last startup error message, if any. Displayed in ImGui when server isn't running.</summary>
        public string? LastError { get; private set; }

        public void Stop()
        {
            _cts?.Cancel();

            // Close all WebSocket connections
            lock (_wsLock)
            {
                foreach (var ws in _wsClients)
                {
                    try { ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Server stopping", CancellationToken.None).Wait(1000); }
                    catch { }
                    try { ws.Dispose(); } catch { }
                }
                _wsClients.Clear();
            }

            _listener?.Stop();
            _listener?.Close();
            _listener = null;
            _log("Web server stopped");
        }

        public void Dispose() => Stop();

        public void UpdateStatus(BotStatusSnapshot status) => _currentStatus = status;

        public bool TryDequeueCommand(out WebCommand command) =>
            _commandQueue.TryDequeue(out command!);

        // ====================================================================
        // Listener loop
        // ====================================================================

        private async Task ListenLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && _listener?.IsListening == true)
            {
                try
                {
                    var ctx = await _listener.GetContextAsync().WaitAsync(ct);
                    _ = Task.Run(() => HandleRequest(ctx, ct), ct);
                }
                catch (OperationCanceledException) { break; }
                catch (HttpListenerException hlex)
                {
                    _log($"Web server listener stopped: {hlex.Message} (ErrorCode={hlex.ErrorCode})");
                    LastError = $"Listener crashed: {hlex.Message}";
                    break;
                }
                catch (Exception ex)
                {
                    _log($"Web server error: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        private async Task HandleRequest(HttpListenerContext ctx, CancellationToken ct)
        {
            var req = ctx.Request;
            var resp = ctx.Response;
            var path = req.Url?.AbsolutePath ?? "/";
            var method = req.HttpMethod;

            try
            {
                // WebSocket upgrade
                if (req.IsWebSocketRequest && path == "/api/ws")
                {
                    await HandleWebSocket(ctx, ct);
                    return;
                }

                // CORS headers
                resp.Headers.Add("Access-Control-Allow-Origin", "*");
                resp.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
                resp.Headers.Add("Access-Control-Allow-Headers", "Content-Type");

                if (method == "OPTIONS")
                {
                    resp.StatusCode = 204;
                    resp.Close();
                    return;
                }

                // Route dispatch
                switch (path)
                {
                    case "/" or "/index.html":
                        await ServeEmbeddedFile(resp, "index.html", "text/html");
                        break;

                    // Status
                    case "/api/status" when method == "GET":
                        await ServeJson(resp, _currentStatus);
                        break;

                    // Control
                    case "/api/control" when method == "POST":
                        await HandleControl(req, resp);
                        break;

                    // Settings
                    case "/api/settings" when method == "GET":
                        await HandleGetSettings(resp);
                        break;
                    case "/api/settings" when method == "POST":
                        await HandleSetSettings(req, resp);
                        break;

                    // Map
                    case "/api/map/terrain" when method == "GET":
                        await HandleMapTerrain(resp);
                        break;

                    // History
                    case "/api/history/loot" when method == "GET":
                        await HandleHistoryLoot(req, resp);
                        break;
                    case "/api/history/runs" when method == "GET":
                        await HandleHistoryRuns(req, resp);
                        break;
                    case "/api/history/events" when method == "GET":
                        await HandleHistoryEvents(req, resp);
                        break;
                    case "/api/maps" when method == "GET":
                        await HandleGetMaps(resp);
                        break;
                    case "/api/ultimatum-mods" when method == "GET":
                        await HandleGetUltimatumMods(resp);
                        break;
                    case "/api/ultimatum-mods" when method == "POST":
                        await HandleSetUltimatumMod(req, resp);
                        break;
                    case "/api/ninja/uniques" when method == "GET":
                        await HandleSearchUniques(req, resp);
                        break;
                    case "/api/altar-mods" when method == "GET":
                        await HandleGetAltarMods(resp);
                        break;
                    case "/api/altar-mods" when method == "POST":
                        await HandleSetAltarMod(req, resp);
                        break;
                    case "/api/loot/reset" when method == "POST":
                        LootTracker?.ResetSession();
                        await ServeJson(resp, new { ok = true });
                        break;
                    case "/api/lab/gems" when method == "GET":
                        await HandleLabGemValuation(resp);
                        break;
                    case "/api/nearby-monsters" when method == "GET":
                        await HandleNearbyMonsters(resp);
                        break;
                    case "/api/player-buffs" when method == "GET":
                        await HandlePlayerBuffs(resp);
                        break;

                    default:
                        resp.StatusCode = 404;
                        await WriteString(resp, "Not found");
                        break;
                }
            }
            catch (Exception ex)
            {
                try
                {
                    resp.StatusCode = 500;
                    await WriteString(resp, $"Error: {ex.Message}");
                }
                catch { }
            }
            finally
            {
                try { resp.Close(); } catch { }
            }
        }

        // ====================================================================
        // WebSocket
        // ====================================================================

        private async Task HandleWebSocket(HttpListenerContext ctx, CancellationToken ct)
        {
            WebSocket? ws = null;
            try
            {
                var wsCtx = await ctx.AcceptWebSocketAsync(null);
                ws = wsCtx.WebSocket;

                lock (_wsLock) { _wsClients.Add(ws); }

                // Keep connection alive — read loop (we don't expect client messages)
                var buffer = new byte[256];
                while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    try
                    {
                        await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                    }
                    catch { break; }
                }
            }
            catch { }
            finally
            {
                if (ws != null)
                {
                    lock (_wsLock) { _wsClients.Remove(ws); }
                    try { ws.Dispose(); } catch { }
                }
            }
        }

        private async Task BroadcastLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(500, ct);

                    List<WebSocket> clients;
                    lock (_wsLock) { clients = _wsClients.ToList(); }

                    if (clients.Count == 0) continue;

                    var json = JsonSerializer.SerializeToUtf8Bytes(_currentStatus, JsonOpts);
                    var segment = new ArraySegment<byte>(json);

                    foreach (var ws in clients)
                    {
                        try
                        {
                            if (ws.State == WebSocketState.Open)
                                await ws.SendAsync(segment, WebSocketMessageType.Text, true, ct);
                            else
                                lock (_wsLock) { _wsClients.Remove(ws); }
                        }
                        catch
                        {
                            lock (_wsLock) { _wsClients.Remove(ws); }
                        }
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _log($"WebSocket broadcast error: {ex.Message}");
                }
            }
        }

        // ====================================================================
        // API handlers
        // ====================================================================

        private async Task HandleControl(HttpListenerRequest req, HttpListenerResponse resp)
        {
            var body = await ReadBody(req);
            var cmd = JsonSerializer.Deserialize<WebCommand>(body, JsonOpts);
            if (cmd == null || string.IsNullOrEmpty(cmd.Action))
            {
                resp.StatusCode = 400;
                await ServeJson(resp, new { error = "Missing 'action' field" });
                return;
            }

            _commandQueue.Enqueue(cmd);
            await ServeJson(resp, new { ok = true, action = cmd.Action });
        }

        private async Task HandleGetSettings(HttpListenerResponse resp)
        {
            if (Settings == null)
            {
                resp.StatusCode = 503;
                await ServeJson(resp, new { error = "Settings not available" });
                return;
            }

            var flat = SettingsApi.SerializeFlat(Settings);
            await ServeJson(resp, flat, pretty: true);
        }

        private async Task HandleSetSettings(HttpListenerRequest req, HttpListenerResponse resp)
        {
            if (Settings == null)
            {
                resp.StatusCode = 503;
                await ServeJson(resp, new { error = "Settings not available" });
                return;
            }

            var body = await ReadBody(req);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (!root.TryGetProperty("key", out var keyEl) || !root.TryGetProperty("value", out var valueEl))
            {
                resp.StatusCode = 400;
                await ServeJson(resp, new { error = "Missing 'key' and/or 'value' fields" });
                return;
            }

            var (success, error) = SettingsApi.Apply(Settings, keyEl.GetString()!, valueEl);
            if (success)
            {
                // Persist to our config file
                ConfigManager?.Save(Settings);
                await ServeJson(resp, new { ok = true });
            }
            else
            {
                resp.StatusCode = 400;
                await ServeJson(resp, new { error });
            }
        }

        private async Task HandleMapTerrain(HttpListenerResponse resp)
        {
            var terrain = _cachedTerrain;
            if (terrain == null)
            {
                resp.StatusCode = 503;
                await ServeJson(resp, new { error = "No terrain data available" });
                return;
            }

            await ServeJson(resp, new
            {
                width = terrain.Width,
                height = terrain.Height,
                originX = terrain.OriginX,
                originY = terrain.OriginY,
                areaHash = _cachedTerrainAreaHash,
                data = Convert.ToBase64String(terrain.Data),
            });
        }

        private async Task HandleHistoryLoot(HttpListenerRequest req, HttpListenerResponse resp)
        {
            var limit = GetQueryInt(req, "limit", 100);
            var data = DataStore?.GetRecentLoot(limit) ?? new();
            await ServeJson(resp, data, pretty: true);
        }

        private async Task HandleHistoryRuns(HttpListenerRequest req, HttpListenerResponse resp)
        {
            var limit = GetQueryInt(req, "limit", 50);
            var data = DataStore?.GetRecentRuns(limit) ?? new();
            await ServeJson(resp, data, pretty: true);
        }

        private async Task HandleHistoryEvents(HttpListenerRequest req, HttpListenerResponse resp)
        {
            var limit = GetQueryInt(req, "limit", 100);
            var data = DataStore?.GetRecentEvents(limit) ?? new();
            await ServeJson(resp, data, pretty: true);
        }

        private async Task HandleGetUltimatumMods(HttpListenerResponse resp)
        {
            if (Settings == null) { resp.StatusCode = 503; await ServeJson(resp, new { error = "not ready" }); return; }

            var overrides = Settings.Mechanics.Ultimatum.ModRanking.DangerOverrides;

            // Build mod list from game files (authoritative), falling back to defaults + overrides
            var modEntries = new Dictionary<string, string>(); // id → display name
            try
            {
                var fileMods = ExileCore.PoEMemory.RemoteMemoryObject.pTheGame?.Files?.UltimatumModifiers?.EntriesList;
                if (fileMods != null)
                {
                    foreach (var m in fileMods)
                    {
                        if (string.IsNullOrEmpty(m.Id)) continue;
                        // Skip internal mods: wave scaling, spawners, empty names with no defaults
                        if (m.Id.StartsWith("UltimatumWave")) continue;
                        if (m.Id == "EnableFaridunModifiers") continue;
                        if (string.IsNullOrWhiteSpace(m.Name) && m.Id.EndsWith("Spawner")) continue;
                        var display = string.IsNullOrWhiteSpace(m.Name) ? m.Id : m.Name;
                        modEntries[m.Id] = display;
                    }
                }
            }
            catch { }

            // Fallback if game files unavailable: use defaults + overrides
            if (modEntries.Count == 0)
            {
                foreach (var k in Mechanics.UltimatumModDanger.Defaults.Keys)
                    modEntries[k] = k;
                foreach (var k in overrides.Keys)
                    if (!modEntries.ContainsKey(k)) modEntries[k] = k;
            }

            var result = modEntries.OrderBy(kv => kv.Value).Select(kv =>
            {
                var id = kv.Key;
                var defaultDanger = Mechanics.UltimatumModDanger.Defaults.TryGetValue(id, out var d) ? d : Mechanics.UltimatumModDanger.DefaultDanger;
                var currentDanger = Mechanics.UltimatumModDanger.GetDanger(id, overrides);
                var isOverridden = overrides.ContainsKey(id);

                return new
                {
                    id,
                    name = kv.Value,
                    defaultDanger,
                    currentDanger,
                    isOverridden,
                };
            }).ToList();

            await ServeJson(resp, result, pretty: true);
        }

        private async Task HandleSetUltimatumMod(HttpListenerRequest req, HttpListenerResponse resp)
        {
            if (Settings == null) { resp.StatusCode = 503; await ServeJson(resp, new { error = "not ready" }); return; }

            var body = await ReadBody(req);
            var doc = System.Text.Json.JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (!root.TryGetProperty("id", out var idEl) || !root.TryGetProperty("danger", out var dangerEl))
            {
                resp.StatusCode = 400;
                await ServeJson(resp, new { error = "requires 'id' and 'danger'" });
                return;
            }

            var modId = idEl.GetString() ?? "";
            var danger = dangerEl.GetInt32();
            var overrides = Settings.Mechanics.Ultimatum.ModRanking.DangerOverrides;

            // If setting back to default, remove the override
            if (Mechanics.UltimatumModDanger.Defaults.TryGetValue(modId, out var def) && danger == def)
                overrides.Remove(modId);
            else
                overrides[modId] = danger;

            // Persist
            ConfigManager?.Save(Settings);
            await ServeJson(resp, new { ok = true, id = modId, danger });
        }

        private async Task HandleGetAltarMods(HttpListenerResponse resp)
        {
            if (Settings == null) { resp.StatusCode = 503; await ServeJson(resp, new { error = "not ready" }); return; }

            var userWeights = Settings.Mechanics.EldritchAltar.ModWeights;
            var allMods = Mechanics.EldritchAltarHandler.AllKnownMods;

            var result = allMods
                .OrderByDescending(kv => kv.Value.Weight) // positive first, then negative
                .Select(kv =>
                {
                    var key = kv.Key;
                    var defaultWeight = kv.Value.Weight;
                    var currentWeight = userWeights.TryGetValue(key, out var uw) ? uw : defaultWeight;
                    return new
                    {
                        id = key,
                        name = kv.Value.Display,
                        defaultWeight,
                        currentWeight,
                        isOverridden = userWeights.ContainsKey(key),
                    };
                }).ToList();

            await ServeJson(resp, result, pretty: true);
        }

        private async Task HandleSetAltarMod(HttpListenerRequest req, HttpListenerResponse resp)
        {
            if (Settings == null) { resp.StatusCode = 503; await ServeJson(resp, new { error = "not ready" }); return; }

            var body = await ReadBody(req);
            var doc = System.Text.Json.JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (!root.TryGetProperty("id", out var idEl) || !root.TryGetProperty("weight", out var weightEl))
            {
                resp.StatusCode = 400;
                await ServeJson(resp, new { error = "requires 'id' and 'weight'" });
                return;
            }

            var modId = idEl.GetString() ?? "";
            var weight = weightEl.GetInt32();
            var userWeights = Settings.Mechanics.EldritchAltar.ModWeights;

            // If setting back to default, remove the override
            if (Mechanics.EldritchAltarHandler.AllKnownMods.TryGetValue(modId, out var known) && weight == known.Weight)
                userWeights.Remove(modId);
            else
                userWeights[modId] = weight;

            ConfigManager?.Save(Settings);
            await ServeJson(resp, new { ok = true, id = modId, weight });
        }

        private async Task HandleSearchUniques(HttpListenerRequest req, HttpListenerResponse resp)
        {
            var ninja = NinjaPrice;
            if (ninja == null || !ninja.IsLoaded)
            {
                resp.StatusCode = 503;
                await ServeJson(resp, new { error = "Price data not loaded" });
                return;
            }

            var query = req.QueryString["q"] ?? "";
            if (query.Length < 2)
            {
                await ServeJson(resp, Array.Empty<object>());
                return;
            }

            var results = ninja.SearchUniques(query, 30);
            var response = results.Select(r => new { name = r.Name, chaos = r.ChaosValue, category = r.Category }).ToList();
            await ServeJson(resp, response);
        }

        private async Task HandleGetMaps(HttpListenerResponse resp)
        {
            var db = MapDatabase;
            var supported = db?.SupportedMaps.ToList() ?? new();
            var result = supported.Select(name => new
            {
                name,
                bossTiles = db?.GetBossTiles(name),
                lastScanned = db?.GetEntry(name)?.LastScanned,
            }).ToList();
            await ServeJson(resp, result, pretty: true);
        }

        private async Task HandleLabGemValuation(HttpListenerResponse resp)
        {
            var ninja = NinjaPrice;
            var gem = GemValuation;
            if (ninja == null)
            {
                resp.StatusCode = 503;
                await ServeJson(resp, new { error = "NinjaPrice service not available" });
                return;
            }
            if (!ninja.IsLoaded)
            {
                resp.StatusCode = 503;
                await ServeJson(resp, new { error = $"Ninja prices not loaded yet (status: {ninja.Status})" });
                return;
            }
            if (gem == null)
            {
                resp.StatusCode = 503;
                await ServeJson(resp, new { error = "GemValuation service not available" });
                return;
            }

            var report = gem.GenerateReport(ninja, topN: 100);
            await ServeJson(resp, report, pretty: true);
        }

        private async Task HandleNearbyMonsters(HttpListenerResponse resp)
        {
            var scanner = ScanNearbyMonsters;
            if (scanner == null)
            {
                resp.StatusCode = 503;
                await ServeJson(resp, new { error = "Not available (bot not running)" });
                return;
            }

            try
            {
                var monsters = scanner();
                // Deduplicate by name, keeping the closest distance and highest rarity
                var grouped = monsters
                    .GroupBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(g => new
                    {
                        name = g.Key,
                        rarity = g.OrderByDescending(m => m.Rarity switch
                        {
                            "Unique" => 3, "Rare" => 2, "Magic" => 1, _ => 0
                        }).First().Rarity,
                        distance = g.Min(m => m.Distance),
                        count = g.Count()
                    })
                    .OrderBy(m => m.distance)
                    .Take(50)
                    .ToList();

                await ServeJson(resp, grouped);
            }
            catch (Exception ex)
            {
                resp.StatusCode = 500;
                await ServeJson(resp, new { error = ex.Message });
            }
        }

        private async Task HandlePlayerBuffs(HttpListenerResponse resp)
        {
            var getter = GetPlayerBuffs;
            if (getter == null)
            {
                resp.StatusCode = 503;
                await ServeJson(resp, new { error = "Not available (bot not running)" });
                return;
            }

            try
            {
                var buffs = getter();
                await ServeJson(resp, buffs.OrderBy(b => b).ToList());
            }
            catch (Exception ex)
            {
                resp.StatusCode = 500;
                await ServeJson(resp, new { error = ex.Message });
            }
        }

        // ====================================================================
        // Helpers
        // ====================================================================

        private static async Task<string> ReadBody(HttpListenerRequest req)
        {
            using var reader = new StreamReader(req.InputStream, req.ContentEncoding);
            return await reader.ReadToEndAsync();
        }

        private static int GetQueryInt(HttpListenerRequest req, string key, int defaultValue)
        {
            var val = req.QueryString[key];
            return int.TryParse(val, out var result) ? result : defaultValue;
        }

        private static async Task ServeJson(HttpListenerResponse resp, object data, bool pretty = false)
        {
            resp.ContentType = "application/json; charset=utf-8";
            var json = JsonSerializer.Serialize(data, pretty ? JsonOptsPretty : JsonOpts);
            await WriteString(resp, json);
        }

        private static async Task ServeEmbeddedFile(HttpListenerResponse resp, string fileName, string contentType)
        {
            var assembly = Assembly.GetExecutingAssembly();
            using var stream = assembly.GetManifestResourceStream($"webui.{fileName}");
            if (stream == null)
            {
                resp.StatusCode = 404;
                await WriteString(resp, $"Embedded resource '{fileName}' not found");
                return;
            }

            resp.ContentType = $"{contentType}; charset=utf-8";
            await stream.CopyToAsync(resp.OutputStream);
        }

        private static async Task WriteString(HttpListenerResponse resp, string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            resp.ContentLength64 = bytes.Length;
            await resp.OutputStream.WriteAsync(bytes);
        }
    }

    // ====================================================================
    // DTOs
    // ====================================================================

    public class BotStatusSnapshot
    {
        public bool Running { get; init; }
        public bool InGame { get; init; }
        public string Mode { get; init; } = "Idle";
        public string Phase { get; init; } = "";
        public string Decision { get; init; } = "";
        public string Status { get; init; } = "";
        public string Area { get; init; } = "";

        public float HpPercent { get; init; }
        public float EsPercent { get; init; }
        public float ManaPercent { get; init; }

        public bool InCombat { get; init; }
        public int NearbyMonsters { get; init; }
        public string? CombatTarget { get; init; }

        public bool IsNavigating { get; init; }
        public int WaypointIndex { get; init; }
        public int WaypointTotal { get; init; }

        public float ExplorationCoverage { get; init; }
        public int ExplorationRegions { get; init; }

        public int LootCandidates { get; init; }
        public float SessionChaos { get; init; }
        public float ChaosPerHour { get; init; }
        public float ChaosPerDivine { get; init; }
        public int ItemsLooted { get; init; }
        public int MapsCompleted { get; init; }
        public string SessionDuration { get; init; } = "";

        // Simulacrum stats (populated only when mode is Simulacrum)
        public int SimWave { get; init; }
        public bool SimWaveActive { get; init; }
        public int SimDeaths { get; init; }
        public int SimRuns { get; init; }
        public float SimAvgWaves { get; init; }
        public string SimAvgRunTime { get; init; } = "";
        public string SimRunTime { get; init; } = "";

        // Labyrinth stats (populated only when mode is Labyrinth)
        public int LabIzaroEncounters { get; init; }
        public int LabDeaths { get; init; }
        public int LabRuns { get; init; }
        public int LabGemsTransformed { get; init; }
        public float LabTotalProfit { get; init; }
        public string LabSelectedGem { get; init; } = "";

        public long Timestamp { get; init; }

        // Map overlay (included in WebSocket updates)
        public float PlayerGridX { get; init; }
        public float PlayerGridY { get; init; }
        public long AreaHash { get; init; }
        public List<MapEntity>? Entities { get; init; }
        public List<float[]>? NavPath { get; init; }

        // Detected skill bar (from game, for settings UI)
        public List<DetectedSkillSlot>? SkillBar { get; init; }
    }

    /// <summary>A skill slot detected from the game's skill bar.</summary>
    public class DetectedSkillSlot
    {
        public int SlotIndex { get; set; }
        public string Key { get; set; } = "";
        public string SkillName { get; set; } = "";
        public string InternalName { get; set; } = "";
        // Skill type flags for auto-classification
        public bool IsSpell { get; set; }
        public bool IsAttack { get; set; }
        public bool IsVaalSkill { get; set; }
        public bool IsInstant { get; set; }
        public bool IsCry { get; set; }
        public bool IsChanneling { get; set; }
        public bool IsTotem { get; set; }
        public bool IsTrap { get; set; }
        public bool IsMine { get; set; }
        public int SoulsPerUse { get; set; }
        public int DeployedCount { get; set; }
    }

    public class WebCommand
    {
        public string Action { get; set; } = "";
        public string? Value { get; set; }
    }
}
