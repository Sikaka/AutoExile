using AutoExile.Statistics;
using Microsoft.Data.Sqlite;

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static void Flush(StatsService stats)
{
    Require(stats.Flush(TimeSpan.FromSeconds(5)), "stats queue did not flush");
    Require(stats.Snapshot.Health.Status == "healthy", $"stats unhealthy: {stats.Snapshot.Health.Error}");
}

var root = Path.Combine(Path.GetTempPath(), "ism-ap-stats-replay", Guid.NewGuid().ToString("N"));
var pluginData = Path.Combine(root, "plugin", "Data");
Directory.CreateDirectory(pluginData);
var pluginDirectory = Path.GetDirectoryName(pluginData)!;
var today = DateTime.Now.ToString("yyyy-MM-dd");
File.WriteAllText(Path.Combine(pluginData, "map_data.json"),
    "{\"Test Map\":{\"bossTiles\":[\"1:2:3\"],\"transitionDetailName\":\"test_transition\"}}");
File.WriteAllText(Path.Combine(pluginDirectory, "lab_memory.json"),
    $"{{\"date\":\"{today}\",\"entries\":[{{\"zone\":\"Test Lab\",\"exitCount\":2," +
    "\"mappings\":[{\"angle\":45.5,\"dest\":\"Aspirant's Trial\"}]}]}");
var database = Path.Combine(root, "db", "stats.db");
var logs = new List<string>();

var productionPath = StatsService.ResolveDatabasePath();
Require(productionPath.EndsWith(Path.Combine("ism ap", "data", "sqlite", "stats.db"), StringComparison.OrdinalIgnoreCase),
    $"unexpected production database path: {productionPath}");
Require(!productionPath.Contains("autoexile", StringComparison.OrdinalIgnoreCase),
    "production database path contains a forbidden product name");

