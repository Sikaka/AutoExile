using System.Drawing;
using System.Drawing.Imaging;
using System.Numerics;
using System.Text.Json;
using ExileCore.Shared.Enums;

namespace AutoExile.Systems
{
    /// <summary>
    /// Snapshot of live game state, captured on the main thread before offloading to thread pool.
    /// Entity references can't be accessed from background threads — extract all needed data here.
    /// </summary>
    public class GameStateSnapshot
    {
        public List<EntitySnapshot> Entities { get; set; } = new();
        public CombatSnapshot? Combat { get; set; }
        public ModeSnapshot? Mode { get; set; }
        public InteractionSnapshot? Interaction { get; set; }
        public LootSnapshot? Loot { get; set; }
    }

    public class EntitySnapshot
    {
        public long Id { get; set; }
        public string Metadata { get; set; } = "";
        public string Path { get; set; } = "";
        public string EntityType { get; set; } = "";
        public Vector2 GridPos { get; set; }
        public float DistanceToPlayer { get; set; }
        public EntityCategory Category { get; set; }
        public bool IsAlive { get; set; }
        public bool IsTargetable { get; set; }
        public bool IsHostile { get; set; }
        public string? Rarity { get; set; }
        /// <summary>Short display name extracted from metadata path.</summary>
        public string ShortName { get; set; } = "";
        /// <summary>Character name for Player entities, empty otherwise.</summary>
        public string RenderName { get; set; } = "";
    }

    public enum EntityCategory
    {
        Monster,
        NPC,
        Player,
        Chest,
        AreaTransition,
        Portal,
        Monolith,
        Stash,
        Other
    }

    public class CombatSnapshot
    {
        public bool InCombat { get; set; }
        public int NearbyMonsterCount { get; set; }
        public int NearbyChaseCount { get; set; }
        public Vector2 PackCenter { get; set; }
        public Vector2 NearbyPackCenter { get; set; }
        public string LastAction { get; set; } = "";
        public string LastSkillAction { get; set; } = "";
        public long? BestTargetId { get; set; }
        public bool WantsToMove { get; set; }
    }

    public class ModeSnapshot
    {
        public string Name { get; set; } = "";
        public string Phase { get; set; } = "";
        public string Status { get; set; } = "";
        public string Decision { get; set; } = "";
        public Dictionary<string, object> Extra { get; set; } = new();
    }

    public class InteractionSnapshot
    {
        public bool IsBusy { get; set; }
        public string Status { get; set; } = "";
    }

    public class LootSnapshot
    {
        public bool HasLootNearby { get; set; }
        public int CandidateCount { get; set; }
        public int FailedCount { get; set; }
        public string LastSkipReason { get; set; } = "";
        public string NinjaBridgeStatus { get; set; } = "";
        public float LootRadius { get; set; }
        public List<LootCandidateSnapshot> Candidates { get; set; } = new();
        public int VisibleGroundLabelCount { get; set; }
    }

    public class LootCandidateSnapshot
    {
        public long EntityId { get; set; }
        public string ItemName { get; set; } = "";
        public float Distance { get; set; }
        public double ChaosValue { get; set; }
        public int InventorySlots { get; set; }
        public double ChaosPerSlot { get; set; }
        public Vector2 GridPos { get; set; }
    }

