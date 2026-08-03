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
        stats.ReplaceLabExitMemory(today, new[]
        {
            new LabExitMemorySnapshot(today, "Test Lab", 2, 90f, "Sanctum"),
        });
        Flush(stats);
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
        Require(!unavailable.ReplaceLabExitMemory(today, Array.Empty<LabExitMemorySnapshot>()),
            "unavailable statistics store accepted an exit-memory write");
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
        Require(upgraded.Snapshot.SchemaVersion == 3 && upgraded.Snapshot.Lifetime.Consumed == 1,
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

    Console.WriteLine("Stats replay passed: modes, best finds, operational import, dedup, restart, legacy import, unavailable DB, v2 upgrade, stale-run recovery, reused activation, shutdown drain.");
}
finally
{
    if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
}
