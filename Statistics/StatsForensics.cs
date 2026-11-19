using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace AutoExile.Statistics;

public sealed partial class StatsService
{
    private const string RuntimeDroppedTotalMetaKey = "runtime_events_dropped_total";
    internal const int RuntimeEventRetentionCount = 500_000;
    internal const int RuntimeEventRetentionDays = 7;
    internal const int RuntimeCriticalEventRetentionDays = 30;
    private const long RuntimeEventRetentionAgeMs = RuntimeEventRetentionDays * 24L * 60 * 60 * 1000;
    private const long RuntimeCriticalEventRetentionAgeMs = RuntimeCriticalEventRetentionDays * 24L * 60 * 60 * 1000;
    private long _lastRuntimeMaintenanceMs;
    private long _droppedForensicEvents;
    private long _pendingForensicOverflowEvents;

    public bool RecordRuntimeEvent(string category, string eventName, string outcome,
        string? reason = null, object? context = null, bool critical = false,
        TimeSpan? timeout = null)
    {
        if (string.IsNullOrWhiteSpace(category) || string.IsNullOrWhiteSpace(eventName)) return false;
        var occurredAt = DateTimeOffset.UtcNow;
        var producerThreadId = Environment.CurrentManagedThreadId;
        var contextJson = context == null ? null : JsonSerializer.Serialize(context);
        Action<SqliteConnection> write = db =>
        {
            using var tx = db.BeginTransaction();
            var committedAt = DateTimeOffset.UtcNow;
            Execute(db, tx, @"INSERT INTO runtime_events(occurred_at_ms,occurred_at_utc,committed_at_ms,producer_thread_id,session_id,run_id,category,event_name,outcome,reason,context_json)
VALUES($at,$utc,$committed,$thread,(SELECT session_id FROM sessions WHERE ended_at_ms IS NULL ORDER BY started_at_ms DESC LIMIT 1),
(SELECT run_id FROM runs WHERE state='open' ORDER BY activated_at_ms DESC LIMIT 1),$category,$event,$outcome,$reason,$context)",
                ("$at", occurredAt.ToUnixTimeMilliseconds()), ("$utc", occurredAt.ToString("O")),
                ("$committed", committedAt.ToUnixTimeMilliseconds()), ("$thread", producerThreadId),
                ("$category", category), ("$event", eventName), ("$outcome", outcome),
                ("$reason", (object?)reason ?? DBNull.Value), ("$context", (object?)contextJson ?? DBNull.Value));
            tx.Commit();
        };
        if (critical) return EnqueueAcknowledged(write, timeout ?? TimeSpan.FromMilliseconds(250));
        return EnqueueForensics(write);
    }