    /// <summary>
    /// Dumps current game terrain, exploration, and pathfinding state to image + JSON files.
    /// Used for offline testing and debugging — triggered by hotkey.
    /// </summary>
    public static class GameStateDump
    {
        /// <summary>
        /// Capture a full game state snapshot and write to the output directory.
        /// Returns the path to the generated files, or an error message.
        /// </summary>
        public static string Dump(
            int[][] pfGrid,
            int[][]? tgtGrid,
            float[][]? heightGrid,
            Vector2 playerGridPos,
            int blinkRange,
            ExplorationMap? exploration,
            NavigationSystem? navigation,
            GameStateSnapshot? snapshot,
            string areaName,
            string outputDir)
        {
            try
            {
                Directory.CreateDirectory(outputDir);

                var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var safeArea = SanitizeFilename(areaName);
                var baseName = $"{safeArea}_{timestamp}";

                var rows = pfGrid.Length;
                var cols = pfGrid[0].Length;
                var px = (int)playerGridPos.X;
                var py = (int)playerGridPos.Y;

                // ── Compute fresh exploration from current position (no prior seen state) ──
                var freshExploration = new ExplorationMap();
                freshExploration.Initialize(pfGrid, tgtGrid, playerGridPos, blinkRange);

                // Compute exploration path sequence (what the bot would visit)
                var explorePath = ComputeExplorationSequence(freshExploration, pfGrid, tgtGrid, playerGridPos, blinkRange);

                // ── Collect blink edges from pathfinding along the exploration route ──
                var blinkEdges = new List<BlinkEdgeInfo>();
                foreach (var seg in explorePath)
                {
                    if (seg.NavPath != null)
                    {
                        for (int i = 0; i < seg.NavPath.Count; i++)
                        {
                            if (seg.NavPath[i].Action == WaypointAction.Blink)
                            {
                                var from = i > 0 ? seg.NavPath[i - 1].Position : seg.NavPath[i].Position;
                                blinkEdges.Add(new BlinkEdgeInfo
                                {
                                    FromWorld = from,
                                    ToWorld = seg.NavPath[i].Position,
                                    FromGrid = new Vector2(from.X * Pathfinding.WorldToGrid, from.Y * Pathfinding.WorldToGrid),
                                    ToGrid = new Vector2(seg.NavPath[i].Position.X * Pathfinding.WorldToGrid, seg.NavPath[i].Position.Y * Pathfinding.WorldToGrid),
                                });
                            }
                        }
                    }
                }

                // ── Render image ──
                var imagePath = Path.Combine(outputDir, baseName + ".png");
                RenderImage(pfGrid, tgtGrid, rows, cols, px, py, freshExploration, explorePath, blinkEdges, snapshot, imagePath);

                // ── Write JSON ──
                var jsonPath = Path.Combine(outputDir, baseName + ".json");
                WriteJson(pfGrid, tgtGrid, heightGrid, rows, cols, playerGridPos, blinkRange, areaName,
                    freshExploration, exploration, explorePath, blinkEdges, navigation, snapshot, jsonPath);

                return $"Dumped to {baseName} (.png + .json)";
            }
            catch (Exception ex)
            {
                return $"Dump failed: {ex.Message}";
            }
        }

        // ═══════════════════════════════════════════════════
        // Exploration sequence computation
        // ═══════════════════════════════════════════════════

        /// <summary>
        /// Simulate the exploration target sequence the bot would follow from the current position.
        /// Returns ordered list of targets with paths. Stops at 95% coverage or 50 targets.
        /// </summary>
        private static List<ExploreStep> ComputeExplorationSequence(
            ExplorationMap exploration, int[][] pfGrid, int[][]? tgtGrid,
            Vector2 playerGridPos, int blinkRange)
        {
            var steps = new List<ExploreStep>();
            var currentPos = playerGridPos;
            const int maxSteps = 50;
            const float coverageTarget = 0.95f;

            for (int i = 0; i < maxSteps; i++)
            {
                if (exploration.ActiveBlobCoverage >= coverageTarget)
                    break;

                var target = exploration.GetNextExplorationTarget(currentPos);
                if (!target.HasValue)
                    break;

                // Try pathfinding to the target
                var worldStart = new Vector2(currentPos.X * Pathfinding.GridToWorld, currentPos.Y * Pathfinding.GridToWorld);
                var worldEnd = new Vector2(target.Value.X * Pathfinding.GridToWorld, target.Value.Y * Pathfinding.GridToWorld);

                List<NavWaypoint>? navPath = null;
                bool pathFailed = false;

                if (tgtGrid != null)
                {
                    var path = Pathfinding.FindPathWithBlinks(pfGrid, tgtGrid, worldStart, worldEnd, blinkRange);
                    if (path.Count > 0)
                    {
                        navPath = Pathfinding.SmoothNavPath(pfGrid, path);
                    }
                    else
                    {
                        pathFailed = true;
                        exploration.MarkRegionFailed(target.Value);
                    }
                }
                else
                {
                    var path = Pathfinding.FindPath(pfGrid, worldStart, worldEnd);
                    if (path.Count > 0)
                        navPath = path.Select(p => new NavWaypoint(p, WaypointAction.Walk)).ToList();
                    else
                    {
                        pathFailed = true;
                        exploration.MarkRegionFailed(target.Value);
                    }
                }

                if (pathFailed)
                {
                    // Retry up to 5 targets per step
                    bool found = false;
                    for (int retry = 0; retry < 5; retry++)
                    {
                        target = exploration.GetNextExplorationTarget(currentPos);
                        if (!target.HasValue) break;

                        worldEnd = new Vector2(target.Value.X * Pathfinding.GridToWorld, target.Value.Y * Pathfinding.GridToWorld);
                        var retryPath = tgtGrid != null
                            ? Pathfinding.FindPathWithBlinks(pfGrid, tgtGrid, worldStart, worldEnd, blinkRange)
                            : Pathfinding.FindPath(pfGrid, worldStart, worldEnd)
                                .Select(p => new NavWaypoint(p, WaypointAction.Walk)).ToList();

                        if (retryPath.Count > 0)
                        {
                            navPath = tgtGrid != null ? Pathfinding.SmoothNavPath(pfGrid, retryPath) : retryPath;
                            found = true;
                            break;
                        }
                        exploration.MarkRegionFailed(target.Value);
                    }
                    if (!found) break;
                }

                steps.Add(new ExploreStep
                {
                    StepIndex = i,
                    TargetGrid = target!.Value,
                    NavPath = navPath,
                    CoverageAtStart = exploration.ActiveBlobCoverage,
                });

                // Simulate "arriving" — mark cells near target as seen
                exploration.Update(target.Value);
                currentPos = target.Value;
            }

            return steps;
        }

