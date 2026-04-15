using System.Numerics;

namespace AutoExile.Systems
{
    /// <summary>
    /// Tracks map exploration coverage using the full walkability grid.
    /// Segments walkable space into blobs (connected regions) via flood fill from player position.
    /// Disconnected areas (Harvest, Wildwood, upper floors) are separate blobs.
    /// Coverage is tracked per-blob — cells within render range of the player are marked "seen".
    /// </summary>
    public class ExplorationMap
    {
        private static int DefaultRange => (int)Pathfinding.NetworkBubbleRadius;

        /// <summary>
        /// Override the seen radius for Update(). Set to a smaller value (e.g., 40) on small maps
        /// so exploration route planning works — the bot must physically visit each region.
        /// Set to 0 to use the default network bubble radius.
        /// </summary>
        public int SeenRadiusOverride { get; set; }

        private int RenderRange => SeenRadiusOverride > 0 ? SeenRadiusOverride : DefaultRange;

        // Region chunk size — blob is divided into NxN grid chunks for navigation targeting
        private const int RegionChunkSize = 80;

        // Minimum walkable cells for a region to be worth exploring
        private const int MinRegionSize = 50;

        // Small pocket threshold — dead-end areas smaller than this are deprioritized
        private const int SmallPocketThreshold = 200;

        // Minimum pathfinding grid value to count as "real" walkable space.
        // Values 1-2 are edge fringe hugging walls/coastlines — not worth exploring.
        private const int MinWalkableValue = 3;

        // ── Public state ──

        public List<Blob> Blobs { get; } = new();
        public int ActiveBlobIndex { get; private set; } = -1;
        public Blob? ActiveBlob => ActiveBlobIndex >= 0 && ActiveBlobIndex < Blobs.Count ? Blobs[ActiveBlobIndex] : null;
        public float ActiveBlobCoverage => ActiveBlob?.Coverage ?? 0f;
        public int TotalBlobCount => Blobs.Count;
        public bool IsInitialized => Blobs.Count > 0;

        // Known transition portals (grid positions where we've seen AreaTransition entities)
        public List<TransitionPortal> KnownTransitions { get; } = new();

        // Regions that pathfinding couldn't reach — skip on future targeting
        public HashSet<int> FailedRegions { get; } = new();

        // Debug info
        public string LastAction { get; private set; } = "";
        public int TotalWalkableCells { get; private set; }

        /// <summary>
        /// Mark a region as unreachable so GetNextExplorationTarget skips it.
        /// Call when pathfinding fails to the region's target.
        /// </summary>
        public void MarkRegionFailed(Vector2 targetGridPos)
        {
            var blob = ActiveBlob;
            if (blob == null) return;

            // Find which region this target was in
            var cell = new Vector2i((int)targetGridPos.X, (int)targetGridPos.Y);
            if (blob.CellToRegion.TryGetValue(cell, out var regionIdx))
            {
                FailedRegions.Add(regionIdx);
                LastAction = $"Marked region {regionIdx} as unreachable";
            }
            else
            {
                // Target may be a centroid not exactly on a cell — find nearest region
                float bestDist = float.MaxValue;
                int bestIdx = -1;
                for (int i = 0; i < blob.Regions.Count; i++)
                {
                    var d = Vector2.Distance(targetGridPos, blob.Regions[i].Center);
                    if (d < bestDist) { bestDist = d; bestIdx = i; }
                }
                if (bestIdx >= 0)
                {
                    FailedRegions.Add(bestIdx);
                    LastAction = $"Marked region {bestIdx} as unreachable (nearest)";
                }
            }
        }

        // ═══════════════════════════════════════════════════
        // Initialization
        // ═══════════════════════════════════════════════════

