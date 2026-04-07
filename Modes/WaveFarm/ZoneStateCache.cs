using AutoExile.Mechanics;
using AutoExile.Systems;

namespace AutoExile.Modes.WaveFarm
{
    /// <summary>
    /// Caches per-zone exploration, threat map, and wave tick state across sub-zone transitions.
    /// When the bot enters a wish/mirage zone and returns, it restores the parent map's
    /// progress instead of re-exploring/re-tracking from scratch.
    ///
    /// Keyed by zone hash. Stores the last N zones (LRU eviction).
    /// </summary>
    internal class ZoneStateCache
    {
        private const int MaxEntries = 3;

        private readonly Dictionary<long, ZoneSnapshot> _cache = new();
        private readonly LinkedList<long> _accessOrder = new(); // MRU at front

        /// <summary>
        /// Save current zone state before transitioning away.
        /// </summary>
        public void Save(long hash, ExplorationMap exploration, ThreatMap threatMap,
            WaveTick wave, MapMechanicManager? mechanics = null)
        {
            var snapshot = new ZoneSnapshot
            {
                Hash = hash,
                SavedAt = DateTime.Now,
                Exploration = exploration.CreateSnapshot(),
                ThreatMap = threatMap.CreateSnapshot(),
                Mechanics = mechanics?.CreateSnapshot(),
                LootAttempts = wave.LootMetrics.PickupAttempts,
                LootSuccesses = wave.LootMetrics.PickupSuccesses,
                LootFailures = wave.LootMetrics.PickupsFailed,
            };

            _cache[hash] = snapshot;
            TouchAccessOrder(hash);
            Evict();
        }

        /// <summary>
        /// Try to restore a previously saved zone state. Returns true if found.
        /// Restores exploration progress, threat map, mechanics, and wave tick metrics.
        /// </summary>
        public bool TryRestore(long hash, ExplorationMap exploration, ThreatMap threatMap,
            WaveTick wave, MapMechanicManager? mechanics = null)
        {
            if (!_cache.TryGetValue(hash, out var snapshot))
                return false;

            exploration.RestoreSnapshot(snapshot.Exploration);
            threatMap.RestoreSnapshot(snapshot.ThreatMap);

            if (mechanics != null && snapshot.Mechanics != null)
                mechanics.RestoreSnapshot(snapshot.Mechanics);

            // Restore cumulative loot metrics so the overlay stays accurate
            wave.LootMetrics.RestoreCounters(
                snapshot.LootAttempts,
                snapshot.LootSuccesses,
                snapshot.LootFailures);

            TouchAccessOrder(hash);
            return true;
        }

        /// <summary>Check if we have cached state for a zone hash.</summary>
        public bool Has(long hash) => _cache.ContainsKey(hash);

        /// <summary>Clear all cached state (e.g., on full map exit).</summary>
        public void Clear()
        {
            _cache.Clear();
            _accessOrder.Clear();
        }

        private void TouchAccessOrder(long hash)
        {
            _accessOrder.Remove(hash);
            _accessOrder.AddFirst(hash);
        }

        private void Evict()
        {
            while (_cache.Count > MaxEntries)
            {
                var oldest = _accessOrder.Last!.Value;
                _accessOrder.RemoveLast();
                _cache.Remove(oldest);
            }
        }
    }

    internal class ZoneSnapshot
    {
        public long Hash;
        public DateTime SavedAt;
        public ExplorationSnapshot Exploration = null!;
        public ThreatMapSnapshot ThreatMap = null!;
        public MechanicsSnapshot? Mechanics;

        // Cumulative loot metrics (not per-zone, but preserves the running total)
        public int LootAttempts;
        public int LootSuccesses;
        public int LootFailures;
    }
}
