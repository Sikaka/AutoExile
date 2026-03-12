using ExileCore;
using ExileCore.PoEMemory.Components;
using ExileCore.Shared.Enums;
using AutoExile.Mechanics;
using AutoExile.Systems;
using System.Numerics;
using Pathfinding = AutoExile.Systems.Pathfinding;

namespace AutoExile.Modes
{
    /// <summary>
    /// Map farming mode — explore the map, fight monsters, loot items.
    /// Combines ExplorationMap (coverage-based navigation), CombatSystem, and LootSystem.
    /// Activated via F5 hotkey or mode switcher. Designed for iterative development —
    /// start with basic explore+fight+loot, add mechanic support over time.
    /// </summary>
    public class MappingMode : IBotMode
    {
        public string Name => "Mapping";

        // ── Phase machine ──
        private MappingPhase _phase = MappingPhase.Idle;
        private DateTime _phaseStartTime = DateTime.Now;

        // ── Explore state ──
        private Vector2? _navTarget;           // current nav target (grid coords)
        private int _exploreTargetsVisited;
        private int _navFailures;              // consecutive pathfind failures
        private float _minCoverage = 0.70f;

        // ── Loot state ──
        private DateTime _lastLootScan = DateTime.MinValue;
        private const float LootScanIntervalMs = 500;
        private long _pendingLootEntityId;
        private string _pendingLootName = "";
        private double _pendingLootValue;

        // ── Clickable interactables (shrines, heist caches) ──
        private long _pendingInteractableId;
        private int _interactableClickAttempts;
        private const int MaxInteractableAttempts = 3;
        private const float InteractableDetectRadius = 120f;  // grid units — shrines visible within network bubble
        private readonly HashSet<long> _failedInteractables = new();

        // ── Mechanic state ──
        private IMapMechanic? _pendingMechanic;    // Detected, navigating to it
        private bool _mechanicActive;               // Mechanic owns the tick loop

        // ── Stats ──
        private DateTime _startTime;

        // ── Status (for overlay) ──
        public MappingPhase Phase => _phase;
        public string Status { get; private set; } = "";
        public string Decision { get; private set; } = "";
        public int ExploreTargetsVisited => _exploreTargetsVisited;
        public DateTime StartTime => _startTime;
        public Vector2? NavTarget => _navTarget;
        public bool IsPaused => _phase == MappingPhase.Paused;

        public enum MappingPhase
        {
            Idle,
            Exploring,
            Fighting,
            Looting,
            Paused,
            Complete,
        }

        public void OnEnter(BotContext ctx)
        {
            _startTime = DateTime.Now;
            _exploreTargetsVisited = 0;
            _navFailures = 0;
            _navTarget = null;
            Decision = "";

            var gc = ctx.Game;

            // Initialize exploration if not already done (area change handles it normally)
            if (!ctx.Exploration.IsInitialized)
            {
                var pfGrid = gc.IngameState?.Data?.RawPathfindingData;
                var tgtGrid = gc.IngameState?.Data?.RawTerrainTargetingData;
                if (pfGrid != null && gc.Player != null)
                {
                    var playerGrid = new Vector2(gc.Player.GridPosNum.X, gc.Player.GridPosNum.Y);
                    ctx.Exploration.Initialize(pfGrid, tgtGrid, playerGrid,
                        ctx.Settings.Build.BlinkRange.Value);
                }
            }

            // Enable combat with configured positioning
            var positioning = Enum.TryParse<CombatPositioning>(ctx.Settings.Build.DefaultPositioning.Value, out var pos)
                ? pos : CombatPositioning.Aggressive;
            ctx.Combat.SetProfile(new CombatProfile
            {
                Enabled = true,
                Positioning = positioning,
            });

            _phase = MappingPhase.Exploring;
            Status = "Started";
            ctx.Log($"Mapping started — {ctx.Exploration.TotalWalkableCells} cells, " +
                    $"{ctx.Exploration.ActiveBlob?.Regions.Count ?? 0} regions");
        }

        public void OnExit()
        {
            _phase = MappingPhase.Idle;
            _navTarget = null;
            _pendingMechanic = null;
            _mechanicActive = false;
            _pendingInteractableId = 0;
            _interactableClickAttempts = 0;
            _failedInteractables.Clear();
            Status = "Stopped";
            Decision = "";
        }

