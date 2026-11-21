using AutoExile.Statistics;
using AutoExile.WebServer;
using Microsoft.Data.Sqlite;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static void Flush(StatsService stats, string phase)
{
    Require(stats.Flush(TimeSpan.FromSeconds(30)), $"{phase}: stats queue did not flush");
    Require(stats.Snapshot.Health.Status == "healthy", $"{phase}: {stats.Snapshot.Health.Error}");
}

static void FlushDegraded(StatsService stats, string phase)
{
    Require(stats.Flush(TimeSpan.FromSeconds(30)), $"{phase}: stats queue did not flush");
    Require(stats.Snapshot.Health.Status == "degraded" &&
            stats.Snapshot.Health.Error?.Contains("writes were dropped", StringComparison.Ordinal) == true,
        $"{phase}: queue loss was not surfaced as degraded health");
}

static long ScalarLong(string database, string sql)
{
    using var db = new SqliteConnection($"Data Source={database};Pooling=False");
    db.Open();
    using var cmd = db.CreateCommand();
    cmd.CommandText = sql;
    return Convert.ToInt64(cmd.ExecuteScalar());
}

static void RequireQuickCheck(string database, string phase)
{
    using var db = new SqliteConnection($"Data Source={database};Mode=ReadOnly;Pooling=False");
    db.Open();
    using var cmd = db.CreateCommand();
    cmd.CommandText = "PRAGMA quick_check";
    var result = Convert.ToString(cmd.ExecuteScalar());
    Require(string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase),
        $"{phase}: SQLite quick_check failed: {result}");
}

static void RunCrashWriter(string database, string pluginDirectory, string readyFile)
{
    using var stats = new StatsService(_ => { }, database);
    if (!stats.Initialize(pluginDirectory)) Environment.Exit(21);
    stats.SetRunning(true);
    stats.BeginSimulacrumActivation("abrupt-crash-run", "Hideout");
    stats.ObserveSimulacrumEntry(8181, "Simulacrum");
    if (!stats.RecordRuntimeEvent("crash", "committed_marker", "persisted",
            context: new { phase = "before_forced_termination" }, critical: true))
        Environment.Exit(22);

    for (var i = 0; i < 2_000; i++)
        stats.RecordRuntimeEvent("crash", "inflight", "queued", context: new { index = i });
    File.WriteAllText(readyFile, "ready");

    for (var i = 2_000; ; i++)
    {
        stats.RecordRuntimeEvent("crash", "inflight", "queued", context: new { index = i });
        if ((i & 255) == 0) Thread.Yield();
    }
}

static int ReserveLoopbackPort()
{
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
    listener.Stop();
    return port;
}

static async Task<JsonDocument> GetJson(HttpClient client, string url)
{
    using var response = await client.GetAsync(url);
    Require(response.StatusCode == HttpStatusCode.OK, $"GET {url}: {(int)response.StatusCode}");
    return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
}

if (args.Length == 4 && args[0] == "--crash-writer")
{
    RunCrashWriter(args[1], args[2], args[3]);
    return;
}

var root = Path.Combine(Path.GetTempPath(), "ism-ap-forensics-smoke", Guid.NewGuid().ToString("N"));
var pluginDirectory = Path.Combine(root, "plugin");
var pluginData = Path.Combine(pluginDirectory, "Data");
Directory.CreateDirectory(pluginData);
var database = Path.Combine(root, "core", "stats.db");
var logs = new List<string>();

