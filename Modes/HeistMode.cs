using ExileCore;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using AutoExile.Systems;
using AutoExile.Modes.Shared;
using System.Linq;
using System.Numerics;
using Pathfinding = AutoExile.Systems.Pathfinding;

namespace AutoExile.Modes
{
    public class HeistMode : IBotMode
    {
        public string Name => "Heist";

        // Exposed state
        public HeistState State => _state;
        public HeistPhase Phase => _phase;
        public string Decision { get; private set; } = "";
        public string StatusText => _status;

        private HeistState _state = new();
        private HeistPhase _phase = HeistPhase.Idle;
        private string _status = "";
        private string _lastAreaName = "";
        private DateTime _phaseStartTime = DateTime.Now;
        private DateTime _lastActionTime = DateTime.MinValue;
        private DateTime _lastRepathTime = DateTime.MinValue;
        private const int RepathCooldownMs = 1250; // min time between A* pathfinding calls

        // Companion wait tracking
        private long _waitingOnEntityId;
        private DateTime _companionWaitStart = DateTime.MinValue;
        private DateTime _lastCompanionClickTime = DateTime.MinValue;
        private int _companionClickAttempts;
        private HeistPhase _returnPhaseAfterDoor; // which phase to return to after door/chest opens

        // Loot
        private readonly LootPickupTracker _lootTracker = new();
        private DateTime _lastLootScanTime = DateTime.MinValue;
        private DateTime _chestLootWindowEnd = DateTime.MinValue; // pause to loot after chest opens

        // Curio display evaluation (grand heist reward room)
        private List<CurioDisplayInfo> _curioDisplays = new();
        private DateTime _lastCurioScanTime = DateTime.MinValue;

        // Navigation / exploration tracking
        private long _pendingInteractionEntityId;
        private int _lastStuckCount;           // track NavigationSystem stuck recoveries
        private int _lastRouteIndex = -1;      // detect route target changes for stuck reset
        private Vector2? _currentExploreTarget; // current explore/pathnode target (fallback)
        private readonly HashSet<Vector2> _visitedPathNodes = new(); // pathnode positions we've reached or failed (fallback)

        public void OnEnter(BotContext ctx)
        {
            _phase = HeistPhase.Idle;
            _state.Reset();
            _status = "Heist mode entered";
            // Heist curio drops are "quest" items — must pick them up
            ctx.Loot.IgnoreQuestItems = false;
        }

        public void OnExit()
        {
            _phase = HeistPhase.Idle;
        }

        public void Tick(BotContext ctx)
        {
            var gc = ctx.Game;

            if (gc.IsLoading)
            {
                _status = "Loading...";
                Decision = "loading";
                return;
            }

            // Detect area changes
            var currentArea = gc.Area?.CurrentArea?.Name ?? "";
            if (!string.IsNullOrEmpty(currentArea) && currentArea != _lastAreaName)
            {
                if (!string.IsNullOrEmpty(_lastAreaName))
                    OnAreaChanged(ctx);
                _lastAreaName = currentArea;
            }

            // In hideout/town — idle
            var isHideout = gc.Area?.CurrentArea?.IsHideout == true;
            var isTown = gc.Area?.CurrentArea?.IsTown == true;
            if (isHideout || isTown)
            {
                if (_phase != HeistPhase.Idle && _phase != HeistPhase.Done)
                {
                    _phase = _state.DeathCount > 0 ? HeistPhase.Done : HeistPhase.Idle;
                    _status = isHideout ? "In hideout" : "In town";
                    Decision = "hideout";
                }
                return;
            }

            // Tick combat — heist corridors are tight, suppress positioning always (walks
            // into walls). Allow targeted skills when standing still (at doors, waiting) or
            // when monsters are nearby, so the build can fight while navigating.
            ctx.Combat.SuppressPositioning = true;
            var activelyMoving = ctx.Navigation.IsNavigating;
            ctx.Combat.SuppressTargetedSkills = activelyMoving && ctx.Combat.NearbyMonsterCount == 0;
            ctx.Combat.Tick(ctx);

            // Tick interaction
            var interactionResult = ctx.Interaction.Tick(gc);
            _lootTracker.HandleResult(interactionResult, ctx);

            // Handle pending entity interaction results (doors, chests, curio, exit)
            if (_pendingInteractionEntityId != 0 && interactionResult != InteractionResult.None
                && interactionResult != InteractionResult.InProgress)
            {
                if (interactionResult == InteractionResult.Succeeded)
                    _state.OpenedEntities.Add(_pendingInteractionEntityId);
                _pendingInteractionEntityId = 0;
            }

            // Always tick state
            _state.Tick(gc);

            // Scan curio displays for valuation overlay (every 2s)
            if ((DateTime.Now - _lastCurioScanTime).TotalSeconds > 2)
            {
                _lastCurioScanTime = DateTime.Now;
                ScanCurioDisplays(gc);
            }

            // Phase machine
            switch (_phase)
            {
                case HeistPhase.Idle:
                    // Detect heist map — companion present
                    if (_state.CompanionEntityId == 0)
                    {
                        // Try to find companion
                        foreach (var e in gc.EntityListWrapper.OnlyValidEntities)
                        {
                            if (e?.Path?.Contains("LeagueHeist/NPCAllies") == true && !e.IsHostile && e.IsAlive)
                            {
                                _phase = HeistPhase.Initializing;
                                _phaseStartTime = DateTime.Now;
                                break;
                            }
                        }
                        if (_phase == HeistPhase.Idle)
                        {
                            _status = "Not in heist map (no companion found)";
                            Decision = "idle";
                        }
                    }
                    else
                    {
                        _phase = HeistPhase.Initializing;
                        _phaseStartTime = DateTime.Now;
                    }
                    break;

                case HeistPhase.Initializing:
                    TickInitializing(ctx, gc);
                    break;

                case HeistPhase.Infiltrating:
                    TickInfiltrating(ctx, gc);
                    break;

                case HeistPhase.AtDoor:
                    TickAtDoor(ctx, gc);
                    break;

                case HeistPhase.AtChest:
                    TickAtChest(ctx, gc);
                    break;

                case HeistPhase.GrabCurio:
                    TickGrabCurio(ctx, gc);
                    break;

                case HeistPhase.Escaping:
                    TickEscaping(ctx, gc);
                    break;

                case HeistPhase.ExitingMap:
                    TickExitingMap(ctx, gc);
                    break;

                case HeistPhase.Done:
                    _status = "Heist complete";
                    Decision = "done";
                    break;
            }
        }

        private void TickInitializing(BotContext ctx, GameController gc)
        {
            // Wait for entities to settle after zone load
            if ((DateTime.Now - _phaseStartTime).TotalSeconds < ctx.Settings.AreaSettleSeconds.Value)
            {
                _status = "Initializing... waiting for entities";
                Decision = "init_wait";
                return;
            }

            _state.Initialize(gc);
            _state.BuildRoute(ctx.Settings.Heist);
            ModeHelpers.EnableDefaultCombat(ctx);

            var routeDesc = string.Join(" → ", _state.PlannedRoute.Select(t => t.Label));
            ctx.Log($"Heist initialized: {_state.Status}");
            ctx.Log($"Route ({_state.PlannedRoute.Count} targets): {routeDesc}");

            // Detect mid-lockdown start: no alert panel, no curio entity, companion present.
            // This happens when bot starts after curio was already grabbed.
            if (!_state.IsAlertPanelVisible && _state.FindCurioEntity(gc) == null
                && _state.CompanionEntityId != 0)
            {
                _state.ForceLockdown();
                _phase = HeistPhase.Escaping;
                _phaseStartTime = DateTime.Now;
                _status = "Lockdown detected (mid-start)";
                Decision = "init_lockdown";
                ctx.Log("Heist: detected lockdown at init — skipping to escape");
                return;
            }

            _phase = HeistPhase.Infiltrating;
            _phaseStartTime = DateTime.Now;
            _status = "Infiltrating";
            Decision = "infiltrating";
        }

