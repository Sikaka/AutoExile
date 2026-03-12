using ExileCore;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using System.Numerics;
using System.Linq;

namespace AutoExile.Systems
{
    /// <summary>
    /// Tracks simulacrum encounter state: monolith, portal, stash positions,
    /// wave state from monolith's StateMachine component, death counter.
    /// Entity IDs are cached and re-resolved each tick — never hold Entity references across ticks.
    /// All positions stored in grid coordinates.
    /// </summary>
    public class SimulacrumState
    {
        // Entity tracking — ID + grid position
        public long? MonolithId { get; private set; }
        public long? PortalId { get; private set; }
        public long? StashId { get; private set; }

        public Vector2? MonolithPosition { get; private set; }
        public Vector2? PortalPosition { get; private set; }
        public Vector2? StashPosition { get; private set; }

        // Wave state — read from monolith's StateMachine component
        public bool IsWaveActive { get; private set; }
        public int CurrentWave { get; private set; }
        public DateTime WaveStartedAt { get; private set; } = DateTime.Now;
        public DateTime CanStartWaveAt { get; private set; } = DateTime.MinValue;

        // Run tracking
        public int DeathCount { get; set; }
        public int RunsCompleted { get; private set; }
        public int HighestWaveThisRun { get; private set; }

        // Last valid monolith update — if stale >10s, assume wave inactive
        private DateTime _lastMonolithUpdate = DateTime.MinValue;

        // Position sanity
        private const float PositionSanityThreshold = 50f;

        // Spawn zone tracking — grid heatmap bucketed into cells, then clustered
        // into distinct spawn zones for patrol routing
        private const int HeatmapCellSize = 30; // ~30 grid units per bucket
        private const int MinHotspotSamples = 10;
        private const int MaxSpawnZones = 6;
        private const float ZoneMergeDistance = 50f; // grid units — merge clusters closer than this
        private readonly Dictionary<(int x, int y), int> _heatmap = new();
        private int _totalSamples;
        private bool _zonesDirty;

        /// <summary>
        /// Distinct spawn zones ranked by density. Patrol through these during waves.
        /// Each is a grid position at the center of a high-activity cluster.
        /// </summary>
        public List<Vector2> SpawnZones { get; private set; } = new();

        /// <summary>
        /// True once we have enough data to provide meaningful spawn zones.
        /// </summary>
        public bool HasSpawnData => _totalSamples >= MinHotspotSamples && SpawnZones.Count > 0;

        /// <summary>
        /// Record monster pack center during combat each tick.
        /// Buckets into a heatmap grid, then clusters into spawn zones.
        /// </summary>
        public void RecordCombatPosition(Vector2 packCenterGrid)
        {
            var cellX = (int)MathF.Floor(packCenterGrid.X / HeatmapCellSize);
            var cellY = (int)MathF.Floor(packCenterGrid.Y / HeatmapCellSize);
            var key = (cellX, cellY);

            _heatmap.TryGetValue(key, out var count);
            _heatmap[key] = count + 1;
            _totalSamples++;
            _zonesDirty = true;
        }

        /// <summary>
        /// Get the next patrol target. Cycles through spawn zones, picking the farthest
        /// unvisited one from the player to ensure we sweep all lanes.
        /// Returns null if no spawn data yet.
        /// </summary>
        public Vector2? GetNextPatrolTarget(Vector2 playerGridPos, Vector2? lastTarget)
        {
            if (_zonesDirty && _totalSamples >= MinHotspotSamples)
                RebuildSpawnZones();

            if (SpawnZones.Count == 0)
                return null;

            // If we just arrived at lastTarget (or no target), pick the farthest zone
            // from current position. This creates a natural patrol sweep across the map.
            float bestDist = -1;
            Vector2? best = null;
            foreach (var zone in SpawnZones)
            {
                // Skip the zone we just came from (if close to it)
                if (lastTarget.HasValue && Vector2.Distance(zone, lastTarget.Value) < 30f)
                    continue;

                var dist = Vector2.Distance(zone, playerGridPos);
                if (dist > bestDist)
                {
                    bestDist = dist;
                    best = zone;
                }
            }

            return best ?? SpawnZones[0];
        }

        /// <summary>
        /// Get the nearest spawn zone to idle at between waves.
        /// </summary>
        public Vector2? GetNearestSpawnZone(Vector2 playerGridPos)
        {
            if (_zonesDirty && _totalSamples >= MinHotspotSamples)
                RebuildSpawnZones();

            if (SpawnZones.Count == 0)
                return MonolithPosition;

            float bestDist = float.MaxValue;
            Vector2? best = null;
            foreach (var zone in SpawnZones)
            {
                var dist = Vector2.Distance(zone, playerGridPos);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = zone;
                }
            }
            return best;
        }

