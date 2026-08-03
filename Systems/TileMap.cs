using ExileCore;
using ExileCore.Shared.Helpers;
using ExileCore.Shared.Interfaces;
using GameOffsets;
using GameOffsets.Native;
using System.Collections.Concurrent;
using System.Numerics;

namespace AutoExile.Systems
{
    /// <summary>
    /// Reads tile metadata from terrain data to locate named landmarks (boss rooms,
    /// league mechanics, exits, etc.) even beyond render range.
    /// Tile positions are stored in grid coordinates (same space as GridPosNum).
    /// </summary>
    public class TileMap
    {
        private ConcurrentDictionary<string, List<Vector2>> _tiles = new();
        private bool _loaded;
        private string _loadedArea = "";
        public int TileCount => _tiles.Count;
        public bool IsLoaded => _loaded;
        public string LoadedArea => _loadedArea;

        /// <summary>
        /// Read tile data from terrain. Call once per zone (on area change).
        /// </summary>
        public bool Load(GameController gc)
        {
            try
            {
                // Access terrain via reflection to avoid hard dependency on GameOffsets field names
                var dataObj = gc.IngameState.Data;
                var memory = gc.Memory;
                if (dataObj == null)
                    return false;

                object? terrainObj = null;
                var dataType = dataObj.GetType();
                var terrainProp = dataType.GetProperty("Terrain");
                if (terrainProp != null)
                    terrainObj = terrainProp.GetValue(dataObj);
                else
                {
                    var terrainField = dataType.GetField("Terrain");
                    if (terrainField != null)
                        terrainObj = terrainField.GetValue(dataObj);
                }

                if (terrainObj == null)
                    return false;

                // Read dimensions via reflection
                var numColsProp = terrainObj.GetType().GetProperty("NumCols");
                var numRowsProp = terrainObj.GetType().GetProperty("NumRows");
                if (numColsProp == null || numRowsProp == null)
                    return false;

                var numColsVal = numColsProp.GetValue(terrainObj);
                var numRowsVal = numRowsProp.GetValue(terrainObj);
                var numCols = Convert.ToInt32(numColsVal);
                var numRows = Convert.ToInt32(numRowsVal);

                if (numCols == 0 || numRows == 0)
                    return false;

                var tiles = new ConcurrentDictionary<string, List<Vector2>>();

                // Try several common field/property names for the target array (offsets can rename these)
                object? tgtArrayVal = null;
                var tryNames = new string[] { "TgtArray", "tgtArray", "TgtFileArray", "TgtFiles", "TgtArrayPtr" };
                foreach (var name in tryNames)
                {
                    var p = terrainObj.GetType().GetProperty(name);
                    if (p != null)
                    {
                        tgtArrayVal = p.GetValue(terrainObj);
                        break;
                    }
                    var f = terrainObj.GetType().GetField(name);
                    if (f != null)
                    {
                        tgtArrayVal = f.GetValue(terrainObj);
                        break;
                    }
                }

                if (tgtArrayVal == null)
                    return false;

                TileStructure[] tileData;
                try
                {
                    // Use dynamic to defer binding — memory.ReadStdVector expects an IntPtr/ptr-like value
                    tileData = memory.ReadStdVector<TileStructure>((dynamic)tgtArrayVal);
                }
                catch
                {
                    return false;
                }

                if (tileData == null || tileData.Length == 0)
                    return false;

                Parallel.ForEach(
                    System.Collections.Concurrent.Partitioner.Create(0, tileData.Length),
                    (range, _) =>
                    {
                        for (int i = range.Item1; i < range.Item2; i++)
                        {
                            try
                            {
                                var tgtTileStruct = memory.Read<TgtTileStruct>(tileData[i].TgtFilePtr);
                                string detailName = memory.Read<TgtDetailStruct>(tgtTileStruct.TgtDetailPtr).name.ToString(memory);
                                string tilePath = tgtTileStruct.TgtPath.ToString(memory);

                                // Grid position: each tile is 23x23 grid cells
                                var gridPos = new Vector2(
                                    i % numCols * 23,
                                    i / numCols * 23
                                );

                                if (!string.IsNullOrEmpty(tilePath))
                                    tiles.GetOrAdd(tilePath, _ => new List<Vector2>()).Add(gridPos);

                                if (!string.IsNullOrEmpty(detailName))
                                    tiles.GetOrAdd(detailName, _ => new List<Vector2>()).Add(gridPos);
                            }
                            catch
                            {
                                // Skip tiles with bad pointers
                            }
                        }
                    });

                _tiles = tiles;
                _loaded = true;
                _loadedArea = gc.Area?.CurrentArea?.Name ?? "unknown";
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Clear tile data (call on area change before reloading).
        /// </summary>
        public void Clear()
        {
            _tiles.Clear();
            _loaded = false;
            _loadedArea = "";
        }

        /// <summary>
        /// Find tile position by name. Tries exact match first, then substring.
        /// Returns position in GRID coordinates (multiply by GridToWorld for world coords).
        /// </summary>
        public Vector2? FindTilePosition(string searchString, Vector2 playerGridPos)
        {
            if (string.IsNullOrEmpty(searchString) || !_loaded)
                return null;

            // Exact match first
            if (_tiles.TryGetValue(searchString, out var exactResults) && exactResults.Count > 0)
            {
                return exactResults
                    .OrderBy(p => Vector2.Distance(playerGridPos, p))
                    .First();
            }

            // Substring search (case-insensitive)
            var searchLower = searchString.ToLowerInvariant();
            Vector2? bestMatch = null;
            float bestDist = float.MaxValue;

            foreach (var kvp in _tiles)
            {
                if (!kvp.Key.ToLowerInvariant().Contains(searchLower))
                    continue;

                foreach (var pos in kvp.Value)
                {
                    var dist = Vector2.Distance(playerGridPos, pos);
                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        bestMatch = pos;
                    }
                }
            }

            return bestMatch;
        }

        /// <summary>
        /// Find all tile entries matching a search string. Returns key → positions.
        /// Useful for debug listing.
        /// </summary>
        public List<(string Key, List<Vector2> Positions)> SearchTiles(string searchString)
        {
            if (string.IsNullOrEmpty(searchString) || !_loaded)
                return new();

            var searchLower = searchString.ToLowerInvariant();
            return _tiles
                .Where(kvp => kvp.Key.ToLowerInvariant().Contains(searchLower))
                .Select(kvp => (kvp.Key, kvp.Value))
                .OrderBy(x => x.Key)
                .ToList();
        }

        /// <summary>
        /// Get positions for an exact key (no substring search).
        /// </summary>
        public List<Vector2>? GetPositions(string key)
        {
            return _tiles.TryGetValue(key, out var positions) ? positions : null;
        }

        /// <summary>
        /// Get all tile keys (for debug browsing).
        /// </summary>
        public IReadOnlyCollection<string> GetAllKeys()
        {
            return _tiles.Keys.ToList().AsReadOnly();
        }

        /// <summary>
        /// Convert grid position to world position for use with our pathfinder.
        /// </summary>
        public static Vector2 GridToWorld(Vector2 gridPos)
        {
            return gridPos * Pathfinding.GridToWorld;
        }
    }
}
