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
    /// <summary>
    /// Follow a named leader character through zones.
    /// Uses InteractionSystem for transition clicks, CombatSystem for fighting,
    /// and LootSystem for item pickup — same patterns as other modes.
    /// </summary>
    public class FollowerMode : IBotMode
    {
        public string Name => "Follower";

        // Configuration (synced from settings each tick by BotCore)
        public string LeaderName { get; set; } = "";
        public float FollowDistance { get; set; } = 28f;
        public float StopDistance { get; set; } = 14f;
        public float TeleportDetectDistance { get; set; } = 138f;
        public bool FollowThroughTransitions { get; set; } = true;
        public bool EnableCombat { get; set; } = true;
        public bool EnableLoot { get; set; } = true;
        public bool LootNearLeaderOnly { get; set; } = true;

        // Exposed state for F6 dump
        public FollowerState State => _state;
        public string StatusText => _status;
        public string Decision => _decision;
        public Vector2? LastLeaderGridPos => _hasLastLeaderPos ? _lastLeaderPos : null;
        public Vector2? TransitionTargetGridPos => _transitionGridPos;

        // State
        private FollowerState _state = FollowerState.SearchingForLeader;
        private string _status = "";
        private string _decision = "";
        private Vector2 _lastLeaderPos;
        private bool _hasLastLeaderPos;
        private string _lastAreaName = "";

        // LOS→pathfind hysteresis — once we switch to pathfinding, stay on the path
        // briefly before re-checking LOS to avoid flicker near terrain corners
        private DateTime _lastPathStartTime = DateTime.MinValue;
        private const float PathHysteresisMs = 500f;

        // Unreachable leader detection — consecutive pathfinding failures suggest
        // leader used a transition (visible but on other side of wall/door)
        private int _consecutivePathFailures;
        private const int PathFailuresBeforeTransition = 3;

        // Transition following
        private Vector2? _transitionGridPos; // grid pos of transition we're heading to (entity ref may go stale)
        private long _transitionEntityId;    // entity ID to re-resolve each tick

        // Party UI teleport
        private DateTime _lastPartyTeleportAttempt = DateTime.MinValue;
        private const float PartyTeleportRetrySeconds = 2.0f; // don't spam the button

        // Loot tracking — only record on confirmed pickup
        private DateTime _lastLootScan = DateTime.MinValue;
        private const float LootScanIntervalMs = 500;
        private readonly LootPickupTracker _lootTracker = new();

        // Quest interactable tracking
        private DateTime _lastQuestScan = DateTime.MinValue;
        private const float QuestScanIntervalMs = 500;
        private readonly HashSet<long> _completedQuestEntities = new(); // only added on confirmed success
        private long _pendingQuestEntityId; // currently being interacted with

        public void OnEnter(BotContext ctx)
        {
            _state = FollowerState.SearchingForLeader;
            _hasLastLeaderPos = false;
            _transitionGridPos = null;
            _transitionEntityId = 0;
            _lastAreaName = "";
            _lootTracker.Reset();
            _completedQuestEntities.Clear();
            _pendingQuestEntityId = 0;
            _decision = "";
            _status = string.IsNullOrEmpty(LeaderName)
                ? "No leader name set — configure in settings"
                : $"Searching for leader: {LeaderName}";

            // Enable combat with default positioning
            if (EnableCombat)
            {
                ctx.Combat.SetProfile(new CombatProfile
                {
                    Enabled = true,
                    Positioning = CombatPositioning.Aggressive,
                });
            }

            ctx.Log($"Follower mode active — leader: {LeaderName}");
        }

        public void OnExit()
        {
            _state = FollowerState.SearchingForLeader;
            _hasLastLeaderPos = false;
            _transitionGridPos = null;
            _transitionEntityId = 0;
            // Note: can't call ctx.Navigation.Stop() or ModeHelpers.CancelAllSystems() here
            // because OnExit() has no BotContext parameter. BotCore handles system cleanup on mode switch.
        }

        public void Tick(BotContext ctx)
        {
            if (string.IsNullOrEmpty(LeaderName))
            {
                _status = "No leader name configured";
                _decision = "idle";
                return;
            }

            var gc = ctx.Game;

            // Handle loading screens
            if (gc.IsLoading)
            {
                _state = FollowerState.WaitingForLoad;
                _status = "Loading...";
                _decision = "loading";
                ctx.Navigation.Stop(gc);
                return;
            }

            if (_state == FollowerState.WaitingForLoad)
            {
                _state = FollowerState.SearchingForLeader;
                _hasLastLeaderPos = false;
            }

            // Detect area changes — cancel all in-flight systems
            var currentArea = gc.Area?.CurrentArea?.Name ?? "";
            if (!string.IsNullOrEmpty(currentArea) && currentArea != _lastAreaName)
            {
                if (!string.IsNullOrEmpty(_lastAreaName))
                {
                    OnAreaChanged(ctx);
                }
                _lastAreaName = currentArea;
            }

            var playerGridPos = GetPlayerGrid(gc);
            var isHideout = gc.Area?.CurrentArea?.IsHideout == true;

            // In hideout: skip combat/loot/skills — only decision is teleport or portal
            if (!isHideout)
            {
                // Combat — tick every frame when enabled
                if (EnableCombat)
                {
                    // Suppress repositioning when we're navigating (following leader or heading to transition)
                    // Suppress targeted skills during loot pickup to avoid cursor interference
                    ctx.Combat.SuppressPositioning = ctx.Navigation.IsNavigating || ctx.Interaction.IsBusy;
                    ctx.Combat.SuppressTargetedSkills = ctx.Interaction.IsBusy;
                    ctx.Combat.Tick(ctx);
                }
            }

            // Tick interaction system (for transition clicks and loot pickups)
            var interactionResult = ctx.Interaction.Tick(gc);

            // Handle pending loot pickup results
            _lootTracker.HandleResult(interactionResult, ctx);

            // Handle pending quest interaction results
            if (_pendingQuestEntityId != 0 && interactionResult != InteractionResult.None
                && interactionResult != InteractionResult.InProgress)
            {
                if (interactionResult == InteractionResult.Succeeded)
                {
                    _completedQuestEntities.Add(_pendingQuestEntityId);
                    ctx.Log($"Quest interaction succeeded (id={_pendingQuestEntityId})");
                }
                else
                {
                    ctx.Log($"Quest interaction failed (id={_pendingQuestEntityId}) — will retry");
                }
                _pendingQuestEntityId = 0;
            }

            // Handle transition interaction result
            if (_state == FollowerState.ClickingTransition)
            {
                if (interactionResult == InteractionResult.Succeeded)
                {
                    _state = FollowerState.WaitingForLoad;
                    _status = "Transition clicked — waiting for load";
                    _decision = "transition_succeeded";
                    return;
                }
                else if (interactionResult == InteractionResult.Failed)
                {
                    ctx.Log("Transition click failed — searching for leader");
                    _state = FollowerState.SearchingForLeader;
                    _transitionGridPos = null;
                    _transitionEntityId = 0;
                    _decision = "transition_failed";
                }
                else if (interactionResult == InteractionResult.InProgress)
                {
                    _status = $"Clicking transition... ({ctx.Interaction.Status})";
                    _decision = "clicking_transition";
                    return;
                }
            }

            // Try to find the leader entity
            var leader = FindLeader(gc);

            if (leader != null)
            {
                HandleLeaderVisible(ctx, gc, leader, playerGridPos);
            }
            else
            {
                HandleLeaderMissing(ctx, gc, playerGridPos);
            }

            // Loot and quest interactions — skip in hideout and when busy with transitions/teleport.
            if (!isHideout
                && _state != FollowerState.NavigatingToTransition && _state != FollowerState.ClickingTransition
                && _state != FollowerState.TeleportingViaParty)
            {
                TickLoot(ctx, gc, leader, playerGridPos);

                // Quest interactables — click quest objects when leader is nearby
                if (ctx.Interaction.IsBusy)
                    ctx.Log($"QuestInteract: skipped — interaction busy ({ctx.Interaction.Status})");
                else
                    TickQuestInteractables(ctx, gc, leader, playerGridPos);
            }
        }

        private void OnAreaChanged(BotContext ctx)
        {
            ModeHelpers.CancelAllSystems(ctx);
            _lootTracker.ResetCount();
            ctx.Loot.ClearFailed();
            _completedQuestEntities.Clear();
            _pendingQuestEntityId = 0;

            _state = FollowerState.SearchingForLeader;
            _hasLastLeaderPos = false;
            _transitionGridPos = null;
            _transitionEntityId = 0;
            _consecutivePathFailures = 0;
            _status = "Area changed — searching for leader";
            _decision = "area_changed";
            ctx.Log("Follower: area changed — reset state, searching for leader");
        }

        private void HandleLeaderVisible(BotContext ctx, GameController gc, Entity leader, Vector2 playerGridPos)
        {
            var leaderGridPos = new Vector2(leader.GridPosNum.X, leader.GridPosNum.Y);

            // Check for teleport — leader moved impossibly far in one tick
            if (_hasLastLeaderPos && FollowThroughTransitions)
            {
                var leaderMoved = Vector2.Distance(_lastLeaderPos, leaderGridPos);
                if (leaderMoved > TeleportDetectDistance)
                {
                    ctx.Log($"Leader teleported ({leaderMoved:F0} grid units) — looking for portal/transition");
                    if (TryFollowLeaderExit(ctx, gc, _lastLeaderPos))
                    {
                        _lastLeaderPos = leaderGridPos;
                        _hasLastLeaderPos = true;
                        return; // Don't fall through to normal following
                    }
                }
            }

            _lastLeaderPos = leaderGridPos;
            _hasLastLeaderPos = true;

            // In hideout: don't follow the leader around — just idle and track position.
            // The only meaningful actions (portal/teleport) happen in HandleLeaderMissing.
            if (gc.Area?.CurrentArea?.IsHideout == true)
            {
                if (ctx.Navigation.IsNavigating)
                    ctx.Navigation.Stop(gc);
                _state = FollowerState.NearLeader;
                _status = $"Hideout — waiting near {LeaderName}";
                _decision = "hideout_idle";
                return;
            }

            // If we're chasing a transition but leader is visible and close, cancel that
            if (_state == FollowerState.NavigatingToTransition || _state == FollowerState.ClickingTransition)
            {
                var distToLeader = Vector2.Distance(playerGridPos, leaderGridPos);
                if (distToLeader < FollowDistance)
                {
                    _state = FollowerState.Following;
                    _transitionGridPos = null;
                    _transitionEntityId = 0;
                    ctx.Navigation.Stop(gc);
                    ctx.Interaction.Cancel(gc);
                    _decision = "leader_close_cancel_transition";
                }
                else
                {
                    // Leader visible but far (same-map transition) — continue transition pursuit.
                    // Check if navigation arrived so we can start clicking.
                    if (_state == FollowerState.NavigatingToTransition)
                        TickTransitionArrival(ctx, gc);
                    return;
                }
            }

            // Normal following
            var dist = Vector2.Distance(playerGridPos, leaderGridPos);

            if (dist > FollowDistance)
            {
                // Check walkable LOS to leader
                var hasLOS = ctx.Navigation.HasWalkableLOS(gc, playerGridPos, leaderGridPos);

                // Hysteresis: if we recently started a path, don't switch back to LOS movement
                // too quickly — prevents flicker near terrain corners
                var pathAge = (DateTime.Now - _lastPathStartTime).TotalMilliseconds;
                var preferPath = ctx.Navigation.IsNavigating && pathAge < PathHysteresisMs;

                if (hasLOS && !preferPath)
                {
                    // LOS clear — direct movement, no pathfinding
                    ctx.Navigation.MoveToward(gc, leaderGridPos);
                    _consecutivePathFailures = 0;
                    _state = FollowerState.Following;
                    _status = $"Following {LeaderName} (LOS, dist: {dist:F0})";
                    _decision = "following_los";
                }
                else if (ctx.Navigation.IsNavigating)
                {
                    // No LOS (or hysteresis active) but already have a path — graft destination
                    ctx.Navigation.UpdateDestination(gc, leaderGridPos,
                        driftThreshold: FollowDistance);
                    _consecutivePathFailures = 0;
                    _state = FollowerState.Following;
                    _status = $"Following {LeaderName} (path, dist: {dist:F0})";
                    _decision = "following_path_update";
                }
                else
                {
                    // No LOS, no active path — start fresh pathfinding
                    if (ctx.Navigation.NavigateTo(gc, leaderGridPos))
                    {
                        _lastPathStartTime = DateTime.Now;
                        _consecutivePathFailures = 0;
                    }
                    else
                    {
                        // Pathfinding failed — leader may be on other side of a transition
                        _consecutivePathFailures++;
                        if (_consecutivePathFailures >= PathFailuresBeforeTransition && FollowThroughTransitions)
                        {
                            ctx.Log($"Leader unreachable ({_consecutivePathFailures} path failures, dist={dist:F0}) — looking for transition near leader");
                            if (TryFollowLeaderExit(ctx, gc, leaderGridPos))
                            {
                                _consecutivePathFailures = 0;
                                return;
                            }
                        }
                    }
                    _state = FollowerState.Following;
                    _status = $"Following {LeaderName} (new path, dist: {dist:F0})";
                    _decision = _consecutivePathFailures > 0
                        ? $"following_path_failed ({_consecutivePathFailures}x)"
                        : "following_new_path";
                }
            }
            else if (dist < StopDistance)
            {
                // Close enough — stop and idle
                if (ctx.Navigation.IsNavigating)
                    ctx.Navigation.Stop(gc);
                _state = FollowerState.NearLeader;
                _status = $"Near {LeaderName} (dist: {dist:F0})";
                _decision = "near_leader";
            }
            else
            {
                // In the follow/stop gap — keep current navigation but don't start new.
                // If no active path, keep direct-moving toward leader when LOS is clear.
                if (!ctx.Navigation.IsNavigating)
                {
                    if (ctx.Navigation.HasWalkableLOS(gc, playerGridPos, leaderGridPos))
                        ctx.Navigation.MoveToward(gc, leaderGridPos);
                }
                _state = FollowerState.Following;
                _status = $"Following {LeaderName} (dist: {dist:F0})";
                _decision = "in_range";
            }
        }

        private void HandleLeaderMissing(BotContext ctx, GameController gc, Vector2 playerGridPos)
        {
            switch (_state)
            {
                case FollowerState.NavigatingToTransition:
                    TickTransitionArrival(ctx, gc);
                    break;

                case FollowerState.ClickingTransition:
                    // InteractionSystem is handling the click — result checked at top of Tick
                    break;

                case FollowerState.TeleportingViaParty:
                    // Waiting for the teleport click to trigger a load screen — retry if it didn't work
                    if ((DateTime.Now - _lastPartyTeleportAttempt).TotalSeconds > PartyTeleportRetrySeconds)
                    {
                        if (TryTeleportViaPartyUI(ctx, gc))
                        {
                            _decision = "party_teleport_retry";
                        }
                        else
                        {
                            _state = FollowerState.SearchingForLeader;
                            _decision = "party_teleport_timeout";
                            _status = "Party teleport failed — searching";
                        }
                    }
                    break;

                case FollowerState.SearchingForLeader:
                case var _ when _state == FollowerState.Following || _state == FollowerState.NearLeader:
                    // In town: party teleport is FIRST priority — leader could be in any map,
                    // portals here might go to the wrong place.
                    // NOT hideout — there we need portals for map device entry.
                    var areaInfo = gc.Area?.CurrentArea;
                    if (areaInfo is { IsTown: true })
                    {
                        if (TryTeleportViaPartyUI(ctx, gc))
                        {
                            // TryTeleportViaPartyUI sets state/status/decision
                        }
                        else
                        {
                            _state = FollowerState.SearchingForLeader;
                            _status = $"Searching for {LeaderName} (in town, waiting for party teleport)...";
                            _decision = "searching_town_no_teleport";
                        }
                    }
                    // In hideout: leader left through a map device portal.
                    // Use smart portal selection — match by zone name or pick 2nd nearest
                    // (nearest is the one leader used, about to vanish).
                    else if (areaInfo is { IsHideout: true })
                    {
                        if (TryFollowLeaderThroughHideoutPortal(ctx, gc))
                        {
                            // sets state/status/decision
                        }
                        else
                        {
                            _state = FollowerState.SearchingForLeader;
                            _status = $"Searching for {LeaderName} (hideout, no portals)...";
                            _decision = "searching_hideout_no_portal";
                        }
                    }
                    // In maps: try portals/transitions to follow leader through exits.
                    else if (_hasLastLeaderPos && TryFollowLeaderExit(ctx, gc, _lastLeaderPos))
                    {
                        // TryFollowLeaderExit sets state/status/decision
                    }
                    else
                    {
                        _state = FollowerState.SearchingForLeader;
                        _status = $"Searching for {LeaderName}...";
                        _decision = _hasLastLeaderPos ? "searching_no_exit" : "searching";
                    }
                    break;

                default:
                    _state = FollowerState.SearchingForLeader;
                    _status = $"Searching for {LeaderName}...";
                    _decision = "searching";
                    break;
            }
        }

        // ─── Loot ─────────────────────────────────────────────────────

        private void TickLoot(BotContext ctx, GameController gc, Entity? leader, Vector2 playerGridPos)
        {
            // Don't loot if already busy with an interaction (pickup in progress)
            if (ctx.Interaction.IsBusy)
                return;

            if (EnableLoot)
            {
                // Standard loot — use LootSystem with full filters
                // If configured, only loot when near the leader
                if (LootNearLeaderOnly && leader != null)
                {
                    var leaderGridPos = new Vector2(leader.GridPosNum.X, leader.GridPosNum.Y);
                    var distToLeader = Vector2.Distance(playerGridPos, leaderGridPos);
                    if (distToLeader > FollowDistance)
                        return;
                }

                if ((DateTime.Now - _lastLootScan).TotalMilliseconds >= LootScanIntervalMs)
                {
                    ctx.Loot.Scan(gc);
                    _lastLootScan = DateTime.Now;
                }

                if (ctx.Loot.HasLootNearby)
                {
                    var (wasInRadius, candidate) = ctx.Loot.PickupNext(ctx.Interaction, ctx.Navigation);
                    if (candidate != null && ctx.Interaction.IsBusy)
                    {
                        _lootTracker.SetPending(candidate.Entity.Id, candidate.ItemName, candidate.ChaosValue);
                        _decision = $"loot: {candidate.ItemName}";
                    }
                }
            }
            else
            {
                // Standard loot disabled — only pick up quest items
                TickQuestItemPickup(ctx, gc);
            }
        }

        /// <summary>
        /// Scan for quest items on the ground and pick them up.
        /// Used when standard loot is disabled — quest items are always grabbed
        /// so the follower doesn't block campaign/quest progression.
        /// </summary>
        private void TickQuestItemPickup(BotContext ctx, GameController gc)
        {
            if ((DateTime.Now - _lastLootScan).TotalMilliseconds < LootScanIntervalMs)
                return;
            _lastLootScan = DateTime.Now;

            try
            {
                var labels = gc.IngameState?.IngameUi?.ItemsOnGroundLabelElement?.VisibleGroundItemLabels;
                if (labels == null) return;

                foreach (var label in labels)
                {
                    if (label.Label == null || !label.Label.IsVisible || label.Entity == null)
                        continue;

                    var worldItemEntity = label.Entity;

                    // Check if this is a quest item
                    Entity? itemEntity = null;
                    if (worldItemEntity.TryGetComponent<WorldItem>(out var worldItem))
                        itemEntity = worldItem.ItemEntity;

                    if (itemEntity is not { IsValid: true })
                        continue;
                    if (!itemEntity.Path.Contains("/Quest", StringComparison.OrdinalIgnoreCase))
                        continue;

                    // Found a quest item — pick it up
                    var withinRadius = worldItemEntity.DistancePlayer <= ctx.Interaction.InteractRadius;
                    ctx.Interaction.PickupGroundItem(worldItemEntity, ctx.Navigation,
                        requireProximity: !withinRadius);

                    var itemName = label.Label.Text ?? "Quest Item";
                    _lootTracker.SetPending(worldItemEntity.Id, itemName, 0);
                    _decision = $"quest item: {itemName}";
                    return; // One at a time
                }
            }
            catch { }
        }

        // ─── Quest interactables ─────────────────────────────────────

        /// <summary>
        /// Scan for quest-relevant interactable entities and click them when leader is nearby.
        /// Covers glyph walls, switches, levers, quest pedestals, etc. — anything targetable
        /// that isn't a monster, chest, player, portal, or area transition.
        /// </summary>
        private void TickQuestInteractables(BotContext ctx, GameController gc, Entity? leader, Vector2 playerGridPos)
        {
            if ((DateTime.Now - _lastQuestScan).TotalMilliseconds < QuestScanIntervalMs)
                return;
            _lastQuestScan = DateTime.Now;

            // Only interact when leader is within follow distance (same logic as loot near leader)
            if (leader == null)
                return;
            var leaderGridPos = new Vector2(leader.GridPosNum.X, leader.GridPosNum.Y);

            Entity? bestTarget = null;
            float bestDist = float.MaxValue;

            foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
            {
                if (!entity.IsTargetable)
                    continue;

                var meta = entity.Metadata ?? "";
                var path = entity.Path ?? "";

                // Positive match: only interact with quest objects
                if (!meta.Contains("/QuestObjects/", StringComparison.OrdinalIgnoreCase) &&
                    !path.Contains("/QuestObjects/", StringComparison.OrdinalIgnoreCase))
                    continue;

                // Skip if already completed or currently being interacted with
                if (_completedQuestEntities.Contains(entity.Id) || entity.Id == _pendingQuestEntityId)
                    continue;

                var entityGridPos = new Vector2(entity.GridPosNum.X, entity.GridPosNum.Y);

                // Leader must be within follow distance of the interactable
                var leaderDist = Vector2.Distance(leaderGridPos, entityGridPos);
                if (leaderDist > FollowDistance)
                    continue;

                // Pick nearest to player
                var playerDist = Vector2.Distance(playerGridPos, entityGridPos);
                if (playerDist < bestDist)
                {
                    bestDist = playerDist;
                    bestTarget = entity;
                }
            }

            if (bestTarget == null)
                return;

            // Log click target info for debugging
            var screenPos = gc.IngameState.Camera.WorldToScreen(bestTarget.BoundsCenterPosNum);
            var posNum = bestTarget.BoundsCenterPosNum;
            ctx.Log($"QuestScan: clicking target posNum=({posNum.X:F0},{posNum.Y:F0},{posNum.Z:F0}) screen=({screenPos.X:F0},{screenPos.Y:F0}) dist={bestDist:F0}");

            // Use generous interact range — don't fight with leader-following navigation.
            // Quest objects are typically visible and clickable from follow distance.
            // If truly too far, we'll navigate; otherwise click directly.
            var needsNav = bestDist > FollowDistance;
            var started = ctx.Interaction.InteractWithEntity(bestTarget, ctx.Navigation,
                requireProximity: needsNav);

            var name = ExtractShortName(bestTarget.Metadata ?? bestTarget.Path ?? "");
            if (started)
            {
                _pendingQuestEntityId = bestTarget.Id;
                _decision = $"quest interact: {name} (dist: {bestDist:F0})";
                ctx.Log($"Follower: interacting with quest object '{name}' (id={bestTarget.Id}, dist={bestDist:F0})");
            }
            else
            {
                ctx.Log($"QuestScan: InteractWithEntity returned false for '{name}' (id={bestTarget.Id}) — interaction busy?");
            }
        }

        private static string ExtractShortName(string metadata)
        {
            if (string.IsNullOrEmpty(metadata)) return "";
            var lastSlash = metadata.LastIndexOf('/');
            return lastSlash >= 0 ? metadata[(lastSlash + 1)..] : metadata;
        }

        // ─── Transition helpers ───────────────────────────────────────

        /// <summary>
        /// Check if navigation to a transition/portal has arrived, and start clicking if so.
        /// Called from both HandleLeaderVisible (same-map transitions) and HandleLeaderMissing.
        /// </summary>
        private void TickTransitionArrival(BotContext ctx, GameController gc)
        {
            if (!ctx.Navigation.IsNavigating)
            {
                var transition = ResolveTransitionEntity(gc);
                if (transition != null)
                {
                    // Already navigated here — click directly, no redundant proximity nav
                    ctx.Interaction.InteractWithEntity(transition, ctx.Navigation,
                        requireProximity: false);
                    _state = FollowerState.ClickingTransition;
                    _status = "Arrived — clicking portal/transition";
                    _decision = "click_transition";
                }
                else
                {
                    _state = FollowerState.SearchingForLeader;
                    _transitionGridPos = null;
                    _transitionEntityId = 0;
                    _status = "Portal/transition entity gone — searching";
                    _decision = "transition_lost";
                }
            }
            else
            {
                _status = "Navigating to portal/transition";
                _decision = "nav_to_transition";
            }
        }

        /// <summary>
        /// Try to find and navigate to whatever exit the leader used.
        /// Priority: town portals (always) > area transitions (if FollowThroughTransitions enabled).
        /// Town portals are the primary party travel mechanism and should always be followed.
        /// </summary>
        private bool TryFollowLeaderExit(BotContext ctx, GameController gc, Vector2 nearGridPos)
        {
            // First: look for town portals / portals (always followed)
            var portal = FindNearestEntity(gc, nearGridPos, includePortals: true, includeTransitions: false);

            // Second: look for area transitions (only if setting enabled)
            var transition = FollowThroughTransitions
                ? FindNearestEntity(gc, nearGridPos, includePortals: false, includeTransitions: true)
                : null;

            // Pick the closer one (prefer portal if equal)
            Entity? target = null;
            if (portal != null && transition != null)
            {
                var portalDist = Vector2.Distance(nearGridPos, new Vector2(portal.GridPosNum.X, portal.GridPosNum.Y));
                var transDist = Vector2.Distance(nearGridPos, new Vector2(transition.GridPosNum.X, transition.GridPosNum.Y));
                target = portalDist <= transDist ? portal : transition;
            }
            else
            {
                target = portal ?? transition;
            }

            if (target == null)
                return false;

            return StartNavigationToEntity(ctx, gc, target);
        }

        /// <summary>
        /// Navigate to a portal/transition entity. Returns false if too far or pathfinding fails.
        /// </summary>
        private bool StartNavigationToEntity(BotContext ctx, GameController gc, Entity target)
        {
            var transGridPos = new Vector2(target.GridPosNum.X, target.GridPosNum.Y);

            // Sanity check — don't navigate to absurdly far entities
            var playerGridPos = GetPlayerGrid(gc);
            var distFromPlayer = Vector2.Distance(playerGridPos, transGridPos);
            if (distFromPlayer > Pathfinding.NetworkBubbleRadius)
            {
                ctx.Log($"Target too far ({distFromPlayer:F0} grid units) — skipping");
                return false;
            }

            _transitionGridPos = transGridPos;
            _transitionEntityId = target.Id;
            _state = FollowerState.NavigatingToTransition;
            ctx.Navigation.Stop(gc);
            ctx.Navigation.NavigateTo(gc, transGridPos, maxNodes: 200000);

            var isPortal = target.Type == EntityType.TownPortal || target.Type == EntityType.Portal;
            _status = isPortal
                ? "Leader gone — heading to portal"
                : "Leader gone — heading to area transition";
            _decision = isPortal ? "follow_portal" : "follow_transition";
            ctx.Log($"Following leader through {(isPortal ? "portal" : "transition")} (dist: {distFromPlayer:F0})");
            return true;
        }

        /// <summary>
        /// Re-resolve the transition entity by ID from the entity list.
        /// Returns null if the entity is gone or no longer targetable.
        /// </summary>
        private Entity? ResolveTransitionEntity(GameController gc)
        {
            if (_transitionEntityId == 0)
                return null;

            foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
            {
                if (entity.Id == _transitionEntityId && entity.IsTargetable)
                    return entity;
            }

            // Entity gone — try to find another transition near the cached position
            if (_transitionGridPos.HasValue)
            {
                var fallback = FindNearestEntity(gc, _transitionGridPos.Value,
                    includePortals: true, includeTransitions: true);
                if (fallback != null)
                {
                    _transitionEntityId = fallback.Id;
                    return fallback;
                }
            }

            return null;
        }

        /// <summary>
        /// Click the "teleport to player" button on the party UI for the leader.
        /// Only works in town/hideout. Returns true if the click was sent.
        /// </summary>
        private bool TryTeleportViaPartyUI(BotContext ctx, GameController gc)
        {
            // Rate-limit attempts
            if ((DateTime.Now - _lastPartyTeleportAttempt).TotalSeconds < PartyTeleportRetrySeconds)
                return false;

            if (!BotInput.CanAct)
                return false;

            try
            {
                var partyElement = gc.IngameState?.IngameUi?.PartyElement;
                if (partyElement?.IsVisible != true)
                    return false;

                var playerElements = partyElement.PlayerElements;
                if (playerElements == null || playerElements.Count == 0)
                    return false;

                foreach (var playerElement in playerElements)
                {
                    if (playerElement?.PlayerName != LeaderName)
                        continue;

                    // TeleportButton API property is broken (returns zero-rect element).
                    // The teleport icon is the last child of the player element — a small square button.
                    // Children: [0]=name, [1]=portrait, [2]=zone, [3]=teleport button
                    var childCount = (int)playerElement.ChildCount;
                    if (childCount < 1)
                        return false;

                    var teleportBtn = playerElement.GetChildAtIndex(childCount - 1);
                    if (teleportBtn == null)
                        return false;

                    var btnRect = teleportBtn.GetClientRect();
                    if (btnRect.Width < 5 || btnRect.Height < 5)
                    {
                        ctx.Log($"Party teleport: button rect too small ({btnRect})");
                        return false;
                    }

                    var windowRect = gc.Window.GetWindowRectangle();
                    var absPos = new Vector2(windowRect.X + btnRect.Center.X, windowRect.Y + btnRect.Center.Y);

                    var sent = BotInput.Click(absPos);
                    _lastPartyTeleportAttempt = DateTime.Now;

                    if (sent)
                    {
                        _state = FollowerState.TeleportingViaParty;
                        _status = $"Teleporting to {LeaderName} via party UI";
                        _decision = "party_teleport";
                        ctx.Log($"Clicked party teleport to {LeaderName} at ({absPos.X:F0},{absPos.Y:F0}), zone: {playerElement.ZoneName}");
                    }

                    return sent;
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// In hideout, find the right map device portal to follow the leader.
        /// Strategy: get leader's zone from party UI, then try to match portal RenderName.
        /// Fallback: pick 2nd nearest portal (nearest is the one leader just used, about to vanish).
        /// </summary>
        private bool TryFollowLeaderThroughHideoutPortal(BotContext ctx, GameController gc)
        {
            var portals = FindAllPortals(gc);
            if (portals.Count == 0)
                return false;

            // Try to get leader's current zone from party UI
            var leaderZone = GetLeaderZoneFromPartyUI(gc);

            // Strategy 1: match portal RenderName to leader's zone
            if (!string.IsNullOrEmpty(leaderZone))
            {
                foreach (var portal in portals)
                {
                    var renderName = portal.RenderName;
                    if (!string.IsNullOrEmpty(renderName) &&
                        leaderZone.Contains(renderName, StringComparison.OrdinalIgnoreCase))
                    {
                        ctx.Log($"Hideout: matched portal '{renderName}' to leader zone '{leaderZone}'");
                        return StartNavigationToEntity(ctx, gc, portal);
                    }
                }
            }

            // Strategy 2: pick 2nd nearest portal, skipping the one closest to leader's last position
            // (that's the one the leader used and it's about to vanish)
            if (portals.Count == 1)
            {
                // Only one portal — use it (no choice)
                ctx.Log("Hideout: only 1 portal available, using it");
                return StartNavigationToEntity(ctx, gc, portals[0]);
            }

            // Sort by distance to leader's last known position (nearest = leader's portal)
            var playerGridPos = GetPlayerGrid(gc);
            var referencePos = _hasLastLeaderPos ? _lastLeaderPos : playerGridPos;

            portals.Sort((a, b) =>
            {
                var da = Vector2.Distance(referencePos, new Vector2(a.GridPosNum.X, a.GridPosNum.Y));
                var db = Vector2.Distance(referencePos, new Vector2(b.GridPosNum.X, b.GridPosNum.Y));
                return da.CompareTo(db);
            });

            // Pick the 2nd nearest (index 1) — skip the leader's portal
            var target = portals[1];
            var targetPos = new Vector2(target.GridPosNum.X, target.GridPosNum.Y);
            ctx.Log($"Hideout: using 2nd nearest portal (skip leader's) at ({targetPos.X:F0},{targetPos.Y:F0}), " +
                    $"leader zone: '{leaderZone ?? "unknown"}'");
            return StartNavigationToEntity(ctx, gc, target);
        }

        /// <summary>
        /// Get the leader's current zone name from the party UI.
        /// Returns null if party UI isn't visible or leader not found.
        /// </summary>
        private string? GetLeaderZoneFromPartyUI(GameController gc)
        {
            try
            {
                var partyElement = gc.IngameState?.IngameUi?.PartyElement;
                if (partyElement?.IsVisible != true)
                    return null;

                var playerElements = partyElement.PlayerElements;
                if (playerElements == null)
                    return null;

                foreach (var pe in playerElements)
                {
                    if (pe?.PlayerName == LeaderName)
                        return pe.ZoneName;
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Find all targetable portals in the current area.
        /// </summary>
        private List<Entity> FindAllPortals(GameController gc)
        {
            var result = new List<Entity>();
            foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
            {
                if (!entity.IsTargetable)
                    continue;

                if (entity.Type == EntityType.TownPortal || entity.Type == EntityType.Portal
                    || IsLeagueMechanicPortal(entity))
                {
                    result.Add(entity);
                }
            }
            return result;
        }

        private Entity? FindLeader(GameController gc)
        {
            foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
            {
                if (entity.Type != EntityType.Player)
                    continue;
                var playerComp = entity.GetComponent<Player>();
                if (playerComp != null && playerComp.PlayerName == LeaderName)
                    return entity;
            }
            return null;
        }

        private Entity? FindNearestEntity(GameController gc, Vector2 nearGridPos,
            bool includePortals, bool includeTransitions)
        {
            Entity? best = null;
            float bestDist = float.MaxValue;

            foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
            {
                if (!entity.IsTargetable)
                    continue;

                var isPortal = entity.Type == EntityType.TownPortal || entity.Type == EntityType.Portal
                    || IsLeagueMechanicPortal(entity);
                var isTransition = entity.Type == EntityType.AreaTransition;

                if (isPortal && !includePortals) continue;
                if (isTransition && !includeTransitions) continue;
                if (!isPortal && !isTransition) continue;

                var entityGridPos = new Vector2(entity.GridPosNum.X, entity.GridPosNum.Y);
                var dist = Vector2.Distance(nearGridPos, entityGridPos);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = entity;
                }
            }

            return best;
        }

        /// <summary>
        /// Detect league mechanic portals that use non-standard EntityTypes (e.g., Effect, MiscellaneousObjects)
        /// instead of Portal/TownPortal/AreaTransition.
        /// </summary>
        private static bool IsLeagueMechanicPortal(Entity entity)
        {
            var path = entity.Path;
            if (string.IsNullOrEmpty(path)) return false;

            return path.Contains("SekhemaPortal")            // Faridun wishes return portal (type: Effect)
                || path.Contains("Faridun/DjinnPortal")      // Faridun wishes entry portal
                || path.Contains("HarvestPortalToggleable"); // Harvest portals (entrance + return)
        }

        private static Vector2 GetPlayerGrid(GameController gc)
        {
            var pos = gc.Player.GridPosNum;
            return new Vector2(pos.X, pos.Y);
        }

        // ─── Render ───────────────────────────────────────────────────

        public void Render(BotContext ctx)
        {
            var gfx = ctx.Graphics;
            if (gfx == null) return;

            var hudX = 100f;
            var hudY = 100f;
            var lineH = 20f;

            // Status with color-coded state
            var color = _state switch
            {
                FollowerState.NearLeader => SharpDX.Color.LimeGreen,
                FollowerState.Following => SharpDX.Color.Yellow,
                FollowerState.NavigatingToTransition or FollowerState.ClickingTransition or FollowerState.TeleportingViaParty => SharpDX.Color.Orange,
                FollowerState.SearchingForLeader => SharpDX.Color.Red,
                _ => SharpDX.Color.White
            };

            gfx.DrawText($"[Follower] {_status}", new Vector2(hudX, hudY), color);
            hudY += lineH;

            gfx.DrawText($"State: {_state} | Leader: {LeaderName}", new Vector2(hudX, hudY), SharpDX.Color.Gray);
            hudY += lineH;

            if (_decision != "")
            {
                gfx.DrawText($"Decision: {_decision}", new Vector2(hudX, hudY), SharpDX.Color.Gray);
                hudY += lineH;
            }

            if (_lootTracker.PickupCount > 0)
            {
                gfx.DrawText($"Loot: {_lootTracker.PickupCount} items", new Vector2(hudX, hudY), SharpDX.Color.Gold);
                hudY += lineH;
            }

            if (EnableCombat && ctx.Combat.InCombat)
            {
                gfx.DrawText($"Combat: {ctx.Combat.NearbyMonsterCount} nearby", new Vector2(hudX, hudY), SharpDX.Color.Red);
                hudY += lineH;
            }

            // Draw leader marker if visible
            var leader = FindLeader(ctx.Game);
            if (leader != null)
            {
                var camera = ctx.Game.IngameState.Camera;
                var leaderScreen = camera.WorldToScreen(leader.BoundsCenterPosNum);
                var windowRect = ctx.Game.Window.GetWindowRectangle();

                if (leaderScreen.X > 0 && leaderScreen.X < windowRect.Width &&
                    leaderScreen.Y > 0 && leaderScreen.Y < windowRect.Height)
                {
                    var ls = new Vector2(leaderScreen.X, leaderScreen.Y);
                    gfx.DrawLine(ls + new Vector2(-10, -10), ls + new Vector2(10, 10), 2, SharpDX.Color.Cyan);
                    gfx.DrawLine(ls + new Vector2(10, -10), ls + new Vector2(-10, 10), 2, SharpDX.Color.Cyan);
                    gfx.DrawText("LEADER", ls + new Vector2(12, -8), SharpDX.Color.Cyan);
                }
            }

            // Draw nav path if navigating
            if (ctx.Navigation.IsNavigating && ctx.Navigation.CurrentNavPath.Count > 1)
            {
                var camera = ctx.Game.IngameState.Camera;
                var playerZ = ctx.Game.Player.PosNum.Z;
                var path = ctx.Navigation.CurrentNavPath;

                for (var i = ctx.Navigation.CurrentWaypointIndex; i < path.Count - 1; i++)
                {
                    var a = path[i].Position;
                    var b = path[i + 1].Position;
                    var sa = camera.WorldToScreen(new System.Numerics.Vector3(a.X * Pathfinding.GridToWorld, a.Y * Pathfinding.GridToWorld, playerZ));
                    var sb = camera.WorldToScreen(new System.Numerics.Vector3(b.X * Pathfinding.GridToWorld, b.Y * Pathfinding.GridToWorld, playerZ));
                    var windowRect = ctx.Game.Window.GetWindowRectangle();

                    if (sa.X > 0 && sa.X < windowRect.Width && sa.Y > 0 && sa.Y < windowRect.Height)
                    {
                        var lineColor = path[i + 1].Action == WaypointAction.Blink
                            ? SharpDX.Color.Magenta : SharpDX.Color.Yellow;
                        gfx.DrawLine(new Vector2(sa.X, sa.Y), new Vector2(sb.X, sb.Y), 2, lineColor);
                    }
                }
            }
        }
    }

    public enum FollowerState
    {
        SearchingForLeader,
        Following,
        NearLeader,
        NavigatingToTransition,
        ClickingTransition,
        TeleportingViaParty,
        WaitingForLoad
    }
}