        private void TickInfiltrating(BotContext ctx, GameController gc)
        {
            // Check lockdown — curio was broken, loot the drop before escaping
            if (_state.IsLockdown)
            {
                ctx.Log("Lockdown detected — grabbing curio loot before escape");
                ctx.Navigation.Stop(gc);
                _visitedPathNodes.Clear(); // doors re-lock, need to re-traverse corridor
                _currentExploreTarget = null;
                _phase = HeistPhase.GrabCurio; // loot the quest item drop first
                _phaseStartTime = DateTime.Now;
                Decision = "lockdown_detected";
                return;
            }

            var playerGrid = gc.Player.GridPosNum;

            // Check for curio entity nearby — if we've reached it, switch to GrabCurio
            var curio = _state.FindCurioEntity(gc);
            if (curio != null && curio.DistancePlayer < 25)
            {
                ctx.Navigation.Stop(gc);
                _phase = HeistPhase.GrabCurio;
                _phaseStartTime = DateTime.Now;
                Decision = "at_curio";
                return;
            }

            // Loot nearby items (every 500ms)
            if ((DateTime.Now - _lastLootScanTime).TotalMilliseconds > 500 && !ctx.Interaction.IsBusy)
            {
                _lastLootScanTime = DateTime.Now;
                TryPickupLoot(ctx, gc);
            }

            // After opening a chest, pause to loot dropped items before resuming navigation
            if (DateTime.Now < _chestLootWindowEnd)
            {
                _status = "Looting chest drops...";
                Decision = "chest_loot_window";
                return; // keep scanning loot above, but don't navigate away
            }

            // Check for blocking doors nearby — but NOT when we're close to a chest route target.
            // Otherwise the bot opens the door to a chest room, then immediately detects the NEXT
            // locked door further down the corridor and walks past the chest to open that one.
            var currentRouteTarget = _state.CurrentTarget;
            bool nearChestTarget = currentRouteTarget != null
                && currentRouteTarget.Type == RouteTargetType.RewardChest
                && Vector2.Distance(playerGrid, currentRouteTarget.GridPos) < 60;

            if (!ctx.Interaction.IsBusy && !nearChestTarget)
            {
                var blockingDoor = FindBlockingDoor(gc, playerGrid);
                if (blockingDoor != null)
                {
                    ctx.Navigation.Stop(gc);
                    StartDoorInteraction(ctx, gc, blockingDoor, HeistPhase.Infiltrating);
                    return;
                }
            }

            // --- Route following ---

            // Advance past completed/skipped route targets
            while (_state.CurrentRouteIndex < _state.PlannedRoute.Count)
            {
                var t = _state.PlannedRoute[_state.CurrentRouteIndex];
                if (t.Reached || t.Skipped)
                {
                    _state.CurrentRouteIndex++;
                    continue;
                }
                if (t.Type == RouteTargetType.RewardChest)
                {
                    // Check by ID (if IDs match) or by checking if any opened chest is near this position
                    if (_state.OpenedEntities.Contains(t.EntityId))
                    {
                        t.Reached = true;
                        _state.CurrentRouteIndex++;
                        continue;
                    }
                    // Position-based check: any opened chest within 15 grid units of route target?
                    bool opened = false;
                    foreach (var id in _state.OpenedEntities)
                    {
                        if (_state.RewardChests.TryGetValue(id, out var c) && Vector2.Distance(c.GridPos, t.GridPos) < 15)
                        {
                            opened = true;
                            break;
                        }
                    }
                    if (opened)
                    {
                        t.Reached = true;
                        _state.CurrentRouteIndex++;
                        continue;
                    }
                }
                break;
            }

            // Reset stuck count when route target changes
            if (_state.CurrentRouteIndex != _lastRouteIndex)
            {
                _lastStuckCount = ctx.Navigation.StuckRecoveries;
                _lastRouteIndex = _state.CurrentRouteIndex;
            }

            var target = _state.CurrentTarget;

            if (target != null && target.Type == RouteTargetType.RewardChest)
            {
                // Check alert budget before committing to this chest
                if (_state.AlertPercent > ctx.Settings.Heist.AlertThreshold.Value)
                {
                    target.Skipped = true;
                    _status = $"Skipping {target.Label} — alert {_state.AlertPercent:F0}%";
                    Decision = "skip_chest_alert";
                    return; // next tick advances past this
                }

                // Check if we're close enough to interact
                var distToChest = Vector2.Distance(playerGrid, target.GridPos);
                if (distToChest < 25)
                {
                    // Find live entity — match by ID first, fall back to nearest HeistChest by position.
                    // TileEntity IDs may not match live entity IDs.
                    Entity? chestEntity = null;
                    float bestChestDist = 15f; // max distance to match a chest to its route target
                    foreach (var e in gc.EntityListWrapper.OnlyValidEntities)
                    {
                        if (e?.Path == null || !e.IsTargetable) continue;
                        if (!e.Path.Contains("HeistChest")) continue;

                        if (e.Id == target.EntityId)
                        {
                            chestEntity = e;
                            break; // exact ID match
                        }

                        // Position match — closest HeistChest to the route target position
                        var d = Vector2.Distance(e.GridPosNum, target.GridPos);
                        if (d < bestChestDist)
                        {
                            var ch = e.GetComponent<Chest>();
                            if (ch?.IsOpened != true)
                            {
                                bestChestDist = d;
                                chestEntity = e;
                            }
                        }
                    }

                    if (chestEntity != null)
                    {
                        var chest = chestEntity.GetComponent<Chest>();
                        if (chest?.IsOpened != true && !ctx.Interaction.IsBusy)
                        {
                            ctx.Navigation.Stop(gc);
                            StartChestInteraction(ctx, gc, chestEntity);
                            return;
                        }
                    }
                    else
                    {
                        // Entity not loaded yet — wait a bit before giving up (might be loading in)
                        if ((DateTime.Now - _phaseStartTime).TotalSeconds > 10)
                        {
                            target.Reached = true;
                            Decision = "chest_not_found";
                            return;
                        }
                        _status = $"Waiting for chest entity near ({target.GridPos.X:F0},{target.GridPos.Y:F0})...";
                        Decision = "chest_loading";
                        return;
                    }
                }

                // Navigate toward the chest
                NavigateToRouteTarget(ctx, gc, playerGrid, target);
            }
            else if (target != null && target.Type == RouteTargetType.Curio)
            {
                // If at the curio marker but no entity, marker was stale
                if (Vector2.Distance(playerGrid, target.GridPos) < 15 && _state.FindCurioEntity(gc) == null)
                {
                    target.Reached = true;
                    _state.ClearCurioTarget();
                    Decision = "curio_marker_reached";
                    return;
                }

                // Update curio position if actual entity appeared
                var curioEntity = _state.FindCurioEntity(gc);
                if (curioEntity != null)
                    target.GridPos = curioEntity.GridPosNum;

                NavigateToRouteTarget(ctx, gc, playerGrid, target);
            }
            else
            {
                // Route exhausted — fall back to pathnode exploration
                FallbackExplore(ctx, gc, playerGrid);
            }
        }

