using ExileCore;
using ExileCore.PoEMemory;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Helpers;
using GameOffsets;
using GameOffsets.Native;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;

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
        /// Read tile data from memory using native TgtDetailStruct inspection.
        /// Captures all tile detail names, tile paths, and grid coordinates map-wide at zone load.
        /// </summary>
        public bool Load(GameController gc)
        {
            try
            {
                var tileList = ReadTilesFromTerrain(gc);
                if (tileList == null || tileList.Count == 0)
                {
                    _loaded = false;
                    return false;
                }

                var tiles = new ConcurrentDictionary<string, List<Vector2>>();

                foreach (var (name, path, gridPos) in tileList)
                {
                    if (!string.IsNullOrEmpty(name))
                        tiles.GetOrAdd(name, _ => new List<Vector2>()).Add(gridPos);

                    if (!string.IsNullOrEmpty(path))
                        tiles.GetOrAdd(path, _ => new List<Vector2>()).Add(gridPos);
                }

                if (tiles.Count > 0)
                {
                    _tiles = tiles;
                    _loaded = true;
                    _loadedArea = gc.Area?.CurrentArea?.Name ?? "unknown";
                    return true;
                }

                _loaded = false;
                return false;
            }
            catch
            {
                _loaded = false;
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
        /// Find tile position by name or Radar wildcard pattern (e.g. "*Labyrinth*").
        /// Returns position in GRID coordinates closest to playerGridPos.
        /// </summary>
        public Vector2? FindTilePosition(string searchString, Vector2 playerGridPos)
        {
            if (string.IsNullOrEmpty(searchString) || !_loaded)
                return null;

            // 1. Exact match
            if (_tiles.TryGetValue(searchString, out var exactResults) && exactResults.Count > 0)
            {
                return exactResults
                    .OrderBy(p => Vector2.Distance(playerGridPos, p))
                    .First();
            }

            // 2. Radar-style wildcard pattern matching (* and ?)
            var regex = ToLikeRegex(searchString);
            Vector2? bestMatch = null;
            float bestDist = float.MaxValue;

            foreach (var kvp in _tiles)
            {
                if (!regex.IsMatch(kvp.Key) && !kvp.Key.Contains(searchString, StringComparison.OrdinalIgnoreCase))
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

        private static System.Text.RegularExpressions.Regex ToLikeRegex(string pattern)
        {
            return new System.Text.RegularExpressions.Regex("^" +
                             System.Text.RegularExpressions.Regex.Escape(pattern)
                                 .Replace(@"\*", ".*")
                                 .Replace(@"\?", ".")
                             + "$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);
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

        /// <summary>
        /// Read native terrain tile structures directly from memory using BindingFlags.
        /// </summary>
        private List<(string Name, string Path, Vector2 Coordinate)> ReadTilesFromTerrain(GameController gc)
        {
            var result = new List<(string Name, string Path, Vector2 Coordinate)>();
            try
            {
                var ingameData = gc.IngameState?.Data;
                if (ingameData == null) return result;

                var terrain = ingameData.Terrain;
                var memory = gc.Memory;
                var terrainType = terrain.GetType();

                // Fix: Include NonPublic and Instance flags to find private/internal fields
                var fields = terrainType.GetFields(
                    System.Reflection.BindingFlags.Public | 
                    System.Reflection.BindingFlags.NonPublic | 
                    System.Reflection.BindingFlags.Instance);

                System.Reflection.FieldInfo? tgtField = null;
                foreach (var field in fields)
                {
                    if (field.FieldType.Name.Contains("NativeVector") || 
                        field.FieldType.Name.Contains("Vector") || 
                        field.FieldType.Name.Contains("NativePtrArray"))
                    {
                        tgtField = field;
                        break;
                    }
                }

                if (tgtField == null) return result;

                var vectorPtr = tgtField.GetValue(terrain);
                if (vectorPtr == null) return result;

                // Read TileStructure vector
                var readMethod = memory.GetType().GetMethod("ReadStdVector")?.MakeGenericMethod(typeof(TileStructure));
                if (readMethod == null) return result;

                TileStructure[]? tileData = readMethod.Invoke(memory, new[] { vectorPtr }) as TileStructure[];
                if (tileData == null || tileData.Length == 0) return result;

                // Calculate tile columns from AreaDimensions (1 tile = 23 grid units)
                int numCols = (int)(ingameData.AreaDimensions.X / 23);
                if (numCols <= 0 && ingameData.RawPathfindingData != null && ingameData.RawPathfindingData.Length > 0)
                    numCols = ingameData.RawPathfindingData[0].Length / 23;

                if (numCols <= 0) return result;

                for (int i = 0; i < tileData.Length; i++)
                {
                    try
                    {
                        var tgtTileStruct = memory.Read<TgtTileStruct>(tileData[i].TgtFilePtr);
                        string detailName = memory.Read<TgtDetailStruct>(tgtTileStruct.TgtDetailPtr).name.ToString(memory);
                        string tilePath = tgtTileStruct.TgtPath.ToString(memory);

                        // Calculate grid position
                        var gridPos = new Vector2(
                            (i % numCols) * 23f,
                            (i / numCols) * 23f
                        );

                        result.Add((detailName, tilePath, gridPos));
                    }
                    catch { }
                }
            }
            catch { }
            return result;
        }
    }
}
