using ExileCore;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using AutoExile.Recording;
using System.IO;
using System.IO.Compression;
using System.Numerics;
using System.Text.Json;
using System.Windows.Forms;

namespace AutoExile.Systems
{
    /// <summary>
    /// Records human gameplay for offline replay analysis.
    /// Activated via F9 hotkey. Captures per-tick snapshots of game state + inputs.
    /// Writes compressed JSON files to Recordings/ folder.
    ///
    /// Usage: toggle with F9. Works with both human play and bot play for comparison.
    /// so inputs come from the human, not the bot. The recorder captures what the game
    /// state looks like each tick and what the human does.
    /// </summary>
    public class HumanGameplayRecorder
    {
        private bool _recording;
        private int _tickCounter;
        private string _outputDir = "";
        private Action<string>? _log;

        // Input tracking — capture raw inputs between ticks
        private readonly List<InputEvent> _pendingInputs = new();
        private DateTime _tickStartTime;
        private DateTime _recordingStartTime;
        private string _recordingAreaName = "";

        // Streaming writer — ticks are flushed to disk incrementally
        private FileStream? _fileStream;
        private GZipStream? _gzipStream;
        private Utf8JsonWriter? _jsonWriter;
        private string _currentFilePath = "";

        // Rate limiting — don't capture every single entity every tick
        private DateTime _lastFullEntityScan = DateTime.MinValue;
        private const float EntityScanIntervalMs = 100; // full scan every 100ms, not every tick
        private List<RecordedEntity> _cachedEntities = new();
        private bool _entitiesUpdatedThisTick;

        // Exploration change tracking — only write regions when coverage changes
        private float _lastWrittenCoverage = -1f;

        // Input polling — track key/mouse state to detect transitions
        private readonly HashSet<Keys> _prevKeysDown = new();
        private bool _prevLmb, _prevRmb;

        // Movement direction tracking for directional density
        private Vector2 _prevPlayerPos;
        private Vector2 _movementDir; // smoothed movement direction

        // Keys we care about for gameplay analysis
        private static readonly Keys[] TrackedKeys = new[]
        {
            // Skill keys
            Keys.Q, Keys.W, Keys.E, Keys.R, Keys.T,
            // Movement
            Keys.Space,
            // Flasks
            Keys.D1, Keys.D2, Keys.D3, Keys.D4, Keys.D5,
            // Common gameplay
            Keys.Tab,          // map overlay
            Keys.Escape,       // close panels
            Keys.I,            // inventory
            Keys.X,            // weapon swap
            Keys.ShiftKey,     // shift (attack in place)
            Keys.ControlKey,   // ctrl (item compare, ctrl-click)
        };

        public bool IsRecording => _recording;
        public int TicksRecorded => _tickCounter;

        public void Initialize(string pluginDir, Action<string>? log = null)
        {
            _outputDir = Path.Combine(pluginDir, "Recordings", "Human");
            Directory.CreateDirectory(_outputDir);
            _log = log;
        }

        /// <summary>Toggle recording on/off.</summary>
        public void Toggle(GameController gc, bool botRunning = false)
        {
            if (_recording)
                Stop();
            else
                Start(gc, botRunning);
        }