        private void TickAtDoor(BotContext ctx, GameController gc)
        {
            var elapsed = (DateTime.Now - _companionWaitStart).TotalSeconds;
            var settings = ctx.Settings.Heist;

            // Find the door entity
            Entity? doorEntity = null;
            foreach (var e in gc.EntityListWrapper.OnlyValidEntities)
                if (e.Id == _waitingOnEntityId) { doorEntity = e; break; }

            if (doorEntity == null || !doorEntity.IsTargetable)
            {
                // Door opened or despawned
                _state.OpenedEntities.Add(_waitingOnEntityId);
                _waitingOnEntityId = 0;
                _phase = _returnPhaseAfterDoor;
                _phaseStartTime = DateTime.Now;
                Decision = "door_opened";
                return;
            }

            var distToDoor = doorEntity.DistancePlayer;

            // Check if it's a click-to-open door (basic or generic) — navigate to it and click
            bool isClickDoor = doorEntity.Path == "Metadata/MiscellaneousObjects/Door"
                || doorEntity.Path?.Contains("Door_Basic") == true;
            if (isClickDoor)
            {
                var sm = doorEntity.GetComponent<StateMachine>();
                var open = HeistState.GetStateValue(sm, "open");
                if (open == 1 || !doorEntity.IsTargetable)
                {
                    _state.OpenedEntities.Add(_waitingOnEntityId);
                    _waitingOnEntityId = 0;
                    _phase = _returnPhaseAfterDoor;
                    _phaseStartTime = DateTime.Now;
                    Decision = "basic_door_opened";
                    return;
                }

                // Navigate closer if far, then click — InteractWithEntity handles final approach
                if (distToDoor > 40)
                {
                    // Too far for interaction — navigate closer first
                    if (!ctx.Navigation.IsNavigating)
                    {
                        var nearWalkable = ctx.Navigation.FindNearestWalkable(gc, doorEntity.GridPosNum, 20);
                        bool started = false;
                        if (nearWalkable.HasValue)
                            started = ctx.Navigation.NavigateTo(gc, nearWalkable.Value);
                        if (!started)
                        {
                            var stepNode = FindPathNodeToward(gc, gc.Player.GridPosNum, doorEntity.GridPosNum);
                            if (stepNode.HasValue)
                                ctx.Navigation.NavigateTo(gc, stepNode.Value);
                        }
                    }
                    _status = $"Moving to basic door... dist: {distToDoor:F0}";
                    Decision = "basic_door_approach";
                    return;
                }

                // Close enough — click the door (InteractWithEntity handles proximity)
                if (ctx.Navigation.IsNavigating)
                    ctx.Navigation.Stop(gc);

                _status = $"Opening basic door... dist: {distToDoor:F0}";
                Decision = "basic_door_clicking";

                if (!ctx.Interaction.IsBusy && (DateTime.Now - _lastCompanionClickTime).TotalSeconds > 2)
                {
                    ctx.Interaction.InteractWithEntity(doorEntity, ctx.Navigation, requireProximity: false);
                    _lastCompanionClickTime = DateTime.Now;
                }

                // Timeout
                if (elapsed > 30)
                {
                    _waitingOnEntityId = 0;
                    _phase = _returnPhaseAfterDoor;
                    _phaseStartTime = DateTime.Now;
                    Decision = "basic_door_timeout";
                }
                return;
            }

            // NPC/Vault door — poll companion progress
            var sm2 = doorEntity.GetComponent<StateMachine>();
            var locked = HeistState.GetStateValue(sm2, "heist_locked");

            if (locked == 0 || !doorEntity.IsTargetable)
            {
                _state.OpenedEntities.Add(_waitingOnEntityId);
                _waitingOnEntityId = 0;
                _phase = _returnPhaseAfterDoor;
                _phaseStartTime = DateTime.Now;
                Decision = "npc_door_opened";
                return;
            }

            // Ensure we're close enough to the door for companion to work.
            // V key works from any distance — companion walks to the entity himself.
            // Stay at 30 grid units to avoid cursor-on-door UI issues during approach.
            distToDoor = doorEntity.DistancePlayer;
            if (distToDoor > 30)
            {
                if (!ctx.Navigation.IsNavigating)
                {
                    // Navigate toward door but stop ~25 units away (between player and door)
                    var dir = Vector2.Normalize(doorEntity.GridPosNum - gc.Player.GridPosNum);
                    var approachTarget = doorEntity.GridPosNum - dir * 25;
                    var nearWalkable = ctx.Navigation.FindNearestWalkable(gc, approachTarget, 15);
                    bool started = false;
                    if (nearWalkable.HasValue)
                        started = ctx.Navigation.NavigateTo(gc, nearWalkable.Value);
                    if (!started)
                    {
                        var stepNode = FindPathNodeToward(gc, gc.Player.GridPosNum, doorEntity.GridPosNum);
                        if (stepNode.HasValue)
                            ctx.Navigation.NavigateTo(gc, stepNode.Value);
                    }
                }
                _status = $"Moving to door... dist: {distToDoor:F0}";
                Decision = "door_approach";
                return;
            }
            else
            {
                // Close enough — stop nav so we stand still for companion
                if (ctx.Navigation.IsNavigating)
                    ctx.Navigation.Stop(gc);
            }

            // Check door's own heist_locked state — 2=untouched, 1=companion assigned, 0=opened
            var doorAccepted = locked < 2; // V was accepted if heist_locked dropped from 2
            var companionWorking = _state.CompanionLockPickProgress > 0 || _state.CompanionIsBusy || doorAccepted;

            _status = $"Waiting for companion... locked:{locked:F0} progress:{_state.CompanionLockPickProgress:F0}% busy:{_state.CompanionIsBusy} ({elapsed:F0}s) dist:{distToDoor:F0}";
            Decision = companionWorking ? "companion_channeling" : "companion_idle";

            // Re-press V if companion hasn't accepted the job yet.
            // Door heist_locked still at 2 means V wasn't received or no companion picked it up.
            if (!companionWorking)
            {
                var timeSinceClick = (DateTime.Now - _lastCompanionClickTime).TotalSeconds;

                // First retry after 2s (initial V may have been eaten by input gate),
                // subsequent retries every 3s
                var retryDelay = _companionClickAttempts == 0 ? 0.5 : 3.0;
                if (timeSinceClick > retryDelay)
                {
                    var sent = BotInput.PressKey(settings.CompanionInteractKey);
                    if (sent)
                    {
                        _lastCompanionClickTime = DateTime.Now;
                        _companionClickAttempts++;
                    }
                }

                // Reset wait timer periodically so timeout doesn't fire while still retrying
                if (elapsed > settings.CompanionRetryDelay.Value)
                    _companionWaitStart = DateTime.Now;
            }

            // Timeout
            if (elapsed > settings.CompanionWaitTimeout.Value)
            {
                ctx.Log($"Companion wait timeout on door {_waitingOnEntityId}");
                _waitingOnEntityId = 0;
                _phase = _returnPhaseAfterDoor;
                _phaseStartTime = DateTime.Now;
                Decision = "companion_timeout";
            }
        }

        private void TickAtChest(BotContext ctx, GameController gc)
        {
            var elapsed = (DateTime.Now - _companionWaitStart).TotalSeconds;
            var settings = ctx.Settings.Heist;

            Entity? chestEntity = null;
            foreach (var e in gc.EntityListWrapper.OnlyValidEntities)
                if (e.Id == _waitingOnEntityId) { chestEntity = e; break; }

            if (chestEntity == null || !chestEntity.IsTargetable)
            {
                _state.OpenedEntities.Add(_waitingOnEntityId);
                _waitingOnEntityId = 0;
                _chestLootWindowEnd = DateTime.Now.AddSeconds(3); // pause to loot drops
                _phase = HeistPhase.Infiltrating;
                _phaseStartTime = DateTime.Now;
                Decision = "chest_opened";
                return;
            }

            var chest = chestEntity.GetComponent<Chest>();
            if (chest?.IsOpened == true)
            {
                _state.OpenedEntities.Add(_waitingOnEntityId);
                _waitingOnEntityId = 0;
                _chestLootWindowEnd = DateTime.Now.AddSeconds(3); // pause to loot drops
                _phase = HeistPhase.Infiltrating;
                _phaseStartTime = DateTime.Now;
                Decision = "chest_looted";
                return;
            }

            // Navigate closer if too far
            var distToChest = chestEntity.DistancePlayer;
            if (distToChest > 15)
            {
                if (!ctx.Navigation.IsNavigating)
                {
                    var nearWalkable = ctx.Navigation.FindNearestWalkable(gc, chestEntity.GridPosNum, 20);
                    if (nearWalkable.HasValue)
                        ctx.Navigation.NavigateTo(gc, nearWalkable.Value);
                }
                _status = $"Moving to chest... dist: {distToChest:F0}";
                Decision = "chest_approach";
                return;
            }
            else if (ctx.Navigation.IsNavigating)
            {
                ctx.Navigation.Stop(gc);
            }

            // Check if chest is heist_locked (needs companion) vs unlocked (direct click)
            var sm = chestEntity.GetComponent<StateMachine>();
            var heistLocked = HeistState.GetStateValue(sm, "heist_locked");

            // heist_locked: 2=untouched, 1=companion assigned, 0=unlocked
            var chestAccepted = heistLocked > 0 && heistLocked < 2;
            var companionWorking = _state.CompanionLockPickProgress > 0 || _state.CompanionIsBusy || chestAccepted;

            _status = $"Opening chest... locked:{heistLocked:F0} progress:{_state.CompanionLockPickProgress:F0}% ({elapsed:F0}s)";
            Decision = companionWorking ? "chest_channeling" : (heistLocked > 0 ? "chest_waiting" : "chest_clicking");

            if (heistLocked > 0 && !companionWorking)
            {
                // Companion hasn't picked up the job — re-press V
                var timeSinceClick = (DateTime.Now - _lastCompanionClickTime).TotalSeconds;
                var retryDelay = _companionClickAttempts == 0 ? 0.5 : 3.0;
                if (timeSinceClick > retryDelay)
                {
                    var sent = BotInput.PressKey(settings.CompanionInteractKey);
                    if (sent) _lastCompanionClickTime = DateTime.Now;
                    _companionClickAttempts++;
                }

                if (elapsed > settings.CompanionRetryDelay.Value)
                    _companionWaitStart = DateTime.Now;
            }
            else if (heistLocked <= 0 && !ctx.Interaction.IsBusy)
            {
                // Unlocked chest — click directly
                ctx.Interaction.InteractWithEntity(chestEntity, ctx.Navigation, requireProximity: true);
            }

            // Timeout
            if (elapsed > settings.CompanionWaitTimeout.Value)
            {
                ctx.Log($"Chest interaction timeout on {_waitingOnEntityId}");
                _state.OpenedEntities.Add(_waitingOnEntityId);
                _waitingOnEntityId = 0;
                _phase = HeistPhase.Infiltrating;
                _phaseStartTime = DateTime.Now;
                Decision = "chest_timeout";
            }
        }

