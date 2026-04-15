using System.Linq;
using System.Numerics;

namespace AutoExile.Mechanics
{
    /// <summary>
    /// Owns all registered in-map mechanics. MappingMode calls into this each tick
    /// to detect, prioritize, and dispatch mechanic handling.
    /// Lives on BotContext so any mode can use it.
    /// </summary>
    public class MapMechanicManager
    {
        private readonly List<IMapMechanic> _mechanics = new();
        private IMapMechanic? _active;
        private DateTime _lastDetectTime = DateTime.MinValue;
        private const float DetectIntervalMs = 1000;

        /// <summary>Currently active mechanic (being worked on), or null.</summary>
        public IMapMechanic? ActiveMechanic => _active;

        /// <summary>Mechanics that have been detected but not yet started.</summary>
        public IReadOnlyList<IMapMechanic> DetectedMechanics => _detected;
        private readonly List<IMapMechanic> _detected = new();

        /// <summary>
        /// Non-repeatable mechanics that have been completed this map.
        /// Repeatable mechanics are reset after completion and won't appear here.
        /// Use CompletionCounts for full history.
        /// </summary>
        public IReadOnlyList<IMapMechanic> CompletedMechanics => _completed;
        private readonly List<IMapMechanic> _completed = new();

        /// <summary>All registered mechanics.</summary>
        public IReadOnlyList<IMapMechanic> AllMechanics => _mechanics;

        /// <summary>
        /// How many times each mechanic has completed this map (by name).
        /// Includes both repeatable and non-repeatable mechanics.
        /// </summary>
        public IReadOnlyDictionary<string, int> CompletionCounts => _completionCounts;
        private readonly Dictionary<string, int> _completionCounts = new();

        public void Register(IMapMechanic mechanic)
        {
            _mechanics.Add(mechanic);
        }

        /// <summary>
        /// Check if all Required mechanics have been completed (or failed/abandoned).
        /// Used by MappingMode to decide if map is "done".
        /// For repeatable mechanics, "Required" means at least one completion.
        /// </summary>
        public bool AllRequiredComplete(BotSettings.MechanicsSettings settings)
        {
            // A Required mechanic is satisfied if: completed at least once, or not found.
            // "Not found" is handled by MappingMode's coverage threshold — if 90%+ explored
            // and mechanic not detected, it's not in this map.
            foreach (var m in _mechanics)
            {
                var mode = GetMechanicMode(m, settings);
                if (mode != MechanicMode.Required) continue;

                // Has it completed at least once?
                if (_completionCounts.TryGetValue(m.Name, out var count) && count > 0)
                    continue;

                // If detected or active but not yet completed, it's not done
                if (_detected.Contains(m) && !m.IsComplete)
                    return false;
                if (_active == m && !m.IsComplete)
                    return false;
            }
            return true;
        }

        /// <summary>
        /// Periodic detection scan. Finds mechanics in the world that haven't been handled yet.
        /// Returns the highest-priority mechanic that needs attention, or null.
        /// </summary>
        public IMapMechanic? DetectAndPrioritize(BotContext ctx)
        {
            if (_active != null) return _active;

            var now = DateTime.Now;
            if ((now - _lastDetectTime).TotalMilliseconds < DetectIntervalMs) return null;
            _lastDetectTime = now;

            foreach (var mechanic in _mechanics)
            {
                if (mechanic.IsComplete) continue;
                // Non-repeatable mechanics stay in _completed once done
                if (_completed.Contains(mechanic)) continue;

                var mode = GetMechanicMode(mechanic, ctx.Settings.Mechanics);
                if (mode == MechanicMode.Skip) continue;

                if (mechanic.Detect(ctx) && !_detected.Contains(mechanic))
                {
                    var countStr = "";
                    if (mechanic.IsRepeatable && _completionCounts.TryGetValue(mechanic.Name, out var c) && c > 0)
                        countStr = $" (#{c + 1})";
                    _detected.Add(mechanic);
                    ctx.Log($"[Mechanics] Detected: {mechanic.Name}{countStr} at {mechanic.AnchorGridPos}");
                }
            }

            // Return first detected, non-complete mechanic
            foreach (var m in _detected)
            {
                if (!m.IsComplete) return m;
            }
            return null;
        }

        /// <summary>
        /// Set a mechanic as the active one. MappingMode calls this when it decides
        /// to engage with a detected mechanic.
        /// </summary>
        public void SetActive(IMapMechanic mechanic)
        {
            _active = mechanic;
        }

        /// <summary>
        /// Tick the active mechanic. Returns the result.
        /// On terminal results, records completion and clears active.
        /// Repeatable mechanics are Reset() so they can detect the next encounter.
        /// </summary>
        public MechanicResult TickActive(BotContext ctx)
        {
            if (_active == null) return MechanicResult.Idle;

            var result = _active.Tick(ctx);

            if (result == MechanicResult.Complete ||
                result == MechanicResult.Abandoned ||
                result == MechanicResult.Failed)
            {
                var name = _active.Name;
                _completionCounts[name] = _completionCounts.GetValueOrDefault(name) + 1;
                ctx.Log($"[Mechanics] {name} finished: {result} (total: {_completionCounts[name]})");

                _detected.Remove(_active);

                if (_active.IsRepeatable)
                {
                    // Reset so it can detect the next encounter
                    _active.Reset();
                }
                else
                {
                    if (!_completed.Contains(_active))
                        _completed.Add(_active);
                }

                _active = null;
            }

            return result;
        }