        /// <summary>
        /// Cluster heatmap cells into distinct spawn zones.
        /// Uses 3x3 kernel scoring to find peaks, then merges nearby peaks.
        /// </summary>
        private void RebuildSpawnZones()
        {
            _zonesDirty = false;

            // Score each cell with 3x3 kernel
            var scored = new List<((int x, int y) cell, int score)>();
            foreach (var (cell, _) in _heatmap)
            {
                int score = 0;
                for (int dx = -1; dx <= 1; dx++)
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        if (_heatmap.TryGetValue((cell.x + dx, cell.y + dy), out var n))
                            score += n;
                    }
                scored.Add((cell, score));
            }

            // Sort by score descending
            scored.Sort((a, b) => b.score.CompareTo(a.score));

            // Greedily pick top cells, merging nearby ones
            var zones = new List<(Vector2 pos, int score)>();
            foreach (var (cell, score) in scored)
            {
                var pos = new Vector2(
                    cell.x * HeatmapCellSize + HeatmapCellSize / 2f,
                    cell.y * HeatmapCellSize + HeatmapCellSize / 2f);

                // Check if too close to an existing zone
                bool merged = false;
                for (int i = 0; i < zones.Count; i++)
                {
                    if (Vector2.Distance(pos, zones[i].pos) < ZoneMergeDistance)
                    {
                        // Weighted merge toward higher-scoring position
                        var existing = zones[i];
                        float totalScore = existing.score + score;
                        var mergedPos = (existing.pos * existing.score + pos * score) / totalScore;
                        zones[i] = (mergedPos, existing.score + score);
                        merged = true;
                        break;
                    }
                }

                if (!merged && zones.Count < MaxSpawnZones)
                    zones.Add((pos, score));

                if (zones.Count >= MaxSpawnZones && merged)
                    break; // enough zones, all new cells will merge into existing
            }