        public void Start(GameController gc, bool botRunning = false)
        {
            if (_recording) return;

            var areaName = gc.Area?.CurrentArea?.Name ?? "Unknown";
            var areaHash = gc.IngameState?.Data?.CurrentAreaHash ?? 0;
            var now = DateTime.Now;

            // Open streaming file — prefix distinguishes human vs bot recordings
            Directory.CreateDirectory(_outputDir);
            var safeName = areaName.Replace(" ", "_");
            var prefix = botRunning ? "bot" : "human";
            _currentFilePath = Path.Combine(_outputDir, $"{prefix}_{safeName}_{now:yyyyMMdd_HHmmss}.json.gz");

            try
            {
                _fileStream = File.Create(_currentFilePath);
                _gzipStream = new GZipStream(_fileStream, CompressionLevel.Fastest);
                _jsonWriter = new Utf8JsonWriter(_gzipStream, new JsonWriterOptions { SkipValidation = true });

                // Write header
                _jsonWriter.WriteStartObject();
                _jsonWriter.WriteString("Version", "1.1");
                _jsonWriter.WriteString("RecordedAt", now);
                _jsonWriter.WriteString("AreaName", areaName);
                _jsonWriter.WriteNumber("AreaHash", areaHash);

                // Terrain (once)
                WriteTerrain(gc);

                // Begin ticks array — each tick is appended incrementally
                _jsonWriter.WritePropertyName("Ticks");
                _jsonWriter.WriteStartArray();
                _jsonWriter.Flush();
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[Recorder] Failed to open file: {ex.Message}");
                CloseWriter();
                return;
            }

            _tickCounter = 0;
            _recordingStartTime = now;
            _recordingAreaName = areaName;
            _recording = true;
            _pendingInputs.Clear();
            _prevKeysDown.Clear();
            _prevLmb = false;
            _prevRmb = false;
            _prevPlayerPos = gc.Player.GridPosNum;
            _movementDir = Vector2.Zero;
            _lastWrittenCoverage = -1f;
            _tickStartTime = now;
            _log?.Invoke($"[Recorder] Started recording in {areaName}");
        }

        private void WriteTerrain(GameController gc)
        {
            try
            {
                var pfGrid = gc.IngameState?.Data?.RawPathfindingData;
                var tgtGrid = gc.IngameState?.Data?.RawTerrainTargetingData;
                if (pfGrid != null && _jsonWriter != null)
                {
                    int rows = pfGrid.Length;
                    int cols = rows > 0 ? pfGrid[0].Length : 0;

                    _jsonWriter.WritePropertyName("Terrain");
                    _jsonWriter.WriteStartObject();
                    _jsonWriter.WriteNumber("Rows", rows);
                    _jsonWriter.WriteNumber("Cols", cols);

                    _jsonWriter.WritePropertyName("PathfindingGrid");
                    _jsonWriter.WriteStartArray();
                    for (int y = 0; y < rows; y++)
                        for (int x = 0; x < cols; x++)
                            _jsonWriter.WriteNumberValue(pfGrid[y][x]);
                    _jsonWriter.WriteEndArray();

                    if (tgtGrid != null)
                    {
                        _jsonWriter.WritePropertyName("TargetingGrid");
                        _jsonWriter.WriteStartArray();
                        for (int y = 0; y < rows; y++)
                            for (int x = 0; x < cols; x++)
                                _jsonWriter.WriteNumberValue(tgtGrid[y][x]);
                        _jsonWriter.WriteEndArray();
                    }

                    _jsonWriter.WriteEndObject();
                }
            }
            catch { }
        }

        public void Stop()
        {
            if (!_recording) return;
            _recording = false;

            var duration = (float)(DateTime.Now - _recordingStartTime).TotalSeconds;

            try
            {
                if (_jsonWriter != null)
                {
                    // Close ticks array
                    _jsonWriter.WriteEndArray();

                    // Write final metadata
                    _jsonWriter.WriteNumber("TickCount", _tickCounter);
                    _jsonWriter.WriteNumber("DurationSeconds", duration);

                    // Close root object
                    _jsonWriter.WriteEndObject();
                    _jsonWriter.Flush();
                }
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[Recorder] Error finalizing: {ex.Message}");
            }

            CloseWriter();

            var sizeKb = File.Exists(_currentFilePath) ? new FileInfo(_currentFilePath).Length / 1024 : 0;
            _log?.Invoke($"[Recorder] Saved {_currentFilePath} ({sizeKb} KB, {_tickCounter} ticks, {duration:F1}s)");
        }

        private void CloseWriter()
        {
            try { _jsonWriter?.Dispose(); } catch { }
            try { _gzipStream?.Dispose(); } catch { }
            try { _fileStream?.Dispose(); } catch { }
            _jsonWriter = null;
            _gzipStream = null;
            _fileStream = null;
        }

