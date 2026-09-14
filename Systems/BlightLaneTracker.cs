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
    /// </summary>
    public class BlightLaneTracker
    {
        public List<List<Vector2>> Lanes { get; private set; } = new();
        public int TotalPathways { get; private set; }

        public Vector2? HubPosition { get; private set; }

        public float[] LaneThreat { get; private set; } = Array.Empty<float>();
        public float[] LaneCoverage { get; private set; } = Array.Empty<float>();
        public float[] LaneDanger { get; private set; } = Array.Empty<float>();
        public int MostDangerousLane { get; private set; } = -1;

        private List<Vector2> _allWaypoints = new();
        private int[] _waypointLaneIndex = Array.Empty<int>();
        private readonly HashSet<long> _knownPathwayIds = new();

        private const float LANE_SPLIT_DISTANCE = 35f;
        private const float LANE_ASSIGN_RADIUS = 40f;
        private const float DEFAULT_TOWER_RADIUS = 40f;

        public static readonly Dictionary<string, int> TowerNameToIndex = new(StringComparer.OrdinalIgnoreCase)
        {
            { "Chilling", 0 }, { "ShockNova", 1 }, { "Empowering", 2 },
            { "Seismic", 3 }, { "Minion", 4 }, { "Fireball", 5 }
        };

        public static readonly Dictionary<string, string> BlightTowerIdToType = new(StringComparer.OrdinalIgnoreCase)
        {
            { "FlameTower1", "Fireball" }, { "FlameTower2", "Fireball" }, { "FlameTower3", "Fireball" },
            { "MeteorTower", "Fireball" }, { "FlamethrowerTower", "Fireball" },
            { "ChillingTower1", "Chilling" }, { "ChillingTower2", "Chilling" }, { "ChillingTower3", "Chilling" },
            { "FreezingTower", "Chilling" }, { "IcePrisonTower", "Chilling" },
            { "ShockingTower1", "ShockNova" }, { "ShockingTower2", "ShockNova" }, { "ShockingTower3", "ShockNova" },
            { "LightningStormTower", "ShockNova" }, { "ArcingTower", "ShockNova" },
            { "StunningTower1", "Seismic" }, { "StunningTower2", "Seismic" }, { "StunningTower3", "Seismic" },
            { "TemporalTower", "Seismic" }, { "PetrificationTower", "Seismic" },
            { "MinionTower1", "Minion" }, { "MinionTower2", "Minion" }, { "MinionTower3", "Minion" },
            { "FlyingMinionTower", "Minion" }, { "TankyMinionTower", "Minion" },
            { "BuffTower1", "Empowering" }, { "BuffTower2", "Empowering" }, { "BuffTower3", "Empowering" },
            { "BuffPlayersTower", "Empowering" }, { "WeakenEnemiesTower", "Empowering" },
        };

        public static readonly HashSet<string> Tier4BranchedIds = new(StringComparer.OrdinalIgnoreCase)
        {
            "MeteorTower", "FlamethrowerTower",
            "FreezingTower", "IcePrisonTower",
            "LightningStormTower", "ArcingTower",
            "TemporalTower", "PetrificationTower",
            "FlyingMinionTower", "TankyMinionTower",
            "BuffPlayersTower", "WeakenEnemiesTower",
        };

        public Vector2? PumpPosition { get; set; }
        public bool HasLaneData => Lanes.Count > 0;

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
                HubPosition = null;
                return;
            }

            TotalPathways = pathways.Count;
            var pump = PumpPosition ?? pathways[0].Pos;

            // Build spatial adjacency graph between all known pathway cells
            int n = pathways.Count;
            var adj = new List<int>[n];
            for (int i = 0; i < n; i++) adj[i] = new List<int>();

            for (int i = 0; i < n; i++)
            {
                for (int j = i + 1; j < n; j++)
                {
                    if (Vector2.Distance(pathways[i].Pos, pathways[j].Pos) <= LANE_SPLIT_DISTANCE)
                    {
                        adj[i].Add(j);
                        adj[j].Add(i);
                    }
                }
            }

            // Find root pathway closest to the pump
            int rootIdx = 0;
            float bestRootDist = float.MaxValue;
            for (int i = 0; i < n; i++)
            {
                float d = Vector2.Distance(pathways[i].Pos, pump);
                if (d < bestRootDist)
                {
                    bestRootDist = d;
                    rootIdx = i;
                }
            }

            // BFS from root to construct outward spanning branches
            var parent = new int[n];
            Array.Fill(parent, -1);
            var dist = new float[n];
            Array.Fill(dist, float.MaxValue);
            var queue = new Queue<int>();
            var hasChildren = new bool[n];
            var visited = new bool[n];

            dist[rootIdx] = 0f;
            visited[rootIdx] = true;
            queue.Enqueue(rootIdx);

            while (queue.Count > 0)
            {
                int u = queue.Dequeue();
                foreach (int v in adj[u])
                {
                    if (!visited[v])
                    {
                        visited[v] = true;
                        parent[v] = u;
                        dist[v] = dist[u] + Vector2.Distance(pathways[u].Pos, pathways[v].Pos);
                        hasChildren[u] = true;
                        queue.Enqueue(v);
                    }
                }
            }

            // Also cover disconnected pathway clusters if any were streamed separately
            for (int i = 0; i < n; i++)
            {
                if (!visited[i])
                {
                    visited[i] = true;
                    dist[i] = Vector2.Distance(pathways[i].Pos, pump);
                    queue.Enqueue(i);
                    while (queue.Count > 0)
                    {
                        int u = queue.Dequeue();
                        foreach (int v in adj[u])
                        {
                            if (!visited[v])
                            {
                                visited[v] = true;
                                parent[v] = u;
                                dist[v] = dist[u] + Vector2.Distance(pathways[u].Pos, pathways[v].Pos);
                                hasChildren[u] = true;
                                queue.Enqueue(v);
                            }
                        }
                    }
                }
            }

            // Identify leaf endpoints (endpoints of every branch)
            var leafIndices = new List<int>();
            for (int i = 0; i < n; i++)
            {
                if (!hasChildren[i])
                    leafIndices.Add(i);
            }

            // Sort leaf endpoints by distance from pump descending
            leafIndices.Sort((a, b) => dist[b].CompareTo(dist[a]));

            Lanes.Clear();
            var usedEndpoints = new List<Vector2>();

            foreach (int leaf in leafIndices)
            {
                var endPos = pathways[leaf].Pos;
                bool tooClose = false;
                foreach (var existing in usedEndpoints)
                {
                    if (Vector2.Distance(existing, endPos) < 20f)
                    {
                        tooClose = true;
                        break;
                    }
                }
                if (tooClose && Lanes.Count > 0) continue;

                var lane = new List<Vector2>();
                int curr = leaf;
                while (curr != -1)
                {
                    lane.Add(pathways[curr].Pos);
                    curr = parent[curr];
                }
                lane.Reverse(); // root/pump -> endpoint

                if (lane.Count > 0)
                {
                    Lanes.Add(lane);
                    usedEndpoints.Add(endPos);
                }
            }

            if (Lanes.Count == 0 && pathways.Count > 0)
            {
                Lanes.Add(pathways.Select(p => p.Pos).ToList());
            }

            // Flatten all waypoints for radius lookups
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

            LaneThreat = new float[Lanes.Count];
            LaneCoverage = new float[Lanes.Count];
            LaneDanger = new float[Lanes.Count];

            ComputeHubPosition(pathways);
        }

        private void ComputeHubPosition(List<(long Id, Vector2 Pos)> pathways)
        {
            if (!PumpPosition.HasValue || pathways.Count == 0)
            {
                HubPosition = null;
                return;
            }

            var pump = PumpPosition.Value;
            float bestDist = float.MaxValue;
            Vector2? bestPos = null;
            foreach (var (_, pos) in pathways)
            {
                var dist = Vector2.Distance(pos, pump);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    bestPos = pos;
                }
            }
            HubPosition = bestPos;
        }

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

        public int FindClosestLane(Vector2 gridPos, out float closestDist)
        {
            bestLaneCheck:
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

        public List<Vector2> GetLaneWaypointsFromPump(int laneIndex, Vector2 pumpPos)
        {
            if (laneIndex < 0 || laneIndex >= Lanes.Count) return new();
            var lane = new List<Vector2>(Lanes[laneIndex]);
            lane.Sort((a, b) => Vector2.Distance(a, pumpPos).CompareTo(Vector2.Distance(b, pumpPos)));
            return lane;
        }

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
                return Vector2.Distance(gridPos, pumpPos);

            var lane = Lanes[bestLane];
            var sorted = new List<(int OrigIdx, Vector2 Pos)>();
            for (int i = 0; i < lane.Count; i++)
                sorted.Add((i, lane[i]));
            sorted.Sort((a, b) => Vector2.Distance(a.Pos, pumpPos).CompareTo(Vector2.Distance(b.Pos, pumpPos)));

            int sortedIdx = 0;
            for (int i = 0; i < sorted.Count; i++)
            {
                if (sorted[i].OrigIdx == bestWpIdx)
                {
                    sortedIdx = i;
                    break;
                }
            }

            float pathDist = bestWpDist;
            for (int i = sortedIdx; i > 0; i--)
                pathDist += Vector2.Distance(sorted[i].Pos, sorted[i - 1].Pos);

            return pathDist;
        }

        public List<Vector2> GetLaneWaypointsWithinRange(int laneIndex, Vector2 pumpPos, float maxRange)
        {
            if (laneIndex < 0 || laneIndex >= Lanes.Count) return new();
            var lane = new List<Vector2>(Lanes[laneIndex]);
            lane.Sort((a, b) => Vector2.Distance(a, pumpPos).CompareTo(Vector2.Distance(b, pumpPos)));
            lane.RemoveAll(wp => Vector2.Distance(wp, pumpPos) > maxRange);
            return lane;
        }

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

        public float GetTowerRadius(IEnumerable<CachedTower> cachedTowers, string towerType)
        {
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