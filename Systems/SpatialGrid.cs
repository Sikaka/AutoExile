using System.Numerics;
using System.Runtime.CompilerServices;

namespace AutoExile.Systems
{
    /// <summary>
    /// Fixed-cell spatial grid for fast proximity queries.
    /// Divides the map into cells of CellSize grid units. Entities are bucketed by cell.
    /// Queries (density, neighbors, directional) check only relevant cells — O(1) per query
    /// instead of O(n) or O(n²).
    ///
    /// Rebuilt each frame from EntityCache data. Lightweight — just arrays of lists.
    /// </summary>
    public class SpatialGrid<T>
    {
        public const int CellSize = 20; // grid units per cell

        private List<(T Item, Vector2 Pos, float Weight)>?[]? _cells;
        private int _cols, _rows;
        private int _offsetX, _offsetY; // grid-space origin offset

        // Reusable result buffer to avoid allocations
        private readonly List<(T Item, Vector2 Pos, float Weight)> _queryBuffer = new(64);

        public int TotalItems { get; private set; }

        /// <summary>
        /// Rebuild the grid from a set of positioned items.
        /// Call once per scan cycle (not every tick).
        /// </summary>
        public void Rebuild(IReadOnlyList<(T Item, Vector2 Pos, float Weight)> items,
            Vector2 center, float radius)
        {
            // Compute grid bounds around center ± radius
            int minX = (int)(center.X - radius);
            int minY = (int)(center.Y - radius);
            int maxX = (int)(center.X + radius);
            int maxY = (int)(center.Y + radius);

            _offsetX = minX;
            _offsetY = minY;
            _cols = (maxX - minX) / CellSize + 1;
            _rows = (maxY - minY) / CellSize + 1;

            int cellCount = _cols * _rows;
            if (_cells == null || _cells.Length < cellCount)
                _cells = new List<(T, Vector2, float)>?[cellCount];
            else
            {
                for (int i = 0; i < cellCount; i++)
                    _cells[i]?.Clear();
            }

            TotalItems = 0;
            foreach (var (item, pos, weight) in items)
            {
                int cx = ((int)pos.X - _offsetX) / CellSize;
                int cy = ((int)pos.Y - _offsetY) / CellSize;
                if (cx < 0 || cx >= _cols || cy < 0 || cy >= _rows) continue;

                int idx = cy * _cols + cx;
                _cells[idx] ??= new List<(T, Vector2, float)>(8);
                _cells[idx]!.Add((item, pos, weight));
                TotalItems++;
            }
        }

        /// <summary>
        /// Get the densest cell position (weighted). Checks each occupied cell + its 8 neighbors.
        /// Returns the cell center with the highest total weight in its 3x3 neighborhood.
        /// O(occupied_cells) — typically much less than O(n²).
        /// </summary>
        public Vector2 FindDensestPosition(Vector2 playerPos)
        {
            if (_cells == null || TotalItems == 0) return playerPos;

            float bestWeight = 0;
            Vector2 bestPos = playerPos;

            for (int cy = 0; cy < _rows; cy++)
            {
                for (int cx = 0; cx < _cols; cx++)
                {
                    var cell = GetCell(cx, cy);
                    if (cell == null || cell.Count == 0) continue;

                    // Sum weight in 3x3 neighborhood
                    float neighborWeight = 0;
                    Vector2 weightedSum = Vector2.Zero;
                    float totalW = 0;

                    for (int dy = -1; dy <= 1; dy++)
                    {
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            var neighbor = GetCell(cx + dx, cy + dy);
                            if (neighbor == null) continue;
                            foreach (var (_, pos, w) in neighbor)
                            {
                                neighborWeight += w;
                                weightedSum += pos * w;
                                totalW += w;
                            }
                        }
                    }

                    if (neighborWeight > bestWeight)
                    {
                        bestWeight = neighborWeight;
                        bestPos = totalW > 0 ? weightedSum / totalW : CellCenter(cx, cy);
                    }
                }
            }

            return bestPos;
        }

