using System.Numerics;

namespace AutoExile.Systems
{
    /// <summary>
    /// Scans TileMap data to detect mechanics and landmarks.
    /// Two modes:
    ///   1. Map-wide (no blob): matches known mechanic prefixes/detail names. Fast, runs at map load.
    ///   2. Blob-relative (with blob cells): finds tile clusters unusual within the active blob.
    ///      Automatically discovers mechanics, transitions, and unknown landmarks.
    /// </summary>
    public static class TileScanner
    {
        /// <summary>Known mechanic tile path prefixes for map-wide detection.</summary>
        private static readonly (string MechanicName, string PathPrefix)[] MechanicPathPrefixes =
        {
            ("Harvest", "Metadata/Terrain/Grove/Harvest/"),
        };

        /// <summary>Known mechanic detail names for classification.</summary>
        private static readonly Dictionary<string, string> MechanicDetailToName = new()
        {
            ["ultimatum_altar"] = "Ultimatum",
            ["abyssfeature"] = "Abyss",
        };

        /// <summary>Detail names to skip (common infrastructure, not landmarks).</summary>
        private static readonly HashSet<string> IgnoredDetailNames = new()
        {
            "forcedblank", "arena", "rock",
        };

        /// <summary>
        /// Mechanics that can be detected via tile data. Used by completion logic
        /// to know if absence from tile scan means "not in this map" vs "not tile-detectable".
        /// </summary>
        public static readonly HashSet<string> TileDetectableMechanics = new()
        {
            "Ultimatum",
            "Harvest",
        };

        private const float TileCenterOffset = 11.5f;
        private const float ClusterMaxDistance = 100f;
        private const int MaxLandmarkTilesInBlob = 50;  // detail names with more tiles than this are too common
        private const int MinClusterSize = 1;            // minimum tiles to form a landmark

