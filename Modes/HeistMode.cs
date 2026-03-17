using ExileCore;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using AutoExile.Systems;
using AutoExile.Modes.Shared;
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

        // Companion wait tracking
        private long _waitingOnEntityId;
        private DateTime _companionWaitStart = DateTime.MinValue;
        private DateTime _lastCompanionClickTime = DateTime.MinValue;
        private int _companionClickAttempts;
        private HeistPhase _returnPhaseAfterDoor; // which phase to return to after door/chest opens

        // Loot
        private readonly LootPickupTracker _lootTracker = new();
        private DateTime _lastLootScanTime = DateTime.MinValue;

        // Navigation / exploration tracking
        private long _pendingInteractionEntityId;
        private int _lastStuckCount;           // track NavigationSystem stuck recoveries
        private Vector2? _currentExploreTarget; // current explore/pathnode target
        private readonly HashSet<Vector2> _visitedPathNodes = new(); // pathnode positions we've reached or failed

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
            // Wait 2 seconds for entities to settle after zone load
            if ((DateTime.Now - _phaseStartTime).TotalSeconds < 2)
            {
                _status = "Initializing... waiting for entities";
                Decision = "init_wait";
                return;
            }

            _state.Initialize(gc);
            ModeHelpers.EnableDefaultCombat(ctx);

            ctx.Log($"Heist initialized: {_state.Status}");

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

            // Check for blocking doors nearby
            if (!ctx.Interaction.IsBusy)
            {
                var blockingDoor = FindBlockingDoor(gc, playerGrid);
                if (blockingDoor != null)
                {
                    ctx.Navigation.Stop(gc);
                    StartDoorInteraction(ctx, gc, blockingDoor, HeistPhase.Infiltrating);
                    return;
                }
            }

            // Evaluate nearby reward chests (if alert allows)
            if (!ctx.Interaction.IsBusy && ctx.Settings.Heist.OpenRewardChests.Value)
            {
                var worthyChest = FindWorthyChest(gc, playerGrid, ctx.Settings.Heist);
                if (worthyChest != null)
                {
                    StartChestInteraction(ctx, gc, worthyChest);
                    return;
                }
            }

            // Navigate toward curio target, or explore forward if not yet visible
            // If we're already at the curio target but no actual curio entity is nearby,
            // the target was a stale HeistPathEndpoint from init — clear it and explore instead
            if (_state.CurioTargetPosition != null
                && Vector2.Distance(playerGrid, _state.CurioTargetPosition.Value) < 15
                && _state.FindCurioEntity(gc) == null)
            {
                _state.ClearCurioTarget();
            }

            if (_state.CurioTargetPosition != null)
            {
                var targetWorld = _state.CurioTargetPosition.Value * Pathfinding.GridToWorld;
                if (!ctx.Navigation.IsNavigating)
                {
                    if (!ctx.Navigation.NavigateTo(gc, targetWorld))
                    {
                        // Can't path to curio — check for blocking door first, then stepping stone
                        var nextDoor = FindNextLockedDoor(gc, playerGrid);
                        if (nextDoor != null)
                        {
                            StartDoorInteraction(ctx, gc, nextDoor, HeistPhase.Infiltrating);
                            return;
                        }

                        var stepNode = FindPathNodeToward(gc, playerGrid, _state.CurioTargetPosition.Value);
                        if (stepNode.HasValue && ctx.Navigation.NavigateTo(gc, stepNode.Value * Pathfinding.GridToWorld))
                        {
                            Decision = $"curio_step ({stepNode.Value.X:F0},{stepNode.Value.Y:F0})";
                        }
                        else
                        {
                            _status = $"Can't reach curio — no path forward";
                            Decision = "curio_blocked";
                        }
                    }
                    else
                    {
                        _status = $"Navigating to curio — alert: {_state.AlertPercent:F0}%";
                        Decision = "navigating_to_curio";
                    }
                }
                else
                {
                    // Update destination if curio position changed (e.g., actual curio entity found)
                    ctx.Navigation.UpdateDestination(gc, targetWorld, 30 * Pathfinding.GridToWorld);
                    _status = $"Navigating to curio — alert: {_state.AlertPercent:F0}%";
                    Decision = "navigating_to_curio";
                }
            }
            else
            {
                // Curio not yet visible — push deeper into the heist.
                // Strategy: navigate to farthest visible HeistPathNode from exit.
                // If no reachable nodes, find and open the next locked door to unlock new corridor.
                if (ctx.Navigation.IsNavigating && _currentExploreTarget.HasValue)
                {
                    // Mark node visited once close enough
                    if (Vector2.Distance(playerGrid, _currentExploreTarget.Value) < 20)
                        _visitedPathNodes.Add(_currentExploreTarget.Value);

                    // Stuck abandonment
                    var stuckDelta = ctx.Navigation.StuckRecoveries - _lastStuckCount;
                    if (stuckDelta >= 3)
                    {
                        _visitedPathNodes.Add(_currentExploreTarget.Value);
                        ctx.Navigation.Stop(gc);
                        _currentExploreTarget = null;
                        Decision = "pathnode_stuck";
                    }
                    else
                    {
                        _status = $"Following path — alert: {_state.AlertPercent:F0}%";
                        Decision = "pathnode_moving";
                    }
                }
                else if (!ctx.Navigation.IsNavigating)
                {
                    // Try pathnodes first
                    var bestNode = FindNextPathNode(gc, playerGrid);
                    if (bestNode.HasValue)
                    {
                        var worldTarget = bestNode.Value * Pathfinding.GridToWorld;
                        if (ctx.Navigation.NavigateTo(gc, worldTarget))
                        {
                            _currentExploreTarget = bestNode.Value;
                            _lastStuckCount = ctx.Navigation.StuckRecoveries;
                            _status = $"Following path — alert: {_state.AlertPercent:F0}%";
                            Decision = $"pathnode ({bestNode.Value.X:F0},{bestNode.Value.Y:F0})";
                        }
                        else
                        {
                            _visitedPathNodes.Add(bestNode.Value);
                            Decision = "pathnode_fail";
                        }
                    }
                    else
                    {
                        // No reachable pathnodes — find the next locked door to open
                        var nextDoor = FindNextLockedDoor(gc, playerGrid);
                        if (nextDoor != null)
                        {
                            // Navigate to the door, then interact
                            if (nextDoor.DistancePlayer < 20)
                            {
                                // Close enough — press interact key
                                ctx.Navigation.Stop(gc);
                                StartDoorInteraction(ctx, gc, nextDoor, HeistPhase.Infiltrating);
                            }
                            else
                            {
                                // Navigate to nearest walkable cell beside the door
                                var nearWalkable = ctx.Navigation.FindNearestWalkable(gc, nextDoor.GridPosNum, 20);
                                if (nearWalkable.HasValue)
                                {
                                    var doorWorld = nearWalkable.Value * Pathfinding.GridToWorld;
                                    ctx.Navigation.NavigateTo(gc, doorWorld);
                                    _currentExploreTarget = nearWalkable.Value;
                                    _lastStuckCount = ctx.Navigation.StuckRecoveries;
                                }
                                _status = $"Navigating to locked door — alert: {_state.AlertPercent:F0}%";
                                Decision = $"nav_to_door ({nextDoor.GridPosNum.X:F0},{nextDoor.GridPosNum.Y:F0})";
                            }
                        }
                        else
                        {
                            _status = "No path forward — no nodes or doors";
                            Decision = "no_path_forward";
                        }
                    }
                }
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

            // Check if it's a basic door — navigate to it and click
            if (doorEntity.Path?.Contains("Door_Basic") == true)
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
                            started = ctx.Navigation.NavigateTo(gc, nearWalkable.Value * Pathfinding.GridToWorld);
                        if (!started)
                        {
                            var stepNode = FindPathNodeToward(gc, gc.Player.GridPosNum, doorEntity.GridPosNum);
                            if (stepNode.HasValue)
                                ctx.Navigation.NavigateTo(gc, stepNode.Value * Pathfinding.GridToWorld);
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

            // Ensure we're close enough to the door for companion to work
            distToDoor = doorEntity.DistancePlayer;
            if (distToDoor > 15)
            {
                if (!ctx.Navigation.IsNavigating)
                {
                    var nearWalkable = ctx.Navigation.FindNearestWalkable(gc, doorEntity.GridPosNum, 20);
                    bool started = false;
                    if (nearWalkable.HasValue)
                        started = ctx.Navigation.NavigateTo(gc, nearWalkable.Value * Pathfinding.GridToWorld);
                    if (!started)
                    {
                        var stepNode = FindPathNodeToward(gc, gc.Player.GridPosNum, doorEntity.GridPosNum);
                        if (stepNode.HasValue)
                            ctx.Navigation.NavigateTo(gc, stepNode.Value * Pathfinding.GridToWorld);
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

            _status = $"Waiting for companion... progress: {_state.CompanionLockPickProgress:F0}% ({elapsed:F0}s) dist:{distToDoor:F0}";
            Decision = "companion_channeling";

            // Only re-press V if companion is completely idle (not busy, no progress)
            // and enough time has passed since last press. This prevents bouncing
            // between multiple nearby interactables.
            if (_state.CompanionLockPickProgress == 0 && !_state.CompanionIsBusy)
            {
                var timeSinceClick = (DateTime.Now - _lastCompanionClickTime).TotalSeconds;

                // Wait at least 5 seconds before re-pressing — companion needs time to walk
                if (timeSinceClick > 5.0)
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
                        ctx.Navigation.NavigateTo(gc, nearWalkable.Value * Pathfinding.GridToWorld);
                }
                _status = $"Moving to chest... dist: {distToChest:F0}";
                Decision = "chest_approach";
                return;
            }
            else if (ctx.Navigation.IsNavigating)
            {
                ctx.Navigation.Stop(gc);
            }

            _status = $"Opening chest... progress: {_state.CompanionLockPickProgress:F0}% ({elapsed:F0}s)";
            Decision = "chest_channeling";

            // Check if chest is heist_locked (needs companion) vs unlocked (direct click)
            var sm = chestEntity.GetComponent<StateMachine>();
            var heistLocked = HeistState.GetStateValue(sm, "heist_locked");

            if (heistLocked > 0 && _state.CompanionLockPickProgress == 0 && !_state.CompanionIsBusy)
            {
                // Companion idle — press V to assign. Wait 5s between presses.
                var timeSinceClick = (DateTime.Now - _lastCompanionClickTime).TotalSeconds;
                if (timeSinceClick > 5.0)
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
                    var exitWorld = _state.ExitPosition.Value * Pathfinding.GridToWorld;
                    if (!ctx.Navigation.NavigateTo(gc, exitWorld))
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
                            var stepWorld = stepNode.Value * Pathfinding.GridToWorld;
                            ctx.Navigation.NavigateTo(gc, stepWorld);
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
                    ctx.Navigation.NavigateTo(gc, _state.ExitPosition.Value * Pathfinding.GridToWorld);
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

            // Basic doors: click directly. NPC doors: press V once to assign companion.
            if (door.Path?.Contains("Door_Basic") == true)
            {
                if (!ctx.Interaction.IsBusy && door.DistancePlayer < 40)
                {
                    ctx.Interaction.InteractWithEntity(door, ctx.Navigation, requireProximity: false);
                    _lastCompanionClickTime = DateTime.Now;
                }
            }
            else if (door.DistancePlayer < 40)
            {
                // Press V once — TickAtDoor will wait for companion to finish
                BotInput.PressKey(ctx.Settings.Heist.CompanionInteractKey);
                _lastCompanionClickTime = DateTime.Now;
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

            // Press V once to assign companion (if close enough)
            if (chest.DistancePlayer < 40)
            {
                BotInput.PressKey(ctx.Settings.Heist.CompanionInteractKey);
                _lastCompanionClickTime = DateTime.Now;
            }

            _status = $"Opening reward chest";
            Decision = "start_chest";
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

                // Basic doors — check open state
                if (entity.Path.Contains("Door_Basic"))
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
                if (entity.Path.Contains("Door_Basic"))
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

            // Doors/chests
            var openedCount = _state.OpenedEntities.Count;
            g.DrawText($"Doors: {_state.Doors.Count} | Chests: {_state.RewardChests.Count} | Opened: {openedCount}",
                new Vector2(hudX, hudY), SharpDX.Color.White);
            hudY += lineH;

            // ═══ World Overlays ═══
            if (gc.Area.CurrentArea.IsHideout || gc.Area.CurrentArea.IsTown)
                return;

            var playerZ = gc.Player.PosNum.Z;

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
                    g.DrawLine(from, to, isBlink ? 3f : 2f, isBlink ? SharpDX.Color.Magenta : SharpDX.Color.Orange);
                }
            }

            // Curio target marker
            if (_state.CurioTargetPosition.HasValue)
            {
                var curioWorld = new Vector3(
                    _state.CurioTargetPosition.Value.X * Pathfinding.GridToWorld,
                    _state.CurioTargetPosition.Value.Y * Pathfinding.GridToWorld, playerZ);
                var curioScreen = cam.WorldToScreen(curioWorld);
                if (curioScreen.X > -200 && curioScreen.X < 2400)
                {
                    g.DrawCircleInWorld(curioWorld, 40f, SharpDX.Color.LimeGreen, 2f);
                    g.DrawText("CURIO", curioScreen + new Vector2(-18, -25), SharpDX.Color.LimeGreen);
                }
            }

            // Current pathnode target
            if (_currentExploreTarget.HasValue && _state.CurioTargetPosition == null)
            {
                var expWorld = new Vector3(
                    _currentExploreTarget.Value.X * Pathfinding.GridToWorld,
                    _currentExploreTarget.Value.Y * Pathfinding.GridToWorld, playerZ);
                var expScreen = cam.WorldToScreen(expWorld);
                if (expScreen.X > -200 && expScreen.X < 2400)
                {
                    g.DrawCircleInWorld(expWorld, 30f, SharpDX.Color.Cyan, 2f);
                    g.DrawText("PATHNODE", expScreen + new Vector2(-28, -25), SharpDX.Color.Cyan);
                }
            }

            // Exit marker
            if (_state.ExitPosition.HasValue)
            {
                var exitWorld = new Vector3(
                    _state.ExitPosition.Value.X * Pathfinding.GridToWorld,
                    _state.ExitPosition.Value.Y * Pathfinding.GridToWorld, playerZ);
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
                var doorWorld = new Vector3(
                    door.GridPos.X * Pathfinding.GridToWorld,
                    door.GridPos.Y * Pathfinding.GridToWorld, playerZ);
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
                var chestWorld = new Vector3(
                    chest.GridPos.X * Pathfinding.GridToWorld,
                    chest.GridPos.Y * Pathfinding.GridToWorld, playerZ);
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
                var compWorld = new Vector3(
                    _state.CompanionPosition.Value.X * Pathfinding.GridToWorld,
                    _state.CompanionPosition.Value.Y * Pathfinding.GridToWorld, playerZ);
                var compScreen = cam.WorldToScreen(compWorld);
                if (compScreen.X > -200 && compScreen.X < 2400)
                    g.DrawText("NPC", compScreen + new Vector2(-10, -20), SharpDX.Color.Yellow);
            }
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