try
{
    // Integration: real schema, WAL, ordering, correlation, report and HTTP contract.
    using (var stats = new StatsService(logs.Add, database))
    {
        Require(stats.Initialize(pluginDirectory), "core: database initialization failed");
        stats.SetRunning(true);
        stats.BeginSimulacrumActivation("forensics-smoke-run", "Hideout");
        stats.ObserveSimulacrumEntry(4242, "Simulacrum");
        Flush(stats, "core setup");

        Require(stats.RecordRuntimeEvent("input", "key_down", "sent", context: new
        {
            key = "Z", origin = "loot_visibility", requestedHoldMs = 80,
        }, critical: true), "sent key event was not acknowledged");
        Require(stats.RecordRuntimeEvent("input", "key_up", "sent", context: new
        {
            key = "Z", origin = "loot_visibility", actualHoldMs = 83,
        }, critical: true), "key-up event was not acknowledged");
        Require(stats.RecordRuntimeEvent("input", "left_down", "dropped", "rate_limit", new
        {
            x = 812, y = 496, elapsedMs = 37,
        }, critical: true), "dropped input event was not acknowledged");
        Require(stats.RecordRuntimeEvent("movement", "suspend", "suspended", context: new
        {
            key = "T", reason = "loot_click",
        }, critical: true), "movement event was not acknowledged");
        Require(stats.RecordRuntimeEvent("navigation", "recovery", "attempted", "path_stall", new
        {
            from = "122,190", target = "127,194", attempt = 2,
        }, critical: true), "navigation event was not acknowledged");
        Require(stats.RecordRuntimeEvent("simulacrum", "wave_timeout", "continued", "active_wave_timeout", new
        {
            wave = 12, elapsedSeconds = 121,
        }, critical: true), "wave timeout event was not acknowledged");
        for (var i = 0; i < 30; i++)
            Require(stats.RecordRuntimeEvent("state", "snapshot", "observed", context: new { sample = i }),
                $"snapshot noise event {i} was not queued");
        Flush(stats, "core events");

        using (var db = new SqliteConnection($"Data Source={database};Pooling=False"))
        {
            db.Open();
            using var schema = db.CreateCommand();
            schema.CommandText = @"SELECT
 (SELECT COUNT(*) FROM pragma_table_info('runtime_events')),
 (SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='ix_runtime_events_time'),
 (SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='ix_runtime_events_category_outcome_time'),
 (SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='ix_runtime_events_category_event_time'),
 (SELECT COUNT(*) FROM schema_migrations WHERE version=4)";
            using var schemaRow = schema.ExecuteReader();
            Require(schemaRow.Read() && schemaRow.GetInt32(0) == 12 && schemaRow.GetInt32(1) == 1 &&
                    schemaRow.GetInt32(2) == 1 && schemaRow.GetInt32(3) == 1 && schemaRow.GetInt32(4) == 1,
                "runtime schema, indexes, or migration marker is incomplete");

            using var journal = db.CreateCommand();
            journal.CommandText = "PRAGMA journal_mode";
            Require(string.Equals(Convert.ToString(journal.ExecuteScalar()), "wal", StringComparison.OrdinalIgnoreCase),
                "runtime database is not using WAL");

            using var events = db.CreateCommand();
            events.CommandText = @"SELECT sequence,occurred_at_ms,committed_at_ms,producer_thread_id,
 session_id,run_id,category,event_name,outcome,reason,context_json
FROM runtime_events WHERE category IN ('input','movement','navigation','simulacrum') ORDER BY sequence";
            using var rows = events.ExecuteReader();
            long previousSequence = 0;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (rows.Read())
            {
                var sequence = rows.GetInt64(0);
                Require(sequence > previousSequence, "event sequence is not strictly monotonic");
                Require(rows.GetInt64(2) >= rows.GetInt64(1), "commit timestamp predates occurrence");
                Require(rows.GetInt32(3) > 0, "producer thread id is missing");
                Require(!rows.IsDBNull(4) && !rows.IsDBNull(5), "session/run correlation is missing");
                Require(!rows.IsDBNull(10) && JsonDocument.Parse(rows.GetString(10)).RootElement.ValueKind == JsonValueKind.Object,
                    "event context is not valid JSON");
                previousSequence = sequence;
                seen.Add($"{rows.GetString(6)}/{rows.GetString(7)}/{rows.GetString(8)}");
            }
            Require(seen.IsSupersetOf(new[]
            {
                "input/key_down/sent", "input/key_up/sent", "input/left_down/dropped",
                "movement/suspend/suspended", "navigation/recovery/attempted",
                "simulacrum/wave_timeout/continued",
            }), "forensic event matrix is incomplete");
        }

        var report = stats.ReadRuntimeEventReport(limit: 1, windowHours: 0);
        Require(report.Error == null && report.WindowHours == 1, $"report clamp failed: {report.Error}");
        Require(report.Events.Count >= 6 && report.Events.Count <= 20,
            "report did not enforce its minimum bounded page while returning current events");
        Require(report.Categories.Any(x => x.Name == "input" && x.Count >= 3),
            "report category breakdown omitted input events");
        Require(report.Outcomes.Any(x => x.Name == "dropped"), "report omitted dropped outcome");
        Require(report.Storage.RetentionDays == 7 && report.Storage.CriticalRetentionDays == 30 &&
                report.Storage.RetentionRows == 500_000,
            "report retention policy is incorrect");

        var filtered = stats.ReadRuntimeEventReport(new RuntimeEventQuery
        {
            Limit = 20,
            WindowHours = 6,
            Category = "input",
            EventName = "key_down",
        });
        Require(filtered.TotalEvents == 1 && filtered.Events.Count == 1 &&
                filtered.Events.All(x => x.Category == "input" && x.EventName == "key_down"),
            "forensics category/event filter returned unrelated events");
        Require(report.HasMore && report.NextBeforeSequence.HasValue,
            "forensics report omitted its continuation cursor");
        var nextPage = stats.ReadRuntimeEventReport(new RuntimeEventQuery
        {
            Limit = 20,
            WindowHours = 6,
            BeforeSequence = report.NextBeforeSequence,
        });
        Require(nextPage.Events.Count > 0 &&
                nextPage.Events.All(x => x.Sequence < report.NextBeforeSequence.GetValueOrDefault()),
            "forensics beforeSeq cursor overlapped the previous page");

        var port = ReserveLoopbackPort();
        using (var server = new BotWebServer(port, networkAccess: false, logs.Add) { Stats = stats })
        using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) })
        {
            server.Start();
            Require(server.IsRunning, $"web server failed: {server.LastError}");
            using var payload = await GetJson(client, $"http://127.0.0.1:{port}/api/history/forensics?limit=25&hours=6");
            var json = payload.RootElement;
            Require(json.GetProperty("windowHours").GetInt32() == 6 &&
                    json.GetProperty("events").GetArrayLength() >= 6 &&
                    json.GetProperty("storage").GetProperty("retentionRows").GetInt32() == 500_000,
                "forensics HTTP response shape or values are incorrect");
            Require(json.GetProperty("events")[0].TryGetProperty("contextJson", out _),
                "forensics HTTP response omitted contextJson");

            using var filteredPayload = await GetJson(client,
                $"http://127.0.0.1:{port}/api/history/forensics?limit=25&hours=6&category=input&eventName=key_down");
            var filteredJson = filteredPayload.RootElement;
            Require(filteredJson.GetProperty("totalEvents").GetInt64() == 1 &&
                    filteredJson.GetProperty("events").GetArrayLength() == 1 &&
                    filteredJson.GetProperty("events")[0].GetProperty("eventName").GetString() == "key_down",
                "forensics HTTP filters returned unrelated events");
        }

        var unavailablePort = ReserveLoopbackPort();
        using (var server = new BotWebServer(unavailablePort, networkAccess: false, logs.Add))
        using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) })
        {
            server.Start();
            using var response = await client.GetAsync($"http://127.0.0.1:{unavailablePort}/api/history/forensics");
            Require(response.StatusCode == HttpStatusCode.ServiceUnavailable,
                "forensics endpoint did not fail closed while stats were unavailable");
            using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Require(payload.RootElement.GetProperty("error").GetString() == "Stats service is not ready.",
                "forensics unavailable response lost its diagnostic error");
        }
    }

    // Integration: committed events and ordering survive a clean restart.
    using (var restarted = new StatsService(logs.Add, database))
    {
        Require(restarted.Initialize(pluginDirectory), "restart: initialization failed");
        var report = restarted.ReadRuntimeEventReport(limit: 50, windowHours: 6);
        Require(report.Events.Any(x => x.Category == "simulacrum" && x.EventName == "wave_timeout"),
            "restart lost a committed forensic event");
        var sequences = report.Events.Select(x => x.Sequence).ToArray();
        Require(sequences.SequenceEqual(sequences.OrderByDescending(x => x)),
            "restart report changed descending sequence ordering");
    }
    RequireQuickCheck(database, "core restart");

    // Integration/crash recovery: terminate a child mid-write and recover committed WAL state.
    var crashDb = Path.Combine(root, "abrupt-crash", "stats.db");
    var crashReady = Path.Combine(root, "abrupt-crash", "writer-ready");
    Directory.CreateDirectory(Path.GetDirectoryName(crashReady)!);
    using (var readySignal = new ManualResetEventSlim(false))
    using (var watcher = new FileSystemWatcher(Path.GetDirectoryName(crashReady)!, Path.GetFileName(crashReady)))
    {
        watcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite;
        watcher.Created += (_, _) => readySignal.Set();
        watcher.Changed += (_, _) => readySignal.Set();
        watcher.EnableRaisingEvents = true;

        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("--crash-writer");
        start.ArgumentList.Add(crashDb);
        start.ArgumentList.Add(pluginDirectory);
        start.ArgumentList.Add(crashReady);
        using var child = Process.Start(start) ?? throw new InvalidOperationException("crash writer did not start");
        if (File.Exists(crashReady)) readySignal.Set();
        if (!readySignal.Wait(TimeSpan.FromSeconds(15)))
        {
            if (!child.HasExited) child.Kill(entireProcessTree: true);
            child.WaitForExit(5_000);
            throw new InvalidOperationException(
                $"crash writer did not reach the forced-termination boundary; stderr={child.StandardError.ReadToEnd()}");
        }
        child.Kill(entireProcessTree: true);
        Require(child.WaitForExit(5_000), "crash writer did not terminate after Kill");
    }
    using (var recovered = new StatsService(logs.Add, crashDb))
    {
        Require(recovered.Initialize(pluginDirectory), "abrupt crash: SQLite recovery failed");
        // Filtered on purpose: the batched writer commits thousands of in-flight rows after the
        // marker before the kill lands, so the marker is no longer among the newest unfiltered rows.
        var report = recovered.ReadRuntimeEventReport(new RuntimeEventQuery
        {
            Limit = 20,
            WindowHours = 6,
            Category = "crash",
            EventName = "committed_marker",
        });
        Require(report.Error == null &&
                report.Events.Any(x => x.Category == "crash" && x.EventName == "committed_marker"),
            $"abrupt crash lost the acknowledged committed marker: {report.Error}");
        Require(recovered.Snapshot.CurrentRun == null &&
                recovered.Snapshot.RecentRuns.Any(x => x.TerminalReason == "plugin_restarted"),
            "abrupt crash did not recover the stale run boundary");
    }
    RequireQuickCheck(crashDb, "abrupt crash recovery");

    // Integration: Dispose drains an ordinary noncritical burst.
    var drainDb = Path.Combine(root, "drain", "stats.db");
    using (var draining = new StatsService(logs.Add, drainDb))
    {
        Require(draining.Initialize(pluginDirectory), "drain: initialization failed");
        for (var i = 0; i < 1_000; i++)
            Require(draining.RecordRuntimeEvent("drain", "event", "queued", context: new { index = i }),
                $"drain: event {i} was rejected");
    }
    Require(ScalarLong(drainDb, "SELECT COUNT(*) FROM runtime_events WHERE category='drain'") == 1_000,
        "Dispose did not drain all accepted noncritical events");
    RequireQuickCheck(drainDb, "shutdown drain");

    // Integration: identical high-volume events stack into the first row plus one repeat summary.
    var stackDb = Path.Combine(root, "stack", "stats.db");
    using (var stacking = new StatsService(logs.Add, stackDb))
    {
        Require(stacking.Initialize(pluginDirectory), "stack: initialization failed");
        for (var i = 0; i < 40; i++)
        {
            Require(stacking.RecordRuntimeEvent("input", "StartMovement", "sent", context: new { key = "R", x = 960.0 + (i % 3), y = 540.0, elapsedMs = i }),
                $"stack: event {i} was rejected");
            Require(stacking.RecordRuntimeEvent("movement", "stop", "inactive", context: new { key = "R", suspendedMs = i * 7 }),
                $"stack: stop {i} was rejected");
        }
        Require(stacking.RecordRuntimeEvent("input", "StartMovement", "sent", context: new { key = "R", x = 400.0, y = 200.0 }),
            "stack: distinct event was rejected");
    }
    Require(ScalarLong(stackDb, "SELECT COUNT(*) FROM runtime_events WHERE category='input' AND event_name='StartMovement'") == 3,
        "stack: repeated input was not stored as first row + summary + distinct row");
    Require(ScalarLong(stackDb, "SELECT COUNT(*) FROM runtime_events WHERE category='movement' AND event_name='stop'") == 2,
        "stack: interleaved repeated movement event was not stacked");
    Require(ScalarLong(stackDb, "SELECT COALESCE(SUM(json_extract(context_json,'$.repeated')),0) FROM runtime_events WHERE category='input' AND json_extract(context_json,'$.coalesced')=1") == 39,
        "stack: repeat summary count is wrong");
    RequireQuickCheck(stackDb, "repeat stacking");

    // Integration/stress: a locked writer must not block producers; overflow is explicit and durable.
    var overflowDb = Path.Combine(root, "overflow", "stats.db");
    var overflowRejected = 0;
    using (var overflow = new StatsService(logs.Add, overflowDb))
    {
        Require(overflow.Initialize(pluginDirectory), "overflow: initialization failed");
        using var blocker = new SqliteConnection($"Data Source={overflowDb};Pooling=False");
        blocker.Open();
        using (var begin = blocker.CreateCommand())
        {
            begin.CommandText = "PRAGMA busy_timeout=100; BEGIN IMMEDIATE";
            begin.ExecuteNonQuery();
        }

        var longest = TimeSpan.Zero;
        for (var i = 0; i < 9_000; i++)
        {
            var started = Stopwatch.GetTimestamp();
            if (!overflow.RecordRuntimeEvent("overflow", "event", "queued", context: new { index = i })) overflowRejected++;
            var elapsed = Stopwatch.GetElapsedTime(started);
            if (elapsed > longest) longest = elapsed;
        }
        Require(overflowRejected > 0, "overflow smoke did not saturate the bounded writer queue");
        Require(longest < TimeSpan.FromMilliseconds(250),
            $"overflow enqueue blocked a producer for {longest.TotalMilliseconds:F0}ms");

        using (var commit = blocker.CreateCommand())
        {
            commit.CommandText = "COMMIT";
            commit.ExecuteNonQuery();
        }
        FlushDegraded(overflow, "overflow recovery");
        var report = overflow.ReadRuntimeEventReport(limit: 500, windowHours: 6);
        Require(report.Storage.DroppedEvents >= overflowRejected,
            "overflow report did not expose rejected forensic events");
        Require(ScalarLong(overflowDb, @"SELECT COUNT(*) FROM runtime_events
WHERE category='telemetry' AND event_name='queue_overflow' AND outcome='dropped'") >= 1,
            "overflow recovery did not persist a queue_overflow marker");
    }
    using (var overflowRestarted = new StatsService(logs.Add, overflowDb))
    {
        Require(overflowRestarted.Initialize(pluginDirectory), "overflow restart failed");
        var report = overflowRestarted.ReadRuntimeEventReport(limit: 20, windowHours: 6);
        Require(report.Storage.DroppedEvents >= overflowRejected,
            "overflow total reset after process restart");
    }
    RequireQuickCheck(overflowDb, "overflow restart");

    // Integration/stress: runtime-event saturation must not cost accounting writes.
    var floodDb = Path.Combine(root, "flood", "stats.db");
    using (var flood = new StatsService(logs.Add, floodDb))
    {
        Require(flood.Initialize(pluginDirectory), "flood: initialization failed");
        flood.SetRunning(true);
        flood.BeginRun("Mapping", "flood-run", "Hideout");
        flood.ObserveRunEntry("Mapping", 5151, "Strand");
        Flush(flood, "flood setup");

        const int floodLoot = 200;
        var floodRejected = 0;
        using (var blocker = new SqliteConnection($"Data Source={floodDb};Pooling=False"))
        {
            blocker.Open();
            using (var begin = blocker.CreateCommand())
            {
                begin.CommandText = "PRAGMA busy_timeout=100; BEGIN IMMEDIATE";
                begin.ExecuteNonQuery();
            }

            for (var i = 0; i < 9_000; i++)
                if (!flood.RecordRuntimeEvent("flood", "event", "queued", context: new { index = i })) floodRejected++;
            Require(floodRejected > 0, "flood did not saturate the runtime-event allowance");
            for (var i = 1; i <= floodLoot; i++)
                flood.RecordLoot($"Flood Item {i}", 1, 1, i, 5151, "Strand");

            using (var commit = blocker.CreateCommand())
            {
                commit.CommandText = "COMMIT";
                commit.ExecuteNonQuery();
            }
        }
        FlushDegraded(flood, "flood recovery");
        Require(flood.Snapshot.Lifetime.ItemsLooted == floodLoot && flood.Snapshot.CurrentRun?.ItemsLooted == floodLoot,
            $"runtime-event saturation dropped accounting writes: lifetime={flood.Snapshot.Lifetime.ItemsLooted}, " +
            $"run={flood.Snapshot.CurrentRun?.ItemsLooted}, expected={floodLoot}");

        // A context that cannot be serialized must not throw into the producer.
        bool badContextAccepted;
        try
        {
            badContextAccepted = flood.RecordRuntimeEvent("flood", "bad_context", "observed",
                context: new { value = double.NaN });
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"unserializable context threw into the producer: {ex.GetType().Name}");
        }
        Require(badContextAccepted, "unserializable context rejected the event");
        Require(flood.Flush(TimeSpan.FromSeconds(30)), "flood bad context: stats queue did not flush");
        Require(ScalarLong(floodDb, @"SELECT COUNT(*) FROM runtime_events
WHERE category='flood' AND event_name='bad_context' AND context_json LIKE '%contextError%'") == 1,
            "unserializable context was not replaced by an error marker");

        // An idle writer still refreshes the snapshot when the game loop pulses.
        var pulseBaseline = flood.Snapshot.Revision;
        flood.Pulse();
        Require(SpinWait.SpinUntil(() => flood.Snapshot.Revision > pulseBaseline, TimeSpan.FromSeconds(5)),
            "Pulse did not refresh the snapshot of an idle writer");
    }
    RequireQuickCheck(floodDb, "flood recovery");

    // Integration/boundary: startup maintenance enforces both age and 500k row caps.
    var retentionDb = Path.Combine(root, "retention", "stats.db");
    using (var setup = new StatsService(logs.Add, retentionDb))
    {
        Require(setup.Initialize(pluginDirectory), "retention: initialization failed");
        Flush(setup, "retention setup");
    }
    using (var seed = new SqliteConnection($"Data Source={retentionDb};Pooling=False"))
    {
        seed.Open();
        using var command = seed.CreateCommand();
        command.CommandText = @"WITH digits(d) AS (VALUES(0),(1),(2),(3),(4),(5),(6),(7),(8),(9)),
nums(n) AS (
 SELECT a.d + b.d*10 + c.d*100 + d.d*1000 + e.d*10000 + f.d*100000
 FROM digits a CROSS JOIN digits b CROSS JOIN digits c CROSS JOIN digits d CROSS JOIN digits e CROSS JOIN digits f
)
INSERT INTO runtime_events(occurred_at_ms,occurred_at_utc,committed_at_ms,producer_thread_id,category,event_name,outcome)
SELECT $now+n,$utc,$now+n,1,'retention','bulk','seed' FROM nums ORDER BY n LIMIT 500005;
INSERT INTO runtime_events(occurred_at_ms,occurred_at_utc,committed_at_ms,producer_thread_id,category,event_name,outcome)
VALUES(1,'1970-01-01T00:00:00.0000000Z',1,1,'retention','expired','seed');";
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$utc", DateTime.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }
    using (var retained = new StatsService(logs.Add, retentionDb))
    {
        Require(retained.Initialize(pluginDirectory), "retention: restart failed");
        Flush(retained, "retention cleanup");
        Require(ScalarLong(retentionDb, "SELECT COUNT(*) FROM runtime_events") == 500_000,
            "row-count retention did not enforce the 500k cap");
        Require(ScalarLong(retentionDb, "SELECT COUNT(*) FROM runtime_events WHERE event_name='expired'") == 0,
            "age retention did not remove the expired event");
        Require(ScalarLong(retentionDb, "SELECT COUNT(*) FROM runtime_events WHERE category='retention' AND event_name='bulk'") == 500_000,
            "retention removed current rows instead of the oldest rows");
    }
    RequireQuickCheck(retentionDb, "retention cleanup");

    Console.WriteLine("Forensics smoke passed: schema/index/WAL, event matrix, ordering/correlation, report bounds, HTTP contract, restart, abrupt-kill WAL recovery, shutdown drain, nonblocking overflow, restart-persistent overflow total, accounting under runtime-event saturation, unserializable context, idle pulse, quick_check, age and 500k retention.");
}
finally
{
    if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
}