        /// <summary>
        /// Initialize from the pathfinding grid. Flood fills from player position to discover
        /// the primary blob (all walkable cells reachable by walking + blink gaps).
        /// </summary>
        public void Initialize(int[][] pfGrid, int[][]? tgtGrid, Vector2 playerGridPos, int blinkRange)
        {
            Clear();

            if (pfGrid == null || pfGrid.Length == 0) return;

            var rows = pfGrid.Length;
            var cols = pfGrid[0].Length;
            var px = (int)playerGridPos.X;
            var py = (int)playerGridPos.Y;

            px = Math.Clamp(px, 0, cols - 1);
            py = Math.Clamp(py, 0, rows - 1);

            // If player is on unwalkable cell, find nearest walkable
            if (pfGrid[py][px] == 0)
            {
                var nearest = FindNearestWalkable(pfGrid, px, py, rows, cols);
                if (nearest == null)
                {
                    LastAction = "No walkable cells found near player";
                    return;
                }
                (px, py) = nearest.Value;
            }

            // Flood fill from player position
            var blobCells = FloodFill(pfGrid, tgtGrid, px, py, rows, cols, blinkRange);

            if (blobCells.Count == 0)
            {
                LastAction = "Flood fill returned 0 cells";
                return;
            }

            var blob = CreateBlob(blobCells, 0);
            Blobs.Add(blob);
            ActiveBlobIndex = 0;
            TotalWalkableCells = blobCells.Count;

            // Mark cells near player as already seen
            Update(playerGridPos);

            LastAction = $"Initialized: {blobCells.Count} cells, {blob.Regions.Count} regions";
        }

        /// <summary>
        /// Enter a new blob (after using a transition portal). Flood fills from new position.
        /// </summary>
        public int EnterNewBlob(int[][] pfGrid, int[][]? tgtGrid, Vector2 playerGridPos, int blinkRange)
        {
            if (pfGrid == null || pfGrid.Length == 0) return -1;

            var rows = pfGrid.Length;
            var cols = pfGrid[0].Length;
            var px = (int)playerGridPos.X;
            var py = (int)playerGridPos.Y;
            px = Math.Clamp(px, 0, cols - 1);
            py = Math.Clamp(py, 0, rows - 1);

            if (pfGrid[py][px] == 0)
            {
                var nearest = FindNearestWalkable(pfGrid, px, py, rows, cols);
                if (nearest == null) return -1;
                (px, py) = nearest.Value;
            }

            // Check if player is already inside an existing blob
            var playerCell = new Vector2i(px, py);
            for (int i = 0; i < Blobs.Count; i++)
            {
                if (Blobs[i].WalkableCells.Contains(playerCell))
                {
                    ActiveBlobIndex = i;
                    Update(playerGridPos);
                    LastAction = $"Re-entered blob {i}";
                    return i;
                }
            }

            // New disconnected area — create new blob
            var blobCells = FloodFill(pfGrid, tgtGrid, px, py, rows, cols, blinkRange);
            if (blobCells.Count == 0) return -1;

            var blob = CreateBlob(blobCells, Blobs.Count);
            Blobs.Add(blob);
            ActiveBlobIndex = Blobs.Count - 1;
            TotalWalkableCells += blobCells.Count;

            Update(playerGridPos);
            LastAction = $"New blob {ActiveBlobIndex}: {blobCells.Count} cells, {blob.Regions.Count} regions";
            return ActiveBlobIndex;
        }

        /// <summary>
        /// Clear all exploration data.
        /// </summary>
        public void Clear()
        {
            Blobs.Clear();
            ActiveBlobIndex = -1;
            KnownTransitions.Clear();
            FailedRegions.Clear();
            TotalWalkableCells = 0;
            LastAction = "";
        }

        /// <summary>
        /// Clear all seen state while preserving blob/region structure.
        /// Use in modes like Simulacrum where the same area must be re-swept
        /// each wave to find newly spawned monsters. Also clears failed regions
        /// since pathability may have changed.
        /// </summary>
        public void ResetSeen()
        {
            foreach (var blob in Blobs)
            {
                blob.SeenCells.Clear();
                blob.Coverage = 0f;
                foreach (var region in blob.Regions)
                    region.SeenCount = 0;
            }
            FailedRegions.Clear();
        }

        // ═══════════════════════════════════════════════════
        // Per-tick update
        // ═══════════════════════════════════════════════════