        // ═══════════════════════════════════════════════════
        // Image rendering
        // ═══════════════════════════════════════════════════

        private static void RenderImage(
            int[][] pfGrid, int[][]? tgtGrid,
            int rows, int cols, int px, int py,
            ExplorationMap exploration,
            List<ExploreStep> explorePath,
            List<BlinkEdgeInfo> blinkEdges,
            GameStateSnapshot? snapshot,
            string outputPath)
        {
            // Crop to the walkable area with padding
            int minX = cols, maxX = 0, minY = rows, maxY = 0;
            for (int y = 0; y < rows; y++)
            {
                for (int x = 0; x < cols; x++)
                {
                    if (pfGrid[y][x] > 0 || (tgtGrid != null && tgtGrid[y][x] > 0))
                    {
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }
                }
            }

            const int pad = 10;
            minX = Math.Max(0, minX - pad);
            minY = Math.Max(0, minY - pad);
            maxX = Math.Min(cols - 1, maxX + pad);
            maxY = Math.Min(rows - 1, maxY + pad);

            var w = maxX - minX + 1;
            var h = maxY - minY + 1;

            using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);

            // Lock bits for fast pixel access
            var rect = new Rectangle(0, 0, w, h);
            var bmpData = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            var pixels = new int[w * h];

            // Walkable cells from exploration blob (for region coloring)
            var blobCells = new HashSet<Vector2i>();
            var cellRegionMap = new Dictionary<Vector2i, int>();
            if (exploration.ActiveBlob != null)
            {
                blobCells = exploration.ActiveBlob.WalkableCells;
                cellRegionMap = exploration.ActiveBlob.CellToRegion;
            }

            // Region colors (cycle through distinct hues)
            var regionColors = new Color[256];
            for (int i = 0; i < 256; i++)
            {
                var hue = (i * 37) % 360; // spread hues
                regionColors[i] = HsvToColor(hue, 0.3f, 0.7f);
            }

            // ── Layer 1: Terrain base ──
            for (int y = minY; y <= maxY; y++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    var pfVal = pfGrid[y][x];
                    var ix = x - minX;
                    var iy = y - minY;

                    Color c;
                    if (pfVal == 0)
                    {
                        // Check if jumpable gap
                        if (tgtGrid != null && tgtGrid[y][x] > 0)
                            c = Color.FromArgb(255, 40, 40, 100); // dark blue = jumpable gap
                        else
                            c = Color.FromArgb(255, 20, 20, 20); // near-black = wall/void
                    }
                    else
                    {
                        // Walkable — color by region if in blob, otherwise by pf value
                        var cell = new Vector2i(x, y);
                        if (cellRegionMap.TryGetValue(cell, out var regionIdx))
                        {
                            c = regionColors[regionIdx % 256];
                            // Darken fringe cells (1-2)
                            if (pfVal <= 2)
                                c = Color.FromArgb(255, c.R / 2, c.G / 2, c.B / 2);
                        }
                        else if (blobCells.Contains(cell))
                        {
                            // In blob but not in a region (too small)
                            c = Color.FromArgb(255, 60, 60, 60);
                        }
                        else
                        {
                            // Walkable but not reachable from player
                            var brightness = 30 + pfVal * 12;
                            c = Color.FromArgb(255, brightness, brightness + 5, brightness);
                        }
                    }

                    pixels[iy * w + ix] = c.ToArgb();
                }
            }