        /// <summary>
        /// Map-wide scan for known mechanics. Runs at map load before exploration.
        /// </summary>
        public static TileScanResult ScanMapWide(TileMap tileMap)
        {
            var result = new TileScanResult
            {
                AreaName = tileMap.LoadedArea,
                IsScanned = true,
            };

            if (!tileMap.IsLoaded) return result;

            var allKeys = tileMap.GetAllKeys();
            var mechanicPositions = new Dictionary<string, List<Vector2>>();

            // Scan for mechanic path prefixes
            foreach (var key in allKeys)
            {
                foreach (var (mechName, prefix) in MechanicPathPrefixes)
                {
                    if (!key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                    var positions = tileMap.GetPositions(key);
                    if (positions == null || positions.Count == 0) continue;
                    if (!mechanicPositions.ContainsKey(mechName))
                        mechanicPositions[mechName] = new List<Vector2>();
                    mechanicPositions[mechName].AddRange(positions);
                    break;
                }
            }

            // Scan for mechanic detail names
            foreach (var (detailName, mechName) in MechanicDetailToName)
            {
                var positions = tileMap.GetPositions(detailName);
                if (positions == null || positions.Count == 0) continue;
                if (!mechanicPositions.ContainsKey(mechName))
                    mechanicPositions[mechName] = new List<Vector2>();
                mechanicPositions[mechName].AddRange(positions);
            }

            // Compute centroids
            foreach (var (mechName, positions) in mechanicPositions)
            {
                result.DetectedMechanics[mechName] = new MechanicTileInfo
                {
                    MechanicName = mechName,
                    MatchedPathPrefix = GetMatchedPrefix(mechName),
                    TileCount = positions.Count,
                    CentroidGridPos = ComputeCentroid(positions),
                };
            }

            return result;
        }

        /// <summary>
        /// Blob-relative landmark scan. Finds tile clusters that are unusual within the active blob.
        /// Call after exploration is initialized. Populates result.Landmarks.
        /// </summary>
        public static void ScanBlobLandmarks(TileScanResult result, TileMap tileMap, HashSet<Vector2i> blobCells)
        {
            result.Landmarks.Clear();
            if (!tileMap.IsLoaded || blobCells.Count == 0) return;

            // Collect all tile detail names that have positions within the blob.
            // A tile position is "in the blob" if the tile center (pos + 11) is walkable.
            var inBlobByDetail = new Dictionary<string, List<Vector2>>();

            foreach (var key in tileMap.GetAllKeys())
            {
                // Only process detail names (semantic), skip path keys (.tdt files)
                if (key.Contains('/') || key.EndsWith(".tdt")) continue;
                if (IgnoredDetailNames.Contains(key)) continue;

                var allPositions = tileMap.GetPositions(key);
                if (allPositions == null) continue;

                List<Vector2>? blobPositions = null;
                foreach (var pos in allPositions)
                {
                    // Check tile center against blob walkable cells
                    var cellX = (int)pos.X + 11;
                    var cellY = (int)pos.Y + 11;
                    if (!blobCells.Contains(new Vector2i(cellX, cellY))) continue;

                    blobPositions ??= new List<Vector2>();
                    blobPositions.Add(pos);
                }

                if (blobPositions != null)
                    inBlobByDetail[key] = blobPositions;
            }

            // Find landmarks: detail names that are uncommon in this blob
            foreach (var (detailName, positions) in inBlobByDetail)
            {
                if (positions.Count > MaxLandmarkTilesInBlob) continue;

                // Cluster the positions spatially
                var clusters = ClusterPositions(positions);

                foreach (var cluster in clusters)
                {
                    if (cluster.Count < MinClusterSize) continue;

                    var centroid = ComputeCentroid(cluster);
                    var type = ClassifyLandmark(detailName, clusters.Count);

                    result.Landmarks.Add(new BlobLandmark
                    {
                        DetailName = detailName,
                        CentroidGridPos = centroid,
                        TileCount = cluster.Count,
                        TotalInBlob = positions.Count,
                        Type = type,
                    });
                }
            }

            // Sort: mechanics first, then transitions, then unknown. Within type, by tile count desc.
            result.Landmarks.Sort((a, b) =>
            {
                int typeCmp = a.Type.CompareTo(b.Type);
                return typeCmp != 0 ? typeCmp : b.TileCount.CompareTo(a.TileCount);
            });
        }

        private static LandmarkType ClassifyLandmark(string detailName, int clusterCount)
        {
            // Known mechanics
            if (MechanicDetailToName.ContainsKey(detailName))
                return LandmarkType.Mechanic;

            // Multiple clusters of same detail name suggests transition markers (one at each end)
            if (clusterCount >= 2)
                return LandmarkType.Transition;

            return LandmarkType.Unknown;
        }

        private static Vector2 ComputeCentroid(List<Vector2> positions)
        {
            var sum = Vector2.Zero;
            foreach (var pos in positions)
                sum += pos;
            return sum / positions.Count + new Vector2(TileCenterOffset, TileCenterOffset);
        }

        private static string GetMatchedPrefix(string mechName)
        {
            foreach (var (name, prefix) in MechanicPathPrefixes)
                if (name == mechName) return prefix;
            foreach (var (detail, name) in MechanicDetailToName)
                if (name == mechName) return detail;
            return "";
        }

        /// <summary>
        /// Group positions into spatial clusters using BFS.
        /// Tiles within ClusterMaxDistance of any tile in the cluster belong together.
        /// </summary>
        private static List<List<Vector2>> ClusterPositions(List<Vector2> positions)
        {
            var clusters = new List<List<Vector2>>();
            var used = new HashSet<int>();

            for (int i = 0; i < positions.Count; i++)
            {
                if (used.Contains(i)) continue;

                var cluster = new List<Vector2>();
                var queue = new Queue<int>();
                queue.Enqueue(i);
                used.Add(i);

                while (queue.Count > 0)
                {
                    var idx = queue.Dequeue();
                    cluster.Add(positions[idx]);

                    for (int j = 0; j < positions.Count; j++)
                    {
                        if (used.Contains(j)) continue;
                        if (Vector2.Distance(positions[idx], positions[j]) <= ClusterMaxDistance)
                        {
                            queue.Enqueue(j);
                            used.Add(j);
                        }
                    }
                }

                clusters.Add(cluster);
            }

            return clusters;
        }
    }
}
