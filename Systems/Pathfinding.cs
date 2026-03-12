using System.Numerics;

namespace AutoExile.Systems
{
    public enum WaypointAction { Walk, Blink }

    public struct NavWaypoint
    {
        public Vector2 Position;
        public WaypointAction Action;

        public NavWaypoint(Vector2 pos, WaypointAction action = WaypointAction.Walk)
        {
            Position = pos;
            Action = action;
        }
    }

    /// <summary>
    /// A* pathfinding on the terrain grid.
    /// Grid values: 0=impassable, 1-5=walkable with cost = 6-value.
    /// Grid layout: data[row][col] = data[y][x].
    /// </summary>
    public static class Pathfinding
    {
        public const float GridToWorld = 10.88f;
        public const float WorldToGrid = 1f / GridToWorld;

        /// <summary>
        /// Conservative network bubble radius in grid units.
        /// Entities enter the client entity list at ~200-215 grid; 180 is a safe "seen" threshold.
        /// </summary>
        public const float NetworkBubbleRadius = 180f;

        // 8-directional movement: cardinal + diagonal
        private static readonly (int dx, int dy, float baseCost)[] Neighbors =
        {
            ( 1,  0, 1f),
            (-1,  0, 1f),
            ( 0,  1, 1f),
            ( 0, -1, 1f),
            ( 1,  1, 1.414f),
            ( 1, -1, 1.414f),
            (-1,  1, 1.414f),
            (-1, -1, 1.414f),
        };

        public static Vector2 GridToWorldPos(int gx, int gy) =>
            new(gx * GridToWorld, gy * GridToWorld);

        public static (int x, int y) WorldToGridPos(Vector2 world) =>
            ((int)(world.X * WorldToGrid), (int)(world.Y * WorldToGrid));

        /// <summary>
        /// Run A* from start to goal on the pathfinding grid.
        /// Returns a list of world positions from start to goal, or empty if no path.
        /// </summary>
        public static List<Vector2> FindPath(int[][] grid, Vector2 worldStart, Vector2 worldEnd, int maxNodes = 50000)
        {
            var result = FindPathInternal(grid, null, worldStart, worldEnd, 0, 0, maxNodes);
            return result.Select(w => w.Position).ToList();
        }

        /// <summary>
        /// Run A* with blink support. When the pathfinder hits a boundary cell (adjacent to pf=0),
        /// it scans across the gap through cells where targeting > 0 to find landing spots.
        /// Only uses blinks when walking the detour costs more than blinkCostPenalty.
        /// </summary>
        /// <param name="pfGrid">Pathfinding grid (0=blocked, 1-5=walkable)</param>
        /// <param name="tgtGrid">Targeting grid (0=solid wall, >0=open air/jumpable)</param>
        /// <param name="worldStart">Start position in world coordinates</param>
        /// <param name="worldEnd">Goal position in world coordinates</param>
        /// <param name="blinkRange">Max blink distance in grid cells</param>
        /// <param name="blinkCostPenalty">Extra cost added to blink edges (higher = prefer walking)</param>
        /// <param name="maxNodes">Max A* nodes to explore</param>
        public static List<NavWaypoint> FindPathWithBlinks(
            int[][] pfGrid, int[][] tgtGrid,
            Vector2 worldStart, Vector2 worldEnd,
            int blinkRange = 40, float blinkCostPenalty = 30f,
            int maxNodes = 80000)
        {
            if (tgtGrid == null || tgtGrid.Length == 0)
            {
                // Fall back to normal pathfinding
                var fallback = FindPath(pfGrid, worldStart, worldEnd, maxNodes);
                return fallback.Select(p => new NavWaypoint(p, WaypointAction.Walk)).ToList();
            }

            return FindPathInternal(pfGrid, tgtGrid, worldStart, worldEnd, blinkRange, blinkCostPenalty, maxNodes);
        }

