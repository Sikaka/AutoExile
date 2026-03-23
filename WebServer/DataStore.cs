using System.Text.Json;
using System.Text.Json.Serialization;

namespace AutoExile.WebServer
{
    /// <summary>
    /// JSONL-based persistent data store for loot history, run sessions, and events.
    /// Zero external dependencies — uses System.Text.Json + append-line files.
    /// Can be upgraded to SQLite later if needed.
    /// </summary>
    public class DataStore
    {
        private string _dataDir = "";
        private string _lootFile = "";
        private string _runsFile = "";
        private string _eventsFile = "";
        private readonly object _writeLock = new();
        private int _nextRunId;
        private readonly Action<string> _log;

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        public DataStore(Action<string> log)
        {
            _log = log;
        }

        public void Initialize(string pluginDir)
        {
            _dataDir = Path.Combine(pluginDir, "Data");
            Directory.CreateDirectory(_dataDir);

            _lootFile = Path.Combine(_dataDir, "loot.jsonl");
            _runsFile = Path.Combine(_dataDir, "runs.jsonl");
            _eventsFile = Path.Combine(_dataDir, "events.jsonl");

            // Determine next run ID from existing data
            var runs = ReadFile<RunRecord>(_runsFile);
            _nextRunId = runs.Count > 0 ? runs.Max(r => r.Id) + 1 : 1;

            _log($"DataStore initialized at {_dataDir} (next run ID: {_nextRunId})");
        }

        // ================================================================
        // Loot recording
        // ================================================================

        public void RecordLoot(string itemName, double chaosValue, int slots, string area, string mode, int? runId = null)
        {
            var record = new LootDataRecord
            {
                Time = DateTime.UtcNow,
                ItemName = itemName,
                ChaosValue = chaosValue,
                Slots = slots,
                Area = area,
                Mode = mode,
                RunId = runId,
            };
            AppendLine(_lootFile, record);
        }

        public List<LootDataRecord> GetRecentLoot(int limit = 100)
        {
            var all = ReadFile<LootDataRecord>(_lootFile);
            return all.TakeLast(limit).Reverse().ToList();
        }

        // ================================================================
        // Run sessions
        // ================================================================

        public int StartRun(string mode, string area)
        {
            var id = Interlocked.Increment(ref _nextRunId);
            var record = new RunRecord
            {
                Id = id,
                StartTime = DateTime.UtcNow,
                Mode = mode,
                Area = area,
            };
            AppendLine(_runsFile, record);
            RecordEvent("run_start", $"Started {mode} in {area}", id.ToString());
            return id;
        }

        public void EndRun(int runId, int highestWave = 0, int deaths = 0, double totalChaos = 0,
            int itemsLooted = 0, bool completed = false)
        {
            // Append an update record — when reading, we merge by ID
            var record = new RunRecord
            {
                Id = runId,
                EndTime = DateTime.UtcNow,
                HighestWave = highestWave,
                Deaths = deaths,
                TotalChaos = totalChaos,
                ItemsLooted = itemsLooted,
                Completed = completed,
                IsUpdate = true,
            };
            AppendLine(_runsFile, record);
            RecordEvent("run_end", $"Run {runId} ended (wave {highestWave}, {deaths} deaths, {totalChaos:F0}c)");
        }

        public List<RunRecord> GetRecentRuns(int limit = 50)
        {
            var all = ReadFile<RunRecord>(_runsFile);

            // Merge updates: later entries with same ID override earlier ones
            var merged = new Dictionary<int, RunRecord>();
            foreach (var r in all)
            {
                if (merged.TryGetValue(r.Id, out var existing))
                {
                    // Merge: update fields that are set
                    if (r.EndTime.HasValue) existing.EndTime = r.EndTime;
                    if (r.HighestWave > 0) existing.HighestWave = r.HighestWave;
                    if (r.Deaths > 0) existing.Deaths = r.Deaths;
                    if (r.TotalChaos > 0) existing.TotalChaos = r.TotalChaos;
                    if (r.ItemsLooted > 0) existing.ItemsLooted = r.ItemsLooted;
                    if (r.Completed) existing.Completed = true;
                }
                else
                {
                    merged[r.Id] = r;
                }
            }

            return merged.Values
                .OrderByDescending(r => r.StartTime)
                .Take(limit)
                .ToList();
        }

        // ================================================================
        // Events
        // ================================================================

        public void RecordEvent(string type, string message, string? details = null)
        {
            var record = new EventRecord
            {
                Time = DateTime.UtcNow,
                Type = type,
                Message = message,
                Details = details,
            };
            AppendLine(_eventsFile, record);
        }

        public List<EventRecord> GetRecentEvents(int limit = 100)
        {
            var all = ReadFile<EventRecord>(_eventsFile);
            return all.TakeLast(limit).Reverse().ToList();
        }

        // ================================================================
        // File I/O
        // ================================================================

        private void AppendLine<T>(string filePath, T record)
        {
            try
            {
                var json = JsonSerializer.Serialize(record, JsonOpts);
                Task.Run(() =>
                {
                    try
                    {
                        lock (_writeLock)
                        {
                            File.AppendAllText(filePath, json + "\n");
                        }
                    }
                    catch (Exception ex)
                    {
                        _log($"DataStore write error: {ex.Message}");
                    }
                });
            }
            catch (Exception ex)
            {
                _log($"DataStore serialize error: {ex.Message}");
            }
        }

        private static List<T> ReadFile<T>(string filePath)
        {
            var results = new List<T>();
            if (!File.Exists(filePath)) return results;

            try
            {
                foreach (var line in File.ReadLines(filePath))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        var record = JsonSerializer.Deserialize<T>(line, JsonOpts);
                        if (record != null) results.Add(record);
                    }
                    catch { } // Skip malformed lines
                }
            }
            catch { }

            return results;
        }
    }

    // ================================================================
    // Data records
    // ================================================================

    public class LootDataRecord
    {
        public DateTime Time { get; set; }
        public string ItemName { get; set; } = "";
        public double ChaosValue { get; set; }
        public int Slots { get; set; } = 1;
        public string? Area { get; set; }
        public string? Mode { get; set; }
        public int? RunId { get; set; }
    }

    public class RunRecord
    {
        public int Id { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public string Mode { get; set; } = "";
        public string? Area { get; set; }
        public int HighestWave { get; set; }
        public int Deaths { get; set; }
        public double TotalChaos { get; set; }
        public int ItemsLooted { get; set; }
        public bool Completed { get; set; }
        public bool IsUpdate { get; set; }
    }

    public class EventRecord
    {
        public DateTime Time { get; set; }
        public string Type { get; set; } = "";
        public string Message { get; set; } = "";
        public string? Details { get; set; }
    }
}