    private bool EnqueueForensics(Action<SqliteConnection> command)
    {
        if (Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _acceptingWrites) == 0 || _writes.IsAddingCompleted)
            return false;
        try
        {
            if (!_writes.TryAdd(command))
            {
                Interlocked.Increment(ref _droppedWrites);
                Interlocked.Increment(ref _droppedForensicEvents);
                Interlocked.Increment(ref _pendingForensicOverflowEvents);
                return false;
            }
            return true;
        }
        catch (InvalidOperationException)
        {
            _log("Forensic event rejected because the stats writer is unavailable.");
            return false;
        }
    }

    private void FlushForensicOverflow(SqliteConnection db)
    {
        var dropped = Interlocked.Exchange(ref _pendingForensicOverflowEvents, 0);
        if (dropped <= 0) return;

        try
        {
            var now = DateTimeOffset.UtcNow;
            using var tx = db.BeginTransaction();
            Execute(db, tx, @"INSERT INTO runtime_events(occurred_at_ms,occurred_at_utc,committed_at_ms,producer_thread_id,session_id,run_id,category,event_name,outcome,reason,context_json)
VALUES($at,$utc,$committed,$thread,(SELECT session_id FROM sessions WHERE ended_at_ms IS NULL ORDER BY started_at_ms DESC LIMIT 1),
(SELECT run_id FROM runs WHERE state='open' ORDER BY activated_at_ms DESC LIMIT 1),'telemetry','queue_overflow','dropped','writer_queue_full',$context)",
                ("$at", now.ToUnixTimeMilliseconds()), ("$utc", now.ToString("O")),
                ("$committed", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
                ("$thread", Environment.CurrentManagedThreadId),
                ("$context", JsonSerializer.Serialize(new { droppedEvents = dropped, queueCapacity = StatsWriteQueueCapacity })));
            Execute(db, tx, @"INSERT INTO stats_meta(key,value) VALUES($key,CAST($count AS TEXT))
ON CONFLICT(key) DO UPDATE SET value=CAST(CAST(stats_meta.value AS INTEGER)+$count AS TEXT)",
                ("$key", RuntimeDroppedTotalMetaKey), ("$count", dropped));
            tx.Commit();
        }
        catch
        {
            Interlocked.Add(ref _pendingForensicOverflowEvents, dropped);
            throw;
        }
    }

    private void LoadForensicCounters(SqliteConnection db)
    {
        var persisted = ScalarString(db, null,
            "SELECT value FROM stats_meta WHERE key=$key", ("$key", RuntimeDroppedTotalMetaKey));
        Interlocked.Exchange(ref _droppedForensicEvents,
            long.TryParse(persisted, out var value) && value > 0 ? value : 0);
    }

    private bool EnqueueAcknowledged(Action<SqliteConnection> command, TimeSpan timeout)
    {
        var acknowledged = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception? failure = null;
        if (!EnqueueForensics(db =>
        {
            try { command(db); }
            catch (Exception ex) { failure = ex; }
            finally { acknowledged.TrySetResult(true); }
        })) return false;
        if (!acknowledged.Task.Wait(timeout))
        {
            _log("Critical forensic event was not acknowledged before timeout.");
            return false;
        }
        if (failure != null) { _log($"Critical forensic event failed: {failure.Message}"); return false; }
        return true;
    }

    private void RetainRuntimeEvents(SqliteConnection db)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (now - _lastRuntimeMaintenanceMs < 60_000) return;
        _lastRuntimeMaintenanceMs = now;
        var cutoff = now - RuntimeEventRetentionAgeMs;
        var criticalCutoff = now - RuntimeCriticalEventRetentionAgeMs;
        Execute(db, null, @"DELETE FROM runtime_events
WHERE (category='session' AND occurred_at_ms < $criticalCutoff)
   OR (category<>'session' AND occurred_at_ms < $cutoff)",
            ("$criticalCutoff", criticalCutoff), ("$cutoff", cutoff));
        Execute(db, null, @"DELETE FROM runtime_events WHERE sequence IN
(SELECT sequence FROM runtime_events WHERE category<>'session'
 ORDER BY sequence DESC LIMIT -1 OFFSET $limit)",
            ("$limit", RuntimeEventRetentionCount));
        Execute(db, null, "PRAGMA wal_checkpoint(PASSIVE)");
        Execute(db, null, "PRAGMA optimize");
    }

    public RuntimeEventReport ReadRuntimeEventReport(int limit = 100, int windowHours = 6)
        => ReadRuntimeEventReport(new RuntimeEventQuery { Limit = limit, WindowHours = windowHours });

    public RuntimeEventReport ReadRuntimeEventReport(RuntimeEventQuery? request)
    {
        request ??= new RuntimeEventQuery();
        var limit = Math.Clamp(request.Limit, 20, 500);
        var windowHours = Math.Clamp(request.WindowHours, 1, 168);
        var query = new RuntimeEventQuery
        {
            Limit = limit,
            WindowHours = windowHours,
            Category = NormalizeRuntimeFilter(request.Category),
            EventName = NormalizeRuntimeFilter(request.EventName),
            Outcome = NormalizeRuntimeFilter(request.Outcome),
            BeforeSequence = request.BeforeSequence is > 0 ? request.BeforeSequence : null,
            SinceSequence = request.SinceSequence is > 0 ? request.SinceSequence : null,
            FromUtc = request.FromUtc,
            ToUtc = request.ToUtc,
        };
        var cutoff = (query.FromUtc ?? DateTimeOffset.UtcNow.AddHours(-windowHours)).ToUnixTimeMilliseconds();
        try
        {
            using var db = new SqliteConnection($"Data Source={_databasePath};Mode=ReadOnly;Cache=Private;Pooling=False");
            db.Open();
            using (var busy = db.CreateCommand())
            {
                busy.CommandText = "PRAGMA busy_timeout=2000";
                busy.ExecuteNonQuery();
            }

            var total = ReadRuntimeCount(db, query, cutoff);
            var oldest = ReadRuntimeEventTime(db, query, cutoff, "MIN");
            var newest = ReadRuntimeEventTime(db, query, cutoff, "MAX");
            var events = ReadRuntimeEvents(db, query, cutoff, limit, out var hasMore);

            return new RuntimeEventReport
            {
                GeneratedAtUtc = DateTime.UtcNow,
                WindowHours = windowHours,
                TotalEvents = total,
                OldestEventUtc = oldest,
                NewestEventUtc = newest,
                Storage = new RuntimeStorageSnapshot
                {
                    DatabaseBytes = FileSize(_databasePath),
                    WalBytes = FileSize(_databasePath + "-wal"),
                    ShmBytes = FileSize(_databasePath + "-shm"),
                    RetentionDays = RuntimeEventRetentionDays,
                    CriticalRetentionDays = RuntimeCriticalEventRetentionDays,
                    RetentionRows = RuntimeEventRetentionCount,
                    PendingWrites = _writes.Count,
                    DroppedEvents = Volatile.Read(ref _droppedForensicEvents),
                },
                Categories = ReadRuntimeBreakdown(db, "category", cutoff),
                Outcomes = ReadRuntimeBreakdown(db, "outcome", cutoff),
                Events = events,
                HasMore = hasMore,
                NextBeforeSequence = hasMore && events.Count > 0 ? events[^1].Sequence : null,
            };
        }
        catch (Exception ex)
        {
            return new RuntimeEventReport
            {
                GeneratedAtUtc = DateTime.UtcNow,
                WindowHours = windowHours,
                Storage = new RuntimeStorageSnapshot
                {
                    DatabaseBytes = FileSize(_databasePath),
                    WalBytes = FileSize(_databasePath + "-wal"),
                    ShmBytes = FileSize(_databasePath + "-shm"),
                    RetentionDays = RuntimeEventRetentionDays,
                    CriticalRetentionDays = RuntimeCriticalEventRetentionDays,
                    RetentionRows = RuntimeEventRetentionCount,
                    PendingWrites = _writes.Count,
                    DroppedEvents = Volatile.Read(ref _droppedForensicEvents),
                },
                Error = ex.Message,
            };
        }
    }

    private static string? NormalizeRuntimeFilter(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim();
        return normalized.Length <= 64 ? normalized : normalized[..64];
    }

    private static string BuildRuntimeEventWhere(SqliteCommand cmd, RuntimeEventQuery query, long cutoff)
    {
        var clauses = new List<string> { "occurred_at_ms >= $cutoff" };
        cmd.Parameters.AddWithValue("$cutoff", cutoff);
        if (query.ToUtc.HasValue)
        {
            clauses.Add("occurred_at_ms <= $to");
            cmd.Parameters.AddWithValue("$to", query.ToUtc.Value.ToUnixTimeMilliseconds());
        }
        if (query.BeforeSequence.HasValue)
        {
            clauses.Add("sequence < $before");
            cmd.Parameters.AddWithValue("$before", query.BeforeSequence.Value);
        }
        if (query.SinceSequence.HasValue)
        {
            clauses.Add("sequence > $since");
            cmd.Parameters.AddWithValue("$since", query.SinceSequence.Value);
        }
        if (query.Category != null)
        {
            clauses.Add("category = $category");
            cmd.Parameters.AddWithValue("$category", query.Category);
        }
        if (query.EventName != null)
        {
            clauses.Add("event_name = $event");
            cmd.Parameters.AddWithValue("$event", query.EventName);
        }
        if (query.Outcome != null)
        {
            clauses.Add("outcome = $outcome");
            cmd.Parameters.AddWithValue("$outcome", query.Outcome);
        }
        return string.Join(" AND ", clauses);
    }

    private static long ReadRuntimeCount(SqliteConnection db, RuntimeEventQuery query, long cutoff)
    {
        using var cmd = db.CreateCommand();
        var where = BuildRuntimeEventWhere(cmd, query, cutoff);
        cmd.CommandText = $"SELECT COUNT(*) FROM runtime_events WHERE {where}";
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
    }

    private static DateTime? ReadRuntimeEventTime(SqliteConnection db, RuntimeEventQuery query,
        long cutoff, string aggregate)
    {
        using var cmd = db.CreateCommand();
        var where = BuildRuntimeEventWhere(cmd, query, cutoff);
        cmd.CommandText = $"SELECT {aggregate}(occurred_at_ms) FROM runtime_events WHERE {where}";
        var value = cmd.ExecuteScalar();
        return value == null || value == DBNull.Value ? null : FromMs(Convert.ToInt64(value));
    }

    private static List<RuntimeEventBreakdown> ReadRuntimeBreakdown(SqliteConnection db, string column, long cutoff)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = $@"SELECT {column},COUNT(*) FROM runtime_events