        private static List<NavWaypoint> FindPathInternal(
            int[][] pfGrid, int[][]? tgtGrid,
            Vector2 worldStart, Vector2 worldEnd,
            int blinkRange, float blinkCostPenalty,
            int maxNodes)
        {
            var (sx, sy) = WorldToGridPos(worldStart);
            var (gx, gy) = WorldToGridPos(worldEnd);

            var rows = pfGrid.Length;
            var cols = pfGrid[0].Length;

            sx = Math.Clamp(sx, 0, cols - 1);
            sy = Math.Clamp(sy, 0, rows - 1);
            gx = Math.Clamp(gx, 0, cols - 1);
            gy = Math.Clamp(gy, 0, rows - 1);

            if (pfGrid[sy][sx] == 0)
                (sx, sy) = FindNearestWalkable(pfGrid, sx, sy, rows, cols);
            if (pfGrid[gy][gx] == 0)
                (gx, gy) = FindNearestWalkable(pfGrid, gx, gy, rows, cols);

            if (pfGrid[sy][sx] == 0 || pfGrid[gy][gx] == 0)
                return new List<NavWaypoint>();

            var open = new PriorityQueue<(int x, int y), float>();
            var gScore = new Dictionary<(int, int), float>();
            var cameFrom = new Dictionary<(int, int), (int x, int y)>();
            // Track which edges were blinks for path reconstruction
            var blinkEdges = tgtGrid != null ? new HashSet<((int, int) from, (int, int) to)>() : null;

            gScore[(sx, sy)] = 0;
            open.Enqueue((sx, sy), Heuristic(sx, sy, gx, gy));

            var explored = 0;

            while (open.Count > 0 && explored < maxNodes)
            {
                var (cx, cy) = open.Dequeue();
                explored++;

                if (cx == gx && cy == gy)
                    return ReconstructNavPath(cameFrom, blinkEdges, sx, sy, gx, gy);

                var currentG = gScore.GetValueOrDefault((cx, cy), float.MaxValue);
                if (currentG == float.MaxValue)
                    continue;

                var hasPf0Neighbor = false;

                foreach (var (dx, dy, baseCost) in Neighbors)
                {
                    var nx = cx + dx;
                    var ny = cy + dy;

                    if (nx < 0 || nx >= cols || ny < 0 || ny >= rows)
                        continue;

                    var cellValue = pfGrid[ny][nx];
                    if (cellValue == 0)
                    {
                        hasPf0Neighbor = true;
                        continue;
                    }

                    var moveCost = baseCost * (6 - cellValue);
                    var tentativeG = currentG + moveCost;
                    var key = (nx, ny);

                    if (!gScore.TryGetValue(key, out var existingG) || tentativeG < existingG)
                    {
                        gScore[key] = tentativeG;
                        cameFrom[key] = (cx, cy);
                        open.Enqueue(key, tentativeG + Heuristic(nx, ny, gx, gy));
                    }
                }

                // Blink expansion: if this cell borders a gap, scan for landing spots
                if (hasPf0Neighbor && tgtGrid != null && blinkRange > 0)
                {
                    foreach (var landing in ScanBlinkLandings(pfGrid, tgtGrid, cx, cy, blinkRange, rows, cols))
                    {
                        var dist = MathF.Sqrt((landing.x - cx) * (landing.x - cx) + (landing.y - cy) * (landing.y - cy));
                        // Penalize wider gaps — perpendicular crossings have fewer gap cells
                        // than diagonal ones for the same wall, making them preferred
                        var gapPenalty = landing.gapWidth * 2f;
                        var blinkCost = dist + blinkCostPenalty + gapPenalty;
                        var tentativeG = currentG + blinkCost;
                        var key = (landing.x, landing.y);

                        if (!gScore.TryGetValue(key, out var existingG) || tentativeG < existingG)
                        {
                            gScore[key] = tentativeG;
                            cameFrom[key] = (cx, cy);
                            blinkEdges!.Add(((cx, cy), key));
                            open.Enqueue(key, tentativeG + Heuristic(landing.x, landing.y, gx, gy));
                        }
                    }
                }
            }

            return new List<NavWaypoint>();
        }