        /// <summary>
        /// Count total weight within radius of a point. O(cells_in_radius).
        /// </summary>
        public float WeightInRadius(Vector2 center, float radius)
        {
            float radiusSq = radius * radius;
            float total = 0;

            int minCx = ((int)(center.X - radius) - _offsetX) / CellSize;
            int maxCx = ((int)(center.X + radius) - _offsetX) / CellSize;
            int minCy = ((int)(center.Y - radius) - _offsetY) / CellSize;
            int maxCy = ((int)(center.Y + radius) - _offsetY) / CellSize;

            for (int cy = Math.Max(0, minCy); cy <= Math.Min(_rows - 1, maxCy); cy++)
            {
                for (int cx = Math.Max(0, minCx); cx <= Math.Min(_cols - 1, maxCx); cx++)
                {
                    var cell = GetCell(cx, cy);
                    if (cell == null) continue;
                    foreach (var (_, pos, w) in cell)
                    {
                        if (Vector2.DistanceSquared(pos, center) <= radiusSq)
                            total += w;
                    }
                }
            }
            return total;
        }

        /// <summary>
        /// Get all items within radius of a point. Returns internal buffer — do not cache.
        /// </summary>
        public List<(T Item, Vector2 Pos, float Weight)> QueryRadius(Vector2 center, float radius)
        {
            _queryBuffer.Clear();
            float radiusSq = radius * radius;

            int minCx = ((int)(center.X - radius) - _offsetX) / CellSize;
            int maxCx = ((int)(center.X + radius) - _offsetX) / CellSize;
            int minCy = ((int)(center.Y - radius) - _offsetY) / CellSize;
            int maxCy = ((int)(center.Y + radius) - _offsetY) / CellSize;

            for (int cy = Math.Max(0, minCy); cy <= Math.Min(_rows - 1, maxCy); cy++)
            {
                for (int cx = Math.Max(0, minCx); cx <= Math.Min(_cols - 1, maxCx); cx++)
                {
                    var cell = GetCell(cx, cy);
                    if (cell == null) continue;
                    foreach (var entry in cell)
                    {
                        if (Vector2.DistanceSquared(entry.Pos, center) <= radiusSq)
                            _queryBuffer.Add(entry);
                    }
                }
            }
            return _queryBuffer;
        }

        /// <summary>
        /// Count items ahead of a position (dot product with direction > threshold).
        /// </summary>
        public (int Ahead, int Behind) CountDirectional(Vector2 playerPos, Vector2 forward,
            float radius, float threshold = 0.5f)
        {
            int ahead = 0, behind = 0;
            float radiusSq = radius * radius;

            int minCx = ((int)(playerPos.X - radius) - _offsetX) / CellSize;
            int maxCx = ((int)(playerPos.X + radius) - _offsetX) / CellSize;
            int minCy = ((int)(playerPos.Y - radius) - _offsetY) / CellSize;
            int maxCy = ((int)(playerPos.Y + radius) - _offsetY) / CellSize;

            for (int cy = Math.Max(0, minCy); cy <= Math.Min(_rows - 1, maxCy); cy++)
            {
                for (int cx = Math.Max(0, minCx); cx <= Math.Min(_cols - 1, maxCx); cx++)
                {
                    var cell = GetCell(cx, cy);
                    if (cell == null) continue;
                    foreach (var (_, pos, _) in cell)
                    {
                        if (Vector2.DistanceSquared(pos, playerPos) > radiusSq) continue;
                        var toEntity = pos - playerPos;
                        if (toEntity.LengthSquared() < 1f) continue;
                        var dot = Vector2.Dot(Vector2.Normalize(toEntity), forward);
                        if (dot > threshold) ahead++;
                        else if (dot < -threshold) behind++;
                    }
                }
            }
            return (ahead, behind);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private List<(T Item, Vector2 Pos, float Weight)>? GetCell(int cx, int cy)
        {
            if (cx < 0 || cx >= _cols || cy < 0 || cy >= _rows) return null;
            return _cells![cy * _cols + cx];
        }

        private Vector2 CellCenter(int cx, int cy) =>
            new(_offsetX + cx * CellSize + CellSize / 2f, _offsetY + cy * CellSize + CellSize / 2f);
    }
}
