using ExileCore;
using System.Numerics;

namespace AutoExile.Systems
{
    public enum WaypointAction { Walk, Blink }

    public struct NavWaypoint
    {
        public Vector2 Position; // grid coordinates
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
    /// All positions are in grid coordinates. Convert to world only at camera/input call sites.
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
        /// Convert grid position to world Vector3 using ExileCore's terrain height lookup.
        /// This is more accurate than manual GridToWorld multiplication because it includes
        /// proper terrain elevation from the game's height map.
        /// </summary>
        public static Vector3 GridToWorld3D(GameController gc, Vector2 gridPos) =>
            gc.IngameState.Data.ToWorldWithTerrainHeight(gridPos);

        /// <summary>
        /// Convert grid position to screen coordinates via ToWorldWithTerrainHeight.
        /// Preferred over manual GridToWorld * 10.88 + manual height lookup.
        /// </summary>
        public static Vector2 GridToScreen(GameController gc, Vector2 gridPos) =>
            gc.IngameState.Camera.WorldToScreen(gc.IngameState.Data.ToWorldWithTerrainHeight(gridPos));

        /// <summary>
        /// Run A* from start to goal on the pathfinding grid.
        /// All positions in grid coordinates.
        /// Returns a list of grid positions from start to goal, or empty if no path.
        /// </summary>
        public static List<Vector2> FindPath(int[][] grid, Vector2 gridStart, Vector2 gridEnd, int maxNodes = 200000)
        {
            var result = FindPathInternal(grid, null, gridStart, gridEnd, 0, 0, maxNodes);
            return result.Select(w => w.Position).ToList();
        }

        /// <summary>
        /// Run A* with blink support. When the pathfinder hits a boundary cell (adjacent to pf=0),
        /// it scans across the gap through cells where targeting > 0 to find landing spots.
        /// Only uses blinks when walking the detour costs more than blinkCostPenalty.
        /// All positions in grid coordinates.
        /// </summary>
        public static List<NavWaypoint> FindPathWithBlinks(
            int[][] pfGrid, int[][] tgtGrid,
            Vector2 gridStart, Vector2 gridEnd,
            int blinkRange = 40, float blinkCostPenalty = 30f,
            int maxNodes = 200000)
        {
            if (tgtGrid == null || tgtGrid.Length == 0)
            {
                // Fall back to normal pathfinding
                var fallback = FindPath(pfGrid, gridStart, gridEnd, maxNodes);
                return fallback.Select(p => new NavWaypoint(p, WaypointAction.Walk)).ToList();
            }

            return FindPathInternal(pfGrid, tgtGrid, gridStart, gridEnd, blinkRange, blinkCostPenalty, maxNodes);
        }

