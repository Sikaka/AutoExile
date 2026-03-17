using ExileCore;
using ExileCore.PoEMemory.Components;
using System.IO;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AutoExile.Systems
{
    /// <summary>
    /// Tick-level state recorder. Captures a ring buffer of per-tick snapshots
    /// for post-hoc analysis of combat, navigation, and decision-making.
    /// Dumps to JSON on triggers (death, HP drop, stuck, hotkey).
    /// </summary>
    public class BotRecorder
    {
        // Ring buffer — ~10 seconds at 60fps
        private const int BufferSize = 600;
        private readonly TickSnapshot[] _buffer = new TickSnapshot[BufferSize];
        private int _writeIndex;
        private int _count;
        private long _tickNumber;

        // HP drop detection
        private float _lastHpPercent = 1f;
        private const float HpDropThreshold = 0.4f; // 40% drop in one tick triggers dump

        // Dump output
        private string _outputDir = "";
        private string _lastDumpStatus = "";
        private DateTime _lastDumpTime = DateTime.MinValue;
        private const int DumpCooldownMs = 5000; // don't dump more than once per 5s

        public string LastDumpStatus => _lastDumpStatus;
        public long TickNumber => _tickNumber;

        public void SetOutputDir(string dir)
        {
            _outputDir = dir;
        }

        /// <summary>
        /// Record one tick of state. Call every tick from BotCore after mode + navigation.
        /// </summary>
        public void RecordTick(GameController gc, string modeName, string modePhase,
            string modeDecision, NavigationSystem nav,
            InteractionSystem? interaction = null, LootSystem? loot = null)
        {
            _tickNumber++;

            var player = gc.Player;
            if (player == null) return;

            var life = player.GetComponent<Life>();
            var playerGrid = player.GridPosNum;
            var hpPercent = life != null && life.MaxHP > 0 ? (float)life.CurHP / life.MaxHP : 1f;
            var esPercent = life != null && life.MaxES > 0 ? (float)life.CurES / life.MaxES : 1f;

            // Snapshot nearby threats (hostile, alive, within 80 grid units)
            var threats = new List<ThreatSnapshot>();
            try
            {
                foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
                {
                    if (entity == null || !entity.IsHostile || !entity.IsAlive) continue;
                    if (entity.DistancePlayer > 80) continue;

                    var ePos = entity.GridPosNum;
                    var threat = new ThreatSnapshot
                    {
                        EntityId = entity.Id,
                        Path = entity.Path?.Split('/').LastOrDefault() ?? "",
                        GridX = ePos.X,
                        GridY = ePos.Y,
                        Distance = entity.DistancePlayer,
                        Rarity = entity.Rarity.ToString()
                    };

                    // Try to read action state
                    try
                    {
                        var actor = entity.GetComponent<Actor>();
                        if (actor != null)
                        {
                            threat.IsAttacking = actor.isAttacking;
                            threat.IsMoving = actor.isMoving;
                            threat.Action = actor.Action.ToString();
                        }
                    }
                    catch { }

                    threats.Add(threat);
                    if (threats.Count >= 20) break; // cap to avoid perf issues
                }
            }
            catch { }

            // Get last action from BotInput
            var recentActions = BotInput.GetRecentActions(1);
            var lastAction = recentActions.Count > 0 ? recentActions[0] : null;

            var snapshot = new TickSnapshot
            {
                Tick = _tickNumber,
                DeltaTime = (float)gc.DeltaTime,
                PlayerGridX = playerGrid.X,
                PlayerGridY = playerGrid.Y,
                HpPercent = hpPercent,
                EsPercent = esPercent,
                IsAlive = player.IsAlive,
                ModeName = modeName,
                ModePhase = modePhase,
                ModeDecision = modeDecision,

                // Navigation state
                IsNavigating = nav.IsNavigating,
                NavWaypointIndex = nav.CurrentWaypointIndex,
                NavPathLength = nav.CurrentNavPath?.Count ?? 0,
                NavStuckRecoveries = nav.StuckRecoveries,
                NavBlinkPending = nav.CurrentNavPath?.Count > nav.CurrentWaypointIndex &&
                    nav.CurrentNavPath.Count > 0 &&
                    nav.CurrentWaypointIndex < nav.CurrentNavPath.Count &&
                    nav.CurrentNavPath[nav.CurrentWaypointIndex].Action == WaypointAction.Blink,
                NavLastRecovery = nav.LastRecoveryAction,

                // Threats
                NearbyThreats = threats,

                // Last action
                LastActionType = lastAction?.Type ?? "",
                LastActionAccepted = lastAction?.Accepted ?? false,
                LastActionKey = lastAction?.Key?.ToString() ?? "",

                // Interaction state — tracks loot pickup lifecycle
                InteractionBusy = interaction?.IsBusy ?? false,
                InteractionStatus = interaction?.Status ?? "",
                InteractionLastFailReason = interaction?.LastFailReason ?? "",

                // Loot state — tracks what's available and what's been blacklisted
                LootCandidateCount = loot?.LootableCount ?? 0,
                LootFailedCount = loot?.FailedCount ?? 0,
                LootHasNearby = loot?.HasLootNearby ?? false,
            };

            _buffer[_writeIndex] = snapshot;
            _writeIndex = (_writeIndex + 1) % BufferSize;
            if (_count < BufferSize) _count++;

            // Auto-dump triggers
            CheckTriggers(gc, hpPercent, player.IsAlive, nav);
            _lastHpPercent = hpPercent;
        }

        /// <summary>
        /// Force a dump (e.g., from hotkey).
        /// </summary>
        public void ForceDump(string reason)
        {
            DumpToFile(reason);
        }

        private void CheckTriggers(GameController gc, float hpPercent, bool isAlive, NavigationSystem nav)
        {
            if ((DateTime.Now - _lastDumpTime).TotalMilliseconds < DumpCooldownMs)
                return;

            // Death
            if (!isAlive && _lastHpPercent > 0)
            {
                DumpToFile("death");
                return;
            }

            // Large HP drop
            if (_lastHpPercent - hpPercent > HpDropThreshold)
            {
                DumpToFile($"hp_drop_{_lastHpPercent:F2}_to_{hpPercent:F2}");
                return;
            }

            // Navigation stuck recovery
            if (nav.StuckRecoveries > 0 && nav.LastRecoveryAction == "Repath")
            {
                DumpToFile("nav_stuck_repath");
            }
        }

        private void DumpToFile(string reason)
        {
            if (string.IsNullOrEmpty(_outputDir) || _count == 0) return;
            _lastDumpTime = DateTime.Now;

            try
            {
                Directory.CreateDirectory(_outputDir);
                var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var safeReason = reason.Replace(" ", "_").Replace("/", "_");
                var filename = $"recording_{safeReason}_{timestamp}.json";
                var path = Path.Combine(_outputDir, filename);

                // Extract ring buffer in chronological order
                var snapshots = new List<TickSnapshot>(_count);
                var startIdx = _count < BufferSize ? 0 : _writeIndex;
                for (int i = 0; i < _count; i++)
                {
                    var idx = (startIdx + i) % BufferSize;
                    if (_buffer[idx] != null)
                        snapshots.Add(_buffer[idx]);
                }

                var dump = new RecordingDump
                {
                    Reason = reason,
                    Timestamp = DateTime.Now.ToString("o"),
                    TickCount = snapshots.Count,
                    DurationSeconds = snapshots.Count > 1
                        ? snapshots.Sum(s => s.DeltaTime)
                        : 0,
                    Snapshots = snapshots
                };

                var json = JsonSerializer.Serialize(dump, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    WriteIndented = false,
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault
                });

                File.WriteAllText(path, json);
                _lastDumpStatus = $"Dumped {snapshots.Count} ticks to {filename} ({reason})";
            }
            catch (Exception ex)
            {
                _lastDumpStatus = $"Dump failed: {ex.Message}";
            }
        }

        /// <summary>
        /// Get the last N tick snapshots (newest first) for programmatic access.
        /// </summary>
        public List<TickSnapshot> GetRecentTicks(int count = 60)
        {
            var result = new List<TickSnapshot>(Math.Min(count, _count));
            for (int i = 0; i < count && i < _count; i++)
            {
                var idx = (_writeIndex - 1 - i + BufferSize) % BufferSize;
                if (_buffer[idx] != null)
                    result.Add(_buffer[idx]);
            }
            return result;
        }
    }

    // ── Snapshot DTOs ──

    public class TickSnapshot
    {
        public long Tick { get; set; }
        public float DeltaTime { get; set; }

        // Player
        public float PlayerGridX { get; set; }
        public float PlayerGridY { get; set; }
        public float HpPercent { get; set; }
        public float EsPercent { get; set; }
        public bool IsAlive { get; set; }

        // Mode
        public string ModeName { get; set; } = "";
        public string ModePhase { get; set; } = "";
        public string ModeDecision { get; set; } = "";

        // Navigation
        public bool IsNavigating { get; set; }
        public int NavWaypointIndex { get; set; }
        public int NavPathLength { get; set; }
        public int NavStuckRecoveries { get; set; }
        public bool NavBlinkPending { get; set; }
        public string NavLastRecovery { get; set; } = "";

        // Threats
        public List<ThreatSnapshot> NearbyThreats { get; set; } = new();

        // Last action
        public string LastActionType { get; set; } = "";
        public bool LastActionAccepted { get; set; }
        public string LastActionKey { get; set; } = "";

        // Interaction (loot pickup lifecycle)
        public bool InteractionBusy { get; set; }
        public string InteractionStatus { get; set; } = "";
        public string InteractionLastFailReason { get; set; } = "";

        // Loot
        public int LootCandidateCount { get; set; }
        public int LootFailedCount { get; set; }
        public bool LootHasNearby { get; set; }
    }

    public class ThreatSnapshot
    {
        public long EntityId { get; set; }
        public string Path { get; set; } = "";
        public float GridX { get; set; }
        public float GridY { get; set; }
        public float Distance { get; set; }
        public string Rarity { get; set; } = "";
        public bool IsAttacking { get; set; }
        public bool IsMoving { get; set; }
        public string Action { get; set; } = "";
    }

    public class RecordingDump
    {
        public string Reason { get; set; } = "";
        public string Timestamp { get; set; } = "";
        public int TickCount { get; set; }
        public float DurationSeconds { get; set; }
        public List<TickSnapshot> Snapshots { get; set; } = new();
    }
}
