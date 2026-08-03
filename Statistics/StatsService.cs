using Microsoft.Data.Sqlite;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;

namespace AutoExile.Statistics
{
    /// <summary>
    /// Durable statistics boundary. Game callbacks only enqueue small commands;
    /// a single worker owns SQLite and publishes one immutable snapshot for HUD/API readers.
    /// </summary>
    public sealed partial class StatsService : IDisposable
    {
        private sealed class LegacyRun
        {
            public int Id { get; init; }
            public bool HasStart { get; set; }
            public long StartedAtMs { get; set; }
            public long? EndedAtMs { get; set; }
            public string Mode { get; set; } = "";
            public string Area { get; set; } = "";
            public int HighestWave { get; set; }
            public int Deaths { get; set; }
            public bool Completed { get; set; }
        }

        private const int CurrentSchemaVersion = 3;
        private readonly Action<string> _log;
        private readonly BlockingCollection<Action<SqliteConnection>> _writes = new(8192);
        private readonly ManualResetEventSlim _initialized = new(false);
        private Thread? _worker;
        private volatile StatsSnapshot _snapshot = new();
        private string _databasePath = "";
        private string _pluginDirectory = "";
        private string _pluginDataDirectory = "";
        private long _revision;
        private int _disposed;
        private int _lastRunning = -1;
        private readonly string? _databasePathOverride;
        private static int _providerInitialized;
        private long _droppedWrites;
        private int _acceptingWrites = 1;
        private long _lastPulseMs;
        private int _bestFindsMinimumChaos = 100;

        public StatsService(Action<string> log, string? databasePathOverride = null)
        {
            _log = log;
            _databasePathOverride = databasePathOverride;
        }

        public StatsSnapshot Snapshot => _snapshot;

        public void SetBestFindsMinimumChaos(int value) =>
            Volatile.Write(ref _bestFindsMinimumChaos, Math.Max(0, value));

        public static string ResolveDatabasePath()
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(local, "ism ap", "data", "sqlite", "stats.db");
        }

        public bool Initialize(string pluginDirectory)
        {
            _pluginDirectory = pluginDirectory;
            _databasePath = _databasePathOverride ?? ResolveDatabasePath();
            _pluginDataDirectory = Path.Combine(pluginDirectory, "Data");
            _snapshot = Unavailable("initializing", null);
            _worker = new Thread(WorkerMain)
            {
                IsBackground = true,
                Name = "Stats SQLite Writer",
            };
            _worker.Start();
            _initialized.Wait(TimeSpan.FromSeconds(5));
            return _snapshot.Health.Status == "healthy";
        }

        public void SetRunning(bool running)
        {
            var value = running ? 1 : 0;
            if (Interlocked.Exchange(ref _lastRunning, value) == value) return;
            Enqueue(db => SetSessionRunning(db, running, NowMs()));
        }

        /// <summary>Requests a snapshot refresh at most once per second without touching SQLite on the game thread.</summary>
        public void Pulse()
        {
            var now = NowMs();
            var previous = Volatile.Read(ref _lastPulseMs);
            if (now - previous < 1000 || Interlocked.CompareExchange(ref _lastPulseMs, now, previous) != previous)
                return;
            Enqueue(_ => { });
        }

        public void BeginSimulacrumActivation(string activationKey, string areaName)
            => BeginRun("Simulacrum", activationKey, areaName, consumed: true, replaceOpenRun: true);

        /// <summary>
        /// Starts one durable run. Replayed activation keys are idempotent and a
        /// different open run is closed as interrupted before the new run starts.
        /// </summary>
        public void BeginRun(string mode, string activationKey, string areaName, bool consumed = false,
            bool replaceOpenRun = false)
        {
            if (string.IsNullOrWhiteSpace(mode) || string.IsNullOrWhiteSpace(activationKey)) return;
            Enqueue(db =>
            {
                using var tx = db.BeginTransaction();
                var now = NowMs();
                var sessionId = EnsureSession(db, tx, now);
                var open = GetOpenRunId(db, tx);
                if (open != null)
                {
                    var existingKey = ScalarString(db, tx,
                        "SELECT activation_key FROM runs WHERE run_id=$id", ("$id", open));
                    if (ActivationKeyMatches(existingKey, activationKey))
                    {
                        tx.Commit();
                        return;
                    }
                    var existingMode = ScalarString(db, tx,
                        "SELECT mode FROM runs WHERE run_id=$id", ("$id", open));
                    if (!replaceOpenRun && string.Equals(existingMode, mode, StringComparison.OrdinalIgnoreCase))
                    {
                        tx.Commit();
                        return;
                    }
                    CloseRun(db, tx, open, null, "replaced_by_new_activation", now);
                }

                var runId = Guid.NewGuid().ToString("N");
                var persistedActivationKey = activationKey;
                if (ScalarInt32(db, tx, "SELECT COUNT(*) FROM runs WHERE activation_key=$key",
                        ("$key", persistedActivationKey)) > 0)
                    persistedActivationKey = $"{activationKey}:{sessionId}:{runId}";
                Execute(db, tx, @"
INSERT INTO runs(run_id,session_id,mode,activation_key,area_name,state,consumed,activated_at_ms,last_observed_at_ms,data_quality)
VALUES($run,$session,$mode,$key,$area,'open',$consumed,$now,$now,'native')",
                    ("$run", runId), ("$session", sessionId), ("$mode", mode), ("$key", persistedActivationKey),
                    ("$area", areaName ?? ""), ("$consumed", consumed ? 1 : 0), ("$now", now));
                if (string.Equals(mode, "Simulacrum", StringComparison.OrdinalIgnoreCase))
                    Execute(db, tx, "INSERT INTO simulacrum_runs(run_id,consumed) VALUES($run,$consumed)",
                        ("$run", runId), ("$consumed", consumed ? 1 : 0));
                InsertEvent(db, tx, $"activation:{persistedActivationKey}", sessionId, runId,
                    "run_started", now, null, JsonSerializer.Serialize(new { mode, consumed }));
                tx.Commit();
            });
        }

        public void ObserveSimulacrumEntry(long instanceHash, string areaName)
            => ObserveRunEntry("Simulacrum", instanceHash, areaName, requireStableInstance: true);

