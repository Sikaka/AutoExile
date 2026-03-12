using ExileCore;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using System.Numerics;

namespace AutoExile.Systems
{
    /// <summary>
    /// Reconstructs blight lanes from BlightPathway entities and tracks per-lane
    /// threat, coverage, and danger scores.
    ///
    /// All positions are in GRID coordinates (entity.GridPosNum).
    /// Lane reconstruction: pathways sorted by descending entity ID, split on
    /// ID gaps or distance > 35 grid units. Each lane is a list of grid positions.
    /// </summary>
    public class BlightLaneTracker
    {
        public List<List<Vector2>> Lanes { get; private set; } = new();
        public int TotalPathways { get; private set; }

        // Per-lane intelligence (indices match Lanes list)
        public float[] LaneThreat { get; private set; } = Array.Empty<float>();
        public float[] LaneCoverage { get; private set; } = Array.Empty<float>();
        public float[] LaneDanger { get; private set; } = Array.Empty<float>();
        public int MostDangerousLane { get; private set; } = -1;

        // All waypoints flattened for quick radius queries
        private List<Vector2> _allWaypoints = new();
        private int[] _waypointLaneIndex = Array.Empty<int>();
        private readonly HashSet<long> _knownPathwayIds = new();

        // Distance thresholds (grid units)
        private const float LANE_SPLIT_DISTANCE = 35f;
        private const float LANE_ASSIGN_RADIUS = 40f;
        private const float DEFAULT_TOWER_RADIUS = 40f;

        // Tower type indices in build menu
        public static readonly Dictionary<string, int> TowerNameToIndex = new(StringComparer.OrdinalIgnoreCase)
        {
            { "Chilling", 0 }, { "ShockNova", 1 }, { "Empowering", 2 },
            { "Seismic", 3 }, { "Minion", 4 }, { "Fireball", 5 }
        };

        // BlightTower.Id → tower type mapping (all tiers)
        public static readonly Dictionary<string, string> BlightTowerIdToType = new(StringComparer.OrdinalIgnoreCase)
        {
            // Fire
            { "FlameTower1", "Fireball" }, { "FlameTower2", "Fireball" }, { "FlameTower3", "Fireball" },
            { "MeteorTower", "Fireball" }, { "FlamethrowerTower", "Fireball" },
            // Cold
            { "ChillingTower1", "Chilling" }, { "ChillingTower2", "Chilling" }, { "ChillingTower3", "Chilling" },
            { "FreezingTower", "Chilling" }, { "IcePrisonTower", "Chilling" },
            // Lightning
            { "ShockingTower1", "ShockNova" }, { "ShockingTower2", "ShockNova" }, { "ShockingTower3", "ShockNova" },
            { "LightningStormTower", "ShockNova" }, { "ArcingTower", "ShockNova" },
            // Physical
            { "StunningTower1", "Seismic" }, { "StunningTower2", "Seismic" }, { "StunningTower3", "Seismic" },
            { "TemporalTower", "Seismic" }, { "PetrificationTower", "Seismic" },
            // Minion
            { "MinionTower1", "Minion" }, { "MinionTower2", "Minion" }, { "MinionTower3", "Minion" },
            { "FlyingMinionTower", "Minion" }, { "TankyMinionTower", "Minion" },
            // Buff
            { "BuffTower1", "Empowering" }, { "BuffTower2", "Empowering" }, { "BuffTower3", "Empowering" },
            { "BuffPlayersTower", "Empowering" }, { "WeakenEnemiesTower", "Empowering" },
        };

        // Tier-4 branched tower IDs (result of tier-3 branch selection, no numeric suffix)
        public static readonly HashSet<string> Tier4BranchedIds = new(StringComparer.OrdinalIgnoreCase)
        {
            "MeteorTower", "FlamethrowerTower",
            "FreezingTower", "IcePrisonTower",
            "LightningStormTower", "ArcingTower",
            "TemporalTower", "PetrificationTower",
            "FlyingMinionTower", "TankyMinionTower",
            "BuffPlayersTower", "WeakenEnemiesTower",
        };

        public bool HasLaneData => Lanes.Count > 0;

        /// <summary>
        /// Scan for new BlightPathway entities and reconstruct lanes if new ones found.
        /// </summary>
        public void Tick(GameController gc)
        {
            bool foundNew = false;
            foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
            {
                if (entity.Path == "Metadata/Terrain/Leagues/Blight/Objects/BlightPathway")
                {
                    if (_knownPathwayIds.Add(entity.Id))
                        foundNew = true;
                }
            }

            if (foundNew)
                ReconstructLanes(gc);
        }

