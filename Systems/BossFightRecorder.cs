using ExileCore;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.PoEMemory.Components;
using ExileCore.Shared.Enums;
using System.IO;
using System.Numerics;

namespace AutoExile.Systems
{
    /// <summary>
    /// Standalone boss fight recorder — runs every tick regardless of bot mode.
    /// Auto-activates when player enters a registered boss arena.
    ///
    /// Recordings are keyed by area instance hash — dying and re-entering the same
    /// instance continues the same log file, giving a complete picture of the fight.
    /// A new file is only created when entering a fresh instance (new hash).
    /// </summary>
    public class BossFightRecorder
    {
        private static readonly HashSet<string> TrackedArenas = new(StringComparer.OrdinalIgnoreCase)
        {
            "Absence of Mercy and Empathy",  // Maven
            "Moment of Trauma",              // Incarnation of Fear
            "The Black Barya",               // Saresh (Faridun League)
        };

        private StreamWriter? _log;
        private string _lastArea = "";
        private long _lastAreaHash;
        private long _activeHash;  // hash of the instance we're recording
        private string _currentArena = "";
        private bool _recording;
        private DateTime _lastTickLog;
        private DateTime _lastDetailedLog;
        private DateTime _fightStart;
        private string _outputDir = "";
        private int _entryCount; // how many times we've entered this instance

        // Intervals
        private const float TickLogMs = 300f;
        private const float DetailedLogMs = 1000f;

        // Track unique monster state changes
        private readonly Dictionary<uint, MonsterSnapshot> _monsterSnapshots = new();

        // Track player state
        private float _lastPlayerHpPct;
        private float _lastPlayerEsPct;

        public bool IsRecording => _recording;

        private struct MonsterSnapshot
        {
            public string Animation;
            public string StateMachine;
            public bool IsAlive;
            public bool IsTargetable;
            public Vector2 GridPos;
        }

        public void Initialize(string pluginDir)
        {
            _outputDir = Path.Combine(pluginDir, "Dumps");
        }