        /// <summary>
        /// Scan from a boundary cell across a pf=0 gap where targeting > 0.
        /// Returns walkable landing spots on the other side within blink range.
        /// Scans in all 8 directions, only following directions that start with pf=0.
        /// Uses actual Euclidean distance (not step count) for range checks,
        /// so diagonal scans don't exceed the intended blink range.
        /// </summary>
        private static List<(int x, int y, int gapWidth)> ScanBlinkLandings(
            int[][] pfGrid, int[][] tgtGrid,
            int bx, int by, int maxRange,
            int rows, int cols)
        {
            var landings = new List<(int x, int y, int gapWidth)>();

            foreach (var (dx, dy, stepDist) in Neighbors)
            {
                var firstX = bx + dx;
                var firstY = by + dy;

                // Only scan directions that start with pf=0 (into a gap)
                if (firstX < 0 || firstX >= cols || firstY < 0 || firstY >= rows)
                    continue;
                if (pfGrid[firstY][firstX] != 0)
                    continue;

                // Walk through the gap, tracking actual Euclidean distance
                var x = firstX;
                var y = firstY;
                var steps = 1;

                while (true)
                {
                    // Check actual Euclidean distance from boundary cell
                    var actualDist = MathF.Sqrt((x - bx) * (x - bx) + (y - by) * (y - by));
                    if (actualDist > maxRange)
                        break;

                    if (x < 0 || x >= cols || y < 0 || y >= rows)
                        break;

                    if (tgtGrid[y][x] == 0)
                        break; // Hit a wall — not jumpable

                    if (pfGrid[y][x] > 0)
                    {
                        // Found walkable terrain on the other side
                        landings.Add((x, y, steps));
                        break;
                    }

                    x += dx;
                    y += dy;
                    steps++;
                }
            }

            return landings;
        }