        private void TickGrabCurio(BotContext ctx, GameController gc)
        {
            // Check lockdown — if already triggered, loot drops then escape
            if (_state.IsLockdown)
            {
                // Wait a moment for items to drop after curio break
                var timeSinceLockdown = (DateTime.Now - _phaseStartTime).TotalSeconds;

                // Try to pick up any nearby items
                if ((DateTime.Now - _lastLootScanTime).TotalMilliseconds > 500 && !ctx.Interaction.IsBusy)
                {
                    _lastLootScanTime = DateTime.Now;
                    TryPickupLoot(ctx, gc);
                }

                // Give at least 3 seconds for items to appear on ground, then wait until
                // no more loot is being picked up
                if (timeSinceLockdown > 3 && !ctx.Interaction.IsBusy && !_lootTracker.HasPending)
                {
                    // Check if there are still items visible nearby
                    ctx.Loot.Scan(gc);
                    var (hasLoot, _) = ctx.Loot.PickupNext(ctx.Interaction, ctx.Navigation);
                    if (!ctx.Interaction.IsBusy)
                    {
                        // No more loot — start escape
                        ctx.Navigation.Stop(gc);
                        _phase = HeistPhase.Escaping;
                        _phaseStartTime = DateTime.Now;
                        Decision = "curio_done_escaping";
                        return;
                    }
                }

                _status = $"Looting curio drops... ({timeSinceLockdown:F0}s)";
                Decision = "curio_looting";
                return;
            }

            // Find curio entity and click it
            var curio = _state.FindCurioEntity(gc);
            if (curio != null && !ctx.Interaction.IsBusy)
            {
                ctx.Interaction.InteractWithEntity(curio, ctx.Navigation, requireProximity: true);
                _status = "Breaking curio display...";
                Decision = "curio_clicking";
                return;
            }

            if (curio == null)
            {
                // Curio may have despawned (already opened) — lockdown should follow
                _status = "Curio gone — waiting for lockdown";
                Decision = "curio_despawned";

                // Safety timeout — if lockdown never triggers, escape anyway
                if ((DateTime.Now - _phaseStartTime).TotalSeconds > 30)
                {
                    _phase = HeistPhase.Escaping;
                    _phaseStartTime = DateTime.Now;
                    Decision = "curio_timeout";
                }
            }
        }

        private void TickEscaping(BotContext ctx, GameController gc)
        {
            var playerGrid = gc.Player.GridPosNum;

            // Detect navigation stuck — likely a re-locked door blocking the path
            if (ctx.Navigation.IsNavigating)
            {
                var stuckDelta = ctx.Navigation.StuckRecoveries - _lastStuckCount;
                if (stuckDelta >= 2)
                {
                    ctx.Navigation.Stop(gc);
                    // Look for any locked door nearby (wider range during escape)
                    var nextDoor = FindNextLockedDoor(gc, playerGrid);
                    if (nextDoor != null)
                    {
                        StartDoorInteraction(ctx, gc, nextDoor, HeistPhase.Escaping);
                        return;
                    }
                    _lastStuckCount = ctx.Navigation.StuckRecoveries;
                }
            }

            // Always check for blocking doors first — lockdown re-locks doors
            if (!ctx.Interaction.IsBusy)
            {
                var blockingDoor = FindBlockingDoor(gc, playerGrid);
                if (blockingDoor != null)
                {
                    ctx.Navigation.Stop(gc);
                    StartDoorInteraction(ctx, gc, blockingDoor, HeistPhase.Escaping);
                    return;
                }
            }

            // Try to find exit if we don't have it yet
            if (_state.ExitPosition == null)
            {
                _state.ScanForExit(gc);
            }

            // Loot nearby items while escaping (quick grabs only)
            if ((DateTime.Now - _lastLootScanTime).TotalMilliseconds > 500 && !ctx.Interaction.IsBusy)
            {
                _lastLootScanTime = DateTime.Now;
                TryPickupLoot(ctx, gc);
            }

            if (_state.ExitPosition != null)
            {
                // Check if we're near exit
                var distToExit = Vector2.Distance(playerGrid, _state.ExitPosition.Value);
                if (distToExit < 20)
                {
                    _phase = HeistPhase.ExitingMap;
                    _phaseStartTime = DateTime.Now;
                    Decision = "at_exit";
                    return;
                }

                // Navigate to exit — try direct path first, fall back to pathnode stepping stones
                if (!ctx.Navigation.IsNavigating)
                {
                    _lastStuckCount = ctx.Navigation.StuckRecoveries;
                    if (!ctx.Navigation.NavigateTo(gc, _state.ExitPosition.Value))
                    {
                        // Direct path blocked — check for door first
                        var nextDoor = FindNextLockedDoor(gc, playerGrid);
                        if (nextDoor != null)
                        {
                            StartDoorInteraction(ctx, gc, nextDoor, HeistPhase.Escaping);
                            return;
                        }

                        // No door — use nearest pathnode toward exit
                        var stepNode = FindPathNodeToward(gc, playerGrid, _state.ExitPosition.Value);
                        if (stepNode.HasValue)
                        {
                                ctx.Navigation.NavigateTo(gc, stepNode.Value);
                            Decision = $"escape_step ({stepNode.Value.X:F0},{stepNode.Value.Y:F0})";
                        }
                        else
                        {
                            Decision = "escape_no_path";
                        }
                    }
                }

                _status = $"Escaping — dist to exit: {distToExit:F0}";
                if (string.IsNullOrEmpty(Decision)) Decision = "escaping";
            }
            else
            {
                // No exit position — navigate toward exit using pathnodes
                // (nearest to exit = closest to where we entered = lowest distance pathnodes
                // that we haven't been to recently)
                _status = "Escaping — searching for exit...";
                Decision = "escape_searching";
                if (!ctx.Navigation.IsNavigating)
                {
                    // Try to find a locked door to open first
                    var nextDoor = FindNextLockedDoor(gc, playerGrid);
                    if (nextDoor != null)
                    {
                        StartDoorInteraction(ctx, gc, nextDoor, HeistPhase.Escaping);
                        return;
                    }
                }
            }
        }

        private void TickExitingMap(BotContext ctx, GameController gc)
        {
            var exit = _state.FindExitEntity(gc);
            if (exit != null && !ctx.Interaction.IsBusy)
            {
                ctx.Interaction.InteractWithEntity(exit, ctx.Navigation, requireProximity: true);
                _status = "Clicking exit...";
                Decision = "clicking_exit";
                return;
            }

            _status = "Waiting for exit interaction...";
            Decision = "exit_wait";

            if ((DateTime.Now - _phaseStartTime).TotalSeconds > 15)
            {
                // Retry — navigate to exit position and try again
                if (_state.ExitPosition != null)
                    ctx.Navigation.NavigateTo(gc, _state.ExitPosition.Value);
                _phaseStartTime = DateTime.Now;
            }
        }

        // --- Helpers ---------------------------------------------------------

        private void StartDoorInteraction(BotContext ctx, GameController gc, Entity door, HeistPhase returnPhase)
        {
            _waitingOnEntityId = door.Id;
            _companionWaitStart = DateTime.Now;
            _lastCompanionClickTime = DateTime.MinValue;
            _companionClickAttempts = 0;
            _returnPhaseAfterDoor = returnPhase;
            _phase = HeistPhase.AtDoor;
            _phaseStartTime = DateTime.Now;

            // Generic doors and Door_Basic: click directly. NPC/Vault doors: press V for companion.
            bool isClickDoor = door.Path == "Metadata/MiscellaneousObjects/Door"
                || door.Path?.Contains("Door_Basic") == true;

            if (isClickDoor)
            {
                if (!ctx.Interaction.IsBusy && door.DistancePlayer < 40)
                {
                    ctx.Interaction.InteractWithEntity(door, ctx.Navigation, requireProximity: false);
                    _lastCompanionClickTime = DateTime.Now;
                }
            }
            else if (door.DistancePlayer < 40)
            {
                // Press V — TickAtDoor will verify it was accepted via heist_locked state
                var sent = BotInput.PressKey(ctx.Settings.Heist.CompanionInteractKey);
                if (sent)
                    _lastCompanionClickTime = DateTime.Now;
                // If not sent (input gate blocked), _lastCompanionClickTime stays at MinValue
                // so TickAtDoor will retry quickly
            }
            // else: TickAtDoor will navigate closer first

            _status = $"Opening door {door.Path?.Split('/').LastOrDefault()}";
            Decision = "start_door";
        }

        private void StartChestInteraction(BotContext ctx, GameController gc, Entity chest)
        {
            _waitingOnEntityId = chest.Id;
            _companionWaitStart = DateTime.Now;
            _lastCompanionClickTime = DateTime.MinValue;
            _companionClickAttempts = 0;
            _phase = HeistPhase.AtChest;
            _phaseStartTime = DateTime.Now;

            // Only press V for locked chests — unlocked chests are clicked directly
            var sm = chest.GetComponent<StateMachine>();
            var heistLocked = HeistState.GetStateValue(sm, "heist_locked");
            if (heistLocked > 0 && chest.DistancePlayer < 40)
            {
                var sent = BotInput.PressKey(ctx.Settings.Heist.CompanionInteractKey);
                if (sent)
                    _lastCompanionClickTime = DateTime.Now;
            }

            _status = $"Opening reward chest";
            Decision = "start_chest";
        }