WHERE occurred_at_ms >= $cutoff GROUP BY {column} ORDER BY COUNT(*) DESC,{column} LIMIT 20";
        cmd.Parameters.AddWithValue("$cutoff", cutoff);
        var result = new List<RuntimeEventBreakdown>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) result.Add(new RuntimeEventBreakdown
        {
            Name = reader.GetString(0),
            Count = reader.GetInt64(1),
        });
        return result;
    }

    private static List<RuntimeEventSnapshot> ReadRuntimeEvents(SqliteConnection db,
        RuntimeEventQuery query, long cutoff, int limit, out bool hasMore)
    {
        using var cmd = db.CreateCommand();
        var where = BuildRuntimeEventWhere(cmd, query, cutoff);
        cmd.CommandText = $@"SELECT sequence,occurred_at_ms,committed_at_ms,producer_thread_id,session_id,run_id,
category,event_name,outcome,reason,context_json FROM runtime_events
WHERE {where} ORDER BY sequence DESC LIMIT $limit";
        cmd.Parameters.AddWithValue("$limit", limit + 1);
        var result = new List<RuntimeEventSnapshot>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) result.Add(new RuntimeEventSnapshot
        {
            Sequence = reader.GetInt64(0),
            OccurredAtUtc = FromMs(reader.GetInt64(1)),
            CommitLatencyMs = Math.Max(0, reader.GetInt64(2) - reader.GetInt64(1)),
            ProducerThreadId = reader.GetInt32(3),
            SessionId = reader.IsDBNull(4) ? null : reader.GetString(4),
            RunId = reader.IsDBNull(5) ? null : reader.GetString(5),
            Category = reader.GetString(6),
            EventName = reader.GetString(7),
            Outcome = reader.GetString(8),
            Reason = reader.IsDBNull(9) ? null : reader.GetString(9),
            ContextJson = reader.IsDBNull(10) ? null : reader.GetString(10),
        });
        hasMore = result.Count > limit;
        if (hasMore) result.RemoveAt(result.Count - 1);
        return result;
    }

    private static long FileSize(string path)
    {
        try { return File.Exists(path) ? new FileInfo(path).Length : 0; }
        catch { return 0; }
    }
}