try
{
    using (var stats = new StatsService(logs.Add, database))
    {
        Require(stats.Initialize(pluginDirectory), "database initialization failed");
        Require(stats.ReadMapMetadata().Single().MapName == "Test Map", "map metadata import failed");
        Require(stats.ReadLabExitMemory(today).Single().DestinationName == "Aspirant's Trial",
            "lab exit memory import failed");
        stats.SetRunning(true);
        stats.BeginSimulacrumActivation("activation-1", "Simulacrum");
        stats.BeginSimulacrumActivation("activation-1", "Simulacrum"); // duplicate delivery
        stats.ObserveSimulacrumEntry(101, "Simulacrum");
        Thread.Sleep(20);
        Flush(stats);
        Require(stats.RecordRuntimeEvent("test", "input", "sent", context: new { key = "A" }, critical: true),
            "critical runtime event was not acknowledged");
        Require(stats.RecordRuntimeEvent("test", "input", "dropped", "rate_limit",
            new { key = "B" }, critical: true), "dropped runtime event was not acknowledged");
        Flush(stats);
        using (var runtimeReader = new SqliteConnection($"Data Source={database};Pooling=False"))
        {
            runtimeReader.Open();
            using var command = runtimeReader.CreateCommand();
            command.CommandText = @"SELECT sequence,outcome,occurred_at_ms,committed_at_ms,producer_thread_id
FROM runtime_events WHERE category='test' ORDER BY sequence";
            using var rows = command.ExecuteReader();
            Require(rows.Read() && rows.GetString(1) == "sent", "sent runtime event was not persisted");
            var firstSequence = rows.GetInt64(0);
            Require(rows.GetInt64(3) >= rows.GetInt64(2) && rows.GetInt32(4) > 0,
                "runtime event producer/commit timing metadata was not persisted");
            Require(rows.Read() && rows.GetString(1) == "dropped" && rows.GetInt64(0) > firstSequence,
                "runtime event sequence/outcome ordering was not durable");
        }
        var forensicReport = stats.ReadRuntimeEventReport(limit: 50, windowHours: 6);
        Require(forensicReport.Error == null && forensicReport.TotalEvents >= 2,
            $"runtime report failed: {forensicReport.Error}");
        Require(forensicReport.Events.Count >= 2 && forensicReport.Categories.Any(x => x.Name == "test"),
            "runtime report omitted event detail or category breakdown");
        Require(forensicReport.Storage.RetentionDays == 7 && forensicReport.Storage.RetentionRows == 500_000,
            "runtime report exposed incorrect retention policy");
        Require(stats.Snapshot.CurrentRun?.WallDurationMs > 0,
            "open run wall duration did not advance");
        for (var wave = 1; wave <= 15; wave++)
        {
            stats.RecordWaveStarted(wave);
            stats.RecordWaveStarted(wave); // duplicate delivery
            stats.RecordWaveCompleted(wave);
        }
        stats.RecordDeath();
        stats.ObserveSimulacrumEntry(101, "Simulacrum");
        stats.RecordLoot("Test Item", 12.345, 1, 777, 101, "Simulacrum");
        stats.RecordLoot("Test Item", 12.345, 1, 777, 101, "Simulacrum");
        stats.EndSimulacrumRun(true, "replay_full_clear");
        Flush(stats);

        var session = stats.Snapshot.Session;
        Require(session.Attempts == 1 && session.Consumed == 1 && session.Entered == 1, "attempt accounting mismatch");
        Require(session.FullClears == 1 && session.WavesCompleted == 15, "full clear accounting mismatch");
        Require(session.Deaths == 1 && session.Entries == 2 && session.Reentries == 1, "death/reentry accounting mismatch");
        Require(session.ItemsLooted == 1 && session.ChaosValueMilli == 12_345, "loot dedup/value mismatch");

        stats.RotateSession("replay_rotation");
        Flush(stats);
        Require(stats.Snapshot.Session.Attempts == 0, "session rotation did not create an empty session");
        Require(stats.Snapshot.Lifetime.FullClears == 1, "session rotation deleted lifetime history");

        stats.BeginSimulacrumActivation("activation-2", "Hideout");
        stats.EndSimulacrumRun(false, "portal_never_entered");
        stats.BeginSimulacrumActivation("activation-3", "Hideout");
        stats.ObserveSimulacrumEntry(303, "Simulacrum");
        stats.RecordWaveStarted(11);
        stats.RecordWaveCompleted(11);
        stats.EndSimulacrumRun(false, "left_after_wave_11");
        Flush(stats);
        Require(stats.Snapshot.Session.NotEnteredRuns == 1, "not-entered result mismatch");
        Require(stats.Snapshot.Session.PartialRuns == 1 && stats.Snapshot.Session.FullClears == 0,
            "partial run was promoted to a clear");

        stats.BeginSimulacrumActivation("activation-4", "Hideout");
        stats.ObserveSimulacrumEntry(404, "Simulacrum");
        stats.RecordWaveStarted(15);
        stats.EndSimulacrumRun(true, "caller_claimed_clear_without_wave_end");
        stats.BeginSimulacrumActivation("activation-5", "Hideout");
        stats.ObserveSimulacrumEntry(505, "Simulacrum");
        stats.EndSimulacrumRun(false, "entered_without_wave_progress");
        Flush(stats);
        Require(stats.Snapshot.Session.FullClears == 0 && stats.Snapshot.Session.PartialRuns == 2,
            "wave 15 start without completion was promoted to a clear");
        Require(stats.Snapshot.Session.NoProgressRuns == 1, "no-progress result mismatch");

        var oldSession = stats.Snapshot.Session.SessionId;
        stats.BeginSimulacrumActivation("activation-6", "Hideout");
        stats.RotateSession("deferred_during_run");
        Flush(stats);
        Require(stats.Snapshot.Session.SessionId == oldSession, "active run was split by session rotation");
        stats.EndSimulacrumRun(false, "finish_deferred_rotation");
        Flush(stats);
        Require(stats.Snapshot.Session.SessionId != oldSession, "deferred rotation was not applied after run end");

        stats.BeginSimulacrumActivation("activation-7", "Hideout");
        stats.ObserveSimulacrumEntry(707, "Simulacrum");
        stats.EndSimulacrumRun(false, "mode_switched");
        stats.RecordLoot("Post-switch Item", 1, 1, 778, 808, "Heist");
        Flush(stats);
        Require(stats.Snapshot.RecentLoot.First(item => item.ItemName == "Post-switch Item").RunId == null,
            "loot after a mode switch was attributed to the closed Simulacrum run");

        stats.BeginRun("Boss", "boss-1", "Hideout", consumed: true);
        stats.ObserveRunEntry("Boss", 909, "Test Boss Zone");
        stats.RecordDeath();
        stats.RecordLoot("Valuable Test Item", 150, 1, 779, 909, "Test Boss Zone");
        stats.EndRun("Boss", "completed", "boss_completed");
        stats.BeginRun("Boss", "boss-2", "Hideout", consumed: true);
        stats.ObserveRunEntry("Boss", 910, "Test Boss Zone");
        stats.EndRun("Boss", "failed", "boss_failed");
        stats.UpsertMapMetadata(new MapMetadataSnapshot("Test Map", new[] { "4:5:6" },
            DateTime.UtcNow, "Metadata/Boss", "new_transition"));
        var labWriteCompleted = 0;
        stats.ReplaceLabExitMemory(today, new[]
        {
            new LabExitMemorySnapshot(today, "Test Lab", 2, 90f, "Sanctum"),
        }, succeeded => Interlocked.Exchange(ref labWriteCompleted, succeeded ? 1 : -1));
        Flush(stats);
        Require(Volatile.Read(ref labWriteCompleted) == 1,
            "lab exit-memory write was not acknowledged after its transaction committed");
        var boss = stats.Snapshot.SessionFor("Boss");
        Require(boss.Attempts == 2 && boss.Consumed == 2 && boss.FullClears == 1 && boss.FailedRuns == 1 && boss.ItemsLooted == 1,
            "mode aggregate mismatch");
        Require(boss.Deaths == 1, "generic run death was not persisted");
        Require(boss.ActiveDurationMs == boss.RunDurationMs,
            "mode rate denominator included unrelated session activity");
        Require(stats.Snapshot.RecentRuns.First(run => run.Mode == "Boss").Consumed,
            "generic run consumed flag was not exposed");
        Require(stats.Snapshot.BestFinds.Single(item => item.ItemName == "Valuable Test Item").Mode == "Boss",
            "best-find query or mode attribution mismatch");
        Require(stats.ReadMapMetadata().Single().BossTiles.Single() == "4:5:6",
            "map metadata upsert failed");
        Require(Math.Abs(stats.ReadLabExitMemory(today).Single().AngleDegrees - 90f) < 0.01f,
            "lab exit memory replacement failed");
    }

    using (var restarted = new StatsService(logs.Add, database))
    {
        Require(restarted.Initialize(pluginDirectory), "restart initialization failed");
        Flush(restarted);
        Require(restarted.Snapshot.Lifetime.Attempts == 9, "restart lost durable runs");
        Require(restarted.Snapshot.Lifetime.FullClears == 2, "restart changed clear count");
        Require(restarted.Snapshot.Lifetime.ItemsLooted == 3,
            "restart changed the deduplicated run loot plus post-switch session loot");
        Require(restarted.Snapshot.LifetimeFor("Boss").FullClears == 1,
            "restart lost mode-specific history");
        Require(restarted.ReadMapMetadata().Single().TransitionDetailName == "new_transition",
            "restart lost map metadata");
        Require(restarted.ReadLabExitMemory(today).Single().DestinationName == "Sanctum",
            "restart lost lab exit memory");
        Require(restarted.RecordRuntimeEvent("test", "restart", "persisted", critical: true),
            "runtime event failed after restart");
        Flush(restarted);
    }

    var retentionDb = Path.Combine(root, "retention", "stats.db");
    using (var retentionSetup = new StatsService(logs.Add, retentionDb))
    {
        Require(retentionSetup.Initialize(pluginDirectory), "retention setup failed");
        Flush(retentionSetup);
    }
    using (var seed = new SqliteConnection($"Data Source={retentionDb};Pooling=False"))
    {
        seed.Open();
        using var command = seed.CreateCommand();
        command.CommandText = @"INSERT INTO runtime_events(
occurred_at_ms,occurred_at_utc,committed_at_ms,producer_thread_id,category,event_name,outcome)
VALUES($old,'1970-01-01T00:00:00.0000000Z',$old,0,'test','old','seed')";
        command.Parameters.AddWithValue("$old", 1L);
        command.ExecuteNonQuery();
    }
    using (var retained = new StatsService(logs.Add, retentionDb))
    {
        Require(retained.Initialize(pluginDirectory), "retention restart failed");
        Flush(retained);
        using var verify = new SqliteConnection($"Data Source={retentionDb};Pooling=False");
        verify.Open();
        using var count = verify.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM runtime_events WHERE event_name='old'";
        Require(Convert.ToInt32(count.ExecuteScalar()) == 0, "age retention did not remove expired runtime event");
    }

    var valuationRepairDb = Path.Combine(root, "valuation-repair", "stats.db");
    using (var valuationRepair = new StatsService(logs.Add, valuationRepairDb))
    {
        Require(valuationRepair.Initialize(pluginDirectory), "valuation repair setup failed");
        valuationRepair.RecordLoot("Large Cluster Jewel", 93400, 1, 8801, 501, "Simulacrum");
        valuationRepair.RecordLoot("Large Cluster Jewel", 40, 1, 8802, 501, "Simulacrum");
        valuationRepair.RecordLoot("Large Cluster Jewel", 95590, 1, 8804, 501, "Simulacrum");
        valuationRepair.RecordLoot("Crimson Jewel", 12000, 1, 8803, 501, "Simulacrum");
        Flush(valuationRepair);
        var outlierLootId = valuationRepair.Snapshot.RecentLoot
            .Single(item => item.ItemName == "Large Cluster Jewel" && item.ChaosValueMilli == 93_400_000)
            .LootId;
        var secondOutlierLootId = valuationRepair.Snapshot.RecentLoot
            .Single(item => item.ItemName == "Large Cluster Jewel" && item.ChaosValueMilli == 95_590_000)
            .LootId;
        valuationRepair.CorrectUnidentifiedVoicesOutliers(954.5, outlierLootId, secondOutlierLootId);
        Flush(valuationRepair);

        var largeClusterValues = valuationRepair.Snapshot.RecentLoot
            .Where(item => item.ItemName == "Large Cluster Jewel")
            .Select(item => item.ChaosValueMilli)
            .OrderBy(value => value)
            .ToArray();
        Require(largeClusterValues.SequenceEqual(new long[] { 40_000, 954_500, 954_500 }),
            "unidentified Voices repair changed the wrong Large Cluster Jewel row");
        Require(valuationRepair.Snapshot.RecentLoot.Single(item => item.ItemName == "Crimson Jewel").ChaosValueMilli == 12_000_000,
            "unidentified Voices repair changed a different jewel type");

        valuationRepair.CorrectUnidentifiedVoicesOutliers(800, outlierLootId);
        Flush(valuationRepair);
        Require(valuationRepair.Snapshot.RecentLoot
                .Any(item => item.LootId == outlierLootId && item.ChaosValueMilli == 954_500),
            "unidentified Voices repair was not idempotent");
    }

    File.WriteAllText(Path.Combine(pluginData, "loot.jsonl"),
        "{\"time\":\"2026-01-01T00:00:00Z\",\"itemName\":\"Legacy Item\",\"chaosValue\":2.5,\"slots\":1,\"area\":\"Legacy\"}\nmalformed\n");
    File.WriteAllText(Path.Combine(pluginData, "runs.jsonl"),
        "{\"id\":7,\"startTime\":\"2026-01-01T00:00:00Z\",\"mode\":\"Simulacrum\",\"area\":\"Hideout\"}\n" +
        "{\"id\":7,\"endTime\":\"2026-01-01T00:10:00Z\",\"highestWave\":15,\"deaths\":2,\"completed\":true,\"isUpdate\":true}\n" +
        "malformed\n");
    var legacyDb = Path.Combine(root, "legacy", "stats.db");
    using (var imported = new StatsService(logs.Add, legacyDb))
    {
        Require(imported.Initialize(pluginDirectory), "legacy initialization failed");
        Flush(imported);
        Require(imported.Snapshot.Lifetime.ItemsLooted == 1, "legacy import skipped valid row");
        Require(imported.Snapshot.Lifetime.Attempts == 1 && imported.Snapshot.Lifetime.FullClears == 1,
            "legacy run start/update rows were not merged");
        Require(imported.Snapshot.Lifetime.Deaths == 2 && imported.Snapshot.Lifetime.WavesCompleted == 15,
            "legacy run update fields were lost");
        Require(imported.Snapshot.Session.Attempts == 0 && imported.Snapshot.Session.ItemsLooted == 0,
            "legacy history contaminated the active session");
    }
    using (var importedAgain = new StatsService(logs.Add, legacyDb))
    {
        Require(importedAgain.Initialize(pluginDirectory), "legacy restart failed");
        Flush(importedAgain);
        Require(importedAgain.Snapshot.Lifetime.ItemsLooted == 1, "legacy import was not idempotent");
        Require(importedAgain.Snapshot.Lifetime.Attempts == 1,
            "legacy run import was not idempotent");
    }

    var retryPluginDirectory = Path.Combine(root, "operational-retry-plugin");
    var retryDataDirectory = Path.Combine(retryPluginDirectory, "Data");
    var retryMapPath = Path.Combine(retryDataDirectory, "map_data.json");
    var retryDb = Path.Combine(root, "operational-retry", "stats.db");
    Directory.CreateDirectory(retryDataDirectory);
    File.WriteAllText(retryMapPath, "{invalid-json");
    using (var failedImport = new StatsService(logs.Add, retryDb))
    {
        Require(failedImport.Initialize(retryPluginDirectory), "failed operational import broke initialization");
        Flush(failedImport);
        Require(failedImport.ReadMapMetadata().Count == 0, "malformed operational data was imported");
    }
    File.WriteAllText(retryMapPath,
        "{\"Recovered Map\":{\"bossTiles\":[\"9:9:9\"],\"transitionDetailName\":\"recovered\"}}");
    using (var retriedImport = new StatsService(logs.Add, retryDb))
    {
        Require(retriedImport.Initialize(retryPluginDirectory), "operational import retry initialization failed");
        Flush(retriedImport);
        Require(retriedImport.ReadMapMetadata().Single().MapName == "Recovered Map",
            "corrected operational data was not retried after a failed import");
    }

    var blockingFile = Path.Combine(root, "not-a-directory");
    File.WriteAllText(blockingFile, "x");
    using (var unavailable = new StatsService(logs.Add, Path.Combine(blockingFile, "stats.db")))
    {
        Require(!unavailable.Initialize(pluginDirectory), "invalid database path unexpectedly initialized");
        unavailable.BeginSimulacrumActivation("safe-noop", "Hideout");
        var unavailableLabWriteCompleted = 0;
        Require(!unavailable.ReplaceLabExitMemory(today, Array.Empty<LabExitMemorySnapshot>(),
                succeeded => Interlocked.Exchange(ref unavailableLabWriteCompleted, succeeded ? 1 : -1)),
            "unavailable statistics store accepted an exit-memory write");
        Require(Volatile.Read(ref unavailableLabWriteCompleted) == -1,
            "rejected exit-memory write did not report persistence failure");
        Require(unavailable.Snapshot.Health.Status == "unavailable", "database failure was not surfaced");
    }

    var upgradeDb = Path.Combine(root, "upgrade", "stats.db");
    var upgradePluginDirectory = Path.Combine(root, "upgrade-plugin");
    Directory.CreateDirectory(upgradePluginDirectory);
    Directory.CreateDirectory(Path.GetDirectoryName(upgradeDb)!);
    using (var old = new SqliteConnection($"Data Source={upgradeDb};Pooling=False"))
    {
        old.Open();
        using var cmd = old.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE schema_migrations(version INTEGER PRIMARY KEY,applied_at_ms INTEGER NOT NULL);
CREATE TABLE sessions(session_id TEXT PRIMARY KEY,started_at_ms INTEGER NOT NULL,ended_at_ms INTEGER,end_reason TEXT,
 active_duration_ms INTEGER NOT NULL DEFAULT 0,resumed_at_ms INTEGER,data_quality TEXT NOT NULL DEFAULT 'native');
CREATE TABLE runs(run_id TEXT PRIMARY KEY,session_id TEXT NOT NULL,mode TEXT NOT NULL,activation_key TEXT NOT NULL UNIQUE,
 instance_hash INTEGER,area_name TEXT NOT NULL DEFAULT '',state TEXT NOT NULL,result TEXT,terminal_reason TEXT,
 activated_at_ms INTEGER NOT NULL,first_entry_at_ms INTEGER,ended_at_ms INTEGER,last_observed_at_ms INTEGER NOT NULL,
 entry_count INTEGER NOT NULL DEFAULT 0,death_count INTEGER NOT NULL DEFAULT 0,wall_duration_ms INTEGER NOT NULL DEFAULT 0,
 data_quality TEXT NOT NULL DEFAULT 'native');
CREATE TABLE simulacrum_runs(run_id TEXT PRIMARY KEY,consumed INTEGER NOT NULL DEFAULT 0,
 highest_wave_started INTEGER NOT NULL DEFAULT 0,waves_completed INTEGER NOT NULL DEFAULT 0);
INSERT INTO schema_migrations VALUES(2,1);
INSERT INTO sessions(session_id,started_at_ms) VALUES('old-session',1);
INSERT INTO runs(run_id,session_id,mode,activation_key,area_name,state,activated_at_ms,last_observed_at_ms)
 VALUES('old-run','old-session','Simulacrum','old-key','Old Area','closed',1,1);
INSERT INTO simulacrum_runs(run_id,consumed) VALUES('old-run',1);";
        cmd.ExecuteNonQuery();
    }
    using (var upgraded = new StatsService(logs.Add, upgradeDb))
    {
        Require(upgraded.Initialize(upgradePluginDirectory), "v2 database upgrade failed");
        Flush(upgraded);
        Require(upgraded.Snapshot.SchemaVersion == 4 && upgraded.Snapshot.Lifetime.Consumed == 1,
            $"v2 consumed data was not migrated to the generic run schema: schema={upgraded.Snapshot.SchemaVersion}, consumed={upgraded.Snapshot.Lifetime.Consumed}, health={upgraded.Snapshot.Health.Status}, error={upgraded.Snapshot.Health.Error}");
    }

    var recoveryDb = Path.Combine(root, "recovery", "stats.db");
    using (var interrupted = new StatsService(logs.Add, recoveryDb))
    {
        Require(interrupted.Initialize(upgradePluginDirectory), "recovery setup failed");
        interrupted.BeginRun("Boss", "stale-boss", "Boss Arena", consumed: true);
        interrupted.ObserveRunEntry("Boss", 1234, "Boss Arena");
        for (var index = 1; index <= 200; index++)
            interrupted.RecordLoot($"Drain Item {index}", 1, 1, index, 1234, "Boss Arena");
        Flush(interrupted);
    }
    using (var recovered = new StatsService(logs.Add, recoveryDb))
    {
        Require(recovered.Initialize(upgradePluginDirectory), "recovery restart failed");
        Flush(recovered);
        Require(recovered.Snapshot.CurrentRun == null, "restart retained a stale open run");
        var stale = recovered.Snapshot.RecentRuns.Single(run => run.Mode == "Boss");
        Require(stale.Result == "interrupted" && stale.TerminalReason == "plugin_restarted",
            "restart did not classify the stale run as interrupted");
        Require(recovered.Snapshot.Lifetime.ItemsLooted == 200,
            "dispose lost queued statistics before the writer exited");
    }

    var reusedActivationDb = Path.Combine(root, "reused-activation", "stats.db");
    using (var reusedActivation = new StatsService(logs.Add, reusedActivationDb))
    {
        Require(reusedActivation.Initialize(upgradePluginDirectory), "reused activation setup failed");
        reusedActivation.BeginRun("Boss", "reused-boss-key", "Boss Arena", consumed: true);
        reusedActivation.ObserveRunEntry("Boss", 2001, "Boss Arena");
        reusedActivation.EndRun("Boss", "completed", "first_completion");
        reusedActivation.BeginRun("Boss", "reused-boss-key", "Boss Arena", consumed: true);
        reusedActivation.BeginRun("Boss", "reused-boss-key", "Boss Arena", consumed: true);
        reusedActivation.ObserveRunEntry("Boss", 2002, "Boss Arena");
        reusedActivation.EndRun("Boss", "completed", "second_completion");
        Flush(reusedActivation);
        Require(reusedActivation.Snapshot.LifetimeFor("Boss").Attempts == 2 &&
                reusedActivation.Snapshot.RecentRuns.Count(run => run.Mode == "Boss") == 2,
            "a reused activation key did not create exactly one new run");
    }

    var detailDb = Path.Combine(root, "run-detail", "stats.db");
    using (var detailSetup = new StatsService(logs.Add, detailDb))
    {
        Require(detailSetup.Initialize(pluginDirectory), "run detail setup failed");
        Flush(detailSetup);
    }
    const long t0 = 1_700_000_000_000L;
    using (var seed = new SqliteConnection($"Data Source={detailDb};Pooling=False"))
    {
        seed.Open();
        using var command = seed.CreateCommand();
        command.CommandText = $@"
INSERT INTO sessions(session_id,started_at_ms) VALUES('detail-session',{t0});
INSERT INTO runs(run_id,session_id,mode,activation_key,area_name,state,result,terminal_reason,consumed,activated_at_ms,first_entry_at_ms,ended_at_ms,last_observed_at_ms,entry_count,death_count,wall_duration_ms)
 VALUES('detail-sim','detail-session','Simulacrum','detail-a','Simulacrum','closed','partial','left',1,{t0},{t0 + 1000},{t0 + 200_000},{t0 + 200_000},1,1,200000);
INSERT INTO simulacrum_runs(run_id,consumed,highest_wave_started,waves_completed) VALUES('detail-sim',1,4,3);
INSERT INTO runs(run_id,session_id,mode,activation_key,area_name,state,result,consumed,activated_at_ms,last_observed_at_ms,wall_duration_ms)
 VALUES('detail-boss','detail-session','Boss','detail-b','Arena','closed','completed',1,{t0 + 300_000},{t0 + 310_000},10000);
INSERT INTO runs(run_id,session_id,mode,activation_key,area_name,state,result,consumed,activated_at_ms,last_observed_at_ms,wall_duration_ms)
 VALUES('detail-big','detail-session','Boss','detail-c','Arena','closed','completed',1,{t0 + 400_000},{t0 + 500_000},100000);
INSERT INTO runs(run_id,session_id,mode,activation_key,area_name,state,result,consumed,activated_at_ms,last_observed_at_ms,wall_duration_ms,data_quality)
 VALUES('detail-legacy','detail-session','Simulacrum','detail-d','Simulacrum','closed','completed',1,{t0 + 600_000},{t0 + 700_000},100000,'legacy_partial');
INSERT INTO simulacrum_runs(run_id,consumed,highest_wave_started,waves_completed) VALUES('detail-legacy',1,15,15);
WITH RECURSIVE c(x) AS (SELECT 1 UNION ALL SELECT x+1 FROM c WHERE x<2001)
INSERT INTO loot_events(loot_id,source_key,session_id,run_id,picked_at_ms,item_name,quantity,chaos_value_milli)
 SELECT 'b'||x,'bs'||x,'detail-session','detail-big',{t0 + 400_000}+x,'Bulk',1,1000 FROM c;
INSERT INTO stats_events(event_id,source_key,session_id,run_id,event_type,occurred_at_ms,wave) VALUES
 ('e9','k9','detail-session','detail-sim','run_ended',{t0 + 200_000},3),
 ('e1','k1','detail-session','detail-sim','run_started',{t0},NULL),
 ('e2','k2','detail-session','detail-sim','wave_started',{t0 + 10_000},1),
 ('e3','k3','detail-session','detail-sim','wave_completed',{t0 + 40_000},1),
 ('e4','k4','detail-session','detail-sim','wave_started',{t0 + 62_000},2),
 ('e5','k5','detail-session','detail-sim','wave_completed',{t0 + 102_000},2),
 ('e6','k6','detail-session','detail-sim','wave_started',{t0 + 105_000},3),
 ('e7','k7','detail-session','detail-sim','wave_completed',{t0 + 165_000},3),
 ('e8','k8','detail-session','detail-sim','wave_started',{t0 + 170_000},4),
 ('ed','kd','detail-session','detail-sim','death',{t0 + 80_000},NULL);
INSERT INTO loot_events(loot_id,source_key,session_id,run_id,picked_at_ms,item_name,quantity,chaos_value_milli) VALUES
 ('l5','s5','detail-session','detail-sim',{t0 + 180_000},'Alpha',1,1000),
 ('l1','s1','detail-session','detail-sim',{t0 + 5_000},'Early',1,1000),
 ('l2','s2','detail-session','detail-sim',{t0 + 20_000},'Alpha',1,2000),
 ('l3','s3','detail-session','detail-sim',{t0 + 70_000},'Alpha',1,3000),
 ('l4','s4','detail-session','detail-sim',{t0 + 110_000},'Beta',2,10000),
 ('l6','s6','detail-session','detail-sim',{t0 + 105_000},'Gamma',1,500);";
        command.ExecuteNonQuery();
    }
    using (var detailStats = new StatsService(logs.Add, detailDb))
    {
        Require(detailStats.Initialize(pluginDirectory), "run detail restart failed");
        Flush(detailStats);
        var detail = detailStats.ReadRunDetail("detail-sim") ?? throw new InvalidOperationException("run detail missing");
        var listRow = detailStats.Snapshot.RecentRuns.Single(run => run.RunId == "detail-sim");
        Require(detail.Run.ItemsLooted == listRow.ItemsLooted && detail.Run.ChaosValueMilli == listRow.ChaosValueMilli &&
                detail.Run.WallDurationMs == listRow.WallDurationMs && detail.Run.WavesCompleted == 3 &&
                detail.Run.HighestWaveStarted == 4 && detail.Run.DeathCount == 1,
            "run detail row differs from list row");
        Require(detail.LootSummary.Items == listRow.ItemsLooted && Math.Abs(detail.LootSummary.ChaosValue - listRow.ChaosValue) < 1e-9 &&
                detail.LootSummary.Items == 7 && Math.Abs(detail.LootSummary.ChaosValue - 17.5d) < 1e-9,
            "loot summary does not match list row");
        Require(detail.Waves.Count == 4 && detail.Waves.Select(w => w.Wave).SequenceEqual(new[] { 1, 2, 3, 4 }),
            "wave rows mismatch");
        Require(detail.Waves.Select(w => w.DurationMs).SequenceEqual(new long?[] { 30_000, 40_000, 60_000, null }),
            "wave durations mismatch");
        Require(detail.Waves.Select(w => w.GapBeforeMs).SequenceEqual(new long?[] { null, 22_000, 3_000, 5_000 }),
            "wave gaps mismatch");
        Require(detail.Waves[3].CompletedAtUtc == null && detail.Waves[3].StartedAtUtc != null, "unfinished wave mismatch");
        var ws = detail.WaveSummary;
        Require(ws.Completed == 3 && Math.Abs(ws.AverageMs.GetValueOrDefault() - 130_000d / 3) < 1e-6 && ws.MedianMs == 40_000 &&
                ws.FastestWave == 1 && ws.FastestMs == 30_000 &&
                ws.SlowestWave == 3 && ws.SlowestMs == 60_000 && ws.TotalWaveMs == 130_000 && ws.TotalGapMs == 30_000,
            "wave summary mismatch");
        Require(detail.Loot.Select(l => l.LootId).SequenceEqual(new[] { "l1", "l2", "l3", "l6", "l4", "l5" }),
            "loot not chronological");
        Require(detail.Loot.Select(l => l.Wave).SequenceEqual(new int?[] { null, 1, 2, 3, 3, 4 }), "loot wave assignment mismatch (a pickup at a wave's start belongs to that wave)");
        Require(detail.Waves.Select(w => w.LootCount).SequenceEqual(new[] { 1, 1, 3, 1 }) &&
                Math.Abs(detail.Waves[2].ChaosValue - 10.5d) < 1e-9, "per-wave loot aggregation mismatch");
        Require(!detail.LootTruncated, "loot unexpectedly truncated");
        Require(detail.LootSummary.TopItems.Select(i => i.ItemName).SequenceEqual(new[] { "Beta", "Alpha", "Early", "Gamma" }) &&
                detail.LootSummary.TopItems[1].Count == 3 && Math.Abs(detail.LootSummary.TopItems[1].ChaosValue - 6d) < 1e-9,
            "top items grouping mismatch");
        Require(detail.Events.Count == 10 && detail.Events[0].EventType == "run_started" &&
                detail.Events[^1].EventType == "run_ended" && detail.Events[^1].Wave == null &&
                detail.Events.Zip(detail.Events.Skip(1), (a, b) => a.OccurredAtUtc <= b.OccurredAtUtc).All(x => x) &&
                detail.Events.Count(e => e.EventType == "death") == 1,
            "events mismatch");

        var bossDetail = detailStats.ReadRunDetail("detail-boss") ?? throw new InvalidOperationException("boss detail missing");
        Require(bossDetail.Waves.Count == 0 && bossDetail.WaveSummary.Completed == 0 && bossDetail.WaveSummary.AverageMs == null &&
                bossDetail.Loot.Count == 0 && bossDetail.LootSummary.Items == 0 && bossDetail.LootSummary.TopItems.Count == 0 &&
                bossDetail.Events.Count == 0, "no-loot non-Simulacrum detail mismatch");
        Require(detailStats.ReadRunDetail("missing-run") == null, "missing run returned a detail");

        var bigDetail = detailStats.ReadRunDetail("detail-big") ?? throw new InvalidOperationException("big detail missing");
        Require(bigDetail.Loot.Count == 2000 && bigDetail.LootTruncated && bigDetail.LootSummary.Items == 2001 &&
                Math.Abs(bigDetail.LootSummary.ChaosValue - 2001d) < 1e-9, "loot cap or full-run totals mismatch");

        var legacyDetail = detailStats.ReadRunDetail("detail-legacy") ?? throw new InvalidOperationException("legacy detail missing");
        Require(legacyDetail.WaveSummary.Completed == 15 && legacyDetail.Run.DataQuality == "legacy_partial" &&
                legacyDetail.WaveSummary.AverageMs == null, "imported run without wave events lost its completed wave count");
    }

    // League seasons: each league starts its own session; older records keep a NULL league.
    var seasonDb = Path.Combine(root, "season", "stats.db");
    var seasonPlugin = Path.Combine(root, "season-plugin");
    Directory.CreateDirectory(Path.Combine(seasonPlugin, "Data"));
    string mercenariesSession;
    using (var seasons = new StatsService(logs.Add, seasonDb))
    {
        Require(seasons.Initialize(seasonPlugin), "season setup failed");
        seasons.SetRunning(true);
        seasons.RecordLoot("Pre-season Item", 2, 1, 9001, 1, "Hideout");
        Flush(seasons);
        var preSession = seasons.Snapshot.Session.SessionId;
        Require(seasons.Snapshot.CurrentLeague == null && seasons.Snapshot.Season == null, "season reported before a league was known");

        seasons.SetLeague("Covens");
        Flush(seasons);
        Require(seasons.Snapshot.CurrentLeague == "Covens" && seasons.Snapshot.Session.SessionId != preSession,
            "tracking start did not open a new session for the league");
        Require(seasons.Snapshot.Season is { ItemsLooted: 0, Attempts: 0 } && seasons.Snapshot.Lifetime.ItemsLooted == 1,
            "pre-season loot leaked into the new season");

        seasons.BeginSimulacrumActivation("season-1", "Hideout");
        seasons.ObserveSimulacrumEntry(4242, "Simulacrum");
        seasons.RecordLoot("Covens Item", 5, 1, 9002, 4242, "Simulacrum");
        seasons.RecordLoot("Covens Best Find", 150, 1, 9003, 4242, "Simulacrum");
        Flush(seasons);
        var covensSession = seasons.Snapshot.Session.SessionId;
        Require(seasons.Snapshot.SeasonFor("Simulacrum") is { Attempts: 1, ItemsLooted: 2 }, "season mode totals missed the run");

        seasons.SetLeague("Mercenaries");
        Flush(seasons);
        Require(seasons.Snapshot.Session.SessionId == covensSession, "league change split an open run");
        seasons.EndSimulacrumRun(false, "season_test");
        Flush(seasons);
        mercenariesSession = seasons.Snapshot.Session.SessionId!;
        Require(mercenariesSession != covensSession && seasons.Snapshot.Season is { Attempts: 0, ItemsLooted: 0 },
            "deferred league rotation was not applied after the run ended");

        seasons.SetLeague("Mercenaries");
        Flush(seasons);
        Require(seasons.Snapshot.Session.SessionId == mercenariesSession, "an unchanged league rotated the session");

        var list = seasons.ReadSeasons();
        Require(list.Select(s => s.League).SequenceEqual(new[] { "Mercenaries", "Covens", null }) &&
                list[0].Current && !list[1].Current && list[1].Runs == 1 && Math.Abs(list[1].ChaosValue - 155) < 1e-9 &&
                list[2].ItemsLooted == 1 && list[2].Runs == 0, "season list mismatch");
        var covens = seasons.ReadSeason("Covens") ?? throw new InvalidOperationException("Covens season missing");
        Require(covens.Summary.Attempts == 1 && covens.Summary.ItemsLooted == 2 && covens.Runs.Count == 1 &&
                covens.ByMode.ContainsKey("Simulacrum") && covens.BestFinds.Select(f => f.ItemName).SequenceEqual(new[] { "Covens Best Find" }),
                "season detail mismatch");
        var preSeason = seasons.ReadSeason("") ?? throw new InvalidOperationException("pre-season bucket missing");
        Require(preSeason.Season.League == null && preSeason.Summary.ItemsLooted == 1 && preSeason.Summary.Attempts == 0 &&
                preSeason.BestFinds.Count == 0, "pre-season bucket mismatch");
        Require(seasons.ReadSeason("Unknown League") == null, "unknown league returned a season");
    }
    using (var seasonRestart = new StatsService(logs.Add, seasonDb))
    {
        Require(seasonRestart.Initialize(seasonPlugin), "season restart failed");
        seasonRestart.SetLeague("Mercenaries");
        Flush(seasonRestart);
        Require(seasonRestart.Snapshot.Session.SessionId == mercenariesSession && seasonRestart.Snapshot.CurrentLeague == "Mercenaries",
            "restart in the same league rotated the session");
    }
    // A league change queued during a run survives a restart and is applied exactly once.
    var pendingDb = Path.Combine(root, "season-pending", "stats.db");
    string runInA;
    using (var pending = new StatsService(logs.Add, pendingDb))
    {
        Require(pending.Initialize(seasonPlugin), "pending season setup failed");
        pending.SetLeague("A");
        pending.BeginSimulacrumActivation("pending-1", "Hideout");
        pending.ObserveSimulacrumEntry(5151, "Simulacrum");
        Flush(pending);
        runInA = pending.Snapshot.CurrentRun!.RunId;
        pending.SetLeague("B");
        Flush(pending);
    }
    string sessionB;
    using (var pendingRestart = new StatsService(logs.Add, pendingDb))
    {
        Require(pendingRestart.Initialize(seasonPlugin), "pending season restart failed");
        pendingRestart.SetLeague("B");
        Flush(pendingRestart);
        sessionB = pendingRestart.Snapshot.Session.SessionId!;
        pendingRestart.BeginSimulacrumActivation("pending-2", "Hideout");
        pendingRestart.ObserveSimulacrumEntry(5252, "Simulacrum");
        pendingRestart.EndSimulacrumRun(false, "pending_test");
        Flush(pendingRestart);
        Require(pendingRestart.Snapshot.Session.SessionId == sessionB, "a league rotation left pending over a restart ran twice");
        Require(pendingRestart.ReadSeason("A")!.Runs.Single().RunId == runInA && pendingRestart.ReadSeason("B")!.Runs.Count == 1,
            "runs landed in the wrong season after a restart");

        // A manual rotation queued during a run is not dropped by a league flip-flop.
        pendingRestart.BeginSimulacrumActivation("pending-3", "Hideout");
        pendingRestart.ObserveSimulacrumEntry(5353, "Simulacrum");
        pendingRestart.RotateSession("manual");
        pendingRestart.SetLeague("C");
        pendingRestart.SetLeague("B");
        pendingRestart.EndSimulacrumRun(false, "flip_test");
        Flush(pendingRestart);
        Require(pendingRestart.Snapshot.Session.SessionId != sessionB && pendingRestart.Snapshot.CurrentLeague == "B",
            "manual rotation was lost after a league flip-flop");
    }
    using (var pendingCheck = new SqliteConnection($"Data Source={pendingDb};Pooling=False"))
    {
        pendingCheck.Open();
        using var command = pendingCheck.CreateCommand();
        command.CommandText = "SELECT end_reason FROM sessions WHERE session_id=$id";
        command.Parameters.AddWithValue("$id", sessionB);
        Require(command.ExecuteScalar() as string == "manual", "manual rotation reason was overwritten");
    }

    var freshSeasonDb = Path.Combine(root, "season-fresh", "stats.db");
    using (var fresh = new StatsService(logs.Add, freshSeasonDb))
    {
        Require(fresh.Initialize(seasonPlugin), "fresh season setup failed");
        Flush(fresh);
        var firstSession = fresh.Snapshot.Session.SessionId;
        fresh.SetLeague("Covens");
        Flush(fresh);
        Require(fresh.Snapshot.Session.SessionId == firstSession && fresh.ReadSeasons().Single().League == "Covens",
            "an empty session was rotated instead of being labelled with the league");
    }

    Console.WriteLine("Stats replay passed: modes, best finds, operational import, dedup, restart, legacy import, unavailable DB, v2 upgrade, stale-run recovery, reused activation, shutdown drain, run detail, league seasons.");
}
finally
{
    if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
}