        /// <summary>
        /// Mark cells within render range of the player as seen. Call each tick.
        /// Uses squared distance check for performance — no sqrt needed.
        /// </summary>
        public void Update(Vector2 playerGridPos)
        {
            var blob = ActiveBlob;
            if (blob == null) return;

            var px = (int)playerGridPos.X;
            var py = (int)playerGridPos.Y;
            var rangeSq = RenderRange * RenderRange;

            // Only check cells in the vicinity — scan a bounding box around player
            var minX = px - RenderRange;
            var maxX = px + RenderRange;
            var minY = py - RenderRange;
            var maxY = py + RenderRange;

            for (int y = minY; y <= maxY; y++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    var cell = new Vector2i(x, y);
                    if (!blob.WalkableCells.Contains(cell)) continue;
                    if (blob.SeenCells.Contains(cell)) continue;

                    var dx = x - px;
                    var dy = y - py;
                    if (dx * dx + dy * dy <= rangeSq)
                    {
                        blob.SeenCells.Add(cell);

                        // Update region seen counts
                        var regionIdx = blob.CellToRegion.GetValueOrDefault(cell, -1);
                        if (regionIdx >= 0 && regionIdx < blob.Regions.Count)
                            blob.Regions[regionIdx].SeenCount++;
                    }
                }
            }

            blob.Coverage = blob.WalkableCells.Count > 0
                ? (float)blob.SeenCells.Count / blob.WalkableCells.Count
                : 0f;
        }

        // ═══════════════════════════════════════════════════
        // Navigation targeting
        // ═══════════════════════════════════════════════════

        /// <summary>
        /// Get the best unexplored region center to navigate to. Returns null if fully explored.
        /// Prefers large unseen regions, weighted by proximity to player.
        /// </summary>
        public Vector2? GetNextExplorationTarget(Vector2 playerGridPos)
        {
            var blob = ActiveBlob;
            if (blob == null) return null;

            Region? bestRegion = null;
            float bestScore = float.MinValue;

            foreach (var region in blob.Regions)
            {
                // Skip fully explored regions. When SeenRadiusOverride is active (small maps),
                // use a tighter threshold — the small radius means even visited regions may
                // only be 90-95% seen, so 80% would skip regions we haven't fully swept.
                float exploredThreshold = SeenRadiusOverride > 0 ? 0.98f : 0.8f;
                if (region.ExploredRatio > exploredThreshold) continue;

                // Skip tiny dead-end pockets
                if (region.CellCount < MinRegionSize) continue;

                // Skip regions we failed to path to
                if (FailedRegions.Contains(region.Index)) continue;

                var unseenCount = region.CellCount - region.SeenCount;
                var dist = Vector2.Distance(playerGridPos, region.Center);

                // Base score: unseen cells per unit distance with sqrt scaling
                float sizeScore = MathF.Sqrt(unseenCount);
                if (region.CellCount < SmallPocketThreshold)
                    sizeScore *= 0.5f; // mild deprioritize for tiny areas

                float score = sizeScore / (dist + 30f);

                // Branch score bonus: prefer regions with escape routes (loop paths)
                // over dead-end branches. BranchScore adds +100 for escape routes.
                // Scale it down relative to distance score so it's a tiebreaker for
                // similar-distance regions, not a dominant override.
                float branchBonus = BranchScore(blob, region);
                score += branchBonus * 0.001f;

                if (score > bestScore)
                {
                    bestScore = score;
                    bestRegion = region;
                }
            }

            if (bestRegion == null) return null;

            // Return the centroid of unseen cells in this region for more accurate targeting
            return GetUnseenCentroid(blob, bestRegion);
        }

        /// <summary>
        /// Find the centroid of unseen cells within a region, then snap to the nearest
        /// actual unseen cell. Raw centroids can land on wall cells (average of cells
        /// on both sides of a corridor), causing navigation to tight/stuck spots.
        /// </summary>
        private Vector2? GetUnseenCentroid(Blob blob, Region region)
        {
            float sumX = 0, sumY = 0;
            int count = 0;

            foreach (var cell in region.Cells)
            {
                if (!blob.SeenCells.Contains(cell))
                {
                    sumX += cell.X;
                    sumY += cell.Y;
                    count++;
                }
            }

            if (count == 0) return null;

            // Snap centroid to nearest actual unseen cell to avoid wall positions
            var centroid = new Vector2(sumX / count, sumY / count);
            float bestDist = float.MaxValue;
            Vector2i bestCell = default;

            foreach (var cell in region.Cells)
            {
                if (blob.SeenCells.Contains(cell)) continue;
                var dx = cell.X - centroid.X;
                var dy = cell.Y - centroid.Y;
                var dist = dx * dx + dy * dy;
                if (dist < bestDist)
                {
                    bestDist = dist;
                    bestCell = cell;
                }
            }

            return new Vector2(bestCell.X, bestCell.Y);
        }