        /// <summary>Navigate toward a route target with door/stepping-stone fallbacks and stuck handling.</summary>
        private void NavigateToRouteTarget(BotContext ctx, GameController gc, Vector2 playerGrid, RouteTarget target)
        {
            // Stuck handling — skip target if stuck too many times
            if (ctx.Navigation.IsNavigating)
            {
                var stuckDelta = ctx.Navigation.StuckRecoveries - _lastStuckCount;
                if (stuckDelta >= 5)
                {
                    target.Skipped = true;
                    ctx.Navigation.Stop(gc);
                    _status = $"Skipping {target.Label} — stuck";
                    Decision = "route_stuck_skip";
                    return;
                }

                // While navigating, also check for locked doors in our path.
                // NavigationSystem may have pathed around a door or tried to blink over it.
                // If we detect a locked door nearby and roughly in our travel direction, stop and open it.
                if (stuckDelta >= 1)
                {
                    var blockingDoor = FindBlockingDoor(gc, playerGrid);
                    if (blockingDoor != null)
                    {
                        ctx.Navigation.Stop(gc);
                        StartDoorInteraction(ctx, gc, blockingDoor, HeistPhase.Infiltrating);
                        return;
                    }
                }
            }

            if (!ctx.Navigation.IsNavigating)
            {
                // Throttle A* pathfinding to avoid lag
                if ((DateTime.Now - _lastRepathTime).TotalMilliseconds < RepathCooldownMs)
                {
                    Decision = $"route_wait → {target.Label}";
                    return;
                }

                // Before pathfinding, check for locked doors — they block A* and cause failures
                var nearbyDoor = FindNextLockedDoor(gc, playerGrid);
                if (nearbyDoor != null)
                {
                    _lastRepathTime = DateTime.Now;
                    StartDoorInteraction(ctx, gc, nearbyDoor, HeistPhase.Infiltrating);
                    return;
                }

                if (!ctx.Navigation.NavigateTo(gc, target.GridPos))
                {
                    _lastRepathTime = DateTime.Now;

                    // Can't path directly — use stepping stones to get closer
                    var stepNode = FindPathNodeToward(gc, playerGrid, target.GridPos);
                    if (stepNode.HasValue && ctx.Navigation.NavigateTo(gc, stepNode.Value))
                    {
                        Decision = $"route_step → {target.Label}";
                    }
                    else
                    {
                        Decision = $"route_blocked → {target.Label}";
                    }
                }
                _lastRepathTime = DateTime.Now;
            }
            else
            {
                ctx.Navigation.UpdateDestination(gc, target.GridPos, 12);
            }

            var idx = _state.CurrentRouteIndex + 1;
            var total = _state.PlannedRoute.Count;
            _status = $"Route [{idx}/{total}]: → {target.Label} — alert: {_state.AlertPercent:F0}%";
            if (string.IsNullOrEmpty(Decision)) Decision = $"route_{target.Label}";
        }

        /// <summary>Fallback exploration using pathnodes when planned route is exhausted.</summary>
        private void FallbackExplore(BotContext ctx, GameController gc, Vector2 playerGrid)
        {
            if (ctx.Navigation.IsNavigating && _currentExploreTarget.HasValue)
            {
                if (Vector2.Distance(playerGrid, _currentExploreTarget.Value) < 20)
                    _visitedPathNodes.Add(_currentExploreTarget.Value);

                var stuckDelta = ctx.Navigation.StuckRecoveries - _lastStuckCount;
                if (stuckDelta >= 3)
                {
                    _visitedPathNodes.Add(_currentExploreTarget.Value);
                    ctx.Navigation.Stop(gc);
                    _currentExploreTarget = null;
                    Decision = "explore_stuck";
                }
                else
                {
                    _status = $"Exploring — alert: {_state.AlertPercent:F0}%";
                    Decision = "exploring";
                }
            }
            else if (!ctx.Navigation.IsNavigating)
            {
                // Throttle A* pathfinding
                if ((DateTime.Now - _lastRepathTime).TotalMilliseconds < RepathCooldownMs)
                {
                    Decision = "explore_wait";
                    return;
                }

                var bestNode = FindNextPathNode(gc, playerGrid);
                if (bestNode.HasValue)
                {
                    if (ctx.Navigation.NavigateTo(gc, bestNode.Value))
                    {
                        _currentExploreTarget = bestNode.Value;
                        _lastStuckCount = ctx.Navigation.StuckRecoveries;
                        _status = $"Exploring — alert: {_state.AlertPercent:F0}%";
                        Decision = $"explore ({bestNode.Value.X:F0},{bestNode.Value.Y:F0})";
                    }
                    else
                    {
                        _visitedPathNodes.Add(bestNode.Value);
                        Decision = "explore_fail";
                    }
                }
                else
                {
                    var nextDoor = FindNextLockedDoor(gc, playerGrid);
                    if (nextDoor != null)
                    {
                        if (nextDoor.DistancePlayer < 20)
                        {
                            ctx.Navigation.Stop(gc);
                            StartDoorInteraction(ctx, gc, nextDoor, HeistPhase.Infiltrating);
                        }
                        else
                        {
                            var nearWalkable = ctx.Navigation.FindNearestWalkable(gc, nextDoor.GridPosNum, 20);
                            if (nearWalkable.HasValue)
                            {
                                ctx.Navigation.NavigateTo(gc, nearWalkable.Value);
                                _currentExploreTarget = nearWalkable.Value;
                                _lastStuckCount = ctx.Navigation.StuckRecoveries;
                            }
                            _status = $"Navigating to locked door";
                            Decision = "nav_to_door";
                        }
                    }
                    else
                    {
                        _status = "No path forward";
                        Decision = "no_path_forward";
                    }
                }
            }
        }

        /// <summary>
        /// Find the best HeistPathNode to navigate to next. Uses the full map-wide path
        /// node list from TileEntities. Picks the farthest unvisited node from the exit
        /// (deepest into the heist) that's within pathfinding range of the player.
        /// </summary>
        private Vector2? FindNextPathNode(GameController gc, Vector2 playerGrid)
        {
            var exitPos = _state.ExitPosition ?? playerGrid;
            Vector2? best = null;
            float bestDistFromExit = 0;

            // Use map-wide path nodes from TileEntities
            foreach (var nodeGrid in _state.PathNodes)
            {
                // Skip nodes we've already visited or are very close to
                if (_visitedPathNodes.Contains(nodeGrid)) continue;
                var distToPlayer = Vector2.Distance(playerGrid, nodeGrid);
                if (distToPlayer < 15) continue;

                // Only consider nodes within reasonable pathfinding range
                // (too far = pathfinder can't reach across doors/walls anyway)
                if (distToPlayer > Pathfinding.NetworkBubbleRadius) continue;

                // Pick the node farthest from exit (deepest into heist)
                var distFromExit = Vector2.Distance(nodeGrid, exitPos);
                if (distFromExit > bestDistFromExit)
                {
                    bestDistFromExit = distFromExit;
                    best = nodeGrid;
                }
            }

            // Fallback: also check live entities for any pathnodes not in TileEntities
            if (best == null)
            {
                foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
                {
                    if (entity?.Path == null) continue;
                    if (!entity.Path.Contains("HeistPathNode") && !entity.Path.Contains("HeistPathEndpoint"))
                        continue;

                    var nodeGrid = entity.GridPosNum;
                    if (_visitedPathNodes.Contains(nodeGrid)) continue;
                    if (Vector2.Distance(playerGrid, nodeGrid) < 15) continue;

                    var distFromExit = Vector2.Distance(nodeGrid, exitPos);
                    if (distFromExit > bestDistFromExit)
                    {
                        bestDistFromExit = distFromExit;
                        best = nodeGrid;
                    }
                }
            }

            return best;
        }

