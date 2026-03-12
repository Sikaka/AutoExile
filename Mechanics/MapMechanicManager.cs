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

        /// <summary>Mechanics that have been completed this map.</summary>
        public IReadOnlyList<IMapMechanic> CompletedMechanics => _completed;
        private readonly List<IMapMechanic> _completed = new();

        /// <summary>All registered mechanics.</summary>
        public IReadOnlyList<IMapMechanic> AllMechanics => _mechanics;

        public void Register(IMapMechanic mechanic)
        {
            _mechanics.Add(mechanic);
        }

        /// <summary>
        /// Check if all Required mechanics have been completed (or failed/abandoned).
        /// Used by MappingMode to decide if map is "done".
        /// </summary>
        public bool AllRequiredComplete(BotSettings.MechanicsSettings settings)
        {
            // For now we only have Ultimatum. When more are added, check each.
            // A Required mechanic is satisfied if: completed, failed, abandoned, or not found.
            // "Not found" is handled by MappingMode's coverage threshold — if 90%+ explored
            // and mechanic not detected, it's not in this map.
            foreach (var m in _mechanics)
            {
                var mode = GetMechanicMode(m, settings);
                if (mode != MechanicMode.Required) continue;

                // If detected but not in a terminal state, it's not done
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
                if (_completed.Contains(mechanic)) continue;

                var mode = GetMechanicMode(mechanic, ctx.Settings.Mechanics);
                if (mode == MechanicMode.Skip) continue;

                if (mechanic.Detect(ctx) && !_detected.Contains(mechanic))
                {
                    _detected.Add(mechanic);
                    ctx.Log($"[Mechanics] Detected: {mechanic.Name} at {mechanic.AnchorGridPos}");
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
        /// On terminal results, moves mechanic to completed list and clears active.
        /// </summary>
        public MechanicResult TickActive(BotContext ctx)
        {
            if (_active == null) return MechanicResult.Idle;

            var result = _active.Tick(ctx);

            if (result == MechanicResult.Complete ||
                result == MechanicResult.Abandoned ||
                result == MechanicResult.Failed)
            {
                ctx.Log($"[Mechanics] {_active.Name} finished: {result}");
                if (!_completed.Contains(_active))
                    _completed.Add(_active);
                _detected.Remove(_active);
                _active = null;
            }

            return result;
        }

        /// <summary>
        /// Reset all mechanics. Called on area change.
        /// </summary>
        public void Reset()
        {
            _active = null;
            _detected.Clear();
            _completed.Clear();
            _lastDetectTime = DateTime.MinValue;
            foreach (var m in _mechanics)
                m.Reset();
        }

        private MechanicMode GetMechanicMode(IMapMechanic mechanic, BotSettings.MechanicsSettings settings)
        {
            // Map mechanic name to its settings mode
            return mechanic.Name switch
            {
                "Ultimatum" => ParseMode(settings.Ultimatum.Mode.Value),
                _ => MechanicMode.Skip,
            };
        }

        private static MechanicMode ParseMode(string value)
        {
            return Enum.TryParse<MechanicMode>(value, out var mode) ? mode : MechanicMode.Skip;
        }
    }
}
