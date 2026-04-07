using System.Numerics;
using AutoExile.Mechanics;

namespace AutoExile.Modes.WaveFarm
{
    /// <summary>
    /// Records mechanic positions seen during exploration for deferred engagement.
    /// Mechanics behind the player or in the plan's deferred set are logged, not engaged.
    /// Engagement happens when the plan says "ready" and coverage threshold is met.
    /// </summary>
    public class DeferredMechanicLog
    {
        private readonly List<DeferredEntry> _entries = new();

        public IReadOnlyList<DeferredEntry> Entries => _entries;
        public int PendingCount => _entries.Count(e => !e.Engaged);

        public struct DeferredEntry
        {
            public IMapMechanic Mechanic;
            public Vector2 GridPos;
            public float CoverageWhenSeen;
            public DateTime SeenAt;
            public bool Engaged;
        }

        /// <summary>
        /// Should this mechanic be deferred? Returns true if yes (logged for later).
        /// </summary>
        public bool ShouldDefer(IMapMechanic mechanic, Vector2 mechanicPos,
            Vector2 playerPos, DirectionTracker dir, IFarmPlan plan, float currentCoverage)
        {
            // Plan explicitly defers this mechanic type
            if (plan.DeferredMechanics.Contains(mechanic.Name))
            {
                Log(mechanic, mechanicPos, currentCoverage);
                return true;
            }

            // Behind the player and we haven't cleared much yet — defer
            if (dir.HasDirection && !dir.IsAhead(playerPos, mechanicPos, -0.3f) && currentCoverage < 0.6f)
            {
                Log(mechanic, mechanicPos, currentCoverage);
                return true;
            }

            return false; // engage now
        }

        /// <summary>
        /// Get the nearest unengaged deferred mechanic that's ready to engage.
        /// Returns null if plan says not yet or nothing pending.
        /// </summary>
        public DeferredEntry? GetReadyMechanic(BotContext ctx, Vector2 playerPos, IFarmPlan plan)
        {
            if (!plan.ShouldEngageDeferredNow(ctx)) return null;

            DeferredEntry? best = null;
            float bestDist = float.MaxValue;

            for (int i = 0; i < _entries.Count; i++)
            {
                var entry = _entries[i];
                if (entry.Engaged || entry.Mechanic.IsComplete) continue;

                var dist = Vector2.Distance(playerPos, entry.GridPos);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = entry;
                }
            }

            return best;
        }

        /// <summary>
        /// Get the nearest unengaged deferred mechanic, ignoring plan timing.
        /// Used by GetPostClearAction — we're already post-clear so timing is irrelevant.
        /// </summary>
        public DeferredEntry? GetNearestUnengaged(Vector2 playerPos)
        {
            DeferredEntry? best = null;
            float bestDist = float.MaxValue;

            for (int i = 0; i < _entries.Count; i++)
            {
                var entry = _entries[i];
                if (entry.Engaged || entry.Mechanic.IsComplete) continue;

                var dist = Vector2.Distance(playerPos, entry.GridPos);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = entry;
                }
            }

            return best;
        }

        /// <summary>Mark a deferred mechanic as engaged (started).</summary>
        public void MarkEngaged(IMapMechanic mechanic)
        {
            for (int i = 0; i < _entries.Count; i++)
            {
                if (_entries[i].Mechanic == mechanic)
                {
                    var entry = _entries[i];
                    entry.Engaged = true;
                    _entries[i] = entry;
                    return;
                }
            }
        }

        public void Reset()
        {
            _entries.Clear();
        }

        private void Log(IMapMechanic mechanic, Vector2 gridPos, float coverage)
        {
            // Don't duplicate
            foreach (var entry in _entries)
            {
                if (entry.Mechanic == mechanic) return;
                // Same type at same position (within 20 grid) = same mechanic
                if (entry.Mechanic.Name == mechanic.Name &&
                    Vector2.Distance(entry.GridPos, gridPos) < 20f)
                    return;
            }

            _entries.Add(new DeferredEntry
            {
                Mechanic = mechanic,
                GridPos = gridPos,
                CoverageWhenSeen = coverage,
                SeenAt = DateTime.Now,
            });
        }
    }
}