        /// <summary>
        /// Find the nearest closed/locked door in the visible entity list.
        /// Includes both NPC doors (need companion key) and basic doors (need clicking).
        /// Used when pathnodes run out — the door is the gateway to the next corridor section.
        /// </summary>
        private Entity? FindNextLockedDoor(GameController gc, Vector2 playerGrid)
        {
            Entity? nearest = null;
            float nearestDist = float.MaxValue;

            foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
            {
                if (entity?.Path == null || !entity.IsTargetable) continue;
                if (_state.OpenedEntities.Contains(entity.Id)) continue;

                bool isDoor = false;

                // Generic doors (Metadata/MiscellaneousObjects/Door) — targetable = closed
                if (entity.Path == "Metadata/MiscellaneousObjects/Door")
                {
                    isDoor = true;
                }
                // Basic doors — check open state
                else if (entity.Path.Contains("Door_Basic"))
                {
                    var sm = entity.GetComponent<StateMachine>();
                    if (HeistState.GetStateValue(sm, "open") == 0)
                        isDoor = true;
                }
                // NPC/Vault doors — check heist_locked state
                else if ((entity.Path.Contains("Door_NPC") && !entity.Path.Contains("Alternate"))
                    || entity.Path.Contains("Vault"))
                {
                    var sm = entity.GetComponent<StateMachine>();
                    if (HeistState.GetStateValue(sm, "heist_locked") > 0)
                    {
                        isDoor = true;
                        // During lockdown, doors re-lock — clear stale opened state
                        _state.OpenedEntities.Remove(entity.Id);
                    }
                }

                if (isDoor && entity.DistancePlayer < nearestDist)
                {
                    nearestDist = entity.DistancePlayer;
                    nearest = entity;
                }
            }
            return nearest;
        }

        /// <summary>
        /// Find the nearest HeistPathNode that's closer to the target than the player is.
        /// Used as a stepping stone when direct pathfinding to a distant target fails.
        /// Uses map-wide TileEntities path nodes filtered to pathfinding range.
        /// </summary>
        private Vector2? FindPathNodeToward(GameController gc, Vector2 playerGrid, Vector2 target)
        {
            var playerDistToTarget = Vector2.Distance(playerGrid, target);
            Vector2? best = null;
            float bestDist = float.MaxValue;

            foreach (var nodeGrid in _state.PathNodes)
            {
                var nodeDistToTarget = Vector2.Distance(nodeGrid, target);

                // Must be closer to target than we are
                if (nodeDistToTarget >= playerDistToTarget) continue;
                // Skip nodes very close to us (already there)
                var distToPlayer = Vector2.Distance(playerGrid, nodeGrid);
                if (distToPlayer < 15) continue;
                // Must be within pathfinding range
                if (distToPlayer > Pathfinding.NetworkBubbleRadius) continue;

                if (distToPlayer < bestDist)
                {
                    bestDist = distToPlayer;
                    best = nodeGrid;
                }
            }
            return best;
        }

        private Entity? FindBlockingDoor(GameController gc, Vector2 playerGrid)
        {
            Entity? nearest = null;
            float nearestDist = float.MaxValue;

            foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
            {
                if (entity?.Path == null || !entity.IsTargetable) continue;
                if (entity.DistancePlayer > 50) continue;

                bool isDoor = false;

                // Generic doors — targetable = closed, click to open
                if (entity.Path == "Metadata/MiscellaneousObjects/Door")
                {
                    isDoor = true;
                }
                else if (entity.Path.Contains("Door_Basic"))
                {
                    var sm = entity.GetComponent<StateMachine>();
                    if (HeistState.GetStateValue(sm, "open") == 0)
                        isDoor = true;
                }
                else if ((entity.Path.Contains("Door_NPC") && !entity.Path.Contains("Alternate"))
                    || entity.Path.Contains("Vault"))
                {
                    var sm = entity.GetComponent<StateMachine>();
                    if (HeistState.GetStateValue(sm, "heist_locked") > 0)
                    {
                        isDoor = true;
                        // During lockdown doors re-lock — clear stale opened state
                        _state.OpenedEntities.Remove(entity.Id);
                    }
                }

                if (isDoor && entity.DistancePlayer < nearestDist)
                {
                    nearestDist = entity.DistancePlayer;
                    nearest = entity;
                }
            }
            return nearest;
        }

        private Entity? FindWorthyChest(GameController gc, Vector2 playerGrid, BotSettings.HeistSettings settings)
        {
            if (_state.AlertPercent > settings.AlertThreshold.Value)
                return null;

            Entity? best = null;
            float bestDist = float.MaxValue;

            foreach (var cached in _state.RewardChests.Values)
            {
                if (_state.OpenedEntities.Contains(cached.Id)) continue;

                // Skip filler side chests (HeistPathChest) — only open reward room/smuggler chests
                if (cached.ChestType == HeistChestType.Normal) continue;

                // Filter by user's per-reward-type toggles
                if (cached.RewardType != HeistRewardType.None && !settings.IsRewardTypeEnabled(cached.RewardType))
                    continue;

                var dist = Vector2.Distance(playerGrid, cached.GridPos);
                if (dist > settings.MaxChestDetour.Value) continue;
                if (dist < bestDist)
                {
                    // Find the live entity
                    foreach (var e in gc.EntityListWrapper.OnlyValidEntities)
                    {
                        if (e.Id == cached.Id && e.IsTargetable)
                        {
                            var chest = e.GetComponent<Chest>();
                            if (chest?.IsOpened != true)
                            {
                                best = e;
                                bestDist = dist;
                            }
                            break;
                        }
                    }
                }
            }
            return best;
        }

        private void TryPickupLoot(BotContext ctx, GameController gc)
        {
            if (_lootTracker.HasPending || ctx.Interaction.IsBusy) return;

            ctx.Loot.Scan(gc);
            var (wasInRadius, candidate) = ctx.Loot.PickupNext(ctx.Interaction, ctx.Navigation);
            if (candidate != null && ctx.Interaction.IsBusy)
            {
                _lootTracker.SetPending(candidate.Entity.Id, candidate.ItemName, candidate.ChaosValue);
            }
        }

        private void OnAreaChanged(BotContext ctx)
        {
            ModeHelpers.CancelAllSystems(ctx);
            _state.OnAreaChanged();
            _lootTracker.ResetCount();
            ctx.Loot.ClearFailed();
            _waitingOnEntityId = 0;
            _pendingInteractionEntityId = 0;
            _visitedPathNodes.Clear();
            _currentExploreTarget = null;
            _phase = HeistPhase.Idle;
            _phaseStartTime = DateTime.Now;
            _status = "Area changed";
            Decision = "area_changed";
            ctx.Log("Heist: area changed — reset state");
        }