        /// <summary>
        /// Capture one tick of gameplay. Call from BotCore.Tick() BEFORE mode tick.
        /// </summary>
        public void RecordTick(GameController gc, BotContext ctx)
        {
            if (!_recording || _jsonWriter == null) return;
            if (gc?.Player == null || !gc.InGame) return;

            var tick = new RecordingTick
            {
                TickNumber = _tickCounter++,
                DeltaTime = ctx.DeltaTime,
                AreaName = gc.Area?.CurrentArea?.Name ?? "",
                AreaHash = gc.IngameState?.Data?.CurrentAreaHash ?? 0,
            };

            // Player state
            tick.Player = CapturePlayer(gc);

            // Cursor position
            try
            {
                var mousePos = ExileCore.Input.MousePosition;
                tick.CursorX = mousePos.X;
                tick.CursorY = mousePos.Y;
            }
            catch { }

            // Entities — only written on scan ticks to avoid repeating identical data
            _entitiesUpdatedThisTick = false;
            if ((DateTime.Now - _lastFullEntityScan).TotalMilliseconds >= EntityScanIntervalMs)
            {
                _cachedEntities = CaptureEntities(gc);
                _lastFullEntityScan = DateTime.Now;
                _entitiesUpdatedThisTick = true;
                tick.Entities = _cachedEntities;
            }
            // Non-scan ticks: Entities stays empty (default), analyzer forward-fills

            // UI state
            tick.UI = CaptureUI(gc);

            // Ground labels (with value from ninja prices)
            tick.GroundLabels = CaptureGroundLabels(gc, ctx);

            // Minimap icons
            if (ctx.MinimapIcons.Count > 0)
            {
                tick.MinimapIcons = new List<MinimapIconSnapshot>(ctx.MinimapIcons.Count);
                foreach (var (id, icon) in ctx.MinimapIcons)
                {
                    tick.MinimapIcons.Add(new MinimapIconSnapshot
                    {
                        Id = id,
                        IconName = icon.IconName,
                        Path = icon.Path,
                        GridX = icon.GridPos.X,
                        GridY = icon.GridPos.Y,
                    });
                }
            }

            // Exploration — always write coverage, but only write full region data when it changes
            if (ctx.Exploration.IsInitialized)
            {
                var coverage = ctx.Exploration.ActiveBlobCoverage;
                tick.ExplorationCoverage = coverage;

                // Only snapshot regions when coverage actually changed
                if (Math.Abs(coverage - _lastWrittenCoverage) > 0.0001f)
                {
                    _lastWrittenCoverage = coverage;
                    var blob = ctx.Exploration.ActiveBlob;
                    if (blob != null && blob.Regions.Count > 0)
                    {
                        tick.Regions = new List<ExplorationRegionSnapshot>(blob.Regions.Count);
                        foreach (var region in blob.Regions)
                        {
                            tick.Regions.Add(new ExplorationRegionSnapshot
                            {
                                Index = region.Index,
                                CenterX = region.Center.X,
                                CenterY = region.Center.Y,
                                CellCount = region.CellCount,
                                SeenCount = region.SeenCount,
                                ExploredRatio = region.ExploredRatio,
                            });
                        }
                    }
                }
            }

            // Combat summary
            tick.NearbyMonsterCount = ctx.Combat.NearbyMonsterCount;
            tick.InCombat = ctx.Combat.InCombat;
            tick.BestTargetId = ctx.Combat.BestTarget?.Id ?? 0;

            // Directional monster density — always use cached entities even on non-scan ticks
            ComputeDirectionalDensity(tick, _cachedEntities);

            // Poll keyboard/mouse state and detect transitions
            PollInputState();

            // Flush pending inputs
            tick.Inputs = new List<InputEvent>(_pendingInputs);
            _pendingInputs.Clear();
            _tickStartTime = DateTime.Now;

            // Stream tick to disk immediately
            try
            {
                JsonSerializer.Serialize(_jsonWriter, tick, _serializeOptions);
                // Flush every 500 ticks (~8s) to balance IO and data safety
                if (_tickCounter % 500 == 0)
                    _jsonWriter.Flush();
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[Recorder] Write error at tick {_tickCounter}: {ex.Message}");
            }

            // Area change detection — auto-stop if returning to hideout
            if (gc.Area.CurrentArea.IsHideout || gc.Area.CurrentArea.IsTown)
            {
                _log?.Invoke("[Recorder] Area changed to hideout/town — stopping");
                Stop();
            }
        }