        public void Pause(BotContext ctx)
        {
            ctx.Navigation.Stop(ctx.Game);
            _phase = MappingPhase.Paused;
            Status = "PAUSED — F5 to resume, F5 again to stop";
        }

        public void Resume()
        {
            if (_phase == MappingPhase.Paused)
            {
                _phase = MappingPhase.Exploring;
                Status = "Resumed";
            }
        }

        public void Tick(BotContext ctx)
        {
            if (_phase == MappingPhase.Idle || _phase == MappingPhase.Paused) return;

            var gc = ctx.Game;
            if (gc?.Player == null || !gc.InGame || !gc.Player.IsAlive) return;

            var playerGrid = new Vector2(gc.Player.GridPosNum.X, gc.Player.GridPosNum.Y);

            // ── Combat (always runs — fires skills while exploring) ──
            // When enough monsters are nearby, pause navigation so combat positioning
            // controls movement (orbit, kite, etc). Resume when monsters are dead.
            if (!_mechanicActive)
                ctx.Combat.Tick(gc, ctx.Settings.Build);

            // Pause/resume navigation based on combat state
            if (!_mechanicActive && ctx.Combat.InCombat && ctx.Combat.WantsToMove)
            {
                // Combat wants to reposition — pause nav so combat controls movement
                if (ctx.Navigation.IsNavigating)
                    ctx.Navigation.Pause();
            }
            else if (!_mechanicActive && !ctx.Combat.InCombat && ctx.Navigation.IsPaused)
            {
                // Combat over — resume navigation
                ctx.Navigation.Resume(gc);
            }

            // ── Mechanics (in-map encounters like Ultimatum) ──
            if (_mechanicActive)
            {
                var result = ctx.Mechanics.TickActive(ctx);
                if (result == MechanicResult.InProgress)
                {
                    // Mechanic owns the loop — don't loot or explore
                    Status = $"Mechanic: {ctx.Mechanics.ActiveMechanic?.Status ?? "working"}";
                    return;
                }

                // Mechanic finished (complete/abandoned/failed)
                _mechanicActive = false;
                _pendingMechanic = null;
                Decision = $"Mechanic result: {result}";
                // Fall through to normal loot/explore
            }

            // Check for pending mechanic to start (navigate to it)
            if (_pendingMechanic != null && !_pendingMechanic.IsComplete)
            {
                // Let the mechanic handle its own navigation
                ctx.Mechanics.SetActive(_pendingMechanic);
                _mechanicActive = true;
                Status = $"Starting mechanic: {_pendingMechanic.Name}";
                return;
            }

            // ── Mechanic detection (periodic scan for in-map encounters) ──
            // Runs before loot so in-progress encounters get immediate priority
            var detectedMechanic = ctx.Mechanics.DetectAndPrioritize(ctx);
            if (detectedMechanic != null && _pendingMechanic == null && !_mechanicActive)
            {
                _pendingMechanic = detectedMechanic;
                Decision = $"Found mechanic: {detectedMechanic.Name}";
                // Start immediately on next iteration (top of tick)
                return;
            }

            // ── Looting (between combat and exploration) ──
            if ((DateTime.Now - _lastLootScan).TotalMilliseconds >= LootScanIntervalMs)
            {
                ctx.Loot.Scan(gc);
                _lastLootScan = DateTime.Now;
            }

            // ── Tick interaction system if busy (loot pickups, shrine clicks, etc.) ──
            if (ctx.Interaction.IsBusy)
            {
                var result = ctx.Interaction.Tick(gc);

                // Handle loot pickup results
                if (result == InteractionResult.Succeeded && _pendingLootName.Length > 0)
                {
                    ctx.LootTracker.RecordItem(_pendingLootName, _pendingLootValue);
                    _pendingLootName = "";
                    ctx.Loot.Scan(gc);
                    _lastLootScan = DateTime.Now;
                }
                else if (result == InteractionResult.Failed && _pendingLootEntityId != 0 && _pendingLootName.Length > 0)
                {
                    ctx.Loot.MarkFailed(_pendingLootEntityId);
                    _pendingLootName = "";
                }

                // Handle interactable (shrine/cache) results
                if (result != InteractionResult.InProgress && result != InteractionResult.None
                    && _pendingInteractableId != 0)
                {
                    var prev = FindEntityById(gc, _pendingInteractableId);
                    if (prev != null && prev.IsTargetable && !IsInteractableDone(prev))
                    {
                        _interactableClickAttempts++;
                        if (_interactableClickAttempts >= MaxInteractableAttempts)
                        {
                            _failedInteractables.Add(_pendingInteractableId);
                            ctx.Log($"[Interactable] Blacklisted after {MaxInteractableAttempts} attempts");
                        }
                    }
                    else
                    {
                        _interactableClickAttempts = 0;
                    }
                    _pendingInteractableId = 0;
                }

                if (ctx.Interaction.IsBusy)
                {
                    if (_pendingLootName.Length > 0)
                    {
                        _phase = MappingPhase.Looting;
                        Status = $"Looting: {_pendingLootName}";
                    }
                    else if (_pendingInteractableId != 0)
                    {
                        Status = $"Clicking interactable";
                    }
                    return;
                }
            }

            // ── Looting (pick up nearby items) ──
            if (ctx.Loot.HasLootNearby && !ctx.Interaction.IsBusy)
            {
                var (wasInRadius, candidate) = ctx.Loot.PickupNext(ctx.Interaction, ctx.Navigation);
                if (candidate != null && ctx.Interaction.IsBusy)
                {
                    _pendingLootEntityId = candidate.Entity.Id;
                    _pendingLootName = candidate.ItemName;
                    _pendingLootValue = candidate.ChaosValue;
                    _phase = MappingPhase.Looting;
                    Status = $"Looting: {candidate.ItemName}";
                    return;
                }
            }

            // ── Clickable interactables (shrines, heist caches — click as we pass by) ──
            if (!ctx.Interaction.IsBusy)
            {
                var target = FindNearbyInteractable(gc, playerGrid);
                if (target != null)
                {
                    if (ctx.Interaction.InteractWithEntity(target, ctx.Navigation))
                    {
                        _pendingInteractableId = target.Id;
                        _interactableClickAttempts = 0;
                        Status = $"Clicking: {target.RenderName ?? target.Path}";
                        ctx.Log($"[Interactable] Clicking {target.Path} dist={target.DistancePlayer:F0}");
                        return;
                    }
                }
            }

            // ── Combat hold — don't explore while fighting ──
            if (ctx.Combat.InCombat && ctx.Combat.WantsToMove)
            {
                _phase = MappingPhase.Fighting;
                Status = $"Fighting: {ctx.Combat.NearbyMonsterCount} monsters ({ctx.Combat.LastSkillAction})";
                return;
            }

            // ── Exploration ──
            if (_phase == MappingPhase.Complete)
                return; // Don't overwrite Complete state

            if (_phase == MappingPhase.Looting || _phase == MappingPhase.Fighting)
            {
                Decision = _phase == MappingPhase.Fighting
                    ? "Combat cleared, resuming exploration"
                    : "Loot cleared, resuming exploration";
            }

            _phase = ctx.Combat.InCombat ? MappingPhase.Fighting : MappingPhase.Exploring;
            var coverage = ctx.Exploration.ActiveBlobCoverage;
            var combatInfo = ctx.Combat.InCombat
                ? $" | Fighting: {ctx.Combat.NearbyMonsterCount} ({ctx.Combat.LastSkillAction})"
                : "";
            Status = $"Coverage: {coverage:P1}{combatInfo}";

            // Stuck abandonment
            if (ctx.Navigation.IsNavigating && !ctx.Navigation.IsPaused &&
                ctx.Navigation.StuckRecoveries >= 5 && _navTarget.HasValue)
            {
                ctx.Log($"Stuck {ctx.Navigation.StuckRecoveries}x, abandoning target");
                ctx.Exploration.MarkRegionFailed(_navTarget.Value);
                ctx.Navigation.Stop(gc);
                _navTarget = null;
                Decision = $"Abandoned stuck target ({ctx.Exploration.FailedRegions.Count} failed regions)";
            }

            // Pick next target when idle (not navigating, or paused nav just resumed)
            if (!ctx.Navigation.IsNavigating)
            {
                if (coverage >= _minCoverage)
                {
                    // Check if there are still meaningful unexplored regions
                    var nextTarget = ctx.Exploration.GetNextExplorationTarget(playerGrid);
                    if (nextTarget.HasValue)
                    {
                        // Keep exploring even past threshold if targets remain
                        NavigateToTarget(ctx, gc, playerGrid, nextTarget.Value);
                    }
                    else
                    {
                        _phase = MappingPhase.Complete;
                        var elapsed = (DateTime.Now - _startTime).TotalSeconds;
                        Status = $"COMPLETE — {coverage:P1} coverage in {elapsed:F0}s";
                        Decision = $"{ctx.Exploration.FailedRegions.Count} unreachable regions";
                    }
                }
                else
                {
                    NavigateToNextExploreTarget(ctx, gc, playerGrid);
                }
            }
        }