        // ═══════════════════════════════════════════════════
        // Transition portal tracking
        // ═══════════════════════════════════════════════════

        /// <summary>
        /// Record a discovered transition portal position. Called when we see an AreaTransition entity.
        /// </summary>
        public void RecordTransition(Vector2 gridPos, string name = "")
        {
            // Don't duplicate
            foreach (var t in KnownTransitions)
            {
                if (Vector2.Distance(t.GridPos, gridPos) < 5f)
                    return;
            }

            KnownTransitions.Add(new TransitionPortal
            {
                GridPos = gridPos,
                Name = name,
                SourceBlobIndex = ActiveBlobIndex,
                DestBlobIndex = -1, // unknown until entered
            });
        }

        // ═══════════════════════════════════════════════════
        // Flood fill
        // ═══════════════════════════════════════════════════

        /// <summary>
        /// BFS flood fill from a starting cell. Includes blink-aware gap crossing
        /// so ledge-separated areas that are reachable via movement skills stay in the same blob.
        /// </summary>
        private static HashSet<Vector2i> FloodFill(int[][] pfGrid, int[][]? tgtGrid, int startX, int startY, int rows, int cols, int blinkRange)
        {
            var visited = new HashSet<Vector2i>();
            var queue = new Queue<Vector2i>();

            var start = new Vector2i(startX, startY);
            queue.Enqueue(start);
            visited.Add(start);

            // 8-directional neighbors
            ReadOnlySpan<(int dx, int dy)> dirs = stackalloc (int, int)[]
            {
                (1, 0), (-1, 0), (0, 1), (0, -1),
                (1, 1), (1, -1), (-1, 1), (-1, -1)
            };

            while (queue.Count > 0)
            {
                var cell = queue.Dequeue();
                var hasPf0Neighbor = false;

                foreach (var (dx, dy) in dirs)
                {
                    var nx = cell.X + dx;
                    var ny = cell.Y + dy;

                    if (nx < 0 || nx >= cols || ny < 0 || ny >= rows) continue;

                    var val = pfGrid[ny][nx];
                    if (val < MinWalkableValue)
                    {
                        // Values 0 = impassable, 1-2 = edge fringe (treat as boundary)
                        if (val == 0) hasPf0Neighbor = true;
                        continue;
                    }

                    var neighbor = new Vector2i(nx, ny);
                    if (visited.Add(neighbor))
                        queue.Enqueue(neighbor);
                }

                // Blink gap crossing: if at a boundary cell, scan for walkable landing spots
                if (hasPf0Neighbor && tgtGrid != null && blinkRange > 0)
                {
                    foreach (var landing in ScanBlinkLandings(pfGrid, tgtGrid, cell.X, cell.Y, blinkRange, rows, cols))
                    {
                        if (pfGrid[landing.y][landing.x] < MinWalkableValue) continue;
                        var landingCell = new Vector2i(landing.x, landing.y);
                        if (visited.Add(landingCell))
                            queue.Enqueue(landingCell);
                    }
                }
            }

            return visited;
        }

        /// <summary>
        /// Scan for blink landing spots from a boundary cell. Mirrors the logic in Pathfinding.
        /// Scans in 8 directions through cells where targeting > 0, looking for walkable cells
        /// on the other side within blink range.
        /// </summary>
        private static List<(int x, int y)> ScanBlinkLandings(int[][] pfGrid, int[][] tgtGrid, int cx, int cy, int blinkRange, int rows, int cols)
        {
            var landings = new List<(int x, int y)>();

            // Scan in 8 cardinal/diagonal directions
            ReadOnlySpan<(int dx, int dy)> dirs = stackalloc (int, int)[]
            {
                (1, 0), (-1, 0), (0, 1), (0, -1),
                (1, 1), (1, -1), (-1, 1), (-1, -1)
            };

            foreach (var (dx, dy) in dirs)
            {
                bool inGap = false;

                for (int step = 1; step <= blinkRange; step++)
                {
                    var nx = cx + dx * step;
                    var ny = cy + dy * step;

                    if (nx < 0 || nx >= cols || ny < 0 || ny >= rows) break;

                    var pfVal = pfGrid[ny][nx];
                    var tgtVal = tgtGrid[ny][nx];

                    if (pfVal == 0)
                    {
                        // In the gap — targeting must be > 0 to continue (jumpable, not wall)
                        if (tgtVal > 0)
                        {
                            inGap = true;
                            continue;
                        }
                        else
                        {
                            break; // hit a wall, stop scanning this direction
                        }
                    }
                    else if (inGap)
                    {
                        // Found walkable ground on the other side of a gap
                        landings.Add((nx, ny));
                        break; // one landing per direction
                    }
                    else
                    {
                        // Still on walkable ground, haven't entered gap yet
                        break;
                    }
                }
            }

            return landings;
        }