        private static readonly JsonSerializerOptions _serializeOptions = new()
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault,
            WriteIndented = false,
        };

        /// <summary>
        /// Record a raw input event. Call from BotInput hooks or input monitoring.
        /// These get batched and attached to the next tick snapshot.
        /// </summary>
        public void RecordInput(InputEventType type, float? x = null, float? y = null, int? key = null)
        {
            if (!_recording) return;
            _pendingInputs.Add(new InputEvent
            {
                TimestampMs = (float)(DateTime.Now - _tickStartTime).TotalMilliseconds,
                Type = type,
                X = x,
                Y = y,
                Key = key,
            });
        }

        // ══════════════════════════════════════════════════════════════
        // Directional density
        // ══════════════════════════════════════════════════════════════

        private void ComputeDirectionalDensity(RecordingTick tick, List<RecordedEntity> entities)
        {
            var playerPos = new Vector2(tick.Player.GridX, tick.Player.GridY);

            // Update smoothed movement direction
            var delta = playerPos - _prevPlayerPos;
            if (delta.LengthSquared() > 1f) // moved at least 1 grid unit
            {
                var normalized = Vector2.Normalize(delta);
                // Exponential smoothing to avoid jitter
                _movementDir = _movementDir.LengthSquared() < 0.01f
                    ? normalized
                    : Vector2.Normalize(_movementDir * 0.7f + normalized * 0.3f);
            }
            _prevPlayerPos = playerPos;

            if (_movementDir.LengthSquared() < 0.01f) return; // no direction yet

            int ahead = 0, behind = 0, flanking = 0;
            foreach (var entity in entities)
            {
                if (entity.EntityType != "Monster" || !entity.IsAlive || !entity.IsHostile)
                    continue;

                var toEntity = new Vector2(entity.GridX - playerPos.X, entity.GridY - playerPos.Y);
                if (toEntity.LengthSquared() < 1f) continue;

                var dot = Vector2.Dot(Vector2.Normalize(toEntity), _movementDir);
                if (dot > 0.707f)       ahead++;    // within ~45° forward
                else if (dot < -0.707f) behind++;   // within ~45° backward
                else                    flanking++; // sides
            }

            tick.MonstersAhead = ahead;
            tick.MonstersBehind = behind;
            tick.MonstersFlanking = flanking;
        }

        // ══════════════════════════════════════════════════════════════
        // Input polling
        // ══════════════════════════════════════════════════════════════

        private void PollInputState()
        {
            var mousePos = ExileCore.Input.MousePosition;
            var ms = (float)(DateTime.Now - _tickStartTime).TotalMilliseconds;

            // Keyboard
            foreach (var key in TrackedKeys)
            {
                bool down = ExileCore.Input.IsKeyDown(key);
                bool wasDown = _prevKeysDown.Contains(key);

                if (down && !wasDown)
                {
                    _pendingInputs.Add(new InputEvent
                    {
                        TimestampMs = ms, Type = InputEventType.KeyDown,
                        Key = (int)key, X = mousePos.X, Y = mousePos.Y,
                    });
                    _prevKeysDown.Add(key);
                }
                else if (!down && wasDown)
                {
                    _pendingInputs.Add(new InputEvent
                    {
                        TimestampMs = ms, Type = InputEventType.KeyUp,
                        Key = (int)key, X = mousePos.X, Y = mousePos.Y,
                    });
                    _prevKeysDown.Remove(key);
                }
            }

            // Mouse buttons
            bool lmb = ExileCore.Input.IsKeyDown(Keys.LButton);
            bool rmb = ExileCore.Input.IsKeyDown(Keys.RButton);

            if (lmb && !_prevLmb)
                _pendingInputs.Add(new InputEvent
                {
                    TimestampMs = ms, Type = InputEventType.LeftClick,
                    X = mousePos.X, Y = mousePos.Y,
                });
            if (rmb && !_prevRmb)
                _pendingInputs.Add(new InputEvent
                {
                    TimestampMs = ms, Type = InputEventType.RightClick,
                    X = mousePos.X, Y = mousePos.Y,
                });

            _prevLmb = lmb;
            _prevRmb = rmb;
        }

