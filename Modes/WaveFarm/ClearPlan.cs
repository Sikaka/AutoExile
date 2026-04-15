using System.Numerics;
using AutoExile.Systems;

namespace AutoExile.Modes.WaveFarm
{
    /// <summary>
    /// Region-based map clearing with observer set-cover.
    ///
    /// Every ExplorationMap Region (the 80×80 colored blocks on the overlay) is a
    /// unit to observe. We don't walk into each one — the network bubble (~180g)
    /// covers whatever is within its observation radius from an adjacent point.
    /// At each step we pick the observer point that brings the most still-pending
    /// regions inside the bubble, navigate there, dwell briefly, then recompute.
    ///
    /// **Observation ≠ killing.** Once a region has been inside the bubble for
    /// <see cref="DwellSeconds"/> it's retired from the queue regardless of what's
    /// in it. Combat is WaveTick's job: pack-engagement takes over whenever InCombat
    /// triggers as we walk between observers. Stragglers that never came into
    /// combat range are handled by WaveTick's post-observation hunt phase once
    /// every region has been observed.
    ///
    /// Loot that drops AFTER a region is retired still gets picked up by the
    /// normal P1/P3 loot priorities — "retired" only means "not in the nav queue".
    /// </summary>
    public class ClearPlan
    {
        private const float DwellSeconds = 0.5f;      // brief "was in bubble" confirmation
        private const float ArriveRadius = 20f;       // "reached" observer point
        private const double PlanStallSeconds = 45.0; // global safety bail
        private const int MinRegionCellCount = 50;    // ignore tiny pockets
        private const float ObservationFloor = 60f;   // clamp: never drop observer radius below this
        private const float AlreadySeenRatio = 0.90f; // pre-seen regions start visited

        private class RegionEntry
        {
            public int Index;
            public Vector2 Center;           // snapped-to-walkable centroid
            public float BoundingRadius;    // farthest cell from raw centroid
            public float ObservationRadius; // max distance for bubble to fully cover the region
            public DateTime DwellStart;     // MinValue = not dwelling
            public bool Visited;
        }

        private readonly Dictionary<int, RegionEntry> _regions = new();
        private readonly HashSet<int> _blockedObservers = new();
        private Vector2? _currentObserver;
        private int _currentObserverRegionIndex = -1;
        private DateTime _planLastProgress = DateTime.MinValue;
        private uint _zoneHash;
        private bool _initialized;

        public bool IsInitialized => _initialized;
        public bool IsComplete => _initialized && PendingCount == 0;
        public Vector2? CurrentTarget => _currentObserver;
        public int RemainingCount => PendingCount;
        public int VisitedCount => CountVisited();
        public int TotalCount => _regions.Count;
        public string Status { get; private set; } = "";

        private int PendingCount
        {
            get
            {
                int n = 0;
                foreach (var r in _regions.Values) if (!r.Visited) n++;
                return n;
            }
        }

        private int CountVisited()
        {
            int n = 0;
            foreach (var r in _regions.Values) if (r.Visited) n++;
            return n;
        }

        public void Initialize(BotContext ctx)
        {
            var gc = ctx.Game;
            var currentHash = gc?.IngameState?.Data?.CurrentAreaHash ?? 0;
            if (_initialized && currentHash == _zoneHash && PendingCount > 0) return;

            Reset();
            _zoneHash = currentHash;

            if (!ctx.Exploration.IsInitialized) return;
            var blob = ctx.Exploration.ActiveBlob;
            if (blob == null || blob.Regions.Count == 0) return;

            foreach (var region in blob.Regions)
            {
                if (region.CellCount < MinRegionCellCount) continue;
                if (ctx.Exploration.FailedRegions.Contains(region.Index)) continue;

                // Region bounding radius: farthest cell from centroid.
                float maxDistSq = 0f;
                foreach (var cell in region.Cells)
                {
                    float dx = cell.X - region.Center.X;
                    float dy = cell.Y - region.Center.Y;
                    float d = dx * dx + dy * dy;
                    if (d > maxDistSq) maxDistSq = d;
                }
                float boundRadius = MathF.Sqrt(maxDistSq);

                // Snap the observation point to actual walkable terrain — the raw
                // centroid can land on a wall for oddly-shaped regions.
                var snappedCenter = ctx.Exploration.SnapToActiveBlob(region.Center, 120f) ?? region.Center;

                var obsRadius = MathF.Max(ObservationFloor, Pathfinding.NetworkBubbleRadius - boundRadius);

                _regions[region.Index] = new RegionEntry
                {
                    Index = region.Index,
                    Center = snappedCenter,
                    BoundingRadius = boundRadius,
                    ObservationRadius = obsRadius,
                    DwellStart = DateTime.MinValue,
                    Visited = region.ExploredRatio > AlreadySeenRatio,
                };
            }

            _initialized = true;
            _planLastProgress = DateTime.Now;
            ctx.Log($"[ClearPlan] Initialized: {PendingCount} pending of {_regions.Count} regions (bubble={Pathfinding.NetworkBubbleRadius:F0}g)");
        }