        // ═══════════════════════════════════════════════════
        // Blob and region creation
        // ═══════════════════════════════════════════════════

        private static Blob CreateBlob(HashSet<Vector2i> cells, int index)
        {
            var blob = new Blob
            {
                Index = index,
                WalkableCells = cells,
            };

            // Find bounding box
            int minX = int.MaxValue, minY = int.MaxValue;
            int maxX = int.MinValue, maxY = int.MinValue;
            foreach (var c in cells)
            {
                if (c.X < minX) minX = c.X;
                if (c.Y < minY) minY = c.Y;
                if (c.X > maxX) maxX = c.X;
                if (c.Y > maxY) maxY = c.Y;
            }

            // Segment into regions using grid chunks
            int chunksX = (maxX - minX) / RegionChunkSize + 1;
            int chunksY = (maxY - minY) / RegionChunkSize + 1;

            // Create region grid
            var regionGrid = new List<Vector2i>[chunksX * chunksY];
            for (int i = 0; i < regionGrid.Length; i++)
                regionGrid[i] = new List<Vector2i>();

            foreach (var cell in cells)
            {
                int rx = (cell.X - minX) / RegionChunkSize;
                int ry = (cell.Y - minY) / RegionChunkSize;
                int ri = ry * chunksX + rx;

                if (ri >= 0 && ri < regionGrid.Length)
                    regionGrid[ri].Add(cell);
            }

            // Create regions from non-empty chunks
            for (int i = 0; i < regionGrid.Length; i++)
            {
                var chunk = regionGrid[i];
                if (chunk.Count < MinRegionSize) continue;

                // Calculate centroid
                float sumX = 0, sumY = 0;
                foreach (var c in chunk)
                {
                    sumX += c.X;
                    sumY += c.Y;
                }

                var region = new Region
                {
                    Index = blob.Regions.Count,
                    Center = new Vector2(sumX / chunk.Count, sumY / chunk.Count),
                    CellCount = chunk.Count,
                    SeenCount = 0,
                    Cells = chunk,
                };

                // Map cells to region index
                foreach (var c in chunk)
                    blob.CellToRegion[c] = region.Index;

                blob.Regions.Add(region);
            }

            // Also add small chunks' cells to the nearest region's mapping
            // (so their seen status still gets tracked)
            for (int i = 0; i < regionGrid.Length; i++)
            {
                var chunk = regionGrid[i];
                if (chunk.Count >= MinRegionSize || chunk.Count == 0) continue;

                // Find nearest region
                float sumX = 0, sumY = 0;
                foreach (var c in chunk) { sumX += c.X; sumY += c.Y; }
                var chunkCenter = new Vector2(sumX / chunk.Count, sumY / chunk.Count);

                int nearestRegion = -1;
                float nearestDist = float.MaxValue;
                for (int r = 0; r < blob.Regions.Count; r++)
                {
                    var d = Vector2.Distance(chunkCenter, blob.Regions[r].Center);
                    if (d < nearestDist)
                    {
                        nearestDist = d;
                        nearestRegion = r;
                    }
                }

                if (nearestRegion >= 0)
                {
                    var target = blob.Regions[nearestRegion];
                    target.CellCount += chunk.Count;
                    target.Cells.AddRange(chunk);
                    foreach (var c in chunk)
                        blob.CellToRegion[c] = nearestRegion;
                }
            }

            // Build region adjacency graph — regions sharing chunk boundaries are neighbors
            BuildRegionAdjacency(blob, minX, minY, chunksX, chunksY);

            return blob;
        }