            // ── Layer 2: Exploration paths (draw lines between steps) ──
            var pathColors = new[] {
                Color.FromArgb(200, 255, 255, 0),   // yellow
                Color.FromArgb(200, 0, 255, 255),   // cyan
                Color.FromArgb(200, 255, 128, 0),   // orange
                Color.FromArgb(200, 128, 255, 0),   // lime
                Color.FromArgb(200, 255, 0, 255),   // magenta
            };

            for (int s = 0; s < explorePath.Count; s++)
            {
                var step = explorePath[s];
                if (step.NavPath == null) continue;

                var pathColor = pathColors[s % pathColors.Length];

                for (int i = 0; i < step.NavPath.Count - 1; i++)
                {
                    var fromGrid = step.NavPath[i].Position * Pathfinding.WorldToGrid;
                    var toGrid = step.NavPath[i + 1].Position * Pathfinding.WorldToGrid;

                    DrawLine(pixels, w, h, minX, minY,
                        (int)fromGrid.X, (int)fromGrid.Y,
                        (int)toGrid.X, (int)toGrid.Y,
                        step.NavPath[i + 1].Action == WaypointAction.Blink
                            ? Color.FromArgb(255, 255, 50, 50) // red for blink segments
                            : pathColor);
                }
            }

            // ── Layer 3: Blink edges (red X marks) ──
            foreach (var edge in blinkEdges)
            {
                var gx = (int)edge.ToGrid.X - minX;
                var gy = (int)edge.ToGrid.Y - minY;
                DrawCross(pixels, w, h, gx, gy, 3, Color.Red);
            }

            // ── Layer 4: Region centers ──
            if (exploration.ActiveBlob != null)
            {
                foreach (var region in exploration.ActiveBlob.Regions)
                {
                    var cx = (int)region.Center.X - minX;
                    var cy = (int)region.Center.Y - minY;
                    // Failed regions = red, explored > 80% = dim, else white
                    Color markerColor;
                    if (exploration.FailedRegions.Contains(region.Index))
                        markerColor = Color.FromArgb(255, 200, 0, 0);
                    else if (region.ExploredRatio > 0.8f)
                        markerColor = Color.FromArgb(255, 80, 80, 80);
                    else
                        markerColor = Color.White;

                    DrawCross(pixels, w, h, cx, cy, 4, markerColor);
                }
            }

            // ── Layer 5: Exploration step targets (numbered) ──
            for (int s = 0; s < explorePath.Count; s++)
            {
                var tx = (int)explorePath[s].TargetGrid.X - minX;
                var ty = (int)explorePath[s].TargetGrid.Y - minY;
                DrawFilledCircle(pixels, w, h, tx, ty, 3, Color.FromArgb(255, 255, 200, 0));
            }

            // ── Layer 6: Entities ──
            if (snapshot?.Entities != null)
            {
                foreach (var ent in snapshot.Entities)
                {
                    var ex = (int)ent.GridPos.X - minX;
                    var ey = (int)ent.GridPos.Y - minY;

                    Color entColor;
                    int entSize;
                    switch (ent.Category)
                    {
                        case EntityCategory.Player:
                            entColor = Color.FromArgb(255, 255, 255, 255); // white = other player
                            entSize = 5;
                            break;
                        case EntityCategory.Monster:
                            entColor = ent.IsAlive
                                ? Color.FromArgb(255, 255, 50, 50)    // red = alive monster
                                : Color.FromArgb(180, 120, 50, 50);   // dark red = corpse
                            entSize = ent.Rarity == "Unique" ? 4 : ent.Rarity == "Rare" ? 3 : 2;
                            break;
                        case EntityCategory.Chest:
                            entColor = Color.FromArgb(255, 255, 200, 0); // gold = chest
                            entSize = 3;
                            break;
                        case EntityCategory.AreaTransition:
                        case EntityCategory.Portal:
                            entColor = Color.FromArgb(255, 0, 200, 255); // cyan = transition/portal
                            entSize = 4;
                            break;
                        case EntityCategory.Monolith:
                            entColor = Color.FromArgb(255, 255, 0, 255); // magenta = monolith
                            entSize = 6;
                            break;
                        case EntityCategory.Stash:
                            entColor = Color.FromArgb(255, 200, 200, 255); // light blue = stash
                            entSize = 4;
                            break;
                        default:
                            entColor = Color.FromArgb(180, 180, 180, 180); // grey = other
                            entSize = 2;
                            break;
                    }

                    DrawFilledCircle(pixels, w, h, ex, ey, entSize, entColor);
                }

                // Draw pack center and nearby pack center if combat is active
                if (snapshot.Combat is { InCombat: true })
                {
                    var pcx = (int)snapshot.Combat.PackCenter.X - minX;
                    var pcy = (int)snapshot.Combat.PackCenter.Y - minY;
                    DrawCross(pixels, w, h, pcx, pcy, 5, Color.FromArgb(255, 255, 100, 100)); // light red X = pack center

                    var npcx = (int)snapshot.Combat.NearbyPackCenter.X - minX;
                    var npcy = (int)snapshot.Combat.NearbyPackCenter.Y - minY;
                    DrawCross(pixels, w, h, npcx, npcy, 4, Color.FromArgb(255, 255, 200, 50)); // yellow X = nearby pack center
                }
            }