        public void Tick(GameController gc)
        {
            if (gc?.Player == null || gc.Area?.CurrentArea == null) return;

            var areaName = gc.Area.CurrentArea.Name ?? "";
            if (string.IsNullOrEmpty(areaName)) return;

            var areaHash = gc.IngameState?.Data?.CurrentAreaHash ?? 0;

            // Detect area change
            if (areaName != _lastArea || areaHash != _lastAreaHash)
            {
                var wasTracked = TrackedArenas.Contains(_lastArea);
                var isTracked = TrackedArenas.Contains(areaName);

                if (_recording && !isTracked)
                {
                    // Left a tracked arena for a non-tracked area (hideout/town after death)
                    // DON'T stop recording — keep the file open for re-entry
                    Log($"--- LEFT ARENA → {areaName} (hash={areaHash}) — recording paused ---");
                }
                else if (_recording && isTracked && areaHash != _activeHash)
                {
                    // Entered a DIFFERENT instance of a tracked arena — new fight
                    StopRecording("New instance");
                    StartRecording(areaName, areaHash);
                }
                else if (!_recording && isTracked)
                {
                    // Entering a tracked arena fresh
                    StartRecording(areaName, areaHash);
                }
                else if (_recording && isTracked && areaHash == _activeHash)
                {
                    // Re-entering the SAME instance (death re-entry)
                    _entryCount++;
                    Log($"--- RE-ENTERED ARENA (entry #{_entryCount}, hash={areaHash}) " +
                        $"player=({gc.Player.GridPosNum.X:F0},{gc.Player.GridPosNum.Y:F0}) ---");
                }

                _lastArea = areaName;
                _lastAreaHash = areaHash;
            }

            // Safety: in tracked arena but not recording (plugin reload mid-fight)
            if (!_recording && TrackedArenas.Contains(areaName))
                StartRecording(areaName, areaHash);

            // Only log ticks when actually in the arena (not in hideout between deaths)
            if (!_recording || !TrackedArenas.Contains(areaName)) return;

            var playerGrid = gc.Player.GridPosNum;
            var now = DateTime.Now;

            // ── Track all unique/rare monsters and log state changes ──
            try
            {
                foreach (var e in gc.EntityListWrapper.ValidEntitiesByType[EntityType.Monster])
                {
                    if (!e.IsHostile) continue;
                    if (e.Rarity != MonsterRarity.Unique && e.Rarity != MonsterRarity.Rare) continue;
                    if (e.DistancePlayer > 150) continue;

                    var id = e.Id;
                    var actor = e.GetComponent<Actor>();
                    var anim = actor?.Animation.ToString() ?? "?";
                    var alive = e.IsAlive;
                    var targetable = e.IsTargetable;
                    var grid = e.GridPosNum;

                    var smStr = "";
                    if (e.TryGetComponent<StateMachine>(out var sm) && sm?.States != null)
                    {
                        foreach (var s in sm.States)
                            smStr += $"{s.Name}={s.Value} ";
                    }

                    var lifeStr = "";
                    var life = e.GetComponent<Life>();
                    if (life != null)
                        lifeStr = $" hp={life.CurHP}/{life.MaxHP}";

                    var shortPath = e.Path?.Split('/').Last() ?? "?";

                    if (_monsterSnapshots.TryGetValue(id, out var prev))
                    {
                        bool changed = prev.Animation != anim
                            || prev.StateMachine != smStr
                            || prev.IsAlive != alive
                            || prev.IsTargetable != targetable
                            || Vector2.Distance(prev.GridPos, grid) > 5;

                        if (changed)
                        {
                            var changes = new List<string>();
                            if (prev.Animation != anim) changes.Add($"anim:{prev.Animation}→{anim}");
                            if (prev.StateMachine != smStr) changes.Add($"sm:{{{smStr.Trim()}}}");
                            if (prev.IsAlive != alive) changes.Add($"alive:{prev.IsAlive}→{alive}");
                            if (prev.IsTargetable != targetable) changes.Add($"tgt:{prev.IsTargetable}→{targetable}");
                            if (Vector2.Distance(prev.GridPos, grid) > 5) changes.Add($"pos:({grid.X:F0},{grid.Y:F0})");

                            Log($"CHANGE {shortPath} [{e.Rarity}] id={id}: {string.Join(" | ", changes)}{lifeStr}");
                        }
                    }
                    else
                    {
                        Log($"NEW {shortPath} [{e.Rarity}] id={id} at ({grid.X:F0},{grid.Y:F0}) " +
                            $"alive={alive} tgt={targetable} {anim}{lifeStr} sm:{{{smStr.Trim()}}}");
                    }

                    _monsterSnapshots[id] = new MonsterSnapshot
                    {
                        Animation = anim,
                        StateMachine = smStr,
                        IsAlive = alive,
                        IsTargetable = targetable,
                        GridPos = grid,
                    };
                }
            }
            catch { }

            // ── Track player HP/ES changes ──
            try
            {
                var pLife = gc.Player.GetComponent<Life>();
                if (pLife != null)
                {
                    float hpPct = pLife.MaxHP > 0 ? (float)pLife.CurHP / pLife.MaxHP * 100 : 0;
                    float esPct = pLife.MaxES > 0 ? (float)pLife.CurES / pLife.MaxES * 100 : 0;

                    if (Math.Abs(hpPct - _lastPlayerHpPct) > 20 || Math.Abs(esPct - _lastPlayerEsPct) > 20)
                    {
                        Log($"PLAYER HP:{hpPct:F0}% (was {_lastPlayerHpPct:F0}%) ES:{esPct:F0}% (was {_lastPlayerEsPct:F0}%) " +
                            $"at ({playerGrid.X:F0},{playerGrid.Y:F0})");
                        _lastPlayerHpPct = hpPct;
                        _lastPlayerEsPct = esPct;
                    }
                }
            }
            catch { }

            // ── Periodic tick log ──
            if ((now - _lastTickLog).TotalMilliseconds >= TickLogMs)
            {
                _lastTickLog = now;
                var elapsed = (now - _fightStart).TotalSeconds;

                var summary = "";
                foreach (var kv in _monsterSnapshots)
                {
                    if (!kv.Value.IsAlive) continue;
                    summary += $" | {kv.Key}:{kv.Value.Animation}@({kv.Value.GridPos.X:F0},{kv.Value.GridPos.Y:F0})";
                }

                Log($"[{elapsed:F1}s] player=({playerGrid.X:F0},{playerGrid.Y:F0}){summary}");
            }

            // ── Full entity dump ──
            if ((now - _lastDetailedLog).TotalMilliseconds >= DetailedLogMs)
            {
                _lastDetailedLog = now;
                LogEntityDump(gc);
            }
        }