        /// <summary>Records an entry or re-entry for the currently open run.</summary>
        public void ObserveRunEntry(string mode, long instanceHash, string areaName, bool requireStableInstance = false)
        {
            if (string.IsNullOrWhiteSpace(mode) || instanceHash == 0) return;
            Enqueue(db =>
            {
                using var tx = db.BeginTransaction();
                var runId = GetOpenRunId(db, tx, mode);
                if (runId == null) { tx.Commit(); return; }
                var now = NowMs();
                var existingHash = ScalarNullableInt64(db, tx,
                    "SELECT instance_hash FROM runs WHERE run_id=$id", ("$id", runId));
                if (requireStableInstance && existingHash.HasValue && existingHash.Value != instanceHash)
                {
                    CloseRun(db, tx, runId, "interrupted", "instance_changed", now);
                    tx.Commit();
                    return;
                }

                Execute(db, tx, @"
UPDATE runs SET instance_hash=COALESCE(instance_hash,$hash), area_name=$area,
 first_entry_at_ms=COALESCE(first_entry_at_ms,$now), entry_count=entry_count+1,
 last_observed_at_ms=$now WHERE run_id=$run",
                    ("$hash", instanceHash), ("$area", areaName ?? ""), ("$now", now), ("$run", runId));
                var entry = ScalarInt32(db, tx, "SELECT entry_count FROM runs WHERE run_id=$id", ("$id", runId));
                var sessionId = ScalarString(db, tx, "SELECT session_id FROM runs WHERE run_id=$id", ("$id", runId))!;
                InsertEvent(db, tx, $"entry:{runId}:{entry}", sessionId, runId,
                    entry == 1 ? "map_entered" : "map_reentered", now, null,
                    JsonSerializer.Serialize(new { instanceHash, areaName, entry }));
                tx.Commit();
            });
        }

        public void RecordWaveStarted(int wave)
        {
            if (wave is < 1 or > 15) return;
            WithOpenRun((db, tx, sessionId, runId, now) =>
            {
                Execute(db, tx, @"UPDATE simulacrum_runs SET highest_wave_started=MAX(highest_wave_started,$wave)
WHERE run_id=$run", ("$wave", wave), ("$run", runId));
                InsertEvent(db, tx, $"wave-start:{runId}:{wave}", sessionId, runId,
                    "wave_started", now, wave, null);
            });
        }

        public void RecordWaveCompleted(int wave)
        {
            if (wave is < 1 or > 15) return;
            WithOpenRun((db, tx, sessionId, runId, now) =>
            {
                Execute(db, tx, @"UPDATE simulacrum_runs SET waves_completed=MAX(waves_completed,$wave)
WHERE run_id=$run", ("$wave", wave), ("$run", runId));
                InsertEvent(db, tx, $"wave-complete:{runId}:{wave}", sessionId, runId,
                    "wave_completed", now, wave, null);
            });
        }

        public void RecordDeath()
        {
            WithOpenRun((db, tx, sessionId, runId, now) =>
            {
                Execute(db, tx, "UPDATE runs SET death_count=death_count+1,last_observed_at_ms=$now WHERE run_id=$run",
                    ("$now", now), ("$run", runId));
                var deaths = ScalarInt32(db, tx, "SELECT death_count FROM runs WHERE run_id=$id", ("$id", runId));
                InsertEvent(db, tx, $"death:{runId}:{deaths}", sessionId, runId,
                    "death", now, null, JsonSerializer.Serialize(new { deaths }));
            });
        }

        public void RecordLoot(string itemName, double chaosValue, int slots, long entityId,
            long instanceHash, string areaName)
        {
            var valueMilli = (long)Math.Round(Math.Max(0d, chaosValue) * 1000d,
                MidpointRounding.AwayFromZero);
            Enqueue(db =>
            {
                using var tx = db.BeginTransaction();
                var now = NowMs();
                var sessionId = EnsureSession(db, tx, now);
                var runId = GetOpenRunId(db, tx);
                var sourceKey = entityId != 0
                    ? $"loot:{runId ?? sessionId}:{instanceHash}:{entityId}"
                    : $"loot:{Guid.NewGuid():N}";
                Execute(db, tx, @"
INSERT OR IGNORE INTO loot_events(loot_id,source_key,session_id,run_id,picked_at_ms,instance_hash,
 area_name,item_name,quantity,slots,chaos_value_milli,valuation_source)
VALUES($id,$source,$session,$run,$now,$hash,$area,$item,1,$slots,$value,'ninja')",
                    ("$id", Guid.NewGuid().ToString("N")), ("$source", sourceKey),
                    ("$session", sessionId), ("$run", (object?)runId ?? DBNull.Value),
                    ("$now", now), ("$hash", instanceHash), ("$area", areaName ?? ""),
                    ("$item", itemName ?? ""), ("$slots", Math.Max(1, slots)), ("$value", valueMilli));
                tx.Commit();
            });
        }

        public void RecordEvent(string eventType, string details)
        {
            Enqueue(db =>
            {
                using var tx = db.BeginTransaction();
                var now = NowMs();
                var sessionId = EnsureSession(db, tx, now);
                var runId = GetOpenRunId(db, tx);
                InsertEvent(db, tx, $"event:{Guid.NewGuid():N}", sessionId, runId,
                    eventType, now, null, details);
                tx.Commit();
            });
        }

        public void EndSimulacrumRun(bool completed, string reason)
            => EndRun("Simulacrum", completed ? "completed" : null, reason);

        /// <summary>Closes the open run for a mode and applies any deferred session rotation.</summary>
        public void EndRun(string mode, string? result, string reason)
        {
            if (string.IsNullOrWhiteSpace(mode)) return;
            Enqueue(db =>
            {
                using var tx = db.BeginTransaction();
                var runId = GetOpenRunId(db, tx, mode);
                if (runId != null)
                    CloseRun(db, tx, runId, result, reason, NowMs());
                ApplyPendingRotation(db, tx, NowMs());
                tx.Commit();
            });
        }

        public void RotateSession(string reason = "manual")
        {
            Enqueue(db =>
            {
                using var tx = db.BeginTransaction();
                var now = NowMs();
                if (GetOpenRunId(db, tx) != null)
                {
                    Execute(db, tx, "INSERT OR REPLACE INTO stats_meta(key,value) VALUES('pending_session_rotation',$reason)",
                        ("$reason", reason));
                }
                else
                {
                    RotateSessionNow(db, tx, now, reason);
                }
                tx.Commit();
            });
        }

        public bool Flush(TimeSpan timeout)
        {
            if (Volatile.Read(ref _disposed) != 0) return false;
            var baseline = Snapshot.Revision;
            Enqueue(_ => { });
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (_writes.Count == 0 && Snapshot.Revision > baseline) return true;
                Thread.Sleep(10);
            }
            return false;
        }

        private void WithOpenRun(Action<SqliteConnection, SqliteTransaction, string, string, long> action)
        {
            Enqueue(db =>
            {
                using var tx = db.BeginTransaction();
                var runId = GetOpenRunId(db, tx);
                if (runId != null)
                {
                    var now = NowMs();
                    var sessionId = ScalarString(db, tx,
                        "SELECT session_id FROM runs WHERE run_id=$id", ("$id", runId))!;
                    action(db, tx, sessionId, runId, now);
                    Execute(db, tx, "UPDATE runs SET last_observed_at_ms=$now WHERE run_id=$run",
                        ("$now", now), ("$run", runId));
                }
                tx.Commit();
            });
        }

        private bool Enqueue(Action<SqliteConnection> command)
        {
            if (Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _acceptingWrites) == 0 || _writes.IsAddingCompleted)
                return false;
            try
            {
                if (!_writes.TryAdd(command))
                {
                    Interlocked.Increment(ref _droppedWrites);
                    _log("Stats queue is full; stats health is degraded.");
                    return false;
                }
                return true;
            }
            catch (InvalidOperationException) { return false; }
        }