        // ══════════════════════════════════════════════════════════════
        // Capture helpers
        // ══════════════════════════════════════════════════════════════

        private static PlayerSnapshot CapturePlayer(GameController gc)
        {
            var player = gc.Player;
            var snap = new PlayerSnapshot
            {
                GridX = player.GridPosNum.X,
                GridY = player.GridPosNum.Y,
                IsAlive = player.IsAlive,
            };

            try
            {
                var life = player.GetComponent<Life>();
                if (life != null)
                {
                    snap.HpPercent = life.MaxHP > 0 ? (float)life.CurHP / life.MaxHP : 0;
                    snap.EsPercent = life.MaxES > 0 ? (float)life.CurES / life.MaxES : 0;
                    snap.ManaPercent = life.MaxMana > 0 ? (float)life.CurMana / life.MaxMana : 0;
                }
            }
            catch { }

            // Animation
            try
            {
                var actor = player.GetComponent<Actor>();
                if (actor != null)
                {
                    snap.Animation = actor.Animation.ToString();
                    snap.AnimationProgress = actor.AnimationController?.AnimationProgress ?? 0;
                }
            }
            catch { }

            // Buffs (names only)
            try
            {
                var buffs = player.Buffs;
                if (buffs != null)
                {
                    snap.Buffs = new List<string>();
                    foreach (var buff in buffs)
                        snap.Buffs.Add(buff.Name);
                }
            }
            catch { }

            return snap;
        }

        // Common path prefixes stripped to save space — reconstructed on load
        private static readonly string[] PathPrefixes = new[]
        {
            "Metadata/Monsters/",
            "Metadata/MiscellaneousObjects/",
            "Metadata/Terrain/",
            "Metadata/Chests/",
            "Metadata/NPC/",
            "Metadata/Effects/",
        };

        private static string StripPathPrefix(string path)
        {
            foreach (var prefix in PathPrefixes)
                if (path.StartsWith(prefix))
                    return path.Substring(prefix.Length);
            return path;
        }

        private static List<RecordedEntity> CaptureEntities(GameController gc)
        {
            var list = new List<RecordedEntity>();
            var playerGrid = gc.Player.GridPosNum;

            try
            {
                foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
                {
                    var type = entity.Type;
                    if (type != EntityType.Monster && type != EntityType.IngameIcon &&
                        type != EntityType.Chest && type != EntityType.Shrine &&
                        type != EntityType.WorldItem && type != EntityType.AreaTransition &&
                        type != EntityType.TownPortal)
                        continue;

                    var dist = Vector2.Distance(entity.GridPosNum, playerGrid);
                    if (dist > 200) continue;

                    var snap = new RecordedEntity
                    {
                        Id = entity.Id,
                        Path = StripPathPrefix(entity.Path ?? ""),
                        RenderName = entity.RenderName ?? "",
                        EntityType = type.ToString(),
                        GridX = entity.GridPosNum.X,
                        GridY = entity.GridPosNum.Y,
                        Distance = dist,
                        IsAlive = entity.IsAlive,
                        IsTargetable = entity.IsTargetable,
                        IsHostile = entity.IsHostile,
                        Rarity = entity.Rarity.ToString(),
                    };

                    if (type == EntityType.Monster && entity.IsAlive)
                    {
                        try
                        {
                            var life = entity.GetComponent<Life>();
                            if (life != null && life.MaxHP > 0)
                                snap.HpPercent = (float)life.CurHP / life.MaxHP;
                        }
                        catch { }
                    }

                    if (type == EntityType.IngameIcon || type == EntityType.Chest)
                    {
                        try
                        {
                            var sm = entity.GetComponent<StateMachine>();
                            if (sm?.States != null)
                            {
                                snap.States = new Dictionary<string, long>();
                                foreach (var state in sm.States)
                                    snap.States[state.Name] = (long)state.Value;
                            }
                        }
                        catch { }
                    }

                    list.Add(snap);
                }
            }
            catch { }

            return list;
        }