            // Sort by score descending (highest activity first)
            zones.Sort((a, b) => b.score.CompareTo(a.score));
            SpawnZones = zones.Select(z => z.pos).ToList();
        }

        /// <summary>
        /// Reset the wave timer to now. Call when bot is paused/resumed to prevent
        /// wall-clock time during pause from triggering wave timeout.
        /// </summary>
        public void ResetWaveTimer()
        {
            WaveStartedAt = DateTime.Now;
        }

        public void Reset()
        {
            MonolithId = null;
            PortalId = null;
            StashId = null;
            MonolithPosition = null;
            PortalPosition = null;
            StashPosition = null;
            IsWaveActive = false;
            CurrentWave = 0;
            WaveStartedAt = DateTime.Now;
            CanStartWaveAt = DateTime.MinValue;
            DeathCount = 0;
            HighestWaveThisRun = 0;
            _lastMonolithUpdate = DateTime.MinValue;
            _heatmap.Clear();
            _totalSamples = 0;
            _zonesDirty = false;
            SpawnZones.Clear();
        }

        /// <summary>
        /// Call on area change to clear entity references but preserve run-level state.
        /// </summary>
        public void OnAreaChanged()
        {
            MonolithId = null;
            PortalId = null;
            StashId = null;
            MonolithPosition = null;
            PortalPosition = null;
            StashPosition = null;
            IsWaveActive = false;
            CurrentWave = 0;
            CanStartWaveAt = DateTime.MinValue;
            _lastMonolithUpdate = DateTime.MinValue;
        }

        public void RecordRunComplete()
        {
            RunsCompleted++;
            HighestWaveThisRun = 0;
        }

        /// <summary>
        /// Push the wave start timer forward. Called when loot is detected between waves
        /// so the full delay restarts after loot is cleared.
        /// </summary>
        public void ResetWaveDelay(float delaySeconds)
        {
            var newTime = DateTime.Now.AddSeconds(delaySeconds);
            if (newTime > CanStartWaveAt)
                CanStartWaveAt = newTime;
        }

        /// <summary>
        /// Tick entity tracking and wave state. Call every tick while in simulacrum map.
        /// </summary>
        public void Tick(GameController gc, float minWaveDelay)
        {
            // --- Track portal ---
            Entity? portal = ResolveById(gc, PortalId, EntityType.TownPortal);
            if (portal == null)
            {
                portal = gc.EntityListWrapper.ValidEntitiesByType[EntityType.TownPortal]
                    .OrderBy(e => e.DistancePlayer)
                    .FirstOrDefault();
                if (portal != null)
                    PortalId = portal.Id;
            }
            if (portal != null)
            {
                var freshPos = portal.GridPosNum;
                if (IsPositionSane(freshPos, PortalPosition))
                    PortalPosition = freshPos;
            }

            // --- Track monolith ---
            Entity? monolith = null;
            if (MonolithId.HasValue)
            {
                monolith = gc.EntityListWrapper.OnlyValidEntities
                    .FirstOrDefault(e => e.Id == MonolithId.Value);
                if (monolith != null && !IsPositionSane(monolith.GridPosNum, MonolithPosition))
                    monolith = null;
            }
            if (monolith == null)
            {
                monolith = gc.EntityListWrapper.OnlyValidEntities
                    .FirstOrDefault(e => e.Metadata?.Contains("Objects/Afflictionator") == true);
                if (monolith != null)
                    MonolithId = monolith.Id;
            }

            if (monolith != null)
            {
                var freshPos = monolith.GridPosNum;
                if (IsPositionSane(freshPos, MonolithPosition))
                    MonolithPosition = freshPos;

                if (monolith.TryGetComponent<StateMachine>(out var state))
                {
                    var isActive = state.States.FirstOrDefault(s => s.Name == "active")?.Value > 0 &&
                                   state.States.FirstOrDefault(s => s.Name == "goodbye")?.Value == 0;
                    var wave = (int)(state.States.FirstOrDefault(s => s.Name == "wave")?.Value ?? 0);

                    // Wave just ended — enforce delay before next start
                    if (IsWaveActive && !isActive)
                        CanStartWaveAt = DateTime.Now.AddSeconds(minWaveDelay);

                    // Wave number changed
                    if (wave != CurrentWave)
                    {
                        WaveStartedAt = DateTime.Now;
                        if (wave > HighestWaveThisRun)
                            HighestWaveThisRun = wave;
                    }

                    IsWaveActive = isActive;
                    CurrentWave = wave;
                    _lastMonolithUpdate = DateTime.Now;
                }
            }
            else if (DateTime.Now > _lastMonolithUpdate.AddSeconds(10))
            {
                // Monolith out of range for too long — assume wave inactive
                IsWaveActive = false;
            }

            // --- Track stash (only search once — position is static) ---
            if (!StashPosition.HasValue)
            {
                Entity? stash = null;
                if (StashId.HasValue)
                {
                    stash = gc.EntityListWrapper.OnlyValidEntities
                        .FirstOrDefault(e => e.Id == StashId.Value);
                }
                if (stash == null)
                {
                    stash = gc.EntityListWrapper.OnlyValidEntities
                        .FirstOrDefault(e => e.Metadata?.Contains("Metadata/MiscellaneousObjects/Stash") == true);
                    if (stash != null)
                        StashId = stash.Id;
                }
                if (stash != null)
                {
                    var freshPos = stash.GridPosNum;
                    if (IsValidPosition(freshPos))
                        StashPosition = freshPos;
                }
            }
        }

        /// <summary>
        /// Resolve an entity by cached ID within a specific entity type.
        /// </summary>
        private Entity? ResolveById(GameController gc, long? id, EntityType type)
        {
            if (!id.HasValue) return null;
            return gc.EntityListWrapper.ValidEntitiesByType[type]
                .FirstOrDefault(e => e.Id == id.Value);
        }

        private static bool IsValidPosition(Vector2 pos)
        {
            if (pos == Vector2.Zero) return false;
            if (Math.Abs(pos.X) > 10000 || Math.Abs(pos.Y) > 10000) return false;
            return true;
        }

        private static bool IsPositionSane(Vector2 freshPos, Vector2? storedPos)
        {
            if (!IsValidPosition(freshPos)) return false;
            if (storedPos.HasValue && Vector2.Distance(freshPos, storedPos.Value) > PositionSanityThreshold)
                return false;
            return true;
        }

        /// <summary>
        /// Convert grid position to world coordinates for NavigateTo.
        /// </summary>
        public static Vector2 ToWorld(Vector2 gridPos) =>
            gridPos * Pathfinding.GridToWorld;

        /// <summary>
        /// Convert grid position to Vector3 world coordinates for WorldToScreen.
        /// </summary>
        public static Vector3 ToWorld3(Vector2 gridPos, float z) =>
            new(gridPos.X * Pathfinding.GridToWorld, gridPos.Y * Pathfinding.GridToWorld, z);
    }
}