        private void WorkerMain()
        {
            SqliteConnection? db = null;
            try
            {
                if (Interlocked.Exchange(ref _providerInitialized, 1) == 0)
                    SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_winsqlite3());
                Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
                db = new SqliteConnection($"Data Source={_databasePath};Mode=ReadWriteCreate;Cache=Private;Pooling=False");
                db.Open();
                Configure(db);
                CreateSchema(db);
                RecoverSessionClock(db);
                ImportLegacyFiles(db, _pluginDataDirectory);
                ImportOperationalFiles(db, _pluginDirectory);
                PublishSnapshot(db, null);
                _log($"Stats database initialized at {_databasePath}");
                _initialized.Set();

                foreach (var command in _writes.GetConsumingEnumerable())
                {
                    try
                    {
                        command(db);
                        // Coalesce bursts so HUD/API aggregation never runs once per loot item.
                        for (var i = 0; i < 255 && _writes.TryTake(out var next); i++)
                            next(db);
                        PublishSnapshot(db, null);
                    }
                    catch (Exception ex)
                    {
                        _log($"Stats write error: {ex.Message}");
                        PublishSnapshot(db, ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                Volatile.Write(ref _acceptingWrites, 0);
                _snapshot = Unavailable("unavailable", ex.Message);
                _log($"Stats database unavailable: {ex.Message}");
                _initialized.Set();
            }
            finally
            {
                if (db != null)
                {
                    try { SetSessionRunning(db, false, NowMs()); } catch { }
                    try { db.Close(); } catch { }
                    db.Dispose();
                }
            }
        }

        private static void Configure(SqliteConnection db)
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = @"
PRAGMA journal_mode=WAL;
PRAGMA foreign_keys=ON;
PRAGMA synchronous=FULL;
PRAGMA busy_timeout=2000;
PRAGMA wal_autocheckpoint=1000;";
            cmd.ExecuteNonQuery();
        }

        private static void CreateSchema(SqliteConnection db)
        {
            using var tx = db.BeginTransaction();
            Execute(db, tx, @"
CREATE TABLE IF NOT EXISTS schema_migrations(version INTEGER PRIMARY KEY,applied_at_ms INTEGER NOT NULL);
CREATE TABLE IF NOT EXISTS stats_meta(key TEXT PRIMARY KEY,value TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS sessions(
 session_id TEXT PRIMARY KEY,started_at_ms INTEGER NOT NULL,ended_at_ms INTEGER,end_reason TEXT,
 active_duration_ms INTEGER NOT NULL DEFAULT 0,resumed_at_ms INTEGER,data_quality TEXT NOT NULL DEFAULT 'native');
CREATE TABLE IF NOT EXISTS runs(
 run_id TEXT PRIMARY KEY,session_id TEXT NOT NULL REFERENCES sessions(session_id),mode TEXT NOT NULL,
 activation_key TEXT NOT NULL UNIQUE,instance_hash INTEGER,area_name TEXT NOT NULL DEFAULT '',state TEXT NOT NULL,
 result TEXT,terminal_reason TEXT,consumed INTEGER NOT NULL DEFAULT 0,activated_at_ms INTEGER NOT NULL,first_entry_at_ms INTEGER,ended_at_ms INTEGER,
 last_observed_at_ms INTEGER NOT NULL,entry_count INTEGER NOT NULL DEFAULT 0,death_count INTEGER NOT NULL DEFAULT 0,
 wall_duration_ms INTEGER NOT NULL DEFAULT 0,data_quality TEXT NOT NULL DEFAULT 'native');
CREATE UNIQUE INDEX IF NOT EXISTS ux_runs_one_open_sim ON runs(mode) WHERE mode='Simulacrum' AND state='open';
CREATE UNIQUE INDEX IF NOT EXISTS ux_runs_one_open ON runs(state) WHERE state='open';
CREATE TABLE IF NOT EXISTS simulacrum_runs(
 run_id TEXT PRIMARY KEY REFERENCES runs(run_id) ON DELETE CASCADE,consumed INTEGER NOT NULL DEFAULT 0,
 highest_wave_started INTEGER NOT NULL DEFAULT 0 CHECK(highest_wave_started BETWEEN 0 AND 15),
 waves_completed INTEGER NOT NULL DEFAULT 0 CHECK(waves_completed BETWEEN 0 AND 15));
CREATE TABLE IF NOT EXISTS stats_events(
 event_id TEXT PRIMARY KEY,source_key TEXT NOT NULL UNIQUE,session_id TEXT NOT NULL REFERENCES sessions(session_id),
 run_id TEXT REFERENCES runs(run_id),event_type TEXT NOT NULL,occurred_at_ms INTEGER NOT NULL,wave INTEGER,details TEXT);
CREATE INDEX IF NOT EXISTS ix_events_time ON stats_events(occurred_at_ms DESC);
CREATE TABLE IF NOT EXISTS loot_events(
 loot_id TEXT PRIMARY KEY,source_key TEXT NOT NULL UNIQUE,session_id TEXT NOT NULL REFERENCES sessions(session_id),
 run_id TEXT REFERENCES runs(run_id),picked_at_ms INTEGER NOT NULL,instance_hash INTEGER,area_name TEXT NOT NULL DEFAULT '',
 item_name TEXT NOT NULL,quantity INTEGER NOT NULL DEFAULT 1,slots INTEGER NOT NULL DEFAULT 1,
 chaos_value_milli INTEGER NOT NULL DEFAULT 0,valuation_source TEXT NOT NULL DEFAULT 'unknown');
CREATE INDEX IF NOT EXISTS ix_loot_time ON loot_events(picked_at_ms DESC);
CREATE INDEX IF NOT EXISTS ix_loot_value ON loot_events(chaos_value_milli DESC,picked_at_ms DESC);
CREATE TABLE IF NOT EXISTS legacy_imports(
 source_path TEXT NOT NULL,source_sha256 TEXT NOT NULL,imported_at_ms INTEGER NOT NULL,
 rows_seen INTEGER NOT NULL,rows_imported INTEGER NOT NULL,status TEXT NOT NULL,
 PRIMARY KEY(source_path,source_sha256));
CREATE TABLE IF NOT EXISTS map_metadata(
 map_name TEXT PRIMARY KEY,boss_tiles_json TEXT,last_scanned_at_ms INTEGER,
 boss_entity_path TEXT,transition_detail_name TEXT);
CREATE TABLE IF NOT EXISTS lab_exit_memory(
 day TEXT NOT NULL,zone_name TEXT NOT NULL,exit_count INTEGER NOT NULL,
 angle_milli_degrees INTEGER NOT NULL,destination_name TEXT NOT NULL,updated_at_ms INTEGER NOT NULL,
 PRIMARY KEY(day,zone_name,exit_count,angle_milli_degrees));
CREATE TABLE IF NOT EXISTS operational_imports(
 source_path TEXT PRIMARY KEY,source_sha256 TEXT NOT NULL,imported_at_ms INTEGER NOT NULL,
 rows_imported INTEGER NOT NULL,status TEXT NOT NULL);
INSERT OR IGNORE INTO schema_migrations(version,applied_at_ms) VALUES(1,$now);
INSERT OR IGNORE INTO schema_migrations(version,applied_at_ms) VALUES(2,$now);", ("$now", NowMs()));
            if (!ColumnExists(db, tx, "runs", "consumed"))
                Execute(db, tx, "ALTER TABLE runs ADD COLUMN consumed INTEGER NOT NULL DEFAULT 0");
            Execute(db, tx, @"UPDATE runs SET consumed=COALESCE(
(SELECT s.consumed FROM simulacrum_runs s WHERE s.run_id=runs.run_id),consumed)");
            Execute(db, tx, "INSERT OR IGNORE INTO schema_migrations(version,applied_at_ms) VALUES($version,$now)",
                ("$version", CurrentSchemaVersion), ("$now", NowMs()));
            tx.Commit();
        }

        private static bool ColumnExists(SqliteConnection db, SqliteTransaction tx, string table, string column)
        {
            using var cmd = db.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $"PRAGMA table_info({table})";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static void RecoverSessionClock(SqliteConnection db)
        {
            using var tx = db.BeginTransaction();
            var now = NowMs();
            var staleRuns = new List<string>();
            using (var cmd = db.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "SELECT run_id FROM runs WHERE state='open'";
                using var reader = cmd.ExecuteReader();
                while (reader.Read()) staleRuns.Add(reader.GetString(0));
            }
            foreach (var runId in staleRuns)
                CloseRun(db, tx, runId, "interrupted", "plugin_restarted", now);
            Execute(db, tx, "UPDATE sessions SET resumed_at_ms=NULL WHERE ended_at_ms IS NULL");
            EnsureSession(db, tx, now);
            tx.Commit();
        }

        private static string EnsureSession(SqliteConnection db, SqliteTransaction tx, long now)
        {
            var sessionId = ScalarString(db, tx,
                "SELECT session_id FROM sessions WHERE ended_at_ms IS NULL ORDER BY started_at_ms DESC LIMIT 1");
            if (sessionId != null) return sessionId;
            sessionId = Guid.NewGuid().ToString("N");
            Execute(db, tx, "INSERT INTO sessions(session_id,started_at_ms) VALUES($id,$now)",
                ("$id", sessionId), ("$now", now));
            return sessionId;
        }

        private static void SetSessionRunning(SqliteConnection db, bool running, long now)
        {
            using var tx = db.BeginTransaction();
            var sessionId = EnsureSession(db, tx, now);
            var resumedAt = ScalarNullableInt64(db, tx,
                "SELECT resumed_at_ms FROM sessions WHERE session_id=$id", ("$id", sessionId));
            if (running && !resumedAt.HasValue)
            {
                Execute(db, tx, "UPDATE sessions SET resumed_at_ms=$now WHERE session_id=$id",
                    ("$now", now), ("$id", sessionId));
            }
            else if (!running && resumedAt.HasValue)
            {
                Execute(db, tx, @"UPDATE sessions SET active_duration_ms=active_duration_ms+MAX(0,$now-resumed_at_ms),
resumed_at_ms=NULL WHERE session_id=$id", ("$now", now), ("$id", sessionId));
            }
            tx.Commit();
        }

        private static string? GetOpenRunId(SqliteConnection db, SqliteTransaction tx, string? mode = null) =>
            mode == null
                ? ScalarString(db, tx, "SELECT run_id FROM runs WHERE state='open' LIMIT 1")
                : ScalarString(db, tx, "SELECT run_id FROM runs WHERE state='open' AND mode=$mode LIMIT 1", ("$mode", mode));

        private static void CloseRun(SqliteConnection db, SqliteTransaction tx, string runId,
            string? explicitResult, string reason, long now)
        {
            var firstEntry = ScalarNullableInt64(db, tx,
                "SELECT first_entry_at_ms FROM runs WHERE run_id=$id", ("$id", runId));
            var mode = ScalarString(db, tx, "SELECT mode FROM runs WHERE run_id=$id", ("$id", runId)) ?? "";
            var isSimulacrum = string.Equals(mode, "Simulacrum", StringComparison.OrdinalIgnoreCase);
            var waves = isSimulacrum
                ? ScalarInt32(db, tx, "SELECT waves_completed FROM simulacrum_runs WHERE run_id=$id", ("$id", runId))
                : 0;
            var inferred = firstEntry == null ? "not_entered" : isSimulacrum && waves == 0 ? "no_progress" : "partial";
            // Simulacrum completion is derived from persisted waves, not caller optimism.
            var result = isSimulacrum && explicitResult == "completed" && waves < 15
                ? "partial"
                : explicitResult ?? inferred;
            Execute(db, tx, @"UPDATE runs SET state='closed',result=$result,terminal_reason=$reason,ended_at_ms=$now,
wall_duration_ms=MAX(0,$now-activated_at_ms),last_observed_at_ms=$now WHERE run_id=$run AND state='open'",
                ("$result", result), ("$reason", reason ?? ""), ("$now", now), ("$run", runId));
            var sessionId = ScalarString(db, tx, "SELECT session_id FROM runs WHERE run_id=$id", ("$id", runId))!;
            InsertEvent(db, tx, $"run-end:{runId}", sessionId, runId, "run_ended", now, waves,
                JsonSerializer.Serialize(new { result, reason }));
        }

        private static void ApplyPendingRotation(SqliteConnection db, SqliteTransaction tx, long now)
        {
            var reason = ScalarString(db, tx,
                "SELECT value FROM stats_meta WHERE key='pending_session_rotation'");
            if (reason == null) return;
            RotateSessionNow(db, tx, now, reason);
            Execute(db, tx, "DELETE FROM stats_meta WHERE key='pending_session_rotation'");
        }

        private static void RotateSessionNow(SqliteConnection db, SqliteTransaction tx, long now, string reason)
        {
            var sessionId = ScalarString(db, tx,
                "SELECT session_id FROM sessions WHERE ended_at_ms IS NULL ORDER BY started_at_ms DESC LIMIT 1");
            var wasRunning = false;
            if (sessionId != null)
            {
                var resumedAt = ScalarNullableInt64(db, tx,
                    "SELECT resumed_at_ms FROM sessions WHERE session_id=$id", ("$id", sessionId));
                wasRunning = resumedAt.HasValue;
                Execute(db, tx, @"UPDATE sessions SET active_duration_ms=active_duration_ms+
CASE WHEN resumed_at_ms IS NULL THEN 0 ELSE MAX(0,$now-resumed_at_ms) END,
resumed_at_ms=NULL,ended_at_ms=$now,end_reason=$reason WHERE session_id=$id",
                    ("$now", now), ("$reason", reason ?? "manual"), ("$id", sessionId));
            }
            var newSessionId = EnsureSession(db, tx, now);
            if (wasRunning)
                Execute(db, tx, "UPDATE sessions SET resumed_at_ms=$now WHERE session_id=$id",
                    ("$now", now), ("$id", newSessionId));
        }

        private void ImportLegacyFiles(SqliteConnection db, string dataDirectory)
        {
            if (!Directory.Exists(dataDirectory)) return;
            foreach (var fileName in new[] { "runs.jsonl", "loot.jsonl", "events.jsonl" })
            {
                var path = Path.Combine(dataDirectory, fileName);
                if (!File.Exists(path)) continue;
                try { ImportLegacyFile(db, path, fileName); }
                catch (Exception ex) { _log($"Legacy stats import skipped for {fileName}: {ex.Message}"); }
            }
        }

        private static void ImportLegacyFile(SqliteConnection db, string path, string fileName)
        {
            var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
            using var tx = db.BeginTransaction();
            if (ScalarInt32(db, tx, @"SELECT COUNT(*) FROM legacy_imports
WHERE source_path=$path AND source_sha256=$hash", ("$path", path), ("$hash", hash)) > 0)
            { tx.Commit(); return; }

            var sessionId = $"legacy-{hash[..24].ToLowerInvariant()}";
            var now = NowMs();
            Execute(db, tx, @"INSERT OR IGNORE INTO sessions(session_id,started_at_ms,ended_at_ms,end_reason,data_quality)
VALUES($id,$now,$now,'legacy_import','legacy_partial')", ("$id", sessionId), ("$now", now));
            var seen = 0;
            var imported = 0;
            var legacyRuns = fileName == "runs.jsonl" ? new Dictionary<int, LegacyRun>() : null;
            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                seen++;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    var at = ReadLegacyTime(root, "time", now);
                    if (fileName == "loot.jsonl")
                    {
                        var item = ReadString(root, "itemName");
                        var chaos = ReadDouble(root, "chaosValue");
                        var slots = ReadInt(root, "slots", 1);
                        Execute(db, tx, @"INSERT OR IGNORE INTO loot_events(loot_id,source_key,session_id,picked_at_ms,
area_name,item_name,quantity,slots,chaos_value_milli,valuation_source)
VALUES($id,$source,$session,$at,$area,$item,1,$slots,$value,'legacy')",
                            ("$id", Guid.NewGuid().ToString("N")), ("$source", $"legacy:{hash}:{seen}"),
                            ("$session", sessionId), ("$at", at), ("$area", ReadString(root, "area")),
                            ("$item", item), ("$slots", Math.Max(1, slots)),
                            ("$value", (long)Math.Round(Math.Max(0, chaos) * 1000d)));
                        imported++;
                    }
                    else if (fileName == "events.jsonl")
                    {
                        InsertEvent(db, tx, $"legacy:{hash}:{seen}", sessionId, null,
                            ReadString(root, "type"), at, null, ReadString(root, "message"));
                        imported++;
                    }
                    else if (legacyRuns != null)
                    {
                        var id = ReadInt(root, "id", seen);
                        if (!legacyRuns.TryGetValue(id, out var run))
                        {
                            run = new LegacyRun { Id = id, StartedAtMs = at };
                            legacyRuns.Add(id, run);
                        }

                        if (!ReadBool(root, "isUpdate"))
                        {
                            run.HasStart = true;
                            run.StartedAtMs = ReadLegacyTime(root, "startTime", at);
                            run.Mode = ReadString(root, "mode");
                            run.Area = ReadString(root, "area");
                        }
                        else
                        {
                            run.EndedAtMs = ReadLegacyNullableTime(root, "endTime") ?? at;
                        }

                        run.HighestWave = Math.Max(run.HighestWave, Math.Clamp(ReadInt(root, "highestWave", 0), 0, 15));
                        run.Deaths = Math.Max(run.Deaths, Math.Max(0, ReadInt(root, "deaths", 0)));
                        run.Completed |= ReadBool(root, "completed");
                    }
                }
                catch { }
            }

            if (legacyRuns != null)
            {
                foreach (var run in legacyRuns.Values.Where(candidate => candidate.HasStart))
                {
                    var runId = $"legacy-{hash[..12].ToLowerInvariant()}-{run.Id}";
                    var endedAt = run.EndedAtMs ?? run.StartedAtMs;
                    var result = run.Completed && run.HighestWave >= 15 ? "completed"
                        : run.HighestWave > 0 ? "partial" : "no_progress";
                    Execute(db, tx, @"INSERT OR IGNORE INTO runs(run_id,session_id,mode,activation_key,area_name,state,result,consumed,
activated_at_ms,ended_at_ms,last_observed_at_ms,death_count,wall_duration_ms,data_quality)
VALUES($run,$session,$mode,$key,$area,'closed',$result,$consumed,$start,$end,$end,$deaths,MAX(0,$end-$start),'legacy_partial')",
                        ("$run", runId), ("$session", sessionId), ("$mode", run.Mode),
                        ("$key", $"legacy:{hash}:{runId}"), ("$area", run.Area), ("$result", result),
                        ("$consumed", string.Equals(run.Mode, "Simulacrum", StringComparison.OrdinalIgnoreCase) ? 1 : 0),
                        ("$start", run.StartedAtMs), ("$end", endedAt), ("$deaths", run.Deaths));
                    if (string.Equals(run.Mode, "Simulacrum", StringComparison.OrdinalIgnoreCase))
                    {
                        Execute(db, tx, @"INSERT OR IGNORE INTO simulacrum_runs(run_id,consumed,highest_wave_started,waves_completed)
VALUES($run,1,$wave,$wave)", ("$run", runId), ("$wave", run.HighestWave));
                    }
                    imported++;
                }
            }
            Execute(db, tx, @"INSERT INTO legacy_imports(source_path,source_sha256,imported_at_ms,rows_seen,rows_imported,status)
VALUES($path,$hash,$now,$seen,$imported,'complete')",
                ("$path", path), ("$hash", hash), ("$now", now), ("$seen", seen), ("$imported", imported));
            tx.Commit();
        }

        private void PublishSnapshot(SqliteConnection db, string? error)
        {
            try
            {
                var currentSession = ScalarString(db, null,
                    "SELECT session_id FROM sessions WHERE ended_at_ms IS NULL ORDER BY started_at_ms DESC LIMIT 1");
                var currentRun = ReadRuns(db, "WHERE r.state='open'", 1).FirstOrDefault();
                _snapshot = new StatsSnapshot
                {
                    Revision = Interlocked.Increment(ref _revision),
                    UpdatedAtUtc = DateTime.UtcNow,
                    Health = new StatsHealth
                    {
                        Status = error == null && Volatile.Read(ref _droppedWrites) == 0 ? "healthy" : "degraded",
                        Error = error ?? (Volatile.Read(ref _droppedWrites) > 0
                            ? $"{Volatile.Read(ref _droppedWrites)} writes were dropped because the queue was full."
                            : null),
                        DatabasePath = _databasePath,
                        PendingWrites = _writes.Count,
                    },
                    CurrentRun = currentRun,
                    Session = ReadAggregate(db, currentSession, null),
                    Lifetime = ReadAggregate(db, null, null),
                    SessionByMode = ReadModeAggregates(db, currentSession),
                    LifetimeByMode = ReadModeAggregates(db, null),
                    RecentRuns = ReadRuns(db, "WHERE r.state='closed'", 50),
                    RecentLoot = ReadRecentLoot(db, 100),
                    BestFinds = ReadBestFinds(db, Volatile.Read(ref _bestFindsMinimumChaos) * 1000L, 20),
                    BestFindsMinimumChaos = Volatile.Read(ref _bestFindsMinimumChaos),
                    RecentEvents = ReadRecentEvents(db, 100),
                };
            }
            catch (Exception ex)
            {
                _snapshot = Unavailable("degraded", error ?? ex.Message);
            }
        }

        private StatsSnapshot Unavailable(string status, string? error) => new()
        {
            Revision = Interlocked.Increment(ref _revision),
            UpdatedAtUtc = DateTime.UtcNow,
            Health = new StatsHealth { Status = status, Error = error, DatabasePath = _databasePath, PendingWrites = _writes.Count },
        };

        private static IReadOnlyDictionary<string, StatsAggregate> ReadModeAggregates(
            SqliteConnection db, string? sessionId)
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = sessionId == null
                ? "SELECT DISTINCT mode FROM runs ORDER BY mode"
                : "SELECT DISTINCT mode FROM runs WHERE session_id=$session ORDER BY mode";
            if (sessionId != null) cmd.Parameters.AddWithValue("$session", sessionId);
            var modes = new List<string>();
            using (var reader = cmd.ExecuteReader())
                while (reader.Read()) modes.Add(reader.GetString(0));

            var result = new Dictionary<string, StatsAggregate>(StringComparer.OrdinalIgnoreCase);
            foreach (var mode in modes)
                result[mode] = ReadAggregate(db, sessionId, mode);
            return result;
        }

        private static StatsAggregate ReadAggregate(SqliteConnection db, string? sessionId, string? mode)
        {
            var predicates = new List<string>();
            if (sessionId != null) predicates.Add("r.session_id=$session");
            if (mode != null) predicates.Add("r.mode=$mode");
            var runFilter = predicates.Count == 0 ? "" : " WHERE " + string.Join(" AND ", predicates);
            using var runCmd = db.CreateCommand();
            runCmd.CommandText = $@"SELECT COUNT(*),COALESCE(SUM(r.consumed),0),COALESCE(SUM(CASE WHEN r.first_entry_at_ms IS NOT NULL THEN 1 ELSE 0 END),0),
COALESCE(SUM(CASE WHEN r.result='completed' THEN 1 ELSE 0 END),0),COALESCE(SUM(CASE WHEN r.result='partial' THEN 1 ELSE 0 END),0),
COALESCE(SUM(CASE WHEN r.result='failed' THEN 1 ELSE 0 END),0),
COALESCE(SUM(CASE WHEN r.result='no_progress' THEN 1 ELSE 0 END),0),COALESCE(SUM(CASE WHEN r.result='not_entered' THEN 1 ELSE 0 END),0),
COALESCE(SUM(CASE WHEN r.result='interrupted' THEN 1 ELSE 0 END),0),COALESCE(SUM(r.entry_count),0),
COALESCE(SUM(CASE WHEN r.entry_count>0 THEN r.entry_count-1 ELSE 0 END),0),COALESCE(SUM(r.death_count),0),
COALESCE(SUM(s.waves_completed),0),COALESCE(SUM(r.wall_duration_ms),0),
COALESCE(AVG(CASE WHEN r.first_entry_at_ms IS NOT NULL THEN s.waves_completed END),0),
COALESCE(AVG(CASE WHEN r.state='closed' THEN r.wall_duration_ms END),0)
FROM runs r LEFT JOIN simulacrum_runs s ON s.run_id=r.run_id{runFilter}";
            if (sessionId != null) runCmd.Parameters.AddWithValue("$session", sessionId);
            if (mode != null) runCmd.Parameters.AddWithValue("$mode", mode);
            using var rr = runCmd.ExecuteReader();
            rr.Read();

            using var lootCmd = db.CreateCommand();
            lootCmd.CommandText = mode == null
                ? "SELECT COALESCE(SUM(l.quantity),0),COALESCE(SUM(l.chaos_value_milli),0) FROM loot_events l" +
                  (sessionId == null ? "" : " WHERE l.session_id=$session")
                : @"SELECT COALESCE(SUM(l.quantity),0),COALESCE(SUM(l.chaos_value_milli),0)
FROM loot_events l JOIN runs r ON r.run_id=l.run_id WHERE r.mode=$mode" +
                  (sessionId == null ? "" : " AND l.session_id=$session");
            if (sessionId != null) lootCmd.Parameters.AddWithValue("$session", sessionId);
            if (mode != null) lootCmd.Parameters.AddWithValue("$mode", mode);
            using var lr = lootCmd.ExecuteReader();
            lr.Read();

            DateTime? started = null;
            long activeMs = 0;
            if (mode != null)
            {
                using var modeDurationCmd = db.CreateCommand();
                modeDurationCmd.CommandText = $@"SELECT MIN(r.activated_at_ms),COALESCE(SUM(
CASE WHEN r.state='open' THEN MAX(0,$now-r.activated_at_ms) ELSE r.wall_duration_ms END),0)
FROM runs r{runFilter}";
                modeDurationCmd.Parameters.AddWithValue("$now", NowMs());
                if (sessionId != null) modeDurationCmd.Parameters.AddWithValue("$session", sessionId);
                modeDurationCmd.Parameters.AddWithValue("$mode", mode);
                using var mr = modeDurationCmd.ExecuteReader();
                if (mr.Read())
                {
                    if (!mr.IsDBNull(0)) started = FromMs(mr.GetInt64(0));
                    activeMs = mr.GetInt64(1);
                }
            }
            else if (sessionId != null)
            {
                using var sessionCmd = db.CreateCommand();
                sessionCmd.CommandText = @"SELECT started_at_ms,active_duration_ms+
CASE WHEN resumed_at_ms IS NULL THEN 0 ELSE MAX(0,$now-resumed_at_ms) END FROM sessions WHERE session_id=$id";
                sessionCmd.Parameters.AddWithValue("$now", NowMs());
                sessionCmd.Parameters.AddWithValue("$id", sessionId);
                using var sr = sessionCmd.ExecuteReader();
                if (sr.Read()) { started = FromMs(sr.GetInt64(0)); activeMs = sr.GetInt64(1); }
            }
            else
            {
                activeMs = ScalarInt64(db, null, @"SELECT COALESCE(SUM(active_duration_ms+
CASE WHEN resumed_at_ms IS NULL THEN 0 ELSE MAX(0,$now-resumed_at_ms) END),0) FROM sessions", ("$now", NowMs()));
            }

            return new StatsAggregate
            {
                Mode = mode, SessionId = sessionId, StartedAtUtc = started, ActiveDurationMs = activeMs,
                Attempts = rr.GetInt32(0), Consumed = rr.GetInt32(1), Entered = rr.GetInt32(2), FullClears = rr.GetInt32(3),
                PartialRuns = rr.GetInt32(4), FailedRuns = rr.GetInt32(5), NoProgressRuns = rr.GetInt32(6),
                NotEnteredRuns = rr.GetInt32(7), InterruptedRuns = rr.GetInt32(8), Entries = rr.GetInt32(9),
                Reentries = rr.GetInt32(10), Deaths = rr.GetInt32(11), WavesCompleted = rr.GetInt32(12),
                RunDurationMs = rr.GetInt64(13), AverageWavesPerEnteredRun = rr.GetDouble(14),
                AverageRunDurationMs = rr.GetDouble(15), ItemsLooted = lr.GetInt32(0), ChaosValueMilli = lr.GetInt64(1),
            };
        }

        private static List<StatsRunSnapshot> ReadRuns(SqliteConnection db, string where, int limit)
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = $@"SELECT r.run_id,r.session_id,r.mode,r.consumed,r.state,r.result,r.terminal_reason,r.area_name,r.instance_hash,
r.activated_at_ms,r.first_entry_at_ms,r.ended_at_ms,r.entry_count,r.death_count,
COALESCE(s.highest_wave_started,0),COALESCE(s.waves_completed,0),
(SELECT COALESCE(SUM(quantity),0) FROM loot_events l WHERE l.run_id=r.run_id),
(SELECT COALESCE(SUM(chaos_value_milli),0) FROM loot_events l WHERE l.run_id=r.run_id),
CASE WHEN r.state='open' THEN MAX(0,$now-r.activated_at_ms) ELSE r.wall_duration_ms END,
r.data_quality FROM runs r LEFT JOIN simulacrum_runs s ON s.run_id=r.run_id
{where} ORDER BY r.activated_at_ms DESC LIMIT $limit";
            cmd.Parameters.AddWithValue("$now", NowMs());
            cmd.Parameters.AddWithValue("$limit", limit);
            var result = new List<StatsRunSnapshot>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) result.Add(new StatsRunSnapshot
            {
                RunId = reader.GetString(0), SessionId = reader.GetString(1), Mode = reader.GetString(2), Consumed = reader.GetInt32(3) != 0,
                State = reader.GetString(4), Result = reader.IsDBNull(5) ? null : reader.GetString(5),
                TerminalReason = reader.IsDBNull(6) ? null : reader.GetString(6), AreaName = reader.GetString(7),
                InstanceHash = reader.IsDBNull(8) ? null : reader.GetInt64(8), ActivatedAtUtc = FromMs(reader.GetInt64(9)),
                FirstEntryAtUtc = reader.IsDBNull(10) ? null : FromMs(reader.GetInt64(10)),
                EndedAtUtc = reader.IsDBNull(11) ? null : FromMs(reader.GetInt64(11)), EntryCount = reader.GetInt32(12),
                DeathCount = reader.GetInt32(13), HighestWaveStarted = reader.GetInt32(14), WavesCompleted = reader.GetInt32(15),
                ItemsLooted = reader.GetInt32(16), ChaosValueMilli = reader.GetInt64(17), WallDurationMs = reader.GetInt64(18),
                DataQuality = reader.GetString(19),
            });
            return result;
        }

        private static List<StatsLootSnapshot> ReadRecentLoot(SqliteConnection db, int limit)
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = @"SELECT l.loot_id,l.run_id,l.picked_at_ms,l.item_name,l.quantity,l.slots,l.chaos_value_milli,l.area_name,
COALESCE((SELECT r.mode FROM runs r WHERE r.run_id=l.run_id),'Session')
FROM loot_events l ORDER BY l.picked_at_ms DESC LIMIT $limit";
            cmd.Parameters.AddWithValue("$limit", limit);
            var result = new List<StatsLootSnapshot>();
            using var r = cmd.ExecuteReader();
            while (r.Read()) result.Add(new StatsLootSnapshot
            {
                LootId = r.GetString(0), RunId = r.IsDBNull(1) ? null : r.GetString(1), PickedAtUtc = FromMs(r.GetInt64(2)),
                ItemName = r.GetString(3), Quantity = r.GetInt32(4), Slots = r.GetInt32(5), ChaosValueMilli = r.GetInt64(6),
                AreaName = r.GetString(7), Mode = r.GetString(8),
            });
            return result;
        }

        private static List<StatsLootSnapshot> ReadBestFinds(SqliteConnection db, long minimumValueMilli, int limit)
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = @"SELECT l.loot_id,l.run_id,l.picked_at_ms,l.item_name,l.quantity,l.slots,l.chaos_value_milli,l.area_name,
COALESCE((SELECT r.mode FROM runs r WHERE r.run_id=l.run_id),'Session')
FROM loot_events l WHERE l.chaos_value_milli >= $minimum
ORDER BY l.chaos_value_milli DESC,l.picked_at_ms DESC LIMIT $limit";
            cmd.Parameters.AddWithValue("$minimum", minimumValueMilli);
            cmd.Parameters.AddWithValue("$limit", limit);
            var result = new List<StatsLootSnapshot>();
            using var r = cmd.ExecuteReader();
            while (r.Read()) result.Add(new StatsLootSnapshot
            {
                LootId = r.GetString(0), RunId = r.IsDBNull(1) ? null : r.GetString(1), PickedAtUtc = FromMs(r.GetInt64(2)),
                ItemName = r.GetString(3), Quantity = r.GetInt32(4), Slots = r.GetInt32(5), ChaosValueMilli = r.GetInt64(6),
                AreaName = r.GetString(7), Mode = r.GetString(8),
            });
            return result;
        }

        private static List<StatsEventSnapshot> ReadRecentEvents(SqliteConnection db, int limit)
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = @"SELECT event_id,run_id,occurred_at_ms,event_type,wave,details
FROM stats_events ORDER BY occurred_at_ms DESC LIMIT $limit";
            cmd.Parameters.AddWithValue("$limit", limit);
            var result = new List<StatsEventSnapshot>();
            using var r = cmd.ExecuteReader();
            while (r.Read()) result.Add(new StatsEventSnapshot
            {
                EventId = r.GetString(0), RunId = r.IsDBNull(1) ? null : r.GetString(1), OccurredAtUtc = FromMs(r.GetInt64(2)),
                EventType = r.GetString(3), Wave = r.IsDBNull(4) ? null : r.GetInt32(4), Details = r.IsDBNull(5) ? null : r.GetString(5),
            });
            return result;
        }

        private static void InsertEvent(SqliteConnection db, SqliteTransaction tx, string sourceKey,
            string sessionId, string? runId, string eventType, long at, int? wave, string? details)
        {
            Execute(db, tx, @"INSERT OR IGNORE INTO stats_events(event_id,source_key,session_id,run_id,event_type,occurred_at_ms,wave,details)
VALUES($id,$source,$session,$run,$type,$at,$wave,$details)",
                ("$id", Guid.NewGuid().ToString("N")), ("$source", sourceKey), ("$session", sessionId),
                ("$run", (object?)runId ?? DBNull.Value), ("$type", eventType), ("$at", at),
                ("$wave", (object?)wave ?? DBNull.Value), ("$details", (object?)details ?? DBNull.Value));
        }

        private static void Execute(SqliteConnection db, SqliteTransaction? tx, string sql,
            params (string Name, object? Value)[] parameters)
        {
            using var cmd = db.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            foreach (var p in parameters) cmd.Parameters.AddWithValue(p.Name, p.Value ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }

        private static string? ScalarString(SqliteConnection db, SqliteTransaction? tx, string sql,
            params (string Name, object? Value)[] parameters) => Scalar(db, tx, sql, parameters) as string;

        private static bool ActivationKeyMatches(string? persistedKey, string requestedKey) =>
            string.Equals(persistedKey, requestedKey, StringComparison.Ordinal) ||
            persistedKey?.StartsWith(requestedKey + ":", StringComparison.Ordinal) == true;

        private static int ScalarInt32(SqliteConnection db, SqliteTransaction? tx, string sql,
            params (string Name, object? Value)[] parameters) => Convert.ToInt32(Scalar(db, tx, sql, parameters) ?? 0);
        private static long ScalarInt64(SqliteConnection db, SqliteTransaction? tx, string sql,
            params (string Name, object? Value)[] parameters) => Convert.ToInt64(Scalar(db, tx, sql, parameters) ?? 0L);
        private static long? ScalarNullableInt64(SqliteConnection db, SqliteTransaction? tx, string sql,
            params (string Name, object? Value)[] parameters)
        {
            var value = Scalar(db, tx, sql, parameters);
            return value == null || value == DBNull.Value ? null : Convert.ToInt64(value);
        }
        private static object? Scalar(SqliteConnection db, SqliteTransaction? tx, string sql,
            params (string Name, object? Value)[] parameters)
        {
            using var cmd = db.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            foreach (var p in parameters) cmd.Parameters.AddWithValue(p.Name, p.Value ?? DBNull.Value);
            return cmd.ExecuteScalar();
        }

        private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        private static DateTime FromMs(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;
        private static string ReadString(JsonElement root, string name) =>
            root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
        private static int ReadInt(JsonElement root, string name, int fallback) =>
            root.TryGetProperty(name, out var value) && value.TryGetInt32(out var result) ? result : fallback;
        private static double ReadDouble(JsonElement root, string name) =>
            root.TryGetProperty(name, out var value) && value.TryGetDouble(out var result) ? result : 0d;
        private static bool ReadBool(JsonElement root, string name) =>
            root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
        private static long ReadLegacyTime(JsonElement root, string name, long fallback)
        {
            if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String) return fallback;
            return DateTimeOffset.TryParse(value.GetString(), out var parsed) ? parsed.ToUnixTimeMilliseconds() : fallback;
        }
        private static long? ReadLegacyNullableTime(JsonElement root, string name)
        {
            if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String) return null;
            return DateTimeOffset.TryParse(value.GetString(), out var parsed) ? parsed.ToUnixTimeMilliseconds() : null;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _writes.CompleteAdding();
            if (_worker != null && !_worker.Join(TimeSpan.FromSeconds(3)))
            {
                _log("Stats shutdown is still draining queued writes; waiting for the SQLite writer.");
                _worker.Join();
            }
            _initialized.Dispose();
            _writes.Dispose();
        }
    }
}