        private void ReconstructLanes(GameController gc)
        {
            var pathways = new List<(long Id, Vector2 Pos)>();
            foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
            {
                if (entity.Path == "Metadata/Terrain/Leagues/Blight/Objects/BlightPathway")
                {
                    var pos = entity.GridPosNum;
                    if (pos.X > 0 && pos.Y > 0)
                        pathways.Add((entity.Id, pos));
                }
            }

            if (pathways.Count == 0)
            {
                Lanes.Clear();
                _allWaypoints.Clear();
                TotalPathways = 0;
                return;
            }

            // Sort by descending ID
            pathways.Sort((a, b) => b.Id.CompareTo(a.Id));

            Lanes.Clear();
            var currentLane = new List<Vector2> { pathways[0].Pos };

            for (int i = 1; i < pathways.Count; i++)
            {
                var prev = pathways[i - 1];
                var curr = pathways[i];

                // Split criteria: non-consecutive IDs or too far apart (grid units)
                bool sameIdChain = Math.Abs(prev.Id - curr.Id) <= 1;
                bool closeEnough = Vector2.Distance(prev.Pos, curr.Pos) <= LANE_SPLIT_DISTANCE;

                if (sameIdChain && closeEnough)
                {
                    currentLane.Add(curr.Pos);
                }
                else
                {
                    if (currentLane.Count > 0)
                        Lanes.Add(currentLane);
                    currentLane = new List<Vector2> { curr.Pos };
                }
            }
            if (currentLane.Count > 0)
                Lanes.Add(currentLane);

            // Flatten all waypoints and build lane index
            _allWaypoints = new List<Vector2>();
            var laneIndexList = new List<int>();
            for (int li = 0; li < Lanes.Count; li++)
            {
                foreach (var wp in Lanes[li])
                {
                    _allWaypoints.Add(wp);
                    laneIndexList.Add(li);
                }
            }
            _waypointLaneIndex = laneIndexList.ToArray();
            TotalPathways = pathways.Count;

            LaneThreat = new float[Lanes.Count];
            LaneCoverage = new float[Lanes.Count];
            LaneDanger = new float[Lanes.Count];
        }

        /// <summary>
        /// Update per-lane threat scores from current monster positions (grid coords).
        /// </summary>
        public void UpdateThreat(GameController gc)
        {
            if (Lanes.Count == 0) return;

            for (int i = 0; i < LaneThreat.Length; i++)
                LaneThreat[i] = 0;

            foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
            {
                if (entity.Type != EntityType.Monster) continue;
                if (!entity.IsHostile || !entity.IsTargetable || !entity.IsAlive) continue;

                var mpos = entity.GridPosNum;
                if (mpos.X <= 0 || mpos.Y <= 0) continue;

                int bestLane = FindClosestLane(mpos, out float bestDist);
                if (bestLane >= 0 && bestDist < LANE_ASSIGN_RADIUS * 3)
                {
                    float weight = entity.Rarity switch
                    {
                        MonsterRarity.Magic => 3f,
                        MonsterRarity.Rare => 10f,
                        MonsterRarity.Unique => 25f,
                        _ => 1f,
                    };
                    float proximity = Math.Max(1f, bestDist);
                    LaneThreat[bestLane] += weight * (LANE_ASSIGN_RADIUS / proximity);
                }
            }
        }

        /// <summary>
        /// Update per-lane coverage scores from cached tower data (includes off-screen towers).
        /// All positions and radii are in grid units.
        /// </summary>
        public void UpdateCoverage(IEnumerable<CachedTower> cachedTowers)
        {
            if (Lanes.Count == 0) return;

            for (int i = 0; i < LaneCoverage.Length; i++)
                LaneCoverage[i] = 0;

            foreach (var ct in cachedTowers)
            {
                if (ct.Position.X <= 0 || ct.Position.Y <= 0) continue;

                float radius = ct.Radius > 0 ? ct.Radius : DEFAULT_TOWER_RADIUS;
                float tierWeight = ct.Tier switch { 1 => 1f, 2 => 2.5f, 3 => 5f, 4 => 7.5f, _ => 1f };
                float radiusSq = radius * radius;

                for (int li = 0; li < Lanes.Count; li++)
                {
                    bool coversLane = false;
                    foreach (var wp in Lanes[li])
                    {
                        if (Vector2.DistanceSquared(wp, ct.Position) <= radiusSq)
                        {
                            coversLane = true;
                            break;
                        }
                    }
                    if (coversLane)
                        LaneCoverage[li] += tierWeight;
                }
            }
        }