        // ═══════════════════════════════════════════════════
        // Snapshot / Restore (for cross-zone caching)
        // ═══════════════════════════════════════════════════

        /// <summary>
        /// Capture a lightweight snapshot of exploration state that can be restored later.
        /// Used by BotCore to cache map progress when entering sub-zones (e.g., Wishes portal).
        /// </summary>
        public ExplorationSnapshot CreateSnapshot()
        {
            var snapshot = new ExplorationSnapshot
            {
                ActiveBlobIndex = ActiveBlobIndex,
                TotalWalkableCells = TotalWalkableCells,
                LastAction = LastAction,
            };

            foreach (var blob in Blobs)
            {
                snapshot.BlobSnapshots.Add(new BlobSnapshot
                {
                    Index = blob.Index,
                    WalkableCells = new HashSet<Vector2i>(blob.WalkableCells),
                    SeenCells = new HashSet<Vector2i>(blob.SeenCells),
                    Regions = blob.Regions.Select(r => new RegionSnapshot
                    {
                        Index = r.Index,
                        Center = r.Center,
                        CellCount = r.CellCount,
                        SeenCount = r.SeenCount,
                        Cells = new List<Vector2i>(r.Cells),
                        Neighbors = new HashSet<int>(r.Neighbors),
                    }).ToList(),
                    CellToRegion = new Dictionary<Vector2i, int>(blob.CellToRegion),
                    Coverage = blob.Coverage,
                });
            }

            foreach (var t in KnownTransitions)
            {
                snapshot.Transitions.Add(new TransitionPortal
                {
                    GridPos = t.GridPos,
                    Name = t.Name,
                    SourceBlobIndex = t.SourceBlobIndex,
                    DestBlobIndex = t.DestBlobIndex,
                });
            }

            snapshot.FailedRegionsCopy = new HashSet<int>(FailedRegions);
            return snapshot;
        }

        /// <summary>
        /// Restore exploration state from a previously captured snapshot.
        /// Replaces all current state.
        /// </summary>
        public void RestoreSnapshot(ExplorationSnapshot snapshot)
        {
            Clear();

            ActiveBlobIndex = snapshot.ActiveBlobIndex;
            TotalWalkableCells = snapshot.TotalWalkableCells;
            LastAction = snapshot.LastAction + " (restored)";

            foreach (var bs in snapshot.BlobSnapshots)
            {
                var blob = new Blob
                {
                    Index = bs.Index,
                    WalkableCells = bs.WalkableCells,
                    SeenCells = bs.SeenCells,
                    CellToRegion = bs.CellToRegion,
                    Coverage = bs.Coverage,
                };

                foreach (var rs in bs.Regions)
                {
                    blob.Regions.Add(new Region
                    {
                        Index = rs.Index,
                        Center = rs.Center,
                        CellCount = rs.CellCount,
                        SeenCount = rs.SeenCount,
                        Cells = rs.Cells,
                        Neighbors = rs.Neighbors,
                    });
                }

                Blobs.Add(blob);
            }

            foreach (var t in snapshot.Transitions)
                KnownTransitions.Add(t);

            foreach (var r in snapshot.FailedRegionsCopy)
                FailedRegions.Add(r);
        }

        // ═══════════════════════════════════════════════════
        // Helpers
        // ═══════════════════════════════════════════════════