        private static UISnapshot CaptureUI(GameController gc)
        {
            var snap = new UISnapshot();
            try
            {
                var ui = gc.IngameState.IngameUi;
                snap.StashOpen = ui.StashElement?.IsVisible == true;
                snap.InventoryOpen = ui.InventoryPanel?.IsVisible == true;
                snap.RitualWindowOpen = ui.RitualWindow?.IsVisible == true;
                snap.AtlasPanelOpen = ui.AtlasPanel?.IsVisible == true;

                // Ritual details
                if (snap.RitualWindowOpen)
                {
                    try
                    {
                        var rw = ui.RitualWindow;
                        var tributeText = rw.GetChildAtIndex(7)?.GetChildAtIndex(0)?.Text ?? "";
                        var digits = new string(tributeText.Where(char.IsDigit).ToArray());
                        if (int.TryParse(digits, out var tribute)) snap.RitualTribute = tribute;

                        var rerollText = rw.GetChildAtIndex(12)?.GetChildAtIndex(0)?.Text ?? "";
                        if (int.TryParse(rerollText.Trim(), out var rerolls)) snap.RitualRerolls = rerolls;
                    }
                    catch { }
                }
            }
            catch { }
            return snap;
        }

        private static List<GroundLabelSnapshot> CaptureGroundLabels(GameController gc, BotContext ctx)
        {
            var list = new List<GroundLabelSnapshot>();
            try
            {
                var labels = gc.IngameState.IngameUi.ItemsOnGroundLabelsVisible;
                if (labels == null) return list;

                foreach (var label in labels)
                {
                    if (label.Label == null) continue;
                    var rect = label.Label.GetClientRect();
                    var itemEntity = label.ItemOnGround;
                    var snap = new GroundLabelSnapshot
                    {
                        EntityId = itemEntity?.Id ?? 0,
                        Text = label.Label.Text ?? "",
                        RectX = rect.X,
                        RectY = rect.Y,
                        RectW = rect.Width,
                        RectH = rect.Height,
                        IsVisible = label.Label.IsVisible,
                    };

                    // Grid position of the item
                    if (itemEntity != null)
                    {
                        snap.GridX = itemEntity.GridPosNum.X;
                        snap.GridY = itemEntity.GridPosNum.Y;
                        snap.Rarity = itemEntity.Rarity.ToString();

                        // Price lookup via ninja
                        try
                        {
                            if (ctx.NinjaPrice?.PriceCount > 0 &&
                                itemEntity.TryGetComponent<WorldItem>(out var worldItem))
                            {
                                var innerItem = worldItem.ItemEntity;
                                if (innerItem != null)
                                {
                                    var price = ctx.NinjaPrice.GetPrice(gc, innerItem);
                                    snap.ChaosValue = price.MaxChaosValue;
                                }
                            }
                        }
                        catch { }
                    }

                    list.Add(snap);
                }
            }
            catch { }
            return list;
        }

        // ══════════════════════════════════════════════════════════════
        // File I/O
        // ══════════════════════════════════════════════════════════════

        /// <summary>Load a recording from a compressed JSON file.</summary>
        public static GameplayRecording? LoadRecording(string filePath)
        {
            using var fileStream = File.OpenRead(filePath);
            using var gzipStream = new GZipStream(fileStream, CompressionMode.Decompress);
            return JsonSerializer.Deserialize<GameplayRecording>(gzipStream);
        }
    }
}