        public void Update(BotContext ctx, Vector2 playerPos)
        {
            if (!_initialized) return;

            if ((DateTime.Now - _planLastProgress).TotalSeconds > PlanStallSeconds)
            {
                int abandoned = PendingCount;
                foreach (var r in _regions.Values) if (!r.Visited) r.Visited = true;
                _currentObserver = null;
                _currentObserverRegionIndex = -1;
                Status = "plan stall — giving up";
                ctx.Log($"[ClearPlan] Global stall after {PlanStallSeconds}s — abandoning {abandoned} regions");
                return;
            }

            // 1. Dwell check. Any pending region inside the bubble for DwellSeconds
            //    gets marked observed — full stop. No kill-check, no loot-check.
            //    Combat is handled by WaveTick's pack engagement as we pass through;
            //    stragglers get chased by the post-observation hunt phase.
            foreach (var r in _regions.Values)
            {
                if (r.Visited) continue;
                float dist = Vector2.Distance(playerPos, r.Center);
                if (dist > r.ObservationRadius)
                {
                    r.DwellStart = DateTime.MinValue;
                    continue;
                }

                if (r.DwellStart == DateTime.MinValue)
                    r.DwellStart = DateTime.Now;

                if ((DateTime.Now - r.DwellStart).TotalSeconds < DwellSeconds)
                    continue;

                r.Visited = true;
                _planLastProgress = DateTime.Now;
            }

            // 2. Reselect observer if the current one no longer covers any pending region.
            if (_currentObserver.HasValue && !CurrentObserverStillUseful(playerPos, _currentObserver.Value))
            {
                _currentObserver = null;
                _currentObserverRegionIndex = -1;
            }

            if (!_currentObserver.HasValue && PendingCount > 0)
                PickNextObserver(playerPos);

            if (_currentObserver.HasValue)
            {
                float d = Vector2.Distance(playerPos, _currentObserver.Value);
                int covered = CountCoverageAt(_currentObserver.Value);
                Status = d <= ArriveRadius
                    ? $"observing {covered} regions ({CountVisited()}/{_regions.Count})"
                    : $"en route → ({_currentObserver.Value.X:F0},{_currentObserver.Value.Y:F0}) [{d:F0}g, covers {covered}]";
            }
            else
            {
                Status = IsComplete ? "complete" : "no reachable observer";
            }
        }

        /// <summary>Called when NavigateTo refuses the current observer point.
        /// Marks that region's center as a non-viable standing point; other observers
        /// may still cover the region when we stand elsewhere.</summary>
        public void SkipCurrent(BotContext ctx)
        {
            if (_currentObserverRegionIndex >= 0)
                _blockedObservers.Add(_currentObserverRegionIndex);
            _currentObserver = null;
            _currentObserverRegionIndex = -1;
            _planLastProgress = DateTime.Now;
            Status = "unreachable observer — reselecting";
        }

        public void Reset()
        {
            _regions.Clear();
            _blockedObservers.Clear();
            _currentObserver = null;
            _currentObserverRegionIndex = -1;
            _initialized = false;
            _zoneHash = 0;
            _planLastProgress = DateTime.MinValue;
            Status = "";
        }

        private bool CurrentObserverStillUseful(Vector2 playerPos, Vector2 observer)
        {
            // While still en route, don't abandon the commitment — we might not cover
            // anything until we've closed most of the distance.
            if (Vector2.Distance(playerPos, observer) > ArriveRadius) return true;
            return CountCoverageAt(observer) > 0;
        }

        private int CountCoverageAt(Vector2 point)
        {
            int n = 0;
            foreach (var r in _regions.Values)
            {
                if (r.Visited) continue;
                if (Vector2.Distance(point, r.Center) <= r.ObservationRadius) n++;
            }
            return n;
        }

        /// <summary>Greedy set-cover: pick the region-center observer that covers the
        /// most still-pending regions; break ties by nearest to the player.</summary>
        private void PickNextObserver(Vector2 playerPos)
        {
            int bestCover = 0;
            float bestDist = float.MaxValue;
            Vector2? best = null;
            int bestIdx = -1;

            foreach (var candidate in _regions.Values)
            {
                if (candidate.Visited) continue;
                if (_blockedObservers.Contains(candidate.Index)) continue;

                int cover = CountCoverageAt(candidate.Center);
                if (cover == 0) continue;

                float d = Vector2.Distance(playerPos, candidate.Center);
                bool better = cover > bestCover ||
                              (cover == bestCover && d < bestDist);
                if (better)
                {
                    bestCover = cover;
                    bestDist = d;
                    best = candidate.Center;
                    bestIdx = candidate.Index;
                }
            }

            _currentObserver = best;
            _currentObserverRegionIndex = bestIdx;
        }
    }
}