        /// <summary>
        /// Build region adjacency by checking which chunk grid cells border each other.
        /// Two regions are neighbors if their chunks are horizontally/vertically adjacent.
        /// </summary>
        private static void BuildRegionAdjacency(Blob blob, int minX, int minY, int chunksX, int chunksY)
        {
            // Map chunk index → region index (some chunks have no region or were merged)
            var chunkToRegion = new Dictionary<int, int>();
            foreach (var cell in blob.WalkableCells)
            {
                int rx = (cell.X - minX) / RegionChunkSize;
                int ry = (cell.Y - minY) / RegionChunkSize;
                int ci = ry * chunksX + rx;
                if (!chunkToRegion.ContainsKey(ci) && blob.CellToRegion.TryGetValue(cell, out var ri))
                    chunkToRegion[ci] = ri;
            }

            // Check 4-connected chunk neighbors
            for (int cy = 0; cy < chunksY; cy++)
            {
                for (int cx = 0; cx < chunksX; cx++)
                {
                    int ci = cy * chunksX + cx;
                    if (!chunkToRegion.TryGetValue(ci, out var regionA)) continue;

                    // Right neighbor
                    if (cx + 1 < chunksX)
                    {
                        int ni = cy * chunksX + (cx + 1);
                        if (chunkToRegion.TryGetValue(ni, out var regionB) && regionA != regionB)
                        {
                            blob.Regions[regionA].Neighbors.Add(regionB);
                            blob.Regions[regionB].Neighbors.Add(regionA);
                        }
                    }
                    // Bottom neighbor
                    if (cy + 1 < chunksY)
                    {
                        int ni = (cy + 1) * chunksX + cx;
                        if (chunkToRegion.TryGetValue(ni, out var regionB) && regionA != regionB)
                        {
                            blob.Regions[regionA].Neighbors.Add(regionB);
                            blob.Regions[regionB].Neighbors.Add(regionA);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Score a region's branch quality using DFS. Counts reachable unseen cells down this branch
        /// and gives a bonus if the branch has escape routes back to explored regions.
        /// A region with only unexplored dead-end neighbors scores lower than one
        /// that connects back to already-explored space (loop/circuit paths).
        /// </summary>
        private float BranchScore(Blob blob, Region startRegion)
        {
            // DFS from startRegion through unexplored neighbors
            var visited = new HashSet<int> { startRegion.Index };
            var stack = new Stack<int>();
            stack.Push(startRegion.Index);

            float unseenCells = 0f;
            bool hasEscapeRoute = false;

            while (stack.Count > 0)
            {
                var idx = stack.Pop();
                var region = blob.Regions[idx];
                unseenCells += region.CellCount - region.SeenCount;

                foreach (var neighborIdx in region.Neighbors)
                {
                    if (FailedRegions.Contains(neighborIdx)) continue;
                    var neighbor = blob.Regions[neighborIdx];

                    if (visited.Contains(neighborIdx))
                    {
                        // Already visited in this DFS — loop/escape found
                        if (neighbor.ExploredRatio > 0.5f)
                            hasEscapeRoute = true;
                        continue;
                    }

                    visited.Add(neighborIdx);

                    // Only follow unexplored branches
                    float exploredThreshold = SeenRadiusOverride > 0 ? 0.98f : 0.8f;
                    if (neighbor.ExploredRatio < exploredThreshold)
                        stack.Push(neighborIdx);
                    else
                        hasEscapeRoute = true; // explored neighbor = escape route
                }
            }

            // Escape route bonus: prefer branches that don't dead-end
            return hasEscapeRoute ? unseenCells + 100f : unseenCells;
        }

        private static (int x, int y)? FindNearestWalkable(int[][] grid, int x, int y, int rows, int cols)
        {
            for (int radius = 1; radius <= 60; radius++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    for (int dy = -radius; dy <= radius; dy++)
                    {
                        if (Math.Abs(dx) != radius && Math.Abs(dy) != radius) continue;
                        var nx = x + dx;
                        var ny = y + dy;
                        if (nx >= 0 && nx < cols && ny >= 0 && ny < rows && grid[ny][nx] >= MinWalkableValue)
                            return (nx, ny);
                    }
                }
            }
            return null;
        }

        // ═══════════════════════════════════════════════════
        // Data types
        // ═══════════════════════════════════════════════════

        public class Blob
        {
            public int Index;
            public HashSet<Vector2i> WalkableCells = new();
            public HashSet<Vector2i> SeenCells = new();
            public List<Region> Regions = new();
            public Dictionary<Vector2i, int> CellToRegion = new();
            public float Coverage;
        }

        /// <summary>
        /// Get the weighted centroid of the active blob (center of all walkable space).
        /// Uses region centers weighted by cell count for efficiency.
        /// Returns null if not initialized.
        /// </summary>
        public Vector2? GetMapCenter()
        {
            var blob = ActiveBlob;
            if (blob == null || blob.Regions.Count == 0) return null;

            var weightedSum = Vector2.Zero;
            int totalCells = 0;
            foreach (var region in blob.Regions)
            {
                weightedSum += region.Center * region.CellCount;
                totalCells += region.CellCount;
            }

            return totalCells > 0 ? weightedSum / totalCells : null;
        }

        /// <summary>
        /// Snap a target grid position to the nearest walkable cell in the active blob.
        /// Returns null if no walkable cell within maxDistance, meaning the target
        /// is in an unreachable area (different blob / behind transition).
        /// Uses region centers as a spatial index for efficiency.
        /// </summary>
        public Vector2? SnapToActiveBlob(Vector2 targetGridPos, float maxDistance = 80f)
        {
            var blob = ActiveBlob;
            if (blob == null) return null;

            var targetCell = new Vector2i((int)targetGridPos.X, (int)targetGridPos.Y);

            // Fast path: target is already in active blob
            if (blob.WalkableCells.Contains(targetCell))
                return targetGridPos;

            // Use region centers to narrow the search area
            float bestDistSq = float.MaxValue;
            Vector2i bestCell = default;
            bool found = false;

            float searchRadius = maxDistance + RegionChunkSize; // include regions whose cells might be within range

            foreach (var region in blob.Regions)
            {
                if (Vector2.Distance(region.Center, targetGridPos) > searchRadius)
                    continue;

                foreach (var cell in region.Cells)
                {
                    var dx = cell.X - targetGridPos.X;
                    var dy = cell.Y - targetGridPos.Y;
                    var distSq = dx * dx + dy * dy;
                    if (distSq < bestDistSq)
                    {
                        bestDistSq = distSq;
                        bestCell = cell;
                        found = true;
                    }
                }
            }

            if (!found) return null;

            var actualDist = MathF.Sqrt(bestDistSq);
            if (actualDist > maxDistance) return null;

            return new Vector2(bestCell.X, bestCell.Y);
        }

        public class Region
        {
            public int Index;
            public Vector2 Center;          // centroid in grid coords
            public int CellCount;           // total walkable cells
            public int SeenCount;           // cells revealed by player proximity
            public List<Vector2i> Cells = new();
            public HashSet<int> Neighbors = new(); // adjacent region indices

            public float ExploredRatio => CellCount > 0 ? (float)SeenCount / CellCount : 0f;
        }

        public class TransitionPortal
        {
            public Vector2 GridPos;
            public string Name = "";
            public int SourceBlobIndex = -1;
            public int DestBlobIndex = -1;  // -1 until we enter it
        }
    }

    /// <summary>Snapshot of full ExplorationMap state for cross-zone caching.</summary>
    public class ExplorationSnapshot
    {
        public int ActiveBlobIndex;
        public int TotalWalkableCells;
        public string LastAction = "";
        public List<BlobSnapshot> BlobSnapshots = new();
        public List<ExplorationMap.TransitionPortal> Transitions = new();
        public HashSet<int> FailedRegionsCopy = new();
    }

    public class BlobSnapshot
    {
        public int Index;
        public HashSet<Vector2i> WalkableCells = new();
        public HashSet<Vector2i> SeenCells = new();
        public List<RegionSnapshot> Regions = new();
        public Dictionary<Vector2i, int> CellToRegion = new();
        public float Coverage;
    }

    public class RegionSnapshot
    {
        public int Index;
        public Vector2 Center;
        public int CellCount;
        public int SeenCount;
        public List<Vector2i> Cells = new();
        public HashSet<int> Neighbors = new();
    }

    /// <summary>
    /// Integer Vector2 for grid cell coordinates. Used as dictionary/hashset keys.
    /// </summary>
    public readonly struct Vector2i : IEquatable<Vector2i>
    {
        public readonly int X;
        public readonly int Y;

        public Vector2i(int x, int y) { X = x; Y = y; }

        public bool Equals(Vector2i other) => X == other.X && Y == other.Y;
        public override bool Equals(object? obj) => obj is Vector2i other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(X, Y);
        public override string ToString() => $"({X},{Y})";

        public static bool operator ==(Vector2i a, Vector2i b) => a.X == b.X && a.Y == b.Y;
        public static bool operator !=(Vector2i a, Vector2i b) => !(a == b);
    }
}
