using ExileCore;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using ExileCore.Shared.Nodes;
using AutoExile.Mechanics;
using AutoExile.Modes.Shared;
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
        private readonly LootPickupTracker _lootTracker = new();

        // ── Clickable interactables (shrines, heist caches) ──
        private long _pendingInteractableId;
        private int _interactableClickAttempts;
        private const int MaxInteractableAttempts = 3;
        private const float InteractableDetectRadius = 120f;  // grid units — Required interactables route toward
        private const float InteractableOptionalRadius = 25f; // grid units — Optional interactables only when passing by
        private string _pendingInteractableName = "";
        private readonly HashSet<long> _failedInteractables = new();

        // ── Mechanic state ──
        private IMapMechanic? _pendingMechanic;    // Detected, navigating to it
        private bool _mechanicActive;               // Mechanic owns the tick loop

        // ── Exit portal (wish zones, sub-zones with return portals) ──
        private Entity? _exitPortal;
        private DateTime _lastExitClickTime = DateTime.MinValue;
        private DateTime _zoneEnteredAt = DateTime.Now;
        private long _lastZoneHash;
        private const float MinZoneSecondsBeforeExit = 20f; // don't exit sub-zones before clearing them

        // ── Sub-zone state (wish zones, harvest groves, etc.) ──
        private bool _isInSubZone;
        private bool _expectingSubZone;       // set when TriggersSubZone mechanic activates
        private long _parentZoneHash;
        private MechanicsSnapshot? _parentMechanicsSnapshot;

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

        // ── Boss rush state ──
        private Vector2? _bossTarget;          // grid position of boss room tile
        private bool _bossRushComplete;        // boss rush done, switch to normal explore
        private bool _bossTargetResolved;      // true after we've attempted to look up boss tiles
        private string _bossRushDebug = "";    // debug info for overlay

        // ── Boss kill verification ──
        private bool _bossKillVerified;              // boss confirmed dead
        private List<Vector2>? _bossKillCheckPositions; // boss tile grid positions to check
        private DateTime? _bossAreaArrivalTime;      // when player entered boss radius
        private const float BossAreaDwellSeconds = 3f; // wait for entities to load

        // ── Priority targets (minimap icon routing) ──
        private Vector2? _priorityTarget;            // current priority nav target (grid)
        private string _priorityTargetReason = "";   // why we're going there
        private readonly HashSet<long> _visitedIconIds = new(); // minimap icon entity IDs already handled

        public enum MappingPhase
        {
            Idle,
            RushingBoss, // navigating to boss room, fighting on the way but not stopping
            Exploring,
            Fighting,
            Looting,
            Paused,
            Exiting,    // navigating to and clicking exit portal (wish zones)
            Complete,
        }

        public void OnEnter(BotContext ctx)
        {
            _startTime = DateTime.Now;
            _zoneEnteredAt = DateTime.Now;
            _lastZoneHash = ctx.Game?.IngameState?.Data?.CurrentAreaHash ?? 0;
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
            ModeHelpers.EnableDefaultCombat(ctx);

            // Boss rush — deferred to first Tick since TileMap may not be loaded yet at OnEnter time
            _bossTarget = null;
            _bossRushComplete = false;
            _bossTargetResolved = false;
            _bossRushDebug = "";

            var rushBossEnabled = ctx.Settings.Mapping.RushBoss.Value;
            ctx.Log($"[Mapping] OnEnter: rushBoss={rushBossEnabled} tileMapLoaded={ctx.TileMap.IsLoaded} area={gc.Area?.CurrentArea?.Name}");

            if (rushBossEnabled)
            {
                _phase = MappingPhase.RushingBoss;
                Status = "Waiting for tile data to resolve boss position...";
                _bossRushDebug = "Pending tile map load";
            }
            else
            {
                _phase = MappingPhase.Exploring;
                Status = "Started";
            }

            ctx.Log($"Mapping — {ctx.Exploration.TotalWalkableCells} cells, " +
                    $"{ctx.Exploration.ActiveBlob?.Regions.Count ?? 0} regions");
        }

        public void OnExit()
        {
            _phase = MappingPhase.Idle;
            _navTarget = null;
            _bossTarget = null;
            _bossRushComplete = false;
            _bossTargetResolved = false;
            _bossRushDebug = "";
            _bossKillVerified = false;
            _bossKillCheckPositions = null;
            _bossAreaArrivalTime = null;
            _priorityTarget = null;
            _priorityTargetReason = "";
            _visitedIconIds.Clear();
            _pendingMechanic = null;
            _mechanicActive = false;
            _pendingInteractableId = 0;
            _pendingInteractableName = "";
            _interactableClickAttempts = 0;
            _failedInteractables.Clear();
            _exitPortal = null;
            _lastZoneHash = 0;
            _isInSubZone = false;
            _expectingSubZone = false;
            _parentZoneHash = 0;
            _parentMechanicsSnapshot = null;
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

            // Detect zone changes (sub-zone transitions like wish zones)
            // Use area hash — area name can be identical between parent map and sub-zone
            var currentHash = gc.IngameState?.Data?.CurrentAreaHash ?? 0;
            if (currentHash != 0 && currentHash != _lastZoneHash)
            {
                if (_lastZoneHash != 0)
                {
                    ctx.Log($"[Mapping] Zone changed: hash {_lastZoneHash} -> {currentHash}, phase={_phase}, subZone={_isInSubZone}");

                    if (_isInSubZone && currentHash == _parentZoneHash)
                    {
                        // ── Returning to parent map from sub-zone ──
                        ctx.Log("[Mapping] Returning to parent map from sub-zone");
                        _isInSubZone = false;
                        _expectingSubZone = false;
                        _parentZoneHash = 0;

                        if (_parentMechanicsSnapshot != null)
                        {
                            ctx.Mechanics.RestoreSnapshot(_parentMechanicsSnapshot);
                            _parentMechanicsSnapshot = null;
                            ctx.Log("[Mapping] Restored parent map mechanics state");
                        }

                        _mechanicActive = false;
                        _pendingMechanic = null;
                    }
                    else if (_expectingSubZone)
                    {
                        // ── Entering sub-zone (mechanic flagged TriggersSubZone) ──
                        // The flag was set when the mechanic activated. The mechanic may have
                        // already timed out or completed by now — that's fine, we still need
                        // to treat this zone as a sub-zone.
                        var triggerMechName = ctx.Mechanics.ActiveMechanic?.Name
                            ?? _pendingMechanic?.Name ?? "Unknown";
                        ctx.Log($"[Mapping] Entering sub-zone (from {triggerMechName}), snapshotting mechanics");

                        // Snapshot parent mechanics state before resetting
                        _parentMechanicsSnapshot = ctx.Mechanics.CreateSnapshot();
                        _parentZoneHash = _lastZoneHash;
                        _isInSubZone = true;
                        _expectingSubZone = false;

                        // Force-complete any still-active mechanic, then reset for fresh sub-zone detection
                        if (_mechanicActive && ctx.Mechanics.ActiveMechanic != null)
                            ctx.Mechanics.ForceCompleteActive();
                        ctx.Mechanics.Reset();

                        // Suppress the triggering mechanic in the sub-zone — the wish zone
                        // has its own Faridun entities that would re-trigger detection
                        ctx.Mechanics.SuppressMechanic(triggerMechName);

                        _mechanicActive = false;
                        _pendingMechanic = null;
                    }

                    // Reset zone-local state for the new zone
                    _zoneEnteredAt = DateTime.Now;
                    _exitPortal = null;
                    _navTarget = null;
                    _exploreTargetsVisited = 0;
                    _navFailures = 0;
                    _bossKillCheckPositions = null;
                    _bossAreaArrivalTime = null;
                    _visitedIconIds.Clear();

                    // Boss rush only in parent map, not sub-zones
                    if (!_isInSubZone && ctx.Settings.Mapping.RushBoss.Value && !_bossRushComplete)
                    {
                        _bossTarget = null;
                        _bossTargetResolved = false;
                        _bossRushDebug = "New zone — re-resolving boss target";
                        _phase = MappingPhase.RushingBoss;
                        ctx.Log("[Mapping] Area changed — re-triggering boss rush");
                    }
                    else if (_phase == MappingPhase.Exiting || _phase == MappingPhase.RushingBoss)
                    {
                        _phase = MappingPhase.Exploring;
                    }
                }
                _lastZoneHash = currentHash;
            }

            var playerGrid = new Vector2(gc.Player.GridPosNum.X, gc.Player.GridPosNum.Y);

            // ── Combat (always runs — fires skills while exploring) ──
            // Navigation always owns movement. CombatSystem scans threats, fires skills,
            // manages flasks, but never repositions. Dense packs and rares trigger detours below.
            if (!_mechanicActive)
            {
                ctx.Combat.SuppressPositioning = true;  // Always — we own movement
                ctx.Combat.SuppressTargetedSkills = ctx.Interaction.IsBusy;
                ctx.Combat.Tick(ctx);
            }

            // ── Boss Rush (navigate to boss, fight on the way, don't stop) ──
            if (_phase == MappingPhase.RushingBoss)
            {
                // Lazy resolve: TileMap may not have been loaded when OnEnter ran
                if (!_bossTargetResolved)
                {
                    if (!ctx.TileMap.IsLoaded)
                    {
                        Status = "Rush boss — waiting for tile map...";
                        _bossRushDebug = "TileMap not loaded yet";
                        return;
                    }

                    _bossTargetResolved = true;
                    var areaName = gc.Area?.CurrentArea?.Name ?? "";
                    var bossTiles = ctx.MapDatabase.GetBossTiles(areaName);

                    if (bossTiles == null || bossTiles.Count == 0)
                    {
                        _bossRushDebug = $"No boss tiles in database for '{areaName}'";
                        ctx.Log($"[Mapping] Boss rush: {_bossRushDebug}");
                        _phase = MappingPhase.Exploring;
                        ModeHelpers.EnableDefaultCombat(ctx);
                        Decision = _bossRushDebug;
                    }
                    else
                    {
                        // Find the first boss tile key that has positions in the tile map
                        foreach (var tileKey in bossTiles)
                        {
                            var positions = ctx.TileMap.GetPositions(tileKey);
                            if (positions != null && positions.Count > 0)
                            {
                                // Tile center = gridPos + half tile (23/2 ≈ 11.5)
                                _bossTarget = positions
                                    .OrderBy(p => Vector2.Distance(playerGrid, p))
                                    .First() + new Vector2(11.5f, 11.5f);
                                _bossRushDebug = $"Found '{tileKey}' at ({_bossTarget.Value.X:F0}, {_bossTarget.Value.Y:F0})";
                                break;
                            }
                            else
                            {
                                _bossRushDebug = $"Tile key '{tileKey}' not found in TileMap ({ctx.TileMap.TileCount} tiles)";
                            }
                        }

                        if (_bossTarget.HasValue)
                        {
                            ctx.Combat.SetProfile(new CombatProfile { Enabled = true, Positioning = CombatPositioning.Aggressive });
                            ctx.Log($"[Mapping] Boss rush: {_bossRushDebug}");
                        }
                        else
                        {
                            ctx.Log($"[Mapping] Boss rush failed: {_bossRushDebug}");
                            _phase = MappingPhase.Exploring;
                            ModeHelpers.EnableDefaultCombat(ctx);
                            Decision = $"Boss rush failed: {_bossRushDebug}";
                        }
                    }

                    if (_phase != MappingPhase.RushingBoss) return;
                }

                if (_bossTarget.HasValue)
                {
                    var distToBoss = Vector2.Distance(playerGrid, _bossTarget.Value);

                    // Arrived at boss area (within 2 tiles = 46 grid units)
                    if (distToBoss < 46)
                    {
                        _bossRushComplete = true;
                        _bossTarget = null;
                        _phase = MappingPhase.Exploring;
                        ModeHelpers.EnableDefaultCombat(ctx);
                        Decision = "Boss area reached — switching to explore+clear";
                        _bossRushDebug = "Complete — arrived at boss area";
                        ctx.Log("Boss rush complete — switching to normal exploration");
                    }
                    else
                    {
                        if (!ctx.Navigation.IsNavigating)
                        {
                            ctx.Log($"[BossRush] Starting nav to ({_bossTarget.Value.X:F0}, {_bossTarget.Value.Y:F0}) dist={distToBoss:F0}");
                            var success = ctx.Navigation.NavigateTo(ctx.Game, _bossTarget.Value);
                            if (!success)
                            {
                                ctx.Log($"[BossRush] NavigateTo failed — no path");
                                _bossRushComplete = true;
                                _bossTarget = null;
                                _phase = MappingPhase.Exploring;
                                ModeHelpers.EnableDefaultCombat(ctx);
                                Decision = "Can't path to boss — exploring normally";
                                _bossRushDebug = "Failed — no path to boss";
                            }
                            else
                            {
                                ctx.Log($"[BossRush] Nav started, waypoints={ctx.Navigation.CurrentNavPath?.Count ?? 0}");
                            }
                        }
                        Status = $"Rushing boss — dist: {distToBoss:F0} nav={ctx.Navigation.IsNavigating}";
                        Decision = $"Rush boss ({distToBoss:F0} away, nav={ctx.Navigation.IsNavigating})";
                        return; // Skip loot, mechanics, explore — just rush
                    }
                }
            }

            // Resume navigation when density drops below threshold (detour over)
            if (!_mechanicActive && ctx.Navigation.IsPaused)
            {
                if (!ctx.Combat.InCombat ||
                    ctx.Combat.NearbyMonsterCount < ctx.Settings.Mapping.MinPackDensity.Value ||
                    ctx.Interaction.IsBusy)
                {
                    ctx.Navigation.Resume(gc);
                }
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
                // If it was a sub-zone mechanic that didn't trigger a zone change
                // (abandoned/failed before entering portal), clear the expectation
                if (result != MechanicResult.Complete)
                    _expectingSubZone = false;
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
                if (_pendingMechanic.TriggersSubZone)
                    _expectingSubZone = true;
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
                var hadPending = _lootTracker.HasPending;
                _lootTracker.HandleResult(result, ctx);
                if (hadPending && result == InteractionResult.Succeeded)
                {
                    ctx.Loot.Scan(gc);
                    _lastLootScan = DateTime.Now;
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
                    if (_lootTracker.HasPending)
                    {
                        _phase = MappingPhase.Looting;
                        Status = $"Looting: {_lootTracker.PendingItemName}";
                    }
                    else if (_pendingInteractableId != 0)
                    {
                        Status = $"Clicking: {_pendingInteractableName} ({ctx.Interaction.Status})";
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
                    _lootTracker.SetPending(candidate.Entity.Id, candidate.ItemName, candidate.ChaosValue);
                    _phase = MappingPhase.Looting;
                    Status = $"Looting: {candidate.ItemName}";
                    return;
                }
            }

            // ── Clickable interactables (shrines, heist caches — click as we pass by) ──
            if (!ctx.Interaction.IsBusy)
            {
                var target = FindNearbyInteractable(gc, playerGrid, ctx.Settings.Mechanics.Interactables);
                if (target != null)
                {
                    if (ctx.Interaction.InteractWithEntity(target, ctx.Navigation))
                    {
                        _pendingInteractableId = target.Id;
                        _pendingInteractableName = target.RenderName ?? target.Path;
                        _interactableClickAttempts = 0;
                        Status = $"Clicking: {_pendingInteractableName}";
                        ctx.Log($"[Interactable] Clicking {target.Path} dist={target.DistancePlayer:F0}");
                        return;
                    }
                }
            }

            // ── Density-gated combat detour ──
            // Only pause exploration for dense packs or rare/unique targets.
            // Otherwise skills fire passively while walking.
            if (ctx.Combat.InCombat)
            {
                int minDensity = ctx.Settings.Mapping.MinPackDensity.Value;
                bool denseEnough = ctx.Combat.NearbyMonsterCount >= minDensity;

                // Check for rare/unique detour
                bool rareDetour = false;
                if (!denseEnough && ctx.Settings.Mapping.DetourForRares.Value && ctx.Combat.BestTarget != null)
                {
                    var rarity = ctx.Combat.BestTarget.Rarity;
                    if (rarity == MonsterRarity.Rare || rarity == MonsterRarity.Unique)
                    {
                        var distToTarget = Vector2.Distance(playerGrid, ctx.Combat.BestTarget.GridPosNum);
                        if (distToTarget <= ctx.Settings.Mapping.MaxDetourDistance.Value)
                            rareDetour = true;
                    }
                }

                if (denseEnough || rareDetour)
                {
                    // Pause nav, pathfind to density cluster
                    if (ctx.Navigation.IsNavigating)
                        ctx.Navigation.Pause();

                    var combatTarget = ctx.Combat.DenseClusterCenter;
                    var distToCluster = Vector2.Distance(playerGrid, combatTarget);

                    if (distToCluster > 10f &&
                        (!ctx.Navigation.IsNavigating || _navTarget == null ||
                         Vector2.Distance(_navTarget.Value, combatTarget) > 20f))
                    {
                        ctx.Navigation.Stop(gc);
                        if (ctx.Navigation.NavigateTo(gc, combatTarget))
                        {
                            _navTarget = combatTarget;
                        }
                    }

                    _phase = MappingPhase.Fighting;
                    var reason = rareDetour ? "rare/unique detour" : $"dense pack ({ctx.Combat.NearbyMonsterCount})";
                    Decision = $"Fighting: {reason} @ ({combatTarget.X:F0},{combatTarget.Y:F0})";
                    Status = $"Fighting: {ctx.Combat.NearbyMonsterCount} monsters ({ctx.Combat.LastSkillAction})";
                    return;
                }
                // else: below density threshold, no rare target — skills fire passively while walking
            }

            // ── Exit portal handling (wish zones / sub-zones) ──
            if (_phase == MappingPhase.Exiting)
            {
                TickExitPortal(ctx, gc);
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

            // ── Boss kill verification (runs every tick when in boss area) ──
            if (ctx.Settings.Mapping.KillBoss.Value && !_bossKillVerified)
                TickBossKillCheck(ctx, gc, playerGrid);

            // Pick next target when idle (not navigating, or paused nav just resumed)
            if (!ctx.Navigation.IsNavigating)
            {
                // Priority 1: Required mechanics/interactables from minimap icons
                var priority = GetPriorityTarget(ctx, playerGrid);
                if (priority.HasValue)
                {
                    NavigateToTarget(ctx, gc, playerGrid, priority.Value);
                    Decision = $"Priority: {_priorityTargetReason}";
                }
                // Priority 2: Boss kill — navigate to boss area if not yet verified
                else if (ctx.Settings.Mapping.KillBoss.Value && !_bossKillVerified && _bossKillCheckPositions != null)
                {
                    var nearest = _bossKillCheckPositions
                        .OrderBy(p => Vector2.Distance(playerGrid, p))
                        .First();
                    var dist = Vector2.Distance(playerGrid, nearest);
                    if (dist > ctx.Settings.Mapping.BossKillRadius.Value)
                    {
                        NavigateToTarget(ctx, gc, playerGrid, nearest);
                        Decision = $"Boss kill: navigating to boss area ({dist:F0}g away)";
                    }
                }
                // Priority 3: Normal exploration
                else if (coverage >= _minCoverage)
                {
                    // Check if outstanding requirements prevent completion
                    bool needsBossKill = ctx.Settings.Mapping.KillBoss.Value && !_bossKillVerified;

                    var nextTarget = ctx.Exploration.GetNextExplorationTarget(playerGrid);
                    if (nextTarget.HasValue)
                    {
                        NavigateToTarget(ctx, gc, playerGrid, nextTarget.Value);
                    }
                    else if (needsBossKill)
                    {
                        // Exploration exhausted but boss not killed — keep waiting near boss area
                        Status = $"Waiting for boss kill — coverage {coverage:P1}";
                    }
                    else
                    {
                        // Truly complete — check for exit portal or mark done
                        var zoneTime = (DateTime.Now - _zoneEnteredAt).TotalSeconds;
                        ctx.Log($"[Mapping] Explore complete path: coverage={coverage:P1} zoneTime={zoneTime:F1}s noTarget=true noBoss=true");
                        var exitPortal = FindExitPortal(ctx, gc, ctx.Combat, "explore-complete");
                        if (exitPortal != null)
                        {
                            _exitPortal = exitPortal;
                            _phase = MappingPhase.Exiting;
                            var elapsed = (DateTime.Now - _startTime).TotalSeconds;
                            Status = $"Explored {coverage:P1} in {elapsed:F0}s — using exit portal";
                            Decision = "Heading to exit portal";
                            ctx.Log($"[Mapping] Exploration complete, found exit portal — returning to parent map");
                        }
                        else
                        {
                            _phase = MappingPhase.Complete;
                            var elapsed = (DateTime.Now - _startTime).TotalSeconds;
                            Status = $"COMPLETE — {coverage:P1} coverage in {elapsed:F0}s";
                            Decision = $"{ctx.Exploration.FailedRegions.Count} unreachable regions";
                        }
                    }
                }
                else
                {
                    // Under coverage threshold — check for priority targets mixed with exploration
                    NavigateToNextExploreTarget(ctx, gc, playerGrid);
                }
            }
        }

        private void NavigateToTarget(BotContext ctx, GameController gc, Vector2 playerGrid, Vector2 targetGrid)
        {
            if (ctx.Navigation.NavigateTo(gc, targetGrid))
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
                    var zoneTime = (DateTime.Now - _zoneEnteredAt).TotalSeconds;
                    var blob = ctx.Exploration.ActiveBlob;
                    var regionCount = blob?.Regions.Count ?? 0;
                    var failedCount = ctx.Exploration.FailedRegions.Count;
                    var totalCells = blob?.WalkableCells.Count ?? 0;
                    var seenCells = blob?.SeenCells.Count ?? 0;
                    ctx.Log($"[Mapping] No explore target: coverage={coverage:P1} zoneTime={zoneTime:F1}s regions={regionCount} failed={failedCount} cells={seenCells}/{totalCells} attempt={attempt}");

                    // Only consider exit portals after meaningful exploration (>10% coverage).
                    // Prevents immediately exiting wish zones / sub-zones before exploring them
                    // (exploration can return null briefly on fresh zone init).
                    var exitPortal = coverage > 0.10f ? FindExitPortal(ctx, gc, ctx.Combat, "no-targets") : null;
                    if (exitPortal != null)
                    {
                        _exitPortal = exitPortal;
                        _phase = MappingPhase.Exiting;
                        Status = $"Explored {coverage:P1} in {elapsed:F0}s — using exit portal";
                        Decision = "Heading to exit portal";
                        ctx.Log($"[Mapping] No more targets, found exit portal — returning");
                    }
                    else if (coverage > 0.10f)
                    {
                        _phase = MappingPhase.Complete;
                        Status = $"COMPLETE — {coverage:P1} coverage in {elapsed:F0}s";
                        Decision = $"No more targets ({ctx.Exploration.FailedRegions.Count} unreachable)";
                    }
                    // else: fresh zone, no targets yet — wait for exploration to populate
                    return;
                }

                if (ctx.Navigation.NavigateTo(gc, target.Value))
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

        // =================================================================
        // Priority Target Selection (minimap icon routing)
        // =================================================================

        /// <summary>
        /// Known minimap icon names → interactable setting mapping.
        /// Icons not listed here are ignored (unknown/unmapped).
        /// </summary>
        private static ListNode? GetInteractableSetting(string iconName, BotSettings.InteractableSettings settings)
        {
            return iconName switch
            {
                "Shrine" => settings.Shrines,
                "Strongbox" => settings.Strongboxes,
                "HeistSumgglersCache" => settings.HeistCaches,  // GGG typo in icon name
                "CraftingUnlockObject" => settings.CraftingRecipes,
                _ => null,
            };
        }

        /// <summary>
        /// Known minimap icon names → mechanic mode setting mapping.
        /// Returns the MechanicMode ListNode for mechanics that have one.
        /// </summary>
        private static ListNode? GetMechanicSetting(string iconName, BotSettings settings)
        {
            return iconName switch
            {
                "UltimatumAltar" => settings.Mechanics.Ultimatum.Mode,
                "RitualRune" or "RitualRuneFinished" => settings.Mechanics.Ritual.Mode,
                "HarvestPortal" => settings.Mechanics.Harvest.Mode,
                "Mirage" => settings.Mechanics.Wishes.Mode,
                "BlightCore" => null, // Blight has its own mode (BlightMode), not a map mechanic
                _ => null,
            };
        }

        /// <summary>
        /// Find the highest-priority unvisited minimap icon target.
        /// Required targets first, then optional. Skips icons in already-explored areas.
        /// </summary>
        private Vector2? GetPriorityTarget(BotContext ctx, Vector2 playerGrid)
        {
            _priorityTarget = null;
            _priorityTargetReason = "";

            if (ctx.MinimapIcons.Count == 0) return null;

            var seen = ctx.Exploration.ActiveBlob?.SeenCells;
            Vector2? bestRequired = null;
            float bestRequiredDist = float.MaxValue;
            string bestRequiredReason = "";

            Vector2? bestOptional = null;
            float bestOptionalDist = float.MaxValue;
            string bestOptionalReason = "";

            foreach (var (id, icon) in ctx.MinimapIcons)
            {
                if (_visitedIconIds.Contains(id)) continue;

                // Check if we've already explored this area (within ~10 grid cells)
                if (seen != null && seen.Contains(new Vector2i((int)icon.GridPos.X, (int)icon.GridPos.Y)))
                {
                    _visitedIconIds.Add(id);
                    continue;
                }

                var dist = Vector2.Distance(playerGrid, icon.GridPos);

                // Check interactable settings
                var interactSetting = GetInteractableSetting(icon.IconName, ctx.Settings.Mechanics.Interactables);
                if (interactSetting != null)
                {
                    if (interactSetting.Value == "Ignore") continue;
                    if (interactSetting.Value == "Required" && dist < bestRequiredDist)
                    {
                        bestRequired = icon.GridPos;
                        bestRequiredDist = dist;
                        bestRequiredReason = $"{icon.IconName} (Required) @ d={dist:F0}";
                    }
                    else if (interactSetting.Value == "Optional" && dist < bestOptionalDist)
                    {
                        bestOptional = icon.GridPos;
                        bestOptionalDist = dist;
                        bestOptionalReason = $"{icon.IconName} (Optional) @ d={dist:F0}";
                    }
                    continue;
                }

                // Check mechanic settings
                var mechSetting = GetMechanicSetting(icon.IconName, ctx.Settings);
                if (mechSetting != null)
                {
                    var mode = mechSetting.Value;
                    if (mode == "Skip") continue;
                    if (mode == "Required" && dist < bestRequiredDist)
                    {
                        bestRequired = icon.GridPos;
                        bestRequiredDist = dist;
                        bestRequiredReason = $"{icon.IconName} (Required) @ d={dist:F0}";
                    }
                    else if (mode == "Optional" && dist < bestOptionalDist)
                    {
                        bestOptional = icon.GridPos;
                        bestOptionalDist = dist;
                        bestOptionalReason = $"{icon.IconName} (Optional) @ d={dist:F0}";
                    }
                }
            }

            // Required first, then optional
            if (bestRequired.HasValue)
            {
                _priorityTarget = bestRequired;
                _priorityTargetReason = bestRequiredReason;
                return bestRequired;
            }
            if (bestOptional.HasValue)
            {
                _priorityTarget = bestOptional;
                _priorityTargetReason = bestOptionalReason;
                return bestOptional;
            }
            return null;
        }

        // =================================================================
        // Boss Kill Verification
        // =================================================================

        /// <summary>
        /// Resolve boss tile positions from MapDatabase. Called lazily on first check.
        /// </summary>
        private void ResolveBossKillPositions(BotContext ctx, GameController gc)
        {
            if (_bossKillCheckPositions != null) return;
            _bossKillCheckPositions = new List<Vector2>();

            var areaName = gc.Area?.CurrentArea?.Name ?? "";
            var bossTiles = ctx.MapDatabase.GetBossTiles(areaName);
            if (bossTiles == null || bossTiles.Count == 0) return;

            foreach (var tileKey in bossTiles)
            {
                var positions = ctx.TileMap.GetPositions(tileKey);
                if (positions != null)
                {
                    foreach (var p in positions)
                        _bossKillCheckPositions.Add(p + new Vector2(11.5f, 11.5f)); // tile center
                }
            }

            if (_bossKillCheckPositions.Count > 0)
                ctx.Log($"[BossKill] Resolved {_bossKillCheckPositions.Count} boss check positions");
        }

        /// <summary>
        /// Check if the boss has been killed. Runs each tick when KillBoss is enabled.
        /// Logic: player is within boss radius + dwell time elapsed + no alive unique monsters in radius.
        /// </summary>
        private void TickBossKillCheck(BotContext ctx, GameController gc, Vector2 playerGrid)
        {
            if (_bossKillVerified) return;

            // Lazy resolve boss positions
            if (!ctx.TileMap.IsLoaded) return;
            ResolveBossKillPositions(ctx, gc);
            if (_bossKillCheckPositions == null || _bossKillCheckPositions.Count == 0)
            {
                // No boss data — can't verify, treat as verified to not block completion
                _bossKillVerified = true;
                ctx.Log("[BossKill] No boss tile data — skipping verification");
                return;
            }

            float bossRadius = ctx.Settings.Mapping.BossKillRadius.Value;

            // Check if player is within radius of any boss tile
            bool inBossArea = false;
            foreach (var pos in _bossKillCheckPositions)
            {
                if (Vector2.Distance(playerGrid, pos) <= bossRadius)
                {
                    inBossArea = true;
                    break;
                }
            }

            if (!inBossArea)
            {
                _bossAreaArrivalTime = null;
                return;
            }

            // Start dwell timer
            if (!_bossAreaArrivalTime.HasValue)
            {
                _bossAreaArrivalTime = DateTime.Now;
                return;
            }

            // Wait for entities to load
            if ((DateTime.Now - _bossAreaArrivalTime.Value).TotalSeconds < BossAreaDwellSeconds)
                return;

            // Check for alive unique monsters in boss radius
            var bossEntry = ctx.MapDatabase.GetEntry(gc.Area?.CurrentArea?.Name ?? "");
            var bossPath = bossEntry?.BossEntityPath;

            foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
            {
                if (entity.Type != EntityType.Monster) continue;
                if (!entity.IsAlive || !entity.IsHostile) continue;
                if (entity.Rarity != MonsterRarity.Unique) continue;

                // Check if within boss radius
                bool nearBoss = false;
                foreach (var pos in _bossKillCheckPositions)
                {
                    if (Vector2.Distance(entity.GridPosNum, pos) <= bossRadius)
                    {
                        nearBoss = true;
                        break;
                    }
                }
                if (!nearBoss) continue;

                // If we have a specific boss path, only match that
                if (bossPath != null && !entity.Path.Contains(bossPath, StringComparison.OrdinalIgnoreCase))
                    continue;

                // Found alive unique in boss area — not killed yet
                return;
            }

            // No alive uniques in boss area — boss killed
            _bossKillVerified = true;
            ctx.Log("[BossKill] Boss kill verified — no alive uniques in boss area");
            Decision = "Boss killed!";
        }

        /// <summary>
        /// Find the nearest clickable interactable within detection radius, filtered by settings.
        /// Optional interactables use a short radius (click when passing by).
        /// Required interactables use the full detect radius (route toward).
        /// </summary>
        private ExileCore.PoEMemory.MemoryObjects.Entity? FindNearbyInteractable(
            GameController gc, Vector2 playerGrid, BotSettings.InteractableSettings settings)
        {
            ExileCore.PoEMemory.MemoryObjects.Entity? best = null;
            float bestDist = float.MaxValue;

            float RadiusFor(ListNode setting) =>
                settings.IsRequired(setting) ? InteractableDetectRadius : InteractableOptionalRadius;

            // Shrines
            if (settings.IsEnabled(settings.Shrines))
            {
                var radius = RadiusFor(settings.Shrines);
                foreach (var entity in gc.EntityListWrapper.ValidEntitiesByType[EntityType.Shrine])
                {
                    if (!entity.IsTargetable) continue;
                    if (_failedInteractables.Contains(entity.Id)) continue;
                    if (!entity.TryGetComponent<Shrine>(out var shrine) || !shrine.IsAvailable) continue;

                    var dist = Vector2.Distance(playerGrid, new Vector2(entity.GridPosNum.X, entity.GridPosNum.Y));
                    if (dist < bestDist && dist <= radius)
                    {
                        bestDist = dist;
                        best = entity;
                    }
                }
            }

            // Chests: strongboxes, heist caches, djinn caches
            foreach (var entity in gc.EntityListWrapper.ValidEntitiesByType[EntityType.Chest])
            {
                if (!entity.IsTargetable) continue;
                if (_failedInteractables.Contains(entity.Id)) continue;
                if (!entity.TryGetComponent<Chest>(out var chest) || chest.IsOpened) continue;

                // Determine which setting applies and check enabled
                ListNode? chestSetting = null;
                if (chest.IsStrongbox)
                    chestSetting = settings.Strongboxes;
                else if (entity.Path.Contains("LeagueHeist/HeistSmuggler"))
                    chestSetting = settings.HeistCaches;
                else if (entity.Path.Contains("Chests/Faridun/"))
                    chestSetting = settings.DjinnCaches;

                // Must be a known type and enabled
                if (chestSetting == null || !settings.IsEnabled(chestSetting))
                    continue;

                var radius = RadiusFor(chestSetting);
                var dist = Vector2.Distance(playerGrid, new Vector2(entity.GridPosNum.X, entity.GridPosNum.Y));
                if (dist < bestDist && dist <= radius)
                {
                    bestDist = dist;
                    best = entity;
                }
            }

            // Crafting recipes (click to unlock)
            if (settings.IsEnabled(settings.CraftingRecipes))
            {
                var radius = RadiusFor(settings.CraftingRecipes);
                foreach (var entity in gc.EntityListWrapper.ValidEntitiesByType[EntityType.IngameIcon])
                {
                    if (!entity.IsTargetable) continue;
                    if (_failedInteractables.Contains(entity.Id)) continue;
                    if (!entity.Path.Contains("CraftingUnlocks/RecipeUnlock")) continue;

                    var dist = Vector2.Distance(playerGrid, new Vector2(entity.GridPosNum.X, entity.GridPosNum.Y));
                    if (dist < bestDist && dist <= radius)
                    {
                        bestDist = dist;
                        best = entity;
                    }
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

        /// <summary>
        /// Find a return/exit portal in the current zone (e.g., SekhemaPortal in wish zones).
        /// Requires minimum time in zone AND no nearby combat before allowing exit.
        /// </summary>
        private Entity? FindExitPortal(BotContext ctx, GameController gc, CombatSystem combat, string caller = "unknown")
        {
            var elapsed = (DateTime.Now - _zoneEnteredAt).TotalSeconds;
            var coverage = ctx.Exploration.ActiveBlobCoverage;

            // Don't exit sub-zones too quickly — need time to fight and loot
            if (elapsed < MinZoneSecondsBeforeExit)
            {
                ctx.Log($"[ExitPortal] ({caller}) Blocked: zone time {elapsed:F1}s < {MinZoneSecondsBeforeExit}s, coverage={coverage:P1}");
                return null;
            }

            // Don't exit while monsters are still nearby
            if (combat.NearbyMonsterCount > 0)
            {
                ctx.Log($"[ExitPortal] ({caller}) Blocked: {combat.NearbyMonsterCount} monsters nearby, zoneTime={elapsed:F1}s");
                return null;
            }

            foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
            {
                if (entity.Path == null) continue;
                if (!entity.IsTargetable) continue;
                if (entity.Path.Contains("SekhemaPortal"))
                {
                    ctx.Log($"[ExitPortal] ({caller}) FOUND SekhemaPortal id={entity.Id} dist={entity.DistancePlayer:F0} zoneTime={elapsed:F1}s coverage={coverage:P1}");
                    return entity;
                }
            }
            return null;
        }

        /// <summary>
        /// Navigate to and click the exit portal to return to the parent map.
        /// </summary>
        private void TickExitPortal(BotContext ctx, GameController gc)
        {
            // Refresh entity reference
            if (_exitPortal != null)
            {
                var refreshed = FindEntityById(gc, _exitPortal.Id);
                if (refreshed != null) _exitPortal = refreshed;
            }

            if (_exitPortal == null || !_exitPortal.IsTargetable)
            {
                // Portal gone — area change should have fired
                Status = "Exit portal gone — waiting for area change";
                return;
            }

            var playerGrid = new Vector2(gc.Player.GridPosNum.X, gc.Player.GridPosNum.Y);
            var portalGrid = new Vector2(_exitPortal.GridPosNum.X, _exitPortal.GridPosNum.Y);
            var dist = Vector2.Distance(playerGrid, portalGrid);

            if (dist > ctx.Interaction.InteractRadius)
            {
                // Navigate to portal
                ctx.Navigation.NavigateTo(gc, portalGrid);
                Status = $"Exiting — walking to portal ({dist:F0}g)";
                return;
            }

            // Close enough — click it
            if (!BotInput.CanAct) return;
            if ((DateTime.Now - _lastExitClickTime).TotalMilliseconds < 500) return;

            var screenPos = gc.IngameState.Camera.WorldToScreen(_exitPortal.BoundsCenterPosNum);
            var windowRect = gc.Window.GetWindowRectangleTimeCache;
            var absPos = new Vector2(screenPos.X + windowRect.X, screenPos.Y + windowRect.Y);

            if (BotInput.Click(absPos))
            {
                _lastExitClickTime = DateTime.Now;
                Status = "Exiting — clicked portal";
            }
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

            var phaseColor = _phase switch
            {
                MappingPhase.RushingBoss => SharpDX.Color.Gold,
                MappingPhase.Fighting => SharpDX.Color.Red,
                MappingPhase.Looting => SharpDX.Color.LimeGreen,
                MappingPhase.Exploring => SharpDX.Color.Cyan,
                MappingPhase.Complete => SharpDX.Color.LimeGreen,
                MappingPhase.Exiting => SharpDX.Color.Magenta,
                MappingPhase.Paused => SharpDX.Color.Yellow,
                _ => SharpDX.Color.White,
            };

            var elapsed = (DateTime.Now - _startTime).TotalSeconds;
            var mins = (int)(elapsed / 60);
            var secs = (int)(elapsed % 60);
            g.DrawText($"MAPPING  {_phase}  {mins}:{secs:D2}", new Vector2(hudX, hudY), phaseColor);
            hudY += lineH;

            // Coverage bar
            if (blob != null)
            {
                var coverage = blob.Coverage;
                var barWidth = 200f;
                var barHeight = 12f;

                g.DrawBox(new SharpDX.RectangleF(hudX, hudY, barWidth, barHeight),
                    new SharpDX.Color(40, 40, 40, 200));
                var fillWidth = barWidth * Math.Min(coverage, 1f);
                var fillColor = coverage >= _minCoverage
                    ? new SharpDX.Color(0, 200, 0, 200)
                    : new SharpDX.Color(200, 200, 0, 200);
                g.DrawBox(new SharpDX.RectangleF(hudX, hudY, fillWidth, barHeight), fillColor);
                var targetX = hudX + barWidth * _minCoverage;
                g.DrawLine(new Vector2(targetX, hudY), new Vector2(targetX, hudY + barHeight), 2f, SharpDX.Color.White);
                g.DrawText($"{coverage:P0}",
                    new Vector2(hudX + barWidth + 8, hudY - 1), SharpDX.Color.White);
                hudY += barHeight + 4;
            }

            // Status + decision (one line each, only when meaningful)
            if (!string.IsNullOrEmpty(Status))
            {
                g.DrawText(Status, new Vector2(hudX, hudY), SharpDX.Color.White);
                hudY += lineH;
            }

            // Combat (inline)
            if (ctx.Combat.InCombat)
            {
                g.DrawText($"Combat: {ctx.Combat.NearbyMonsterCount} nearby  {ctx.Combat.LastSkillAction}",
                    new Vector2(hudX, hudY), SharpDX.Color.Red);
                hudY += lineH;
            }

            // Loot
            if (ctx.Loot.LootableCount > 0)
            {
                g.DrawText($"Loot: {ctx.Loot.LootableCount} items", new Vector2(hudX, hudY), SharpDX.Color.LimeGreen);
                hudY += lineH;
            }

            // Boss status (only when kill boss is enabled or rushing)
            if (_bossTarget.HasValue)
            {
                var dist = Vector2.Distance(playerGrid, _bossTarget.Value);
                g.DrawText($"Boss: {dist:F0}g away", new Vector2(hudX, hudY), SharpDX.Color.Gold);
                hudY += lineH;
            }
            else if (ctx.Settings.Mapping.KillBoss.Value)
            {
                var bossColor = _bossKillVerified ? SharpDX.Color.LimeGreen : SharpDX.Color.OrangeRed;
                g.DrawText(_bossKillVerified ? "Boss: KILLED" : "Boss: ALIVE", new Vector2(hudX, hudY), bossColor);
                hudY += lineH;
            }

            // Priority target
            if (_priorityTarget.HasValue)
            {
                g.DrawText($"Priority: {_priorityTargetReason}", new Vector2(hudX, hudY), SharpDX.Color.Yellow);
                hudY += lineH;
            }

            // Mechanics (compact: active + completed counts)
            if (ctx.Mechanics.ActiveMechanic != null)
            {
                g.DrawText($"Mechanic: {ctx.Mechanics.ActiveMechanic.Name} — {ctx.Mechanics.ActiveMechanic.Status}",
                    new Vector2(hudX, hudY), SharpDX.Color.Orange);
                hudY += lineH;
            }
            var completedCount = ctx.Mechanics.CompletionCounts.Values.Sum();
            if (completedCount > 0)
            {
                var mechList = string.Join(", ", ctx.Mechanics.CompletionCounts
                    .Select(kv => kv.Value > 1 ? $"{kv.Key} x{kv.Value}" : kv.Key));
                g.DrawText($"Done: {mechList}", new Vector2(hudX, hudY), SharpDX.Color.DarkGreen);
                hudY += lineH;
            }

            // ═══ World Overlays ═══

            // Nav target marker
            if (_navTarget.HasValue && ctx.Navigation.IsNavigating)
            {
                var targetWorld = new Vector3(
                    _navTarget.Value.X * Pathfinding.GridToWorld,
                    _navTarget.Value.Y * Pathfinding.GridToWorld, playerZ);
                var targetScreen = cam.WorldToScreen(targetWorld);
                if (targetScreen.X > -200 && targetScreen.X < 2400)
                {
                    var markerColor = _priorityTarget.HasValue ? SharpDX.Color.Yellow : SharpDX.Color.Cyan;
                    var markerLabel = _priorityTarget.HasValue ? "PRIORITY" : "NAV";
                    g.DrawCircleInWorld(targetWorld, 30f, markerColor, 2f);
                    g.DrawText(markerLabel, targetScreen + new Vector2(-20, -25), markerColor);
                }
            }

            // Navigation path
            if (ctx.Navigation.IsNavigating)
            {
                var path = ctx.Navigation.CurrentNavPath;
                for (int i = ctx.Navigation.CurrentWaypointIndex; i < path.Count - 1; i++)
                {
                    var from = cam.WorldToScreen(new Vector3(path[i].Position.X * Pathfinding.GridToWorld, path[i].Position.Y * Pathfinding.GridToWorld, playerZ));
                    var to = cam.WorldToScreen(new Vector3(path[i + 1].Position.X * Pathfinding.GridToWorld, path[i + 1].Position.Y * Pathfinding.GridToWorld, playerZ));
                    if (from.X < -200 || from.X > 2400 || to.X < -200 || to.X > 2400) continue;

                    var isBlink = path[i + 1].Action == WaypointAction.Blink;
                    var pathColor = isBlink ? SharpDX.Color.Magenta : SharpDX.Color.Orange;
                    g.DrawLine(from, to, isBlink ? 3f : 2f, pathColor);
                }
            }

            // Boss target world marker
            if (_bossTarget.HasValue)
            {
                var bossWorld = new Vector3(
                    _bossTarget.Value.X * (float)Pathfinding.GridToWorld,
                    _bossTarget.Value.Y * (float)Pathfinding.GridToWorld, playerZ);
                g.DrawCircleInWorld(bossWorld, 60f, SharpDX.Color.Gold, 3f);
                var bossScreen = cam.WorldToScreen(bossWorld);
                if (bossScreen.X > -200 && bossScreen.X < 2400)
                    g.DrawText("BOSS", bossScreen + new Vector2(-15, -20), SharpDX.Color.Gold);
            }

            // Active mechanic overlay
            if (ctx.Mechanics.ActiveMechanic != null)
                ctx.Mechanics.ActiveMechanic.Render(ctx);
        }
    }
}