        private void NavigateToTarget(BotContext ctx, GameController gc, Vector2 playerGrid, Vector2 targetGrid)
        {
            var worldTarget = targetGrid * Pathfinding.GridToWorld;
            if (ctx.Navigation.NavigateTo(gc, worldTarget))
            {
                _navTarget = targetGrid;
                _exploreTargetsVisited++;
                _navFailures = 0;
                Decision = $"Exploring #{_exploreTargetsVisited} @ ({targetGrid.X:F0},{targetGrid.Y:F0})";
            }
            else
            {
                ctx.Exploration.MarkRegionFailed(targetGrid);
                _navFailures++;
                Decision = $"Pathfind failed to ({targetGrid.X:F0},{targetGrid.Y:F0})";
            }
        }

        private void NavigateToNextExploreTarget(BotContext ctx, GameController gc, Vector2 playerGrid)
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                var target = ctx.Exploration.GetNextExplorationTarget(playerGrid);
                if (!target.HasValue)
                {
                    var coverage = ctx.Exploration.ActiveBlobCoverage;
                    var elapsed = (DateTime.Now - _startTime).TotalSeconds;
                    _phase = MappingPhase.Complete;
                    Status = $"COMPLETE — {coverage:P1} coverage in {elapsed:F0}s";
                    Decision = $"No more targets ({ctx.Exploration.FailedRegions.Count} unreachable)";
                    return;
                }