        /// <summary>
        /// Smooth a path with blink awareness. Only smooths between walk waypoints
        /// within the same walking segment — never smooths across blink boundaries.
        /// </summary>
        public static List<NavWaypoint> SmoothNavPath(int[][] grid, List<NavWaypoint> path)
        {
            if (path.Count <= 2)
                return path;

            var result = new List<NavWaypoint>();
            var rows = grid.Length;
            var cols = grid[0].Length;

            // Split into segments at blink boundaries, smooth each walking segment
            var segStart = 0;
            for (var i = 0; i < path.Count; i++)
            {
                if (path[i].Action == WaypointAction.Blink || i == path.Count - 1)
                {
                    // Smooth the walking segment [segStart..i) or [segStart..i]
                    var segEnd = path[i].Action == WaypointAction.Blink ? i : i + 1;
                    if (segEnd - segStart >= 2)
                    {
                        var segment = path.GetRange(segStart, segEnd - segStart)
                            .Select(w => w.Position).ToList();
                        var smoothed = SmoothPath(grid, segment);
                        result.AddRange(smoothed.Select(p => new NavWaypoint(p, WaypointAction.Walk)));
                    }
                    else if (segEnd > segStart)
                    {
                        result.AddRange(path.GetRange(segStart, segEnd - segStart));
                    }

                    // Add the blink waypoint itself
                    if (path[i].Action == WaypointAction.Blink)
                    {
                        result.Add(path[i]);
                        segStart = i + 1;
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Simplify a grid path by removing intermediate points that are on a straight line.
        /// Uses line-of-sight checks on the grid to skip unnecessary waypoints.
        /// Limits max segment length to avoid long shortcuts past wall corners
        /// where cursor-based movement can clip obstacles.
        /// </summary>
        public static List<Vector2> SmoothPath(int[][] grid, List<Vector2> path)
        {
            if (path.Count <= 2)
                return path;

            // Max smoothed segment length in world units (~100 grid cells)
            const float maxSegmentLength = 100f * GridToWorld;

            var rows = grid.Length;
            var cols = grid[0].Length;
            var result = new List<Vector2> { path[0] };
            var current = 0;

            while (current < path.Count - 1)
            {
                var farthest = current + 1;
                for (var i = path.Count - 1; i > current + 1; i--)
                {
                    // Skip candidates that are too far — long segments cause wall clipping
                    if (Vector2.Distance(path[current], path[i]) > maxSegmentLength)
                        continue;

                    if (HasLineOfSight(grid, path[current], path[i], rows, cols))
                    {
                        farthest = i;
                        break;
                    }
                }

                result.Add(path[farthest]);
                current = farthest;
            }

            return result;
        }

        private static bool HasLineOfSight(int[][] grid, Vector2 a, Vector2 b, int rows, int cols)
        {
            var (ax, ay) = WorldToGridPos(a);
            var (bx, by) = WorldToGridPos(b);

            var dx = Math.Abs(bx - ax);
            var dy = Math.Abs(by - ay);
            var sx = ax < bx ? 1 : -1;
            var sy = ay < by ? 1 : -1;
            var err = dx - dy;

            var cx = ax;
            var cy = ay;

            // Require pf >= 3 for LOS — values 1-2 are wall fringe cells.
            // Walking through fringe cells causes wall clipping with cursor-based movement.
            while (cx != bx || cy != by)
            {
                if (cx < 0 || cx >= cols || cy < 0 || cy >= rows || grid[cy][cx] < 3)
                    return false;

                var e2 = 2 * err;
                if (e2 > -dy) { err -= dy; cx += sx; }
                if (e2 < dx) { err += dx; cy += sy; }
            }

            return true;
        }

        private static float Heuristic(int ax, int ay, int bx, int by)
        {
            var dx = Math.Abs(ax - bx);
            var dy = Math.Abs(ay - by);
            return Math.Max(dx, dy) + 0.414f * Math.Min(dx, dy);
        }

        private static List<NavWaypoint> ReconstructNavPath(
            Dictionary<(int, int), (int x, int y)> cameFrom,
            HashSet<((int, int) from, (int, int) to)>? blinkEdges,
            int sx, int sy, int gx, int gy)
        {
            var gridPath = new List<(int x, int y, bool isBlink)>();
            var current = (gx, gy);
            while (current != (sx, sy))
            {
                var prev = cameFrom[current];
                var isBlink = blinkEdges != null && blinkEdges.Contains((prev, current));
                gridPath.Add((current.Item1, current.Item2, isBlink));
                current = prev;
            }
            gridPath.Add((sx, sy, false));
            gridPath.Reverse();

            return gridPath.Select(p =>
                new NavWaypoint(GridToWorldPos(p.x, p.y), p.isBlink ? WaypointAction.Blink : WaypointAction.Walk)
            ).ToList();
        }

        private static List<Vector2> ReconstructPath(
            Dictionary<(int, int), (int, int)> cameFrom,
            int sx, int sy, int gx, int gy)
        {
            var path = new List<(int x, int y)>();
            var current = (gx, gy);
            while (current != (sx, sy))
            {
                path.Add(current);
                current = cameFrom[current];
            }
            path.Add((sx, sy));
            path.Reverse();

            return path.Select(p => GridToWorldPos(p.x, p.y)).ToList();
        }

        private static (int x, int y) FindNearestWalkable(int[][] grid, int x, int y, int rows, int cols)
        {
            for (var radius = 1; radius < 20; radius++)
            {
                for (var dx = -radius; dx <= radius; dx++)
                {
                    for (var dy = -radius; dy <= radius; dy++)
                    {
                        if (Math.Abs(dx) != radius && Math.Abs(dy) != radius)
                            continue;
                        var nx = x + dx;
                        var ny = y + dy;
                        if (nx >= 0 && nx < cols && ny >= 0 && ny < rows && grid[ny][nx] >= 1)
                            return (nx, ny);
                    }
                }
            }
            return (x, y);
        }
    }
}