        /// <summary>
        /// Compute danger = threat / (coverage + 1).
        /// </summary>
        public void UpdateDanger()
        {
            MostDangerousLane = -1;
            float maxDanger = 0;

            for (int i = 0; i < Lanes.Count; i++)
            {
                LaneDanger[i] = LaneThreat[i] / (LaneCoverage[i] + 1f);
                if (LaneDanger[i] > maxDanger)
                {
                    maxDanger = LaneDanger[i];
                    MostDangerousLane = i;
                }
            }
        }

        /// <summary>
        /// Find the closest lane to a grid position.
        /// </summary>
        public int FindClosestLane(Vector2 gridPos, out float closestDist)
        {
            int bestLane = -1;
            closestDist = float.MaxValue;

            for (int li = 0; li < Lanes.Count; li++)
            {
                foreach (var wp in Lanes[li])
                {
                    float dist = Vector2.Distance(wp, gridPos);
                    if (dist < closestDist)
                    {
                        closestDist = dist;
                        bestLane = li;
                    }
                }
            }
            return bestLane;
        }

        /// <summary>
        /// Score a foundation position by how many lane waypoints it covers.
        /// Uses danger-weighted scoring when threat data is available.
        /// All positions and radius in grid units.
        /// </summary>
        public float ScoreFoundation(Vector2 gridPos, float radius)
        {
            if (Lanes.Count == 0) return 0;

            float score = 0;
            float radiusSq = radius * radius;
            var scoredLanes = new bool[Lanes.Count];

            for (int i = 0; i < _allWaypoints.Count; i++)
            {
                if (Vector2.DistanceSquared(_allWaypoints[i], gridPos) <= radiusSq)
                {
                    int laneIdx = _waypointLaneIndex[i];
                    if (!scoredLanes[laneIdx])
                    {
                        scoredLanes[laneIdx] = true;
                        float danger = laneIdx < LaneDanger.Length ? Math.Max(LaneDanger[laneIdx], 0.5f) : 1f;

                        int waypointsOnLane = 0;
                        foreach (var lwp in Lanes[laneIdx])
                        {
                            if (Vector2.DistanceSquared(lwp, gridPos) <= radiusSq)
                                waypointsOnLane++;
                        }
                        score += waypointsOnLane * danger;
                    }
                }
            }
            return score;
        }

        /// <summary>
        /// Count how many distinct lanes pass within radius of a grid position.
        /// </summary>
        public int CountLanesNearPosition(Vector2 gridPos, float radius)
        {
            int laneCount = 0;
            float radiusSq = radius * radius;
            foreach (var lane in Lanes)
            {
                foreach (var wp in lane)
                {
                    if (Vector2.DistanceSquared(wp, gridPos) <= radiusSq)
                    {
                        laneCount++;
                        break;
                    }
                }
            }
            return laneCount;
        }

        /// <summary>
        /// Get lane waypoints ordered from pump outward (closest to pump first).
        /// pumpPos in grid coordinates.
        /// </summary>
        public List<Vector2> GetLaneWaypointsFromPump(int laneIndex, Vector2 pumpPos)
        {
            if (laneIndex < 0 || laneIndex >= Lanes.Count) return new();
            var lane = new List<Vector2>(Lanes[laneIndex]);
            lane.Sort((a, b) => Vector2.Distance(a, pumpPos).CompareTo(Vector2.Distance(b, pumpPos)));
            return lane;
        }

        /// <summary>
        /// Estimate path distance from a grid position to the pump along the nearest lane.
        /// All positions in grid units. Returns grid-unit distance.
        /// </summary>
        public float EstimatePathDistanceToPump(Vector2 gridPos, Vector2 pumpPos)
        {
            if (Lanes.Count == 0) return Vector2.Distance(gridPos, pumpPos);

            int bestLane = -1;
            int bestWpIdx = -1;
            float bestWpDist = float.MaxValue;

            for (int li = 0; li < Lanes.Count; li++)
            {
                for (int wi = 0; wi < Lanes[li].Count; wi++)
                {
                    float d = Vector2.Distance(Lanes[li][wi], gridPos);
                    if (d < bestWpDist)
                    {
                        bestWpDist = d;
                        bestLane = li;
                        bestWpIdx = wi;
                    }
                }
            }

            if (bestLane < 0 || bestWpDist > LANE_ASSIGN_RADIUS * 5)
                return Vector2.Distance(gridPos, pumpPos); // not on any lane, use direct

            // Sort this lane's waypoints by distance to pump so index 0 = pump end
            var lane = Lanes[bestLane];
            var sorted = new List<(int OrigIdx, Vector2 Pos)>();
            for (int i = 0; i < lane.Count; i++)
                sorted.Add((i, lane[i]));
            sorted.Sort((a, b) => Vector2.Distance(a.Pos, pumpPos).CompareTo(Vector2.Distance(b.Pos, pumpPos)));

            // Find where our bestWpIdx falls in the sorted order
            int sortedIdx = 0;
            for (int i = 0; i < sorted.Count; i++)
            {
                if (sorted[i].OrigIdx == bestWpIdx)
                {
                    sortedIdx = i;
                    break;
                }
            }

            // Sum segment distances from monster's waypoint back toward pump
            float pathDist = bestWpDist; // distance from monster to nearest waypoint
            for (int i = sortedIdx; i > 0; i--)
                pathDist += Vector2.Distance(sorted[i].Pos, sorted[i - 1].Pos);

            return pathDist;
        }