            // ── Layer 6b: Loot candidates (bright green diamonds) ──
            if (snapshot?.Loot?.Candidates != null)
            {
                foreach (var loot in snapshot.Loot.Candidates)
                {
                    var lx = (int)loot.GridPos.X - minX;
                    var ly = (int)loot.GridPos.Y - minY;
                    // Diamond shape — 4 pixels in a + pattern
                    var lootColor = Color.FromArgb(255, 100, 255, 100);
                    DrawCross(pixels, w, h, lx, ly, 3, lootColor);
                    DrawFilledCircle(pixels, w, h, lx, ly, 2, lootColor);
                }
            }

            // ── Layer 7: Player position (large green marker) ──
            DrawFilledCircle(pixels, w, h, px - minX, py - minY, 5, Color.FromArgb(255, 0, 255, 0));

            // ── Layer 8: Network bubble radius circle ──
            DrawCircle(pixels, w, h, px - minX, py - minY, (int)Pathfinding.NetworkBubbleRadius,
                Color.FromArgb(100, 0, 200, 255));

            // Copy pixels to bitmap
            System.Runtime.InteropServices.Marshal.Copy(pixels, 0, bmpData.Scan0, pixels.Length);
            bmp.UnlockBits(bmpData);

            bmp.Save(outputPath, ImageFormat.Png);
        }

        // ═══════════════════════════════════════════════════
        // JSON output
        // ═══════════════════════════════════════════════════

        private static void WriteJson(
            int[][] pfGrid, int[][]? tgtGrid, float[][]? heightGrid,
            int rows, int cols,
            Vector2 playerGridPos, int blinkRange,
            string areaName,
            ExplorationMap freshExploration,
            ExplorationMap? liveExploration,
            List<ExploreStep> explorePath,
            List<BlinkEdgeInfo> blinkEdges,
            NavigationSystem? navigation,
            GameStateSnapshot? snapshot,
            string jsonPath)
        {
            var data = new Dictionary<string, object>
            {
                ["metadata"] = new Dictionary<string, object>
                {
                    ["timestamp"] = DateTime.Now.ToString("o"),
                    ["areaName"] = areaName,
                    ["playerGrid"] = new[] { playerGridPos.X, playerGridPos.Y },
                    ["playerWorld"] = new[] { playerGridPos.X * Pathfinding.GridToWorld, playerGridPos.Y * Pathfinding.GridToWorld },
                    ["gridRows"] = rows,
                    ["gridCols"] = cols,
                    ["blinkRange"] = blinkRange,
                    ["gridToWorld"] = Pathfinding.GridToWorld,
                    ["networkBubbleRadius"] = Pathfinding.NetworkBubbleRadius,
                },
                ["terrain"] = BuildTerrainSection(pfGrid, tgtGrid, rows, cols),
                ["exploration"] = BuildExplorationSection(freshExploration),
                ["liveExploration"] = liveExploration?.IsInitialized == true
                    ? BuildExplorationSection(liveExploration)
                    : null!,
                ["exploreSequence"] = explorePath.Select(s => new Dictionary<string, object>
                {
                    ["step"] = s.StepIndex,
                    ["targetGrid"] = new[] { s.TargetGrid.X, s.TargetGrid.Y },
                    ["coverageAtStart"] = Math.Round(s.CoverageAtStart, 4),
                    ["pathWaypoints"] = s.NavPath?.Count ?? 0,
                    ["blinkCount"] = s.NavPath?.Count(w => w.Action == WaypointAction.Blink) ?? 0,
                }).ToList(),
                ["blinkEdges"] = blinkEdges.Select(e => new Dictionary<string, object>
                {
                    ["fromGrid"] = new[] { Math.Round(e.FromGrid.X, 1), Math.Round(e.FromGrid.Y, 1) },
                    ["toGrid"] = new[] { Math.Round(e.ToGrid.X, 1), Math.Round(e.ToGrid.Y, 1) },
                }).ToList(),
            };

            // Add current navigation state if active
            if (navigation?.IsNavigating == true && navigation.CurrentNavPath != null)
            {
                data["currentNavigation"] = new Dictionary<string, object>
                {
                    ["isNavigating"] = true,
                    ["destination"] = navigation.Destination.HasValue
                        ? new[] { navigation.Destination.Value.X, navigation.Destination.Value.Y }
                        : null!,
                    ["waypointCount"] = navigation.CurrentNavPath.Count,
                    ["currentWaypoint"] = navigation.CurrentWaypointIndex,
                    ["blinkCount"] = navigation.BlinkCount,
                    ["lastPathfindMs"] = navigation.LastPathfindMs,
                    ["stuckRecoveries"] = navigation.StuckRecoveries,
                };
            }

            // Add live game state snapshot
            if (snapshot != null)
            {
                data["entities"] = snapshot.Entities.Select(e => new Dictionary<string, object>
                {
                    ["id"] = e.Id,
                    ["shortName"] = e.ShortName,
                    ["metadata"] = e.Metadata,
                    ["path"] = e.Path,
                    ["entityType"] = e.EntityType,
                    ["category"] = e.Category.ToString(),
                    ["gridPos"] = new[] { Math.Round(e.GridPos.X, 1), Math.Round(e.GridPos.Y, 1) },
                    ["distToPlayer"] = Math.Round(e.DistanceToPlayer, 1),
                    ["isAlive"] = e.IsAlive,
                    ["isTargetable"] = e.IsTargetable,
                    ["isHostile"] = e.IsHostile,
                    ["rarity"] = e.Rarity ?? "",
                    ["renderName"] = e.RenderName,
                }).ToList();

                if (snapshot.Combat != null)
                {
                    data["combat"] = new Dictionary<string, object>
                    {
                        ["inCombat"] = snapshot.Combat.InCombat,
                        ["nearbyMonsterCount"] = snapshot.Combat.NearbyMonsterCount,
                        ["nearbyChaseCount"] = snapshot.Combat.NearbyChaseCount,
                        ["packCenter"] = new[] { Math.Round(snapshot.Combat.PackCenter.X, 1), Math.Round(snapshot.Combat.PackCenter.Y, 1) },
                        ["nearbyPackCenter"] = new[] { Math.Round(snapshot.Combat.NearbyPackCenter.X, 1), Math.Round(snapshot.Combat.NearbyPackCenter.Y, 1) },
                        ["lastAction"] = snapshot.Combat.LastAction,
                        ["lastSkillAction"] = snapshot.Combat.LastSkillAction,
                        ["bestTargetId"] = snapshot.Combat.BestTargetId ?? 0L,
                        ["wantsToMove"] = snapshot.Combat.WantsToMove,
                    };
                }

                if (snapshot.Mode != null)
                {
                    var modeData = new Dictionary<string, object>
                    {
                        ["name"] = snapshot.Mode.Name,
                        ["phase"] = snapshot.Mode.Phase,
                        ["status"] = snapshot.Mode.Status,
                        ["decision"] = snapshot.Mode.Decision,
                    };
                    foreach (var kv in snapshot.Mode.Extra)
                        modeData[kv.Key] = kv.Value;
                    data["mode"] = modeData;
                }

                if (snapshot.Interaction != null)
                {
                    data["interaction"] = new Dictionary<string, object>
                    {
                        ["isBusy"] = snapshot.Interaction.IsBusy,
                        ["status"] = snapshot.Interaction.Status,
                    };
                }

                if (snapshot.Loot != null)
                {
                    data["loot"] = new Dictionary<string, object>
                    {
                        ["hasLootNearby"] = snapshot.Loot.HasLootNearby,
                        ["candidateCount"] = snapshot.Loot.CandidateCount,
                        ["failedCount"] = snapshot.Loot.FailedCount,
                        ["lastSkipReason"] = snapshot.Loot.LastSkipReason,
                        ["ninjaBridgeStatus"] = snapshot.Loot.NinjaBridgeStatus,
                        ["lootRadius"] = snapshot.Loot.LootRadius,
                        ["visibleGroundLabels"] = snapshot.Loot.VisibleGroundLabelCount,
                        ["candidates"] = snapshot.Loot.Candidates.Select(c => new Dictionary<string, object>
                        {
                            ["entityId"] = c.EntityId,
                            ["itemName"] = c.ItemName,
                            ["gridPos"] = new[] { Math.Round(c.GridPos.X, 1), Math.Round(c.GridPos.Y, 1) },
                            ["distance"] = Math.Round(c.Distance, 1),
                            ["chaosValue"] = Math.Round(c.ChaosValue, 1),
                            ["inventorySlots"] = c.InventorySlots,
                            ["chaosPerSlot"] = Math.Round(c.ChaosPerSlot, 1),
                        }).ToList(),
                    };
                }
            }

            var options = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(jsonPath, JsonSerializer.Serialize(data, options));
        }

        /// <summary>
        /// Build a compact terrain section. Instead of full grids (which are huge),
        /// store bounding box + run-length encoded walkable/gap data.
        /// </summary>
        private static Dictionary<string, object> BuildTerrainSection(int[][] pfGrid, int[][]? tgtGrid, int rows, int cols)
        {
            // Find bounding box of non-zero terrain
            int minX = cols, maxX = 0, minY = rows, maxY = 0;
            for (int y = 0; y < rows; y++)
            {
                for (int x = 0; x < cols; x++)
                {
                    if (pfGrid[y][x] > 0 || (tgtGrid != null && tgtGrid[y][x] > 0))
                    {
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }
                }
            }

            // Store cropped grids as flat arrays (much more compact than jagged JSON arrays)
            var cropW = maxX - minX + 1;
            var cropH = maxY - minY + 1;
            var pfFlat = new byte[cropW * cropH];
            byte[]? tgtFlat = tgtGrid != null ? new byte[cropW * cropH] : null;

            for (int y = 0; y < cropH; y++)
            {
                for (int x = 0; x < cropW; x++)
                {
                    pfFlat[y * cropW + x] = (byte)pfGrid[minY + y][minX + x];
                    if (tgtFlat != null)
                        tgtFlat[y * cropW + x] = (byte)tgtGrid![minY + y][minX + x];
                }
            }

            var result = new Dictionary<string, object>
            {
                ["cropOrigin"] = new[] { minX, minY },
                ["cropWidth"] = cropW,
                ["cropHeight"] = cropH,
                // Base64 encode for compactness — each byte is 0-5 grid value
                ["pathfindingGrid"] = Convert.ToBase64String(pfFlat),
            };

            if (tgtFlat != null)
                result["targetingGrid"] = Convert.ToBase64String(tgtFlat);

            return result;
        }

        private static Dictionary<string, object> BuildExplorationSection(ExplorationMap exploration)
        {
            var blobs = new List<Dictionary<string, object>>();
            foreach (var blob in exploration.Blobs)
            {
                var regions = blob.Regions.Select(r => new Dictionary<string, object>
                {
                    ["index"] = r.Index,
                    ["center"] = new[] { Math.Round(r.Center.X, 1), Math.Round(r.Center.Y, 1) },
                    ["cellCount"] = r.CellCount,
                    ["seenCount"] = r.SeenCount,
                    ["exploredRatio"] = Math.Round(r.ExploredRatio, 4),
                }).ToList();

                blobs.Add(new Dictionary<string, object>
                {
                    ["index"] = blob.Index,
                    ["walkableCells"] = blob.WalkableCells.Count,
                    ["seenCells"] = blob.SeenCells.Count,
                    ["coverage"] = Math.Round(blob.Coverage, 4),
                    ["regionCount"] = blob.Regions.Count,
                    ["regions"] = regions,
                });
            }

            return new Dictionary<string, object>
            {
                ["activeBlobIndex"] = exploration.ActiveBlobIndex,
                ["totalWalkableCells"] = exploration.TotalWalkableCells,
                ["failedRegions"] = exploration.FailedRegions.ToList(),
                ["blobs"] = blobs,
                ["transitions"] = exploration.KnownTransitions.Select(t => new Dictionary<string, object>
                {
                    ["gridPos"] = new[] { Math.Round(t.GridPos.X, 1), Math.Round(t.GridPos.Y, 1) },
                    ["name"] = t.Name,
                    ["sourceBlobIndex"] = t.SourceBlobIndex,
                    ["destBlobIndex"] = t.DestBlobIndex,
                }).ToList(),
            };
        }

        // ═══════════════════════════════════════════════════
        // Drawing helpers
        // ═══════════════════════════════════════════════════

        private static void DrawLine(int[] pixels, int w, int h, int offsetX, int offsetY,
            int x0, int y0, int x1, int y1, Color color)
        {
            x0 -= offsetX; y0 -= offsetY;
            x1 -= offsetX; y1 -= offsetY;

            var dx = Math.Abs(x1 - x0);
            var dy = Math.Abs(y1 - y0);
            var sx = x0 < x1 ? 1 : -1;
            var sy = y0 < y1 ? 1 : -1;
            var err = dx - dy;

            while (true)
            {
                if (x0 >= 0 && x0 < w && y0 >= 0 && y0 < h)
                    pixels[y0 * w + x0] = color.ToArgb();

                if (x0 == x1 && y0 == y1) break;
                var e2 = 2 * err;
                if (e2 > -dy) { err -= dy; x0 += sx; }
                if (e2 < dx) { err += dx; y0 += sy; }
            }
        }

        private static void DrawCross(int[] pixels, int w, int h, int cx, int cy, int size, Color color)
        {
            var argb = color.ToArgb();
            for (int d = -size; d <= size; d++)
            {
                var x1 = cx + d; var y1 = cy + d;
                var x2 = cx + d; var y2 = cy - d;
                if (x1 >= 0 && x1 < w && y1 >= 0 && y1 < h) pixels[y1 * w + x1] = argb;
                if (x2 >= 0 && x2 < w && y2 >= 0 && y2 < h) pixels[y2 * w + x2] = argb;
            }
        }

        private static void DrawFilledCircle(int[] pixels, int w, int h, int cx, int cy, int r, Color color)
        {
            var argb = color.ToArgb();
            for (int dy = -r; dy <= r; dy++)
            {
                for (int dx = -r; dx <= r; dx++)
                {
                    if (dx * dx + dy * dy <= r * r)
                    {
                        var x = cx + dx;
                        var y = cy + dy;
                        if (x >= 0 && x < w && y >= 0 && y < h)
                            pixels[y * w + x] = argb;
                    }
                }
            }
        }

        private static void DrawCircle(int[] pixels, int w, int h, int cx, int cy, int r, Color color)
        {
            var argb = color.ToArgb();
            // Bresenham circle
            int x = r, y = 0, err = 1 - r;
            while (x >= y)
            {
                SetPixelSafe(pixels, w, h, cx + x, cy + y, argb);
                SetPixelSafe(pixels, w, h, cx - x, cy + y, argb);
                SetPixelSafe(pixels, w, h, cx + x, cy - y, argb);
                SetPixelSafe(pixels, w, h, cx - x, cy - y, argb);
                SetPixelSafe(pixels, w, h, cx + y, cy + x, argb);
                SetPixelSafe(pixels, w, h, cx - y, cy + x, argb);
                SetPixelSafe(pixels, w, h, cx + y, cy - x, argb);
                SetPixelSafe(pixels, w, h, cx - y, cy - x, argb);
                y++;
                if (err < 0) err += 2 * y + 1;
                else { x--; err += 2 * (y - x) + 1; }
            }
        }

        private static void SetPixelSafe(int[] pixels, int w, int h, int x, int y, int argb)
        {
            if (x >= 0 && x < w && y >= 0 && y < h)
                pixels[y * w + x] = argb;
        }

        private static Color HsvToColor(float h, float s, float v)
        {
            var hi = (int)(h / 60f) % 6;
            var f = h / 60f - (int)(h / 60f);
            var p = v * (1 - s);
            var q = v * (1 - f * s);
            var t = v * (1 - (1 - f) * s);

            float r, g, b;
            switch (hi)
            {
                case 0: r = v; g = t; b = p; break;
                case 1: r = q; g = v; b = p; break;
                case 2: r = p; g = v; b = t; break;
                case 3: r = p; g = q; b = v; break;
                case 4: r = t; g = p; b = v; break;
                default: r = v; g = p; b = q; break;
            }

            return Color.FromArgb(255, (int)(r * 255), (int)(g * 255), (int)(b * 255));
        }

        private static string SanitizeFilename(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray())
                .Replace(' ', '_');
        }

        // ═══════════════════════════════════════════════════
        // Data types
        // ═══════════════════════════════════════════════════

        private class ExploreStep
        {
            public int StepIndex;
            public Vector2 TargetGrid;
            public List<NavWaypoint>? NavPath;
            public float CoverageAtStart;
        }

        private class BlinkEdgeInfo
        {
            public Vector2 FromWorld;
            public Vector2 ToWorld;
            public Vector2 FromGrid;
            public Vector2 ToGrid;
        }
    }
}