        public void Render(BotContext ctx)
        {
            if (ctx.Graphics == null) return;
            var g = ctx.Graphics;
            var gc = ctx.Game;
            if (!gc.InGame) return;

            var cam = gc.IngameState.Camera;

            // ═══ HUD Panel ═══
            var hudX = 20f;
            var hudY = 100f;
            var lineH = 16f;

            g.DrawText("=== HEIST ===", new Vector2(hudX, hudY), SharpDX.Color.Cyan);
            hudY += lineH;

            var phaseColor = _phase switch
            {
                HeistPhase.Infiltrating => SharpDX.Color.Yellow,
                HeistPhase.AtDoor => SharpDX.Color.Orange,
                HeistPhase.AtChest => SharpDX.Color.Orange,
                HeistPhase.GrabCurio => SharpDX.Color.LimeGreen,
                HeistPhase.Escaping => SharpDX.Color.Red,
                HeistPhase.ExitingMap => SharpDX.Color.Red,
                HeistPhase.Done => SharpDX.Color.Gray,
                _ => SharpDX.Color.White,
            };
            g.DrawText($"Phase: {_phase}", new Vector2(hudX, hudY), phaseColor);
            hudY += lineH;

            g.DrawText($"Decision: {Decision}", new Vector2(hudX, hudY), SharpDX.Color.Gray);
            hudY += lineH;

            g.DrawText(_status, new Vector2(hudX, hudY), SharpDX.Color.White);
            hudY += lineH;

            // Alert bar
            if (_state.IsAlertPanelVisible || _state.AlertPercent > 0)
            {
                var barWidth = 200f;
                var barHeight = 14f;
                var alertPct = _state.AlertPercent / 100f;
                var alertColor = _state.IsLockdown
                    ? new SharpDX.Color(255, 0, 0, 200)
                    : alertPct > 0.7f
                        ? new SharpDX.Color(255, 100, 0, 200)
                        : new SharpDX.Color(200, 200, 0, 200);
                g.DrawBox(new SharpDX.RectangleF(hudX, hudY, barWidth, barHeight),
                    new SharpDX.Color(40, 40, 40, 200));
                g.DrawBox(new SharpDX.RectangleF(hudX, hudY, barWidth * Math.Min(alertPct, 1f), barHeight),
                    alertColor);
                var alertLabel = _state.IsLockdown ? "LOCKDOWN" : $"Alert: {_state.AlertPercent:F0}%";
                g.DrawText(alertLabel, new Vector2(hudX + barWidth + 8, hudY - 1), SharpDX.Color.White);
                hudY += barHeight + 4;
            }

            // Nav state
            var navStatus = ctx.Navigation.IsNavigating
                ? (ctx.Navigation.IsPaused ? "PAUSED" : "navigating")
                : "idle";
            g.DrawText($"Nav: {navStatus} | Stuck: {ctx.Navigation.StuckRecoveries}", new Vector2(hudX, hudY), SharpDX.Color.White);
            hudY += lineH;

            // Combat state
            g.DrawText($"Combat: {(ctx.Combat.InCombat ? "FIGHTING" : "clear")} | Nearby: {ctx.Combat.NearbyMonsterCount}",
                new Vector2(hudX, hudY), ctx.Combat.InCombat ? SharpDX.Color.Red : SharpDX.Color.White);
            hudY += lineH;

            // Key positions
            var curioStr = _state.CurioTargetPosition.HasValue
                ? $"({_state.CurioTargetPosition.Value.X:F0},{_state.CurioTargetPosition.Value.Y:F0})"
                : "unknown";
            g.DrawText($"Curio: {curioStr}", new Vector2(hudX, hudY), SharpDX.Color.LimeGreen);
            hudY += lineH;

            var exitStr = _state.ExitPosition.HasValue
                ? $"({_state.ExitPosition.Value.X:F0},{_state.ExitPosition.Value.Y:F0})"
                : "unknown";
            g.DrawText($"Exit: {exitStr}", new Vector2(hudX, hudY), SharpDX.Color.Aqua);
            hudY += lineH;

            // Exploration
            if (ctx.Exploration.IsInitialized)
            {
                var blob = ctx.Exploration.ActiveBlob;
                if (blob != null)
                    g.DrawText($"Coverage: {blob.Coverage:P1}", new Vector2(hudX, hudY), SharpDX.Color.White);
                hudY += lineH;
            }

            // Route progress
            if (_state.PlannedRoute.Count > 0)
            {
                var completed = _state.PlannedRoute.Count(t => t.Reached);
                var skipped = _state.PlannedRoute.Count(t => t.Skipped);
                var currentLabel = _state.CurrentTarget?.Label ?? "done";
                g.DrawText($"Route: {completed}/{_state.PlannedRoute.Count} done ({skipped} skipped) → {currentLabel}",
                    new Vector2(hudX, hudY), SharpDX.Color.Gold);
                hudY += lineH;
            }

            // Doors/chests
            var openedCount = _state.OpenedEntities.Count;
            g.DrawText($"Doors: {_state.Doors.Count} | Chests: {_state.RewardChests.Count} | Opened: {openedCount}",
                new Vector2(hudX, hudY), SharpDX.Color.White);
            hudY += lineH;

            // ═══ World Overlays ═══
            if (gc.Area.CurrentArea.IsHideout || gc.Area.CurrentArea.IsTown)
                return;

            // Navigation path
            if (ctx.Navigation.IsNavigating)
            {
                var path = ctx.Navigation.CurrentNavPath;
                for (int i = ctx.Navigation.CurrentWaypointIndex; i < path.Count - 1; i++)
                {
                    var from = Pathfinding.GridToScreen(gc, path[i].Position);
                    var to = Pathfinding.GridToScreen(gc, path[i + 1].Position);
                    if (from.X < -200 || from.X > 2400 || to.X < -200 || to.X > 2400) continue;
                    var isBlink = path[i + 1].Action == WaypointAction.Blink;
                    g.DrawLine(from, to, isBlink ? 3f : 2f, isBlink ? SharpDX.Color.Magenta : SharpDX.Color.Orange);
                }
            }

            // Route target markers
            for (int i = 0; i < _state.PlannedRoute.Count; i++)
            {
                var rt = _state.PlannedRoute[i];
                var rtWorld = Pathfinding.GridToWorld3D(gc, rt.GridPos);
                var rtScreen = cam.WorldToScreen(rtWorld);
                if (rtScreen.X < -200 || rtScreen.X > 2400) continue;

                var isActive = i == _state.CurrentRouteIndex;
                var color = rt.Reached ? SharpDX.Color.DarkGray
                    : rt.Skipped ? SharpDX.Color.DarkRed
                    : isActive ? SharpDX.Color.White
                    : SharpDX.Color.Gold;

                g.DrawCircleInWorld(rtWorld, isActive ? 35f : 25f, color, isActive ? 3f : 1.5f);
                g.DrawText($"{i + 1}:{rt.Label}", rtScreen + new Vector2(-20, -25), color);
            }

            // Fallback explore target (when route is exhausted)
            if (_currentExploreTarget.HasValue && _state.CurrentTarget == null)
            {
                var expWorld = Pathfinding.GridToWorld3D(gc, _currentExploreTarget.Value);
                var expScreen = cam.WorldToScreen(expWorld);
                if (expScreen.X > -200 && expScreen.X < 2400)
                {
                    g.DrawCircleInWorld(expWorld, 30f, SharpDX.Color.Cyan, 2f);
                    g.DrawText("EXPLORE", expScreen + new Vector2(-28, -25), SharpDX.Color.Cyan);
                }
            }

            // Exit marker
            if (_state.ExitPosition.HasValue)
            {
                var exitWorld = Pathfinding.GridToWorld3D(gc, _state.ExitPosition.Value);
                var exitScreen = cam.WorldToScreen(exitWorld);
                if (exitScreen.X > -200 && exitScreen.X < 2400)
                {
                    g.DrawCircleInWorld(exitWorld, 40f, SharpDX.Color.Aqua, 2f);
                    g.DrawText("EXIT", exitScreen + new Vector2(-14, -25), SharpDX.Color.Aqua);
                }
            }

            // Doors (closed = red, open = green)
            foreach (var door in _state.Doors.Values)
            {
                var doorWorld = Pathfinding.GridToWorld3D(gc, door.GridPos);
                var doorScreen = cam.WorldToScreen(doorWorld);
                if (doorScreen.X < -200 || doorScreen.X > 2400) continue;
                var isOpen = _state.OpenedEntities.Contains(door.Id);
                var doorColor = isOpen ? SharpDX.Color.Green : SharpDX.Color.Red;
                g.DrawCircleInWorld(doorWorld, 15f, doorColor, 1.5f);
                if (!isOpen)
                    g.DrawText("DOOR", doorScreen + new Vector2(-16, -20), doorColor);
            }

            // Reward chests (unopened only)
            foreach (var chest in _state.RewardChests.Values)
            {
                if (_state.OpenedEntities.Contains(chest.Id)) continue;
                var chestWorld = Pathfinding.GridToWorld3D(gc, chest.GridPos);
                var chestScreen = cam.WorldToScreen(chestWorld);
                if (chestScreen.X < -200 || chestScreen.X > 2400) continue;
                var circleColor = chest.ChestType == HeistChestType.RewardRoom ? SharpDX.Color.Gold
                    : chest.ChestType == HeistChestType.Smugglers ? SharpDX.Color.LimeGreen
                    : SharpDX.Color.Yellow;
                g.DrawCircleInWorld(chestWorld, 15f, circleColor, 1.5f);
                var chestColor = chest.ChestType == HeistChestType.RewardRoom ? SharpDX.Color.Gold
                    : chest.ChestType == HeistChestType.Smugglers ? SharpDX.Color.LimeGreen
                    : SharpDX.Color.Yellow;
                g.DrawText(chest.RewardLabel, chestScreen + new Vector2(-20, -20), chestColor);
            }

            // Companion marker
            if (_state.CompanionPosition.HasValue)
            {
                var compScreen = Pathfinding.GridToScreen(gc, _state.CompanionPosition.Value);
                if (compScreen.X > -200 && compScreen.X < 2400)
                    g.DrawText("NPC", compScreen + new Vector2(-10, -20), SharpDX.Color.Yellow);
            }

            // ═══ Curio Display Valuation Overlay ═══
            if (_curioDisplays.Count > 0)
            {
                // Find the best value for highlighting
                double bestValue = 0;
                foreach (var cd in _curioDisplays)
                    if (!cd.IsOpened && cd.ChaosValue > bestValue) bestValue = cd.ChaosValue;

                // World markers on each curio display
                foreach (var cd in _curioDisplays)
                {
                    var cdWorld = Pathfinding.GridToWorld3D(gc, cd.GridPos);
                    var cdScreen = cam.WorldToScreen(cdWorld);
                    if (cdScreen.X < -200 || cdScreen.X > 2400) continue;

                    bool isBest = !cd.IsOpened && cd.ChaosValue > 0 && cd.ChaosValue >= bestValue;
                    var color = cd.IsOpened ? SharpDX.Color.DarkGray
                        : isBest ? SharpDX.Color.LimeGreen
                        : SharpDX.Color.White;

                    var priceStr = cd.ChaosValue > 0 ? $"{cd.ChaosValue:F0}c" : "?";
                    var label = $"{cd.ItemName} — {priceStr}";

                    g.DrawCircleInWorld(cdWorld, isBest ? 30f : 20f, color, isBest ? 3f : 1.5f);
                    g.DrawText(label, cdScreen + new Vector2(-40, -30), color);
                    if (!string.IsNullOrEmpty(cd.ClassName))
                        g.DrawText($"({cd.Rarity} {cd.ClassName})", cdScreen + new Vector2(-40, -15), SharpDX.Color.Gray);
                }

                // HUD list panel (sorted by value)
                var listX = hudX;
                var listY = hudY + 8;
                g.DrawText("=== CURIO REWARDS ===", new Vector2(listX, listY), SharpDX.Color.Gold);
                listY += lineH;

                foreach (var cd in _curioDisplays)
                {
                    bool isBest = !cd.IsOpened && cd.ChaosValue > 0 && cd.ChaosValue >= bestValue;
                    var color = cd.IsOpened ? SharpDX.Color.DarkGray
                        : isBest ? SharpDX.Color.LimeGreen
                        : SharpDX.Color.White;

                    var prefix = isBest ? ">> " : "   ";
                    var priceStr = cd.ChaosValue > 0 ? $"{cd.ChaosValue:F0}c" : "?c";
                    var suffix = cd.IsOpened ? " [OPENED]" : "";
                    g.DrawText($"{prefix}{priceStr} — {cd.ItemName}{suffix}", new Vector2(listX, listY), color);
                    listY += lineH;
                }
            }

            // ═══ Minimap Route Overlay (ImGui) ═══
            RenderMinimapRoute(gc);
        }

