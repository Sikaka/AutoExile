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

    /// <summary>One runtime event waiting for the writer. Plain data: nothing is captured from the caller.</summary>
    private sealed class RuntimeEventRecord
    {
        public long OccurredAtMs { get; init; }
        public string OccurredAtUtc { get; init; } = "";
        public int ProducerThreadId { get; init; }
        public string Category { get; init; } = "";
        public string EventName { get; init; } = "";
        public string Outcome { get; init; } = "";
        public string? Reason { get; init; }
        public string? ContextJson { get; init; }
        /// <summary>Set for critical events; completed only after the row is committed.</summary>
        public TaskCompletionSource<bool>? Acknowledged { get; init; }
        public Exception? Failure { get; set; }
    }

    // Writer thread only: the open batch of consecutive runtime events.
    private readonly List<RuntimeEventRecord> _eventBatch = new(RuntimeEventBatchSize);
    private SqliteTransaction? _eventTx;
    private SqliteCommand? _eventInsert;

    /// <summary>
    /// Queues one runtime event. Never throws into the caller: this runs on the game thread.
    /// A critical event is acknowledged only after its row is committed.
    /// </summary>
    public bool RecordRuntimeEvent(string category, string eventName, string outcome,
        string? reason = null, object? context = null, bool critical = false,
        TimeSpan? timeout = null)
    {
        if (string.IsNullOrWhiteSpace(category) || string.IsNullOrWhiteSpace(eventName)) return false;
        try
        {
            var occurredAt = DateTimeOffset.UtcNow;
            if (!critical && IsCoalescedCategory(category))
            {
                var contextJson = SerializeRuntimeContext(context);
                return RecordCoalesced(category, eventName, outcome ?? "", reason, contextJson, occurredAt);
            }
            var record = new RuntimeEventRecord
            {
                OccurredAtMs = occurredAt.ToUnixTimeMilliseconds(),
                OccurredAtUtc = occurredAt.ToString("O"),
                ProducerThreadId = Environment.CurrentManagedThreadId,
                Category = category,
                EventName = eventName,
                Outcome = outcome ?? "",
                Reason = reason,
                ContextJson = SerializeRuntimeContext(context),
                Acknowledged = critical
                    ? new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)
                    : null,
            };
            if (!EnqueueRuntimeEvent(record, critical)) return false;
            if (record.Acknowledged == null) return true;

            if (!record.Acknowledged.Task.Wait(timeout ?? TimeSpan.FromMilliseconds(250)))
            {
                _log("Critical forensic event was not acknowledged before timeout.");
                return false;
            }
            if (!record.Acknowledged.Task.Result)
            {
                _log($"Critical forensic event failed: {record.Failure?.Message}");
                return false;
            }
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // ── Repeat stacking ──
    // A burst of the same high-volume event (a stuck movement loop presses R 15 times a
    // second) is stored as its first row plus one summary row carrying the repeat count.
    internal const long CoalesceWindowMs = 2000;
    internal const long CoalesceMaxSpanMs = 10_000;
    private const int CoalesceMaxKeys = 128;
    private readonly object _coalesceLock = new();
    private readonly Dictionary<string, CoalesceEntry> _coalesce = new();
    private long _lastCoalesceSweepMs;

    private sealed class CoalesceEntry
    {
        public string Category = "", EventName = "", Outcome = "";
        public string? Reason;
        public long FirstRepeatAtMs, LastAtMs;
        public int Repeats;
        public string? LastContextJson;
    }

    private static bool IsCoalescedCategory(string category)
        => category is "input" or "movement" or "navigation";

    private static readonly System.Text.RegularExpressions.Regex VolatileField = new(
        "\"(\\w*Ms|suppressed|totalRecoveries|sameSpot)\":[^,}]*,?",
        System.Text.RegularExpressions.RegexOptions.Compiled);
    // Numeric JSON values only (after ':' '[' or ','); digits inside strings such as "D1" stay exact.
    private static readonly System.Text.RegularExpressions.Regex Number = new(
        "(?<=[:\\[,])-?\\d+(\\.\\d+)?([eE][+-]?\\d+)?(?=[,}\\]])",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Same event, same rounded context: timings and counters dropped, coordinates in 10-unit cells.</summary>
    internal static string CoalesceKey(string category, string eventName, string outcome, string? reason, string? contextJson)
    {
        var ctx = contextJson ?? "";
        if (ctx.Length > 0)
        {
            ctx = VolatileField.Replace(ctx, "");
            ctx = Number.Replace(ctx, m => double.TryParse(m.Value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var v)
                ? Math.Round(v / 10d).ToString(System.Globalization.CultureInfo.InvariantCulture) : m.Value);
        }
        return $"{category}|{eventName}|{outcome}|{reason}|{ctx}";
    }

    private bool RecordCoalesced(string category, string eventName, string outcome, string? reason,
        string? contextJson, DateTimeOffset occurredAt)
    {
        var now = occurredAt.ToUnixTimeMilliseconds();
        var key = CoalesceKey(category, eventName, outcome, reason, contextJson);
        lock (_coalesceLock)
        {
            if (_coalesce.TryGetValue(key, out var entry) && now - entry.LastAtMs <= CoalesceWindowMs)
            {
                if (entry.Repeats == 0) entry.FirstRepeatAtMs = now;
                entry.Repeats++;
                entry.LastAtMs = now;
                entry.LastContextJson = contextJson;
                if (now - entry.FirstRepeatAtMs >= CoalesceMaxSpanMs) EmitCoalesceSummary(entry);
                SweepCoalesced(now, false);
                return true;
            }
            if (entry != null)
            {
                EmitCoalesceSummary(entry);
                _coalesce.Remove(key);
            }
            if (_coalesce.Count >= CoalesceMaxKeys) SweepCoalesced(now, true);
            _coalesce[key] = new CoalesceEntry
            {
                Category = category, EventName = eventName, Outcome = outcome, Reason = reason, LastAtMs = now,
            };
            SweepCoalesced(now, false);
        }
        return EnqueueRuntimeEvent(new RuntimeEventRecord
        {
            OccurredAtMs = now,
            OccurredAtUtc = occurredAt.ToString("O"),
            ProducerThreadId = Environment.CurrentManagedThreadId,
            Category = category,
            EventName = eventName,
            Outcome = outcome,
            Reason = reason,
            ContextJson = contextJson,
        }, false);
    }

    /// <summary>Writes pending repeat summaries whose window has closed; all of them when forced.</summary>
    internal void FlushCoalescedRuntimeEvents(bool force)
    {
        lock (_coalesceLock) SweepCoalesced(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), force);
    }

    // Caller holds _coalesceLock.
    private void SweepCoalesced(long now, bool force)
    {
        if (!force && now - _lastCoalesceSweepMs < 500) return;
        _lastCoalesceSweepMs = now;
        List<string>? expired = null;
        foreach (var (key, entry) in _coalesce)
        {
            if (!force && now - entry.LastAtMs <= CoalesceWindowMs) continue;
            EmitCoalesceSummary(entry);
            (expired ??= new()).Add(key);
        }
        if (expired != null) foreach (var key in expired) _coalesce.Remove(key);
    }

    // Caller holds _coalesceLock.
    private void EmitCoalesceSummary(CoalesceEntry entry)
    {
        if (entry.Repeats <= 0) return;
        var at = DateTimeOffset.FromUnixTimeMilliseconds(entry.LastAtMs);
        var context = "{\"coalesced\":true,\"repeated\":" + entry.Repeats
            + ",\"spanMs\":" + (entry.LastAtMs - entry.FirstRepeatAtMs)
            + ",\"firstRepeatAtUtc\":" + JsonSerializer.Serialize(DateTimeOffset.FromUnixTimeMilliseconds(entry.FirstRepeatAtMs).ToString("O"))
            + ",\"last\":" + (entry.LastContextJson ?? "null") + "}";
        EnqueueRuntimeEvent(new RuntimeEventRecord
        {
            OccurredAtMs = entry.LastAtMs,
            OccurredAtUtc = at.ToString("O"),
            ProducerThreadId = Environment.CurrentManagedThreadId,
            Category = entry.Category,
            EventName = entry.EventName,
            Outcome = entry.Outcome,
            Reason = entry.Reason,
            ContextJson = context,
        }, false);
        entry.Repeats = 0;
        entry.FirstRepeatAtMs = 0;
    }

    private static string? SerializeRuntimeContext(object? context)
    {
        if (context == null) return null;
        try { return JsonSerializer.Serialize(context); }
        catch (Exception ex) { return JsonSerializer.Serialize(new { contextError = ex.GetType().Name }); }
    }

    private bool EnqueueRuntimeEvent(RuntimeEventRecord record, bool critical)
    {
        if (Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _acceptingWrites) == 0 || _writes.IsAddingCompleted)
            return false;
        try
        {
            // Non-critical events stop at the allowance; the reserve belongs to accounting and critical events.
            if ((critical || _writes.Count < RuntimeEventQueueAllowance) && _writes.TryAdd(new WriteItem(record)))
                return true;
            Interlocked.Increment(ref _droppedWrites);
            Interlocked.Increment(ref _droppedForensicEvents);
            Interlocked.Increment(ref _pendingForensicOverflowEvents);
            return false;
        }
        catch (InvalidOperationException)
        {
            _log("Forensic event rejected because the stats writer is unavailable.");
            return false;
        }
    }

    /// <summary>Adds one event to the open batch; commits when the batch is full or the event is critical.</summary>
    private void AppendRuntimeEvent(SqliteConnection db, RuntimeEventRecord record)
    {
        try
        {
            // Listed first so a failure to open the transaction still accounts for this event.
            _eventBatch.Add(record);
            if (_eventTx == null) BeginRuntimeEventBatch(db);
            var insert = _eventInsert!;
            insert.Parameters["$at"].Value = record.OccurredAtMs;
            insert.Parameters["$utc"].Value = record.OccurredAtUtc;
            insert.Parameters["$committed"].Value = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            insert.Parameters["$thread"].Value = record.ProducerThreadId;
            insert.Parameters["$category"].Value = record.Category;
            insert.Parameters["$event"].Value = record.EventName;
            insert.Parameters["$outcome"].Value = record.Outcome;
            insert.Parameters["$reason"].Value = (object?)record.Reason ?? DBNull.Value;
            insert.Parameters["$context"].Value = (object?)record.ContextJson ?? DBNull.Value;
            insert.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            AbortRuntimeEventBatch(ex);
            throw;
        }

        if (record.Acknowledged != null || _eventBatch.Count >= RuntimeEventBatchSize)
            CommitRuntimeEventBatch();
    }

    private void BeginRuntimeEventBatch(SqliteConnection db)
    {
        _eventTx = db.BeginTransaction();
        // No accounting command runs while a batch is open, so session and run are constant for the batch.
        var sessionId = ScalarString(db, _eventTx,
            "SELECT session_id FROM sessions WHERE ended_at_ms IS NULL ORDER BY started_at_ms DESC LIMIT 1");
        var runId = ScalarString(db, _eventTx,
            "SELECT run_id FROM runs WHERE state='open' ORDER BY activated_at_ms DESC LIMIT 1");
        var insert = db.CreateCommand();
        _eventInsert = insert;
        insert.Transaction = _eventTx;
        insert.CommandText = @"INSERT INTO runtime_events(occurred_at_ms,occurred_at_utc,committed_at_ms,producer_thread_id,session_id,run_id,category,event_name,outcome,reason,context_json)
VALUES($at,$utc,$committed,$thread,$session,$run,$category,$event,$outcome,$reason,$context)";
        insert.Parameters.AddWithValue("$session", (object?)sessionId ?? DBNull.Value);
        insert.Parameters.AddWithValue("$run", (object?)runId ?? DBNull.Value);
        foreach (var name in new[] { "$at", "$utc", "$committed", "$thread", "$category", "$event", "$outcome", "$reason", "$context" })
            insert.Parameters.AddWithValue(name, DBNull.Value);
    }

    /// <summary>Commits the open batch, then acknowledges its critical events.</summary>
    private void CommitRuntimeEventBatch()
    {
        if (_eventTx == null) return;
        try
        {
            _eventTx.Commit();
        }
        catch (Exception ex)
        {
            AbortRuntimeEventBatch(ex);
            throw;
        }

        foreach (var record in _eventBatch)
            record.Acknowledged?.TrySetResult(true);
        ReleaseRuntimeEventBatch();
    }

    /// <summary>Rolls the open batch back, fails its acknowledgements and counts its rows as lost.</summary>
    private void AbortRuntimeEventBatch(Exception failure)
    {
        var lost = _eventBatch.Count;
        foreach (var record in _eventBatch)
        {
            if (record.Acknowledged == null) continue;
            record.Failure = failure;
            record.Acknowledged.TrySetResult(false);
        }
        try { _eventTx?.Rollback(); } catch { }
        ReleaseRuntimeEventBatch();
        if (lost <= 0) return;
        Interlocked.Add(ref _droppedWrites, lost);
        Interlocked.Add(ref _droppedForensicEvents, lost);
        Interlocked.Add(ref _pendingForensicOverflowEvents, lost);
    }

    private void ReleaseRuntimeEventBatch()
    {
        _eventBatch.Clear();
        try { _eventInsert?.Dispose(); } catch { }
        try { _eventTx?.Dispose(); } catch { }
        _eventInsert = null;
        _eventTx = null;
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