        private void LogEntityDump(GameController gc)
        {
            Log("--- ENTITY DUMP ---");
            try
            {
                foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
                {
                    if (entity.DistancePlayer > 120) continue;
                    var path = entity.Path;
                    if (path == null) continue;

                    if (path == "Metadata/Effects/Effect" || path == "Metadata/Effects/PermanentEffect")
                        continue;
                    if (path.Contains("Skitterbot") || entity.Type == EntityType.Player)
                        continue;

                    var shortPath = path.Split('/').Last();
                    var grid = entity.GridPosNum;

                    var smStr = "";
                    if (entity.TryGetComponent<StateMachine>(out var sm) && sm?.States != null)
                    {
                        foreach (var s in sm.States)
                            smStr += $"{s.Name}={s.Value} ";
                    }

                    var actStr = "";
                    if (entity.Type == EntityType.Monster)
                    {
                        var actor = entity.GetComponent<Actor>();
                        if (actor != null) actStr = $" act={actor.Animation}";
                    }

                    var lifeStr = "";
                    if (entity.Type == EntityType.Monster && entity.IsAlive)
                    {
                        var life = entity.GetComponent<Life>();
                        if (life != null) lifeStr = $" hp={life.CurHP}/{life.MaxHP}";
                    }

                    Log($"  {entity.Id} | {shortPath} | ({grid.X:F0},{grid.Y:F0}) d={entity.DistancePlayer:F0} " +
                        $"| alive={entity.IsAlive} tgt={entity.IsTargetable} {entity.Rarity}" +
                        $"{actStr}{lifeStr} | sm:{smStr}");
                }
            }
            catch (InvalidOperationException) { }
            Log("--- END DUMP ---");
        }

        private void StartRecording(string arenaName, long areaHash)
        {
            if (_recording) return;
            try
            {
                Directory.CreateDirectory(_outputDir);
                var safeName = arenaName.Replace(" ", "_");
                var fileName = $"boss_recorder_{safeName}_{areaHash}_{DateTime.Now:yyyyMMdd_HHmmss}.txt";
                var logPath = Path.Combine(_outputDir, fileName);
                _log = new StreamWriter(logPath, append: false) { AutoFlush = true };

                _recording = true;
                _currentArena = arenaName;
                _activeHash = areaHash;
                _fightStart = DateTime.Now;
                _entryCount = 1;
                _lastTickLog = DateTime.MinValue;
                _lastDetailedLog = DateTime.MinValue;
                _monsterSnapshots.Clear();
                _lastPlayerHpPct = 100;
                _lastPlayerEsPct = 100;

                Log($"{'=',-80}");
                Log($"BOSS FIGHT RECORDING — {arenaName}");
                Log($"Instance hash: {areaHash} — Entry #1 — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                Log($"{'=',-80}");
            }
            catch { _recording = false; }
        }

        private void StopRecording(string reason)
        {
            if (!_recording) return;
            var duration = (DateTime.Now - _fightStart).TotalSeconds;
            Log($"{'=',-80}");
            Log($"RECORDING ENDED — {reason} — duration: {duration:F1}s — entries: {_entryCount}");
            Log($"{'=',-80}");
            _log?.Dispose();
            _log = null;
            _recording = false;
            _activeHash = 0;
            _monsterSnapshots.Clear();
        }

        private void Log(string msg)
        {
            _log?.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {msg}");
        }

        public void Dispose()
        {
            StopRecording("Plugin unload");
        }
    }
}