        /// <summary>
        /// Draw numbered route targets on the large minimap using ImGui, similar to DieselBot's
        /// grid explorer overlay. Shows transparent boxes with numbers at each route target position.
        /// </summary>
        private void RenderMinimapRoute(GameController gc)
        {
            if (_state.PlannedRoute.Count == 0) return;

            try
            {
                var largeMap = gc.IngameState.IngameUi.Map.LargeMap
                    .AsObject<ExileCore.PoEMemory.Elements.SubMap>();
                if (largeMap == null || !largeMap.IsVisible) return;

                var mapCenter = largeMap.MapCenter;
                var mapScale = (float)largeMap.MapScale;
                var playerRender = gc.Player.GetComponent<Render>();
                if (playerRender == null) return;

                var playerPos = gc.Player.GridPosNum;
                var playerHeight = -playerRender.RenderStruct.Height;

                var heightData = gc.IngameState?.Data?.RawTerrainHeightData;

                var rect = gc.Window.GetWindowRectangle();
                ImGuiNET.ImGui.SetNextWindowSize(new Vector2(rect.Width, rect.Height));
                ImGuiNET.ImGui.SetNextWindowPos(new Vector2(rect.Left, rect.Top));
                ImGuiNET.ImGui.Begin("heist_route_overlay",
                    ImGuiNET.ImGuiWindowFlags.NoDecoration |
                    ImGuiNET.ImGuiWindowFlags.NoInputs |
                    ImGuiNET.ImGuiWindowFlags.NoMove |
                    ImGuiNET.ImGuiWindowFlags.NoScrollWithMouse |
                    ImGuiNET.ImGuiWindowFlags.NoSavedSettings |
                    ImGuiNET.ImGuiWindowFlags.NoFocusOnAppearing |
                    ImGuiNET.ImGuiWindowFlags.NoBringToFrontOnFocus |
                    ImGuiNET.ImGuiWindowFlags.NoBackground);

                var dl = ImGuiNET.ImGui.GetWindowDrawList();
                const float boxHalf = 12f;

                Vector2 ToMap(Vector2 gp)
                {
                    float h = GetTerrainHeight(heightData, gp);
                    return mapCenter + GridDeltaToMap(gp - playerPos, playerHeight + h, mapScale);
                }

                uint white = ImGuiNET.ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 0.9f));

                for (int i = 0; i < _state.PlannedRoute.Count; i++)
                {
                    var rt = _state.PlannedRoute[i];
                    var isActive = i == _state.CurrentRouteIndex;

                    uint fill;
                    if (rt.Reached)
                        fill = ImGuiNET.ImGui.ColorConvertFloat4ToU32(new Vector4(0f, 0.7f, 0f, 0.5f));    // green = done
                    else if (rt.Skipped)
                        fill = ImGuiNET.ImGui.ColorConvertFloat4ToU32(new Vector4(0.5f, 0f, 0f, 0.5f));    // dark red = skipped
                    else if (isActive)
                        fill = ImGuiNET.ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 0f, 0.7f));      // yellow = current
                    else
                        fill = ImGuiNET.ImGui.ColorConvertFloat4ToU32(new Vector4(0.9f, 0.7f, 0f, 0.5f));  // gold = pending

                    uint outline = ImGuiNET.ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 0.3f));

                    var center = ToMap(rt.GridPos);
                    var tl = center + new Vector2(-boxHalf, -boxHalf);
                    var br = center + new Vector2(boxHalf, boxHalf);

                    dl.AddRectFilled(tl, br, fill, 3f);
                    dl.AddRect(tl, br, outline, 3f);
                    dl.AddText(center - new Vector2(4, 6), white, (i + 1).ToString());

                    // Connect to next target with a line
                    if (i + 1 < _state.PlannedRoute.Count && !rt.Reached && !rt.Skipped)
                    {
                        var nextCenter = ToMap(_state.PlannedRoute[i + 1].GridPos);
                        uint lineColor = ImGuiNET.ImGui.ColorConvertFloat4ToU32(new Vector4(0.6f, 0.6f, 0.6f, 0.4f));
                        dl.AddLine(center, nextCenter, lineColor, 1f);
                    }
                }

                ImGuiNET.ImGui.End();
            }
            catch { }
        }

        // --- Curio Display Evaluation ---

        private class CurioDisplayInfo
        {
            public long EntityId;
            public Vector2 GridPos;
            public string ItemName = "";
            public string BaseName = "";
            public string ClassName = "";
            public string Rarity = "";
            public double ChaosValue;
            public bool IsOpened;
        }

        /// <summary>
        /// Scan nearby HeistChestPrimaryTarget entities, read their HeistRewardDisplay items,
        /// and price them via the NinjaPrice PluginBridge.
        /// </summary>
        private void ScanCurioDisplays(GameController gc)
        {
            _curioDisplays.Clear();

            Func<Entity, double>? getPrice = null;
            try { getPrice = gc.PluginBridge.GetMethod<Func<Entity, double>>("NinjaPrice.GetValue"); }
            catch { }

            foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
            {
                if (entity?.Path == null || !entity.Path.Contains("HeistChestPrimaryTarget"))
                    continue;

                var info = new CurioDisplayInfo
                {
                    EntityId = entity.Id,
                    GridPos = entity.GridPosNum,
                };

                var chest = entity.GetComponent<Chest>();
                info.IsOpened = chest?.IsOpened == true || !entity.IsTargetable;

                try
                {
                    var hrd = entity.GetComponent<HeistRewardDisplay>();
                    var rewardItem = hrd?.RewardItem;
                    if (rewardItem != null && rewardItem.IsValid)
                    {
                        var baseType = gc.Files.BaseItemTypes.Translate(rewardItem.Path);
                        info.BaseName = baseType?.BaseName ?? "";
                        info.ClassName = baseType?.ClassName ?? "";

                        var mods = rewardItem.GetComponent<Mods>();
                        if (mods != null)
                        {
                            info.Rarity = mods.ItemRarity.ToString();
                            info.ItemName = mods.UniqueName ?? info.BaseName;
                        }
                        else
                        {
                            info.ItemName = info.BaseName;
                        }

                        if (getPrice != null)
                            info.ChaosValue = getPrice(rewardItem);
                    }
                }
                catch { }

                _curioDisplays.Add(info);
            }

            // Sort by value descending
            _curioDisplays.Sort((a, b) => b.ChaosValue.CompareTo(a.ChaosValue));
        }

        // Camera projection constants for minimap overlay
        private const float GridToWorldMultiplier = 250f / 23f;
        private const double CameraAngle = 38.7 * Math.PI / 180;
        private static readonly float CamCos = (float)Math.Cos(CameraAngle);
        private static readonly float CamSin = (float)Math.Sin(CameraAngle);

        private static Vector2 GridDeltaToMap(Vector2 delta, float deltaZ, float mapScale)
        {
            deltaZ /= GridToWorldMultiplier;
            return mapScale * new Vector2(
                (delta.X - delta.Y) * CamCos,
                (deltaZ - (delta.X + delta.Y)) * CamSin);
        }

        private static float GetTerrainHeight(float[][]? heightData, Vector2 pos)
        {
            if (heightData == null) return 0f;
            int x = (int)pos.X, y = (int)pos.Y;
            if (y >= 0 && y < heightData.Length && x >= 0 && x < heightData[y].Length)
                return heightData[y][x];
            return 0f;
        }
    }

    public enum HeistPhase
    {
        Idle,
        Initializing,
        Infiltrating,
        AtDoor,
        AtChest,
        GrabCurio,
        Escaping,
        ExitingMap,
        Done
    }
}
