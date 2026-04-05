using System.Numerics;
using AutoExile.Systems;

namespace AutoExile.Modes.WaveFarm
{
    /// <summary>
    /// Wraps LootSystem with directional awareness for wave farming.
    /// Forward loot is always grabbed. Backtrack loot only if above value threshold.
    /// Tracks pickup metrics for efficiency analysis.
    /// </summary>
    public class LootFilter
    {
        // ── Metrics ──
        public int PickupAttempts { get; private set; }
        public int PickupSuccesses { get; private set; }
        public int PickupsFailed { get; private set; }
        public float SuccessRate => PickupAttempts > 0 ? (float)PickupSuccesses / PickupAttempts : 0;

        private float _grabRadius = 25f;

        /// <summary>
        /// Find the best forward loot candidate (ahead of player or within grab radius).
        /// Returns null if nothing worth picking up ahead.
        /// </summary>
        public LootCandidate? GetForwardLoot(LootSystem loot, Vector2 playerPos,
            DirectionTracker dir, float forwardAngle)
        {
            foreach (var c in loot.Candidates)
            {
                var itemPos = c.Entity.GridPosNum;
                var dist = Vector2.Distance(playerPos, itemPos);

                // Always grab if right next to us
                if (dist <= _grabRadius)
                    return c;

                // Otherwise must be ahead
                if (dir.IsAhead(playerPos, itemPos, forwardAngle))
                    return c;
            }
            return null;
        }

        /// <summary>
        /// Find high-value loot behind the player that justifies backtracking.
        /// Returns null if nothing behind exceeds the threshold.
        /// </summary>
        public LootCandidate? GetBacktrackLoot(LootSystem loot, Vector2 playerPos,
            DirectionTracker dir, float forwardAngle, double valueThreshold)
        {
            LootCandidate? best = null;
            double bestValue = valueThreshold;

            foreach (var c in loot.Candidates)
            {
                if (c.ChaosValue < valueThreshold) continue;

                var itemPos = c.Entity.GridPosNum;
                // Only consider items that are behind us
                if (!dir.IsAhead(playerPos, itemPos, forwardAngle) && c.ChaosValue > bestValue)
                {
                    bestValue = c.ChaosValue;
                    best = c;
                }
            }
            return best;
        }

        /// <summary>Record a pickup attempt.</summary>
        public void RecordAttempt() => PickupAttempts++;

        /// <summary>Record a successful pickup.</summary>
        public void RecordSuccess() => PickupSuccesses++;

        /// <summary>Record a failed pickup.</summary>
        public void RecordFailure() => PickupsFailed++;

        /// <summary>Restore counters from a snapshot (zone state cache).</summary>
        public void RestoreCounters(int attempts, int successes, int failures)
        {
            PickupAttempts = attempts;
            PickupSuccesses = successes;
            PickupsFailed = failures;
        }

        public void Reset()
        {
            PickupAttempts = 0;
            PickupSuccesses = 0;
            PickupsFailed = 0;
        }
    }
}