        /// <summary>
        /// Force the active mechanic to completed state. Called before caching area state
        /// so that the mechanic (e.g., Wishes after portal entry) is marked done when restored.
        /// </summary>
        public void ForceCompleteActive()
        {
            if (_active == null) return;

            var name = _active.Name;
            _completionCounts[name] = _completionCounts.GetValueOrDefault(name) + 1;

            _detected.Remove(_active);

            if (_active.IsRepeatable)
            {
                _active.Reset();
            }
            else
            {
                if (!_completed.Contains(_active))
                    _completed.Add(_active);
            }

            _active = null;
        }

        /// <summary>
        /// Reset all mechanics. Called on area change.
        /// </summary>
        public void Reset()
        {
            _active = null;
            _detected.Clear();
            _completed.Clear();
            _completionCounts.Clear();
            _lastDetectTime = DateTime.MinValue;
            foreach (var m in _mechanics)
                m.Reset();
        }

        /// <summary>
        /// Mark a mechanic as completed by name, preventing re-detection.
        /// Used when entering sub-zones to suppress the triggering mechanic.
        /// </summary>
        public void SuppressMechanic(string name)
        {
            foreach (var m in _mechanics)
            {
                if (m.Name == name && !_completed.Contains(m))
                {
                    _completed.Add(m);
                    _completionCounts[name] = _completionCounts.GetValueOrDefault(name) + 1;
                }
            }
        }

        /// <summary>
        /// Capture which mechanics are completed/detected so state survives cross-zone trips.
        /// </summary>
        public MechanicsSnapshot CreateSnapshot()
        {
            return new MechanicsSnapshot
            {
                CompletedNames = _completed.Select(m => m.Name).ToHashSet(),
                DetectedNames = _detected.Select(m => m.Name).ToHashSet(),
                CompletionCounts = new Dictionary<string, int>(_completionCounts),
            };
        }

        /// <summary>
        /// Restore mechanic completion state from a snapshot. Marks matching mechanics
        /// as complete so they won't be re-detected.
        /// </summary>
        public void RestoreSnapshot(MechanicsSnapshot snapshot)
        {
            _active = null;
            _detected.Clear();
            _completed.Clear();
            _completionCounts.Clear();
            _lastDetectTime = DateTime.MinValue;

            foreach (var m in _mechanics)
            {
                m.Reset();
                // Non-repeatable: put back in completed list so they won't re-detect
                if (!m.IsRepeatable && snapshot.CompletedNames.Contains(m.Name))
                    _completed.Add(m);
            }

            // Restore counts for all mechanics (repeatable and non-repeatable)
            foreach (var kvp in snapshot.CompletionCounts)
                _completionCounts[kvp.Key] = kvp.Value;
        }

        /// <summary>
        /// Get the total completion count for a mechanic by name.
        /// </summary>
        public int GetCompletionCount(string name)
        {
            return _completionCounts.GetValueOrDefault(name);
        }

        /// <summary>
        /// Check if all mechanics marked ExitAfter=true have completed.
        /// Returns false if no mechanics have ExitAfter=true (fall through to coverage-based exit).
        /// Returns true when ALL exit-after mechanics are satisfied (completed at least once).
        /// </summary>
        public bool AreExitMechanicsComplete(BotSettings settings)
        {
            bool anyExitAfter = false;
            foreach (var m in _mechanics)
            {
                var exitAfter = GetExitAfter(m, settings);
                if (!exitAfter) continue;

                anyExitAfter = true;

                // Must have completed at least once
                if (!_completionCounts.TryGetValue(m.Name, out var count) || count == 0)
                    return false;
            }
            return anyExitAfter;
        }

        /// <summary>
        /// Check if any registered mechanic is detected but not yet completed.
        /// Used to delay map exit until in-progress mechanics finish.
        /// </summary>
        public bool HasPendingMechanics()
        {
            if (_active != null && !_active.IsComplete)
                return true;
            foreach (var m in _detected)
            {
                if (!m.IsComplete) return true;
            }
            return false;
        }

        private static bool GetExitAfter(IMapMechanic mechanic, BotSettings settings)
        {
            return mechanic.Name switch
            {
                "Ultimatum" => settings.Mechanics.Ultimatum.ExitAfter.Value,
                "Harvest" => settings.Mechanics.Harvest.ExitAfter.Value,
                "Wishes" => settings.Mechanics.Wishes.ExitAfter.Value,
                "Essence" => settings.Mechanics.Essence.ExitAfter.Value,
                "Ritual" => settings.Mechanics.Ritual.ExitAfter.Value,
                _ => false,
            };
        }

        private MechanicMode GetMechanicMode(IMapMechanic mechanic, BotSettings.MechanicsSettings settings)
        {
            // Map mechanic name to its settings mode
            return mechanic.Name switch
            {
                "Ultimatum" => ParseMode(settings.Ultimatum.Mode.Value),
                "Harvest" => ParseMode(settings.Harvest.Mode.Value),
                "Wishes" => ParseMode(settings.Wishes.Mode.Value),
                "Essence" => ParseMode(settings.Essence.Mode.Value),
                "Ritual" => ParseMode(settings.Ritual.Mode.Value),
                _ => MechanicMode.Skip,
            };
        }

        private static MechanicMode ParseMode(string value)
        {
            return Enum.TryParse<MechanicMode>(value, out var mode) ? mode : MechanicMode.Skip;
        }
    }

    /// <summary>Snapshot of mechanic completion state for cross-zone caching.</summary>
    public class MechanicsSnapshot
    {
        public HashSet<string> CompletedNames = new();
        public HashSet<string> DetectedNames = new();
        public Dictionary<string, int> CompletionCounts = new();
    }
}