        private static List<NavWaypoint> FindPathInternal(
            int[][] pfGrid, int[][]? tgtGrid,
            Vector2 gridStart, Vector2 gridEnd,
            int blinkRange, float blinkCostPenalty,
            int maxNodes)
        {
            var sx = (int)gridStart.X;
            var sy = (int)gridStart.Y;
            var gx = (int)gridEnd.X;
            var gy = (int)gridEnd.Y;

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
                        // Penalize wider gaps heavily — perpendicular crossings have fewer gap cells
                        // than diagonal ones for the same wall, strongly preferring straight-across jumps.
                        var gapPenalty = landing.gapWidth * 5f;
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
                        // Found walkable terrain — push deeper to avoid landing at the edge.
                        // Continue along the same direction for a few more cells while still walkable.
                        const int landingBuffer = 3;
                        var lx = x;
                        var ly = y;
                        for (var b = 0; b < landingBuffer; b++)
                        {
                            var nx = lx + dx;
                            var ny = ly + dy;
                            if (nx < 0 || nx >= cols || ny < 0 || ny >= rows)
                                break;
                            if (pfGrid[ny][nx] < 3) // stop at walls and fringe cells
                                break;
                            var bufferDist = MathF.Sqrt((nx - bx) * (nx - bx) + (ny - by) * (ny - by));
                            if (bufferDist > maxRange)
                                break;
                            lx = nx;
                            ly = ny;
                        }
                        landings.Add((lx, ly, steps));
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
        /// After smoothing, pulls back the last walk waypoint before each blink so
        /// the player approaches from a few cells back, giving clean blink line-up.
        /// All positions in grid coordinates.
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

                    // Pull back the last walk waypoint before a blink so the player
                    // approaches from a few cells into safe terrain, not right at the edge.
                    // This gives the blink a clean perpendicular line-up.
                    if (path[i].Action == WaypointAction.Blink && result.Count >= 1)
                    {
                        var blinkPos = path[i].Position;
                        var approachPos = result[result.Count - 1].Position;
                        var pullbackPos = PullBackFromEdge(grid, approachPos, blinkPos, rows, cols);
                        if (pullbackPos.HasValue)
                            result[result.Count - 1] = new NavWaypoint(pullbackPos.Value, WaypointAction.Walk);
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
        /// Pull an approach waypoint back from the gap edge along the blink direction.
        /// Moves the point away from the blink target (deeper into safe terrain) by pullbackCells.
        /// Returns null if the original position is already safe enough or pullback isn't walkable.
        /// All positions in grid coordinates.
        /// </summary>
        private static Vector2? PullBackFromEdge(int[][] grid, Vector2 approachGrid, Vector2 blinkGrid,
            int rows, int cols)
        {
            const int pullbackCells = 4; // how many grid cells to pull back from edge

            var ax = (int)approachGrid.X;
            var ay = (int)approachGrid.Y;
            var bx = (int)blinkGrid.X;
            var by = (int)blinkGrid.Y;

            // Direction from blink target back toward approach (away from gap)
            var dx = ax - bx;
            var dy = ay - by;
            var len = MathF.Sqrt(dx * dx + dy * dy);
            if (len < 1f) return null;

            // Check if approach point is already deep in walkable terrain (pf >= 3).
            // If the cell and its neighbors are solidly walkable, no pullback needed.
            if (ax >= 0 && ax < cols && ay >= 0 && ay < rows && grid[ay][ax] >= 4)
                return null;

            // Pull back along the approach→blink line (opposite direction)
            var ndx = dx / len;
            var ndy = dy / len;
            for (var step = pullbackCells; step >= 1; step--)
            {
                var px = ax + (int)MathF.Round(ndx * step);
                var py = ay + (int)MathF.Round(ndy * step);
                if (px >= 0 && px < cols && py >= 0 && py < rows && grid[py][px] >= 3)
                    return new Vector2(px, py);
            }

            return null; // couldn't find a safe pullback — keep original
        }

        /// <summary>
        /// Simplify a grid path by removing intermediate points that are on a straight line.
        /// Uses line-of-sight checks on the grid to skip unnecessary waypoints.
        /// Limits max segment length to avoid long shortcuts past wall corners
        /// where cursor-based movement can clip obstacles.
        /// All positions in grid coordinates.
        /// </summary>
        public static List<Vector2> SmoothPath(int[][] grid, List<Vector2> path)
        {
            if (path.Count <= 2)
                return path;

            // Max smoothed segment length in grid units
            const float maxSegmentLength = 100f;

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

        /// <summary>
        /// Check walkable line of sight between two grid positions.
        /// Returns true if all cells along the Bresenham line have pathfinding value >= 4
        /// (no walls or fringe cells). Uses the pathfinding grid, NOT the targeting grid.
        /// </summary>
        public static bool HasLineOfSight(int[][] pfGrid, Vector2 gridA, Vector2 gridB)
        {
            if (pfGrid == null || pfGrid.Length == 0) return false;
            return HasLineOfSight(pfGrid, gridA, gridB, pfGrid.Length, pfGrid[0].Length);
        }

        private static bool HasLineOfSight(int[][] grid, Vector2 a, Vector2 b, int rows, int cols)
        {
            var ax = (int)a.X;
            var ay = (int)a.Y;
            var bx = (int)b.X;
            var by = (int)b.Y;

            var dx = Math.Abs(bx - ax);
            var dy = Math.Abs(by - ay);
            var sx = ax < bx ? 1 : -1;
            var sy = ay < by ? 1 : -1;
            var err = dx - dy;

            var cx = ax;
            var cy = ay;

            // Require pf >= 4 for LOS — ensures clearance from walls.
            // Value 3 (mid-gradient) is too close to walls for direct cursor movement
            // since the player has width and would clip narrow corridors.
            while (cx != bx || cy != by)
            {
                if (cx < 0 || cx >= cols || cy < 0 || cy >= rows || grid[cy][cx] < 4)
                    return false;

                var e2 = 2 * err;
                if (e2 > -dy) { err -= dy; cx += sx; }
                if (e2 < dx) { err += dx; cy += sy; }
            }

            return true;
        }

        /// <summary>
        /// Check if a grid cell is walkable (pathfinding value >= 3).
        /// Values 1-2 are wall fringe cells that cause clipping.
        /// Grid layout: data[y][x].
        /// </summary>
        public static bool IsWalkableCell(int[][] pfGrid, int gx, int gy)
        {
            if (gy < 0 || gy >= pfGrid.Length) return false;
            if (gx < 0 || gx >= pfGrid[gy].Length) return false;
            return pfGrid[gy][gx] >= 3;
        }

        /// <summary>
        /// Check targeting-layer line of sight between two grid positions.
        /// Returns true if all cells along the Bresenham line have targeting value > 0,
        /// meaning skills/projectiles can pass through. Does NOT imply walkability.
        /// </summary>
        public static bool HasTargetingLOS(int[][] tgtGrid, int gx1, int gy1, int gx2, int gy2)
        {
            int rows = tgtGrid.Length;
            if (rows == 0) return false;
            int cols = tgtGrid[0].Length;

            var dx = Math.Abs(gx2 - gx1);
            var dy = Math.Abs(gy2 - gy1);
            var sx = gx1 < gx2 ? 1 : -1;
            var sy = gy1 < gy2 ? 1 : -1;
            var err = dx - dy;

            var cx = gx1;
            var cy = gy1;

            while (cx != gx2 || cy != gy2)
            {
                if (cx < 0 || cx >= cols || cy < 0 || cy >= rows || tgtGrid[cy][cx] == 0)
                    return false;

                var e2 = 2 * err;
                if (e2 > -dy) { err -= dy; cx += sx; }
                if (e2 < dx) { err += dx; cy += sy; }
            }

            return true;
        }

        /// <summary>
        /// Find the nearest walkable cell (pf >= 3) within searchRadius of the given grid position.
        /// Returns null if nothing walkable found. Searches in expanding rings.
        /// </summary>
        public static (int x, int y)? FindNearestWalkableCell(int[][] pfGrid, int gx, int gy, int searchRadius = 10)
        {
            if (IsWalkableCell(pfGrid, gx, gy))
                return (gx, gy);

            for (int r = 1; r <= searchRadius; r++)
            {
                for (int dx = -r; dx <= r; dx++)
                {
                    for (int dy = -r; dy <= r; dy++)
                    {
                        if (Math.Abs(dx) != r && Math.Abs(dy) != r) continue; // ring only
                        if (IsWalkableCell(pfGrid, gx + dx, gy + dy))
                            return (gx + dx, gy + dy);
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Get terrain height at a grid position from the height data.
        /// Returns fallbackZ if height data is unavailable or position is out of bounds.
        /// </summary>
        public static float GetTerrainHeight(float[][]? heightGrid, int gx, int gy, float fallbackZ)
        {
            if (heightGrid == null) return fallbackZ;
            if (gy < 0 || gy >= heightGrid.Length) return fallbackZ;
            var row = heightGrid[gy];
            if (row == null || gx < 0 || gx >= row.Length) return fallbackZ;
            return row[gx];
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
                new NavWaypoint(new Vector2(p.x, p.y), p.isBlink ? WaypointAction.Blink : WaypointAction.Walk)
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

            return path.Select(p => new Vector2(p.x, p.y)).ToList();
        }

        private static (int x, int y) FindNearestWalkable(int[][] grid, int x, int y, int rows, int cols)
        {
            for (var radius = 1; radius <= 60; radius++)
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