        /// <summary>
        /// Get lane waypoints within a max distance from pump, ordered pump-outward.
        /// All in grid units.
        /// </summary>
        public List<Vector2> GetLaneWaypointsWithinRange(int laneIndex, Vector2 pumpPos, float maxRange)
        {
            if (laneIndex < 0 || laneIndex >= Lanes.Count) return new();
            var lane = new List<Vector2>(Lanes[laneIndex]);
            lane.Sort((a, b) => Vector2.Distance(a, pumpPos).CompareTo(Vector2.Distance(b, pumpPos)));
            lane.RemoveAll(wp => Vector2.Distance(wp, pumpPos) > maxRange);
            return lane;
        }

        /// <summary>
        /// Get lane indices sorted by danger (most dangerous first).
        /// </summary>
        public List<int> GetLanesOrderedByDanger()
        {
            var indices = new List<int>();
            for (int i = 0; i < Lanes.Count; i++)
                indices.Add(i);
            indices.Sort((a, b) =>
            {
                float da = a < LaneDanger.Length ? LaneDanger[a] : 0;
                float db = b < LaneDanger.Length ? LaneDanger[b] : 0;
                return db.CompareTo(da);
            });
            return indices;
        }

        // --- Static helpers ---

        public static string? GetBlightTowerId(Entity entity)
        {
            try
            {
                if (entity != null && entity.TryGetComponent<BlightTower>(out var bt) && !string.IsNullOrEmpty(bt.Id))
                    return bt.Id;
            }
            catch { }
            return null;
        }

        public static string? GetTypeFromBlightTowerId(string blightTowerId)
        {
            if (blightTowerId != null && BlightTowerIdToType.TryGetValue(blightTowerId, out var type))
                return type;
            return null;
        }

        public static int GetTierFromBlightTowerId(string blightTowerId)
        {
            if (string.IsNullOrEmpty(blightTowerId)) return 1;
            if (Tier4BranchedIds.Contains(blightTowerId)) return 4;
            if (blightTowerId.Length > 0 && char.IsDigit(blightTowerId[^1]))
                return blightTowerId[^1] - '0';
            return 1;
        }

        /// <summary>
        /// Get the effect radius for a tower type in grid units.
        /// </summary>
        public float GetTowerRadius(IEnumerable<CachedTower> cachedTowers, string towerType)
        {
            // Find first cached tower of this type that has a real radius
            foreach (var ct in cachedTowers)
            {
                if (ct.Radius > 0 && string.Equals(ct.TowerType, towerType, StringComparison.OrdinalIgnoreCase))
                    return ct.Radius;
            }
            return DEFAULT_TOWER_RADIUS;
        }

        public string GetDebugText()
        {
            if (Lanes.Count == 0)
                return $"Lanes: 0, Pathways: {TotalPathways}";

            var parts = new List<string>();
            for (int i = 0; i < Lanes.Count && i < LaneDanger.Length; i++)
            {
                if (LaneThreat[i] > 0 || LaneCoverage[i] > 0)
                    parts.Add($"L{i}:T{LaneThreat[i]:F0}/C{LaneCoverage[i]:F0}/D{LaneDanger[i]:F1}");
            }
            var dangerStr = parts.Count > 0 ? " | " + string.Join(" ", parts) : "";
            var topLane = MostDangerousLane >= 0 ? $" Top:L{MostDangerousLane}" : "";
            return $"Lanes: {Lanes.Count}, WP: {TotalPathways}{topLane}{dangerStr}";
        }
    }
}