                var worldTarget = target.Value * Pathfinding.GridToWorld;
                if (ctx.Navigation.NavigateTo(gc, worldTarget))
                {
                    _navTarget = target.Value;
                    _exploreTargetsVisited++;
                    _navFailures = 0;
                    Decision = $"Exploring #{_exploreTargetsVisited} @ ({target.Value.X:F0},{target.Value.Y:F0})";
                    return;
                }

                ctx.Exploration.MarkRegionFailed(target.Value);
                _navFailures++;
                ctx.Log($"Pathfind failed to ({target.Value.X:F0},{target.Value.Y:F0}), marking unreachable");
            }

            Decision = $"5 consecutive pathfind failures ({ctx.Exploration.FailedRegions.Count} total unreachable)";
        }

        /// <summary>
        /// Find the nearest clickable interactable (shrine or heist cache) within detection radius.
        /// </summary>
        private ExileCore.PoEMemory.MemoryObjects.Entity? FindNearbyInteractable(GameController gc, Vector2 playerGrid)
        {
            ExileCore.PoEMemory.MemoryObjects.Entity? best = null;
            float bestDist = float.MaxValue;

            // Shrines
            foreach (var entity in gc.EntityListWrapper.ValidEntitiesByType[EntityType.Shrine])
            {
                if (!entity.IsTargetable) continue;
                if (_failedInteractables.Contains(entity.Id)) continue;
                if (!entity.TryGetComponent<Shrine>(out var shrine) || !shrine.IsAvailable) continue;

                var dist = Vector2.Distance(playerGrid, new Vector2(entity.GridPosNum.X, entity.GridPosNum.Y));
                if (dist < bestDist && dist <= InteractableDetectRadius)
                {
                    bestDist = dist;
                    best = entity;
                }
            }

            // Heist caches (Smuggler's Cache — chest type, path contains LeagueHeist)
            foreach (var entity in gc.EntityListWrapper.ValidEntitiesByType[EntityType.Chest])
            {
                if (!entity.IsTargetable) continue;
                if (_failedInteractables.Contains(entity.Id)) continue;
                if (!entity.Path.Contains("LeagueHeist/HeistSmuggler")) continue;
                if (entity.TryGetComponent<Chest>(out var chest) && chest.IsOpened) continue;

                var dist = Vector2.Distance(playerGrid, new Vector2(entity.GridPosNum.X, entity.GridPosNum.Y));
                if (dist < bestDist && dist <= InteractableDetectRadius)
                {
                    bestDist = dist;
                    best = entity;
                }
            }

            // Crafting recipes (click to unlock)
            foreach (var entity in gc.EntityListWrapper.ValidEntitiesByType[EntityType.IngameIcon])
            {
                if (!entity.IsTargetable) continue;
                if (_failedInteractables.Contains(entity.Id)) continue;
                if (!entity.Path.Contains("CraftingUnlocks/RecipeUnlock")) continue;

                var dist = Vector2.Distance(playerGrid, new Vector2(entity.GridPosNum.X, entity.GridPosNum.Y));
                if (dist < bestDist && dist <= InteractableDetectRadius)
                {
                    bestDist = dist;
                    best = entity;
                }
            }

            return best;
        }

        /// <summary>
        /// Check if an interactable has been successfully activated (shrine claimed or chest opened).
        /// </summary>
        private static bool IsInteractableDone(ExileCore.PoEMemory.MemoryObjects.Entity entity)
        {
            if (entity.TryGetComponent<Shrine>(out var shrine))
                return !shrine.IsAvailable;
            if (entity.TryGetComponent<Chest>(out var chest))
                return chest.IsOpened;
            return !entity.IsTargetable;
        }

        /// <summary>
        /// Find an entity by ID from the valid entity list.
        /// </summary>
        private static ExileCore.PoEMemory.MemoryObjects.Entity? FindEntityById(GameController gc, long entityId)
        {
            foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
            {
                if (entity.Id == entityId) return entity;
            }
            return null;
        }

        public void Render(BotContext ctx)
        {
            if (_phase == MappingPhase.Idle) return;

            var g = ctx.Graphics;
            var gc = ctx.Game;
            if (g == null || gc?.Player == null || !gc.InGame) return;

            var cam = gc.IngameState.Camera;
            var playerZ = gc.Player.PosNum.Z;
            var playerGrid = gc.Player.GridPosNum;
            var blob = ctx.Exploration.ActiveBlob;

            // ═══ HUD Panel ═══
            var hudX = 20f;
            var hudY = 200f;
            var lineH = 18f;

            var titleColor = _phase == MappingPhase.Complete ? SharpDX.Color.LimeGreen
                : _phase == MappingPhase.Paused ? SharpDX.Color.Yellow
                : SharpDX.Color.Cyan;
            g.DrawText("=== MAPPING ===", new Vector2(hudX, hudY), titleColor);
            hudY += lineH;

            var phaseColor = _phase switch
            {
                MappingPhase.Fighting => SharpDX.Color.Red,
                MappingPhase.Looting => SharpDX.Color.LimeGreen,
                MappingPhase.Exploring => SharpDX.Color.Yellow,
                MappingPhase.Complete => SharpDX.Color.LimeGreen,
                MappingPhase.Paused => SharpDX.Color.Yellow,
                _ => SharpDX.Color.White,
            };
            g.DrawText($"Phase: {_phase}", new Vector2(hudX, hudY), phaseColor);
            hudY += lineH;
            g.DrawText(Status, new Vector2(hudX, hudY), SharpDX.Color.White);
            hudY += lineH;
            g.DrawText($"Decision: {Decision}", new Vector2(hudX, hudY), SharpDX.Color.Gray);
            hudY += lineH;

            // Coverage bar
            if (blob != null)
            {
                var coverage = blob.Coverage;
                var barWidth = 200f;
                var barHeight = 14f;
                var barX = hudX;
                var barY = hudY;

                g.DrawBox(new SharpDX.RectangleF(barX, barY, barWidth, barHeight),
                    new SharpDX.Color(40, 40, 40, 200));
                var fillWidth = barWidth * Math.Min(coverage, 1f);
                var fillColor = coverage >= _minCoverage
                    ? new SharpDX.Color(0, 200, 0, 200)
                    : new SharpDX.Color(200, 200, 0, 200);
                g.DrawBox(new SharpDX.RectangleF(barX, barY, fillWidth, barHeight), fillColor);
                var targetX = barX + barWidth * _minCoverage;
                g.DrawLine(new Vector2(targetX, barY), new Vector2(targetX, barY + barHeight), 2f, SharpDX.Color.White);
                g.DrawText($"{coverage:P1} / {_minCoverage:P0}",
                    new Vector2(barX + barWidth + 8, barY - 1), SharpDX.Color.White);
                hudY += barHeight + 4;

                g.DrawText($"Cells: {blob.SeenCells.Count}/{blob.WalkableCells.Count} | Regions: {blob.Regions.Count}",
                    new Vector2(hudX, hudY), SharpDX.Color.Gray);
                hudY += lineH;
            }

            g.DrawText($"Targets visited: {_exploreTargetsVisited} | Failed regions: {ctx.Exploration.FailedRegions.Count}",
                new Vector2(hudX, hudY), SharpDX.Color.Gray);
            hudY += lineH;

            // Loot info
            if (ctx.Loot.LootableCount > 0)
            {
                g.DrawText($"Loot: {ctx.Loot.LootableCount} items nearby",
                    new Vector2(hudX, hudY), SharpDX.Color.LimeGreen);
                hudY += lineH;
            }

            // Navigation
            if (ctx.Navigation.IsNavigating)
            {
                g.DrawText($"Nav: wp {ctx.Navigation.CurrentWaypointIndex + 1}/{ctx.Navigation.CurrentNavPath.Count} " +
                           $"stuck={ctx.Navigation.StuckRecoveries} pathfind={ctx.Navigation.LastPathfindMs}ms",
                    new Vector2(hudX, hudY), SharpDX.Color.Gray);
                hudY += lineH;
            }

            // Combat
            if (ctx.Combat.InCombat)
            {
                g.DrawText($"Combat: {ctx.Combat.NearbyMonsterCount} monsters | {ctx.Combat.LastSkillAction}",
                    new Vector2(hudX, hudY), SharpDX.Color.Red);
                hudY += lineH;
            }

            var elapsed = (DateTime.Now - _startTime).TotalSeconds;
            g.DrawText($"Time: {elapsed:F0}s", new Vector2(hudX, hudY), SharpDX.Color.DarkGray);
            hudY += lineH;

            // Top unexplored regions
            if (blob != null)
            {
                hudY += 4f;
                g.DrawText("Unexplored Regions:", new Vector2(hudX, hudY), SharpDX.Color.White);
                hudY += lineH;

                int regionShown = 0;
                foreach (var region in blob.Regions.OrderBy(r => r.ExploredRatio))
                {
                    if (regionShown >= 5) break;
                    if (region.ExploredRatio >= 0.8f) continue;

                    var isNavTarget = _navTarget.HasValue &&
                        Vector2.Distance(region.Center, _navTarget.Value) < 40f;
                    var color = isNavTarget ? SharpDX.Color.Cyan
                        : new SharpDX.Color((byte)255, (byte)(255 * region.ExploredRatio), (byte)0, (byte)255);
                    var marker = isNavTarget ? ">>>" : "   ";
                    var dist = Vector2.Distance(playerGrid, region.Center);
                    g.DrawText($"{marker} R{region.Index}: {region.ExploredRatio:P0} ({region.CellCount} cells) d={dist:F0}",
                        new Vector2(hudX, hudY), color);
                    hudY += lineH - 2;
                    regionShown++;
                }
            }

            // Transitions
            if (ctx.Exploration.KnownTransitions.Count > 0)
            {
                hudY += 4f;
                g.DrawText($"Transitions: {ctx.Exploration.KnownTransitions.Count}", new Vector2(hudX, hudY), SharpDX.Color.White);
                hudY += lineH;
                foreach (var t in ctx.Exploration.KnownTransitions)
                {
                    g.DrawText($"  {t.Name} ({t.GridPos.X:F0},{t.GridPos.Y:F0})",
                        new Vector2(hudX, hudY), SharpDX.Color.LightGray);
                    hudY += lineH - 2;
                }
            }

            // Mechanics info
            if (ctx.Mechanics.ActiveMechanic != null || ctx.Mechanics.DetectedMechanics.Count > 0
                || ctx.Mechanics.CompletedMechanics.Count > 0)
            {
                hudY += 4f;
                g.DrawText("Mechanics:", new Vector2(hudX, hudY), SharpDX.Color.White);
                hudY += lineH;

                if (ctx.Mechanics.ActiveMechanic != null)
                {
                    g.DrawText($"  Active: {ctx.Mechanics.ActiveMechanic.Name} — {ctx.Mechanics.ActiveMechanic.Status}",
                        new Vector2(hudX, hudY), SharpDX.Color.Orange);
                    hudY += lineH;
                }

                foreach (var m in ctx.Mechanics.DetectedMechanics)
                {
                    g.DrawText($"  Pending: {m.Name}", new Vector2(hudX, hudY), SharpDX.Color.Yellow);
                    hudY += lineH - 2;
                }

                foreach (var m in ctx.Mechanics.CompletedMechanics)
                {
                    g.DrawText($"  Done: {m.Name}", new Vector2(hudX, hudY), SharpDX.Color.LimeGreen);
                    hudY += lineH - 2;
                }
            }

            hudY += 6f;
            g.DrawText("[F5] pause/resume | Running=ON for movement", new Vector2(hudX, hudY), SharpDX.Color.DarkGray);

            // ═══ World Overlays ═══

            // Current nav target
            if (_navTarget.HasValue && ctx.Navigation.IsNavigating)
            {
                var targetWorld = new Vector3(
                    _navTarget.Value.X * Pathfinding.GridToWorld,
                    _navTarget.Value.Y * Pathfinding.GridToWorld, playerZ);
                var targetScreen = cam.WorldToScreen(targetWorld);
                if (targetScreen.X > -200 && targetScreen.X < 2400)
                {
                    g.DrawCircleInWorld(targetWorld, 30f, SharpDX.Color.Cyan, 2f);
                    g.DrawText("EXPLORE", targetScreen + new Vector2(-20, -25), SharpDX.Color.Cyan);
                }
            }

            // Navigation path
            if (ctx.Navigation.IsNavigating)
            {
                var path = ctx.Navigation.CurrentNavPath;
                for (int i = ctx.Navigation.CurrentWaypointIndex; i < path.Count - 1; i++)
                {
                    var from = cam.WorldToScreen(new Vector3(path[i].Position.X, path[i].Position.Y, playerZ));
                    var to = cam.WorldToScreen(new Vector3(path[i + 1].Position.X, path[i + 1].Position.Y, playerZ));
                    if (from.X < -200 || from.X > 2400 || to.X < -200 || to.X > 2400) continue;

                    var isBlink = path[i + 1].Action == WaypointAction.Blink;
                    var pathColor = isBlink ? SharpDX.Color.Magenta : SharpDX.Color.Orange;
                    g.DrawLine(from, to, isBlink ? 3f : 2f, pathColor);
                }
            }

            // Network bubble
            var playerWorld = gc.Player.PosNum;
            var bubbleRadius = Pathfinding.NetworkBubbleRadius * Pathfinding.GridToWorld;
            g.DrawCircleInWorld(new Vector3(playerWorld.X, playerWorld.Y, playerZ),
                bubbleRadius, new SharpDX.Color(100, 100, 255, 30), 1f);

            // Region centers (unexplored)
            if (blob != null)
            {
                foreach (var region in blob.Regions)
                {
                    if (region.ExploredRatio >= 0.8f) continue;

                    var regionWorld = new Vector3(
                        region.Center.X * Pathfinding.GridToWorld,
                        region.Center.Y * Pathfinding.GridToWorld, playerZ);
                    var regionScreen = cam.WorldToScreen(regionWorld);
                    if (regionScreen.X < -200 || regionScreen.X > 2400 || regionScreen.Y < -200 || regionScreen.Y > 1500)
                        continue;

                    var alpha = (byte)(255 * (1f - region.ExploredRatio));
                    var regionColor = new SharpDX.Color((byte)255, alpha, (byte)0, (byte)(alpha * 0.6f));
                    g.DrawCircleInWorld(regionWorld, 20f, regionColor, 1f);
                    g.DrawText($"{region.ExploredRatio:P0}", regionScreen + new Vector2(-12, -8),
                        new SharpDX.Color((byte)255, alpha, (byte)0, alpha));
                }
            }

            // Transition markers
            foreach (var t in ctx.Exploration.KnownTransitions)
            {
                var tWorld = new Vector3(
                    t.GridPos.X * Pathfinding.GridToWorld,
                    t.GridPos.Y * Pathfinding.GridToWorld, playerZ);
                var tScreen = cam.WorldToScreen(tWorld);
                if (tScreen.X < -200 || tScreen.X > 2400) continue;

                g.DrawCircleInWorld(tWorld, 25f, SharpDX.Color.Purple, 2f);
                g.DrawText(t.Name, tScreen + new Vector2(-20, -20), SharpDX.Color.Purple);
            }

            // Active mechanic overlay
            if (ctx.Mechanics.ActiveMechanic != null)
            {
                ctx.Mechanics.ActiveMechanic.Render(ctx);
            }
        }
    }
}
