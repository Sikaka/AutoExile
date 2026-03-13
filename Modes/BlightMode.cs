using ExileCore;
using ExileCore.PoEMemory;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.Elements;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using AutoExile.Systems;
using AutoExile.Modes.Shared;
using System.Numerics;

namespace AutoExile.Modes
{
    /// <summary>
    /// Full blight farming loop:
    /// Hideout: store items → open blighted map → enter portal
    /// In map: pump → fast-forward → towers → sweep → loot → exit via portal
    /// Death: revive (handled by BotCore) → re-enter map if portals remain
    /// </summary>
    public class BlightMode : IBotMode
    {
        public string Name => "Blight";

        private BlightState _blight = new();
        private BlightPhase _phase = BlightPhase.Idle;
        private DateTime _lastActionTime = DateTime.MinValue;
        private DateTime _phaseStartTime = DateTime.Now;

        // Tower management
        private TowerAction? _towerAction;
        private DateTime _lastTowerActionEndAt = DateTime.MinValue;

        // Shared components
        private readonly LootPickupTracker _lootTracker = new();
        private readonly HideoutFlow _hideoutFlow = new();

        // Settings reference
        private BotSettings.BlightSettings _settings = new();

        // Chest opening state
        private Vector2? _currentChestTarget;

        // Sweep state
        private SweepMode _sweepMode = SweepMode.Hunt;
        private bool _returningToPump;
        private long _huntTargetId;
        private Vector2 _huntTargetPos;
        private HashSet<int> _patrolledLanes = new();
        private int _currentPatrolLane = -1;
        private int _patrolWaypointIndex;
        private List<Vector2> _patrolWaypoints = new();
        private DateTime _patrolLaneStartTime;

        // Action cooldown for major actions (pump click, fast-forward)
        private const float MajorActionCooldownMs = 500f;

        // Hideout/loop tracking
        private bool _mapCompleted;
        private string _lastMapAreaName = "";
        private const int MaxDeaths = 5; // give up after this many deaths per map

        // Public for ImGui display
        public BlightState State => _blight;
        public BlightPhase Phase => _phase;
        public string StatusText { get; private set; } = "";
        public string TowerActionStatus => _towerAction != null
            ? $"{_towerAction.CurrentPhase}: {_towerAction.Status}"
            : "";

        public void OnEnter(BotContext ctx)
        {
            _settings = ctx.Settings.Blight;
            _mapCompleted = false;
            _lastMapAreaName = "";

            // Enable combat — blight needs skills for sweep + self-defense
            ModeHelpers.EnableDefaultCombat(ctx);

            // Determine starting phase based on where we are
            var gc = ctx.Game;
            if (gc.Area.CurrentArea.IsHideout || gc.Area.CurrentArea.IsTown)
            {
                _phase = BlightPhase.InHideout;
                _phaseStartTime = DateTime.Now;
                StatusText = "In hideout — preparing";
            }
            else
            {
                // Already in a map — initialize blight state
                _blight.Reset();
                _blight.InitializeFromCurrentEntities(gc);
                _phase = BlightPhase.FindPump;
                _phaseStartTime = DateTime.Now;
                StatusText = "In map — finding pump";
            }
        }

        public void OnEntityAdded(Entity entity) => _blight.OnEntityAdded(entity);
        public void OnEntityRemoved(Entity entity, Vector2 playerPos) => _blight.OnEntityRemoved(entity, playerPos);

        public void OnExit()
        {
            _blight.Reset();
            _phase = BlightPhase.Idle;
            _towerAction = null;
        }

        public void Tick(BotContext ctx)
        {
            var gc = ctx.Game;

            // Detect area changes
            var currentArea = gc.Area?.CurrentArea?.Name ?? "";
            if (!string.IsNullOrEmpty(currentArea) && currentArea != _lastMapAreaName)
            {
                OnAreaChanged(ctx, currentArea);
                _lastMapAreaName = currentArea;
            }

            // Always tick blight state + combat when in map
            if (gc.Area?.CurrentArea != null && !gc.Area.CurrentArea.IsHideout && !gc.Area.CurrentArea.IsTown)
            {
                _blight.Tick(gc);
                ctx.Combat.Tick(gc, ctx.Settings.Build);
            }

            // Tick interaction system
            var interactionResult = ctx.Interaction.Tick(gc);

            switch (_phase)
            {
                // --- Hideout phases ---
                case BlightPhase.InHideout:
                case BlightPhase.StashItems:
                case BlightPhase.OpenMap:
                case BlightPhase.EnterPortal:
                    var hideoutSignal = _hideoutFlow.Tick(ctx);
                    StatusText = _hideoutFlow.Status;
                    if (hideoutSignal == HideoutSignal.PortalTimeout)
                    {
                        _blight.Reset();
                        _phase = BlightPhase.InHideout;
                        _phaseStartTime = DateTime.Now;
                        _hideoutFlow.Start(MapDeviceSystem.IsAnyBlightMap);
                        StatusText = "No portal found — starting new map";
                    }
                    break;

                // --- Map phases ---
                case BlightPhase.FindPump:
                    TickFindPump(ctx);
                    break;
                case BlightPhase.NavigateToPump:
                    TickNavigateToPump(ctx);
                    break;
                case BlightPhase.StartEncounter:
                    TickStartEncounter(ctx);
                    break;
                case BlightPhase.FastForward:
                    TickFastForward(ctx);
                    break;
                case BlightPhase.TowerManagement:
                    TickTowerManagement(ctx);
                    break;
                case BlightPhase.WaitForCompletion:
                    TickWaitForCompletion(ctx);
                    break;
                case BlightPhase.Sweep:
                    TickSweep(ctx);
                    break;
                case BlightPhase.OpenChests:
                    TickOpenChests(ctx, interactionResult);
                    break;
                case BlightPhase.ExitMap:
                    TickExitMap(ctx);
                    break;
                case BlightPhase.Done:
                    StatusText = _blight.EncounterSucceeded ? "Blight complete — success!" : "Blight complete — failed";
                    break;

                case BlightPhase.Idle:
                    StatusText = "Idle";
                    break;
            }
        }

        // =================================================================
        // Area change detection
        // =================================================================

        private void OnAreaChanged(BotContext ctx, string newArea)
        {
            var gc = ctx.Game;

            // Cancel all in-flight systems on any area change (bug fix)
            ModeHelpers.CancelAllSystems(ctx);
            _hideoutFlow.Cancel();

            if (gc.Area.CurrentArea.IsHideout || gc.Area.CurrentArea.IsTown)
            {
                // Arrived in hideout — decide next step
                if (_mapCompleted)
                {
                    // Map was completed, start new cycle
                    _phase = BlightPhase.InHideout;
                    _phaseStartTime = DateTime.Now;
                    _mapCompleted = false;
                    _hideoutFlow.Start(MapDeviceSystem.IsAnyBlightMap);
                    StatusText = "Back in hideout — starting new map";
                }
                else if (_blight.DeathCount > 0 && _blight.DeathCount < MaxDeaths)
                {
                    // Died and revived — try to re-enter map via portal
                    _phase = BlightPhase.EnterPortal;
                    _phaseStartTime = DateTime.Now;
                    _hideoutFlow.StartPortalReentry();
                    StatusText = $"Revived (death {_blight.DeathCount}) — re-entering map";
                }
                else if (_blight.DeathCount >= MaxDeaths)
                {
                    // Too many deaths — start fresh
                    _blight.Reset();
                    _phase = BlightPhase.InHideout;
                    _phaseStartTime = DateTime.Now;
                    _hideoutFlow.Start(MapDeviceSystem.IsAnyBlightMap);
                    StatusText = "Too many deaths — starting new map";
                }
                else
                {
                    _phase = BlightPhase.InHideout;
                    _phaseStartTime = DateTime.Now;
                    _hideoutFlow.Start(MapDeviceSystem.IsAnyBlightMap);
                }
            }
            else
            {
                // Entered a map — start looking for pump
                var deathCount = _blight.DeathCount; // preserve across reset
                _blight.Reset();
                _blight.DeathCount = deathCount;
                _blight.InitializeFromCurrentEntities(gc);
                _phase = BlightPhase.FindPump;
                _phaseStartTime = DateTime.Now;
                _towerAction = null;
                _nudgedForPump = false;
                StatusText = "Entered map — finding pump";
            }
        }

        // =================================================================
        // Map phases
        // =================================================================

        private bool _nudgedForPump;

        private void TickFindPump(BotContext ctx)
        {
            if (_blight.PumpPosition.HasValue)
            {
                _phase = BlightPhase.NavigateToPump;
                _phaseStartTime = DateTime.Now;
                _nudgedForPump = false;
                StatusText = "Pump found — navigating";
                return;
            }

            if (_blight.IsEncounterActive)
            {
                _phase = BlightPhase.TowerManagement;
                _phaseStartTime = DateTime.Now;
                _nudgedForPump = false;
                StatusText = "Encounter already active — managing towers";
                return;
            }

            var elapsed = (DateTime.Now - _phaseStartTime).TotalSeconds;

            // If pump not found after 2s, do a small movement to trigger entity loading
            // (portal can land right on the pump, which may not load until player moves)
            if (!_nudgedForPump && elapsed > 2)
            {
                _nudgedForPump = true;
                var gc = ctx.Game;
                var playerGrid = gc.Player.GridPosNum;
                var nudgeTarget = new Vector2(playerGrid.X + 5, playerGrid.Y) * Systems.Pathfinding.GridToWorld;
                ctx.Navigation.NavigateTo(gc, nudgeTarget);
                StatusText = "Nudging to trigger entity loading...";
                return;
            }

            StatusText = "Searching for blight pump...";

            if (elapsed > 30)
            {
                // Pump not found — but if we see blight entities (towers, monsters),
                // the encounter is in progress (re-entry after death). Skip to sweep.
                bool hasBlightEntities = _blight.CachedTowers.Count > 0 ||
                    _blight.CachedMonsters.Values.Any(m => m.AssumedAlive);

                if (hasBlightEntities)
                {
                    // Force encounter state so sweep/completion logic works
                    _blight.IsEncounterActive = true;
                    _blight.IsTimerDone = true;
                    _blight.TimerDoneAt ??= DateTime.Now;
                    EnterSweepPhase();
                    StatusText = "Pump not found but blight entities present — sweeping";
                    return;
                }

                StatusText = "No pump found — timeout";
                _phase = BlightPhase.Done;
            }
        }

        private void TickNavigateToPump(BotContext ctx)
        {
            if (!_blight.PumpPosition.HasValue)
            {
                _phase = BlightPhase.FindPump;
                return;
            }

            if (_blight.IsEncounterActive)
            {
                ctx.Navigation.Stop(ctx.Game);
                _phase = BlightPhase.TowerManagement;
                _phaseStartTime = DateTime.Now;
                StatusText = "Encounter active — managing towers";
                return;
            }

            var playerPos = ctx.Game.Player.GridPosNum;
            var dist = Vector2.Distance(playerPos, _blight.PumpPosition.Value);

            if (dist < 18f)
            {
                ctx.Navigation.Stop(ctx.Game);
                _phase = BlightPhase.StartEncounter;
                _phaseStartTime = DateTime.Now;
                StatusText = "Near pump — starting encounter";
                return;
            }

            if (!ctx.Navigation.IsNavigating)
            {
                var success = ctx.Navigation.NavigateTo(ctx.Game, BlightState.ToWorld(_blight.PumpPosition.Value));
                if (!success)
                {
                    StatusText = "No path to pump";
                    _phase = BlightPhase.Done;
                    return;
                }
            }

            StatusText = $"Navigating to pump (dist: {dist:F0})";
        }

        private void TickStartEncounter(BotContext ctx)
        {
            if (_blight.IsEncounterActive)
            {
                _phase = BlightPhase.FastForward;
                _phaseStartTime = DateTime.Now;
                StatusText = "Encounter started — waiting for fast-forward";
                return;
            }

            if (!ModeHelpers.CanAct(_lastActionTime, MajorActionCooldownMs)) return;

            var gc = ctx.Game;
            Entity? pump = FindPumpEntity(gc);
            if (pump == null)
            {
                StatusText = "Pump entity not found for clicking";
                return;
            }

            if (pump.TryGetComponent<StateMachine>(out var states))
            {
                bool readyToStart = false;
                foreach (var s in states.States)
                {
                    if (s.Name == "ready_to_start" && s.Value > 0)
                    {
                        readyToStart = true;
                        break;
                    }
                }

                if (!readyToStart)
                {
                    StatusText = "Waiting for pump to become ready...";
                    return;
                }
            }

            ModeHelpers.ClickEntity(gc, pump, ref _lastActionTime);
            StatusText = "Clicking pump to start encounter";

            if ((DateTime.Now - _phaseStartTime).TotalSeconds > 30)
            {
                StatusText = "Timeout starting encounter";
                _phase = BlightPhase.Done;
            }
        }

        private void TickFastForward(BotContext ctx)
        {
            if (_blight.HasClickedFastForward)
            {
                _phase = BlightPhase.TowerManagement;
                _phaseStartTime = DateTime.Now;
                StatusText = "Fast-forwarded — managing towers";
                return;
            }

            if ((DateTime.Now - _phaseStartTime).TotalSeconds < 2)
            {
                StatusText = "Waiting before fast-forward...";
                return;
            }

            if (!ModeHelpers.CanAct(_lastActionTime, MajorActionCooldownMs)) return;

            var gc = ctx.Game;
            try
            {
                var skipButton = gc.IngameState.IngameUi.LeagueMechanicButtons?.GetChildAtIndex(2);
                if (skipButton != null && skipButton.IsVisible)
                {
                    var rect = skipButton.GetClientRect();
                    var center = new Vector2(rect.Center.X, rect.Center.Y);
                    if (!DoClickRelative(gc, center)) return;
                    _blight.HasClickedFastForward = true;
                    StatusText = "Clicked fast-forward";
                }
                else
                {
                    StatusText = "Fast-forward button not visible yet";
                }
            }
            catch
            {
                StatusText = "Error finding fast-forward button";
            }

            if ((DateTime.Now - _phaseStartTime).TotalSeconds > 10)
            {
                _blight.HasClickedFastForward = true;
                _phase = BlightPhase.TowerManagement;
                StatusText = "Fast-forward timeout — managing towers";
            }
        }

        // --- Tower Management with safety positioning ---

        private void TickTowerManagement(BotContext ctx)
        {
            if (_blight.IsEncounterDone)
            {
                CancelTowerAction(ctx);
                EnterOpenChestsPhase();
                StatusText = "Encounter done — opening chests";
                return;
            }

            if (_blight.IsTimerDone)
            {
                CancelTowerAction(ctx);
                _phase = BlightPhase.WaitForCompletion;
                _phaseStartTime = DateTime.Now;
                StatusText = "Timer done — clearing remaining monsters";
                return;
            }

            TickTowerLoop(ctx);
        }

        private void TickWaitForCompletion(BotContext ctx)
        {
            if (_blight.IsEncounterDone)
            {
                CancelTowerAction(ctx);
                EnterOpenChestsPhase();
                StatusText = "Encounter complete — looting";
                return;
            }

            var sweepDelay = _settings.SweepDelayAfterTimerSeconds.Value;
            if ((DateTime.Now - _phaseStartTime).TotalSeconds > sweepDelay)
            {
                // Always enter sweep after delay — monsters may exist beyond render range
                // even if AliveMonsterCount == 0. Sweep patrols lanes to find them.
                CancelTowerAction(ctx);
                EnterSweepPhase();
                return;
            }

            // Timer is done — prioritize combat over tower actions.
            // If nearby monsters exist, cancel tower actions and let combat positioning
            // take over (Combat.Tick runs before this, but tower navigation overrides
            // combat movement each tick). Only build towers when no immediate threats.
            if (ctx.Combat.NearbyChaseCount > 0)
            {
                if (_towerAction != null)
                    CancelTowerAction(ctx);
                StatusText = $"Fighting — {ctx.Combat.NearbyChaseCount} nearby, {_blight.AliveMonsterCount} alive";
            }
            else
            {
                TickTowerLoop(ctx);
                StatusText = $"Waiting — {_blight.AliveMonsterCount} monsters alive";
            }
        }

        // --- Tower action loop with safety positioning ---

        private void TickTowerLoop(BotContext ctx)
        {
            var gc = ctx.Game;

            if (ctx.Interaction.IsBusy)
                return;

            // Tick active tower action
            if (_towerAction != null)
            {
                _towerAction.Tick(gc);
                if (_towerAction.IsComplete)
                {
                    if (_towerAction.Succeeded)
                    {
                        StatusText = _towerAction.Status;
                        // Just built/upgraded — immediately try upgrading again (stay at tower)
                        _towerAction = null;
                        if (!TryStartTowerAction(ctx, TowerAction.ActionType.Upgrade))
                        {
                            // No upgrade available — apply normal cooldown before next action
                            _lastTowerActionEndAt = DateTime.Now;
                        }
                    }
                    else
                    {
                        StatusText = $"Tower failed: {_towerAction.Status}";
                        _towerAction = null;
                        _lastTowerActionEndAt = DateTime.Now;
                    }
                }
                else
                {
                    StatusText = $"Tower: {_towerAction.CurrentPhase} — {_towerAction.Status}";
                }
                return;
            }

            // Build cooldown
            if ((DateTime.Now - _lastTowerActionEndAt).TotalMilliseconds < _settings.TowerBuildCooldownMs.Value)
            {
                // While waiting, stay near pump for safety
                TickSafetyPosition(ctx);
                StatusText = $"Tower cooldown — {_blight.LaneDebug}";
                return;
            }

            // Try upgrade first, then build
            if (!TryStartTowerAction(ctx, TowerAction.ActionType.Upgrade))
                TryStartTowerAction(ctx, TowerAction.ActionType.Build);

            if (_towerAction == null)
            {
                // No tower actions available — hold position near pump
                TickSafetyPosition(ctx);
                StatusText = $"No tower actions — {_blight.LaneDebug}";
                _lastTowerActionEndAt = DateTime.Now;
            }
        }

        /// <summary>
        /// When idle between tower actions, stay within safety range of the pump.
        /// Don't wander off — minions will clear nearby enemies.
        /// </summary>
        private void TickSafetyPosition(BotContext ctx)
        {
            if (!_blight.PumpPosition.HasValue) return;
            var gc = ctx.Game;
            var playerPos = gc.Player.GridPosNum;
            var pumpPos = _blight.PumpPosition.Value;
            var distToPump = Vector2.Distance(playerPos, pumpPos);

            // Safety radius = half of tower approach distance (stay close to pump)
            float safetyRadius = _settings.TowerApproachDistance.Value * 0.75f;

            if (distToPump > safetyRadius && !ctx.Navigation.IsNavigating)
            {
                // Move back toward pump (but not on top of it)
                var dir = Vector2.Normalize(pumpPos - playerPos);
                var targetPos = pumpPos - dir * 10f; // stand 10 grid units from pump
                ctx.Navigation.NavigateTo(gc, BlightState.ToWorld(targetPos));
            }
        }

        private bool TryStartTowerAction(BotContext ctx, TowerAction.ActionType type)
        {
            var action = new TowerAction(type, _blight, _settings, ctx.Navigation);
            action.Tick(ctx.Game);
            if (action.CurrentPhase == TowerAction.Phase.Failed)
                return false;

            _towerAction = action;
            return true;
        }

        private void CancelTowerAction(BotContext ctx)
        {
            if (_towerAction != null)
            {
                _towerAction.Cancel(ctx.Game);
                _towerAction = null;
            }
            ctx.Navigation.Stop(ctx.Game);
        }

        // =================================================================
        // Sweep — with monster avoidance
        // =================================================================

        private void EnterSweepPhase()
        {
            _phase = BlightPhase.Sweep;
            _phaseStartTime = DateTime.Now;
            _sweepMode = SweepMode.Hunt;
            _returningToPump = false;
            _huntTargetId = 0;
            _patrolledLanes.Clear();
            _currentPatrolLane = -1;
            _patrolWaypoints.Clear();
            _patrolWaypointIndex = 0;
        }

        private void TickSweep(BotContext ctx)
        {
            if (_blight.IsEncounterDone)
            {
                ctx.Navigation.Stop(ctx.Game);
                EnterOpenChestsPhase();
                StatusText = "Encounter complete — looting";
                return;
            }

            // No timeout — sweep continues until success=1 or fail=1.
            // Patrol will cycle through lanes repeatedly to find remaining monsters.

            var gc = ctx.Game;
            var playerPos = gc.Player.GridPosNum;
            var pumpPos = _blight.PumpPosition ?? playerPos;

            // Safety: if monsters near pump, rush back
            if (_blight.PumpUnderAttack)
            {
                ctx.Navigation.Stop(gc);
                if (Vector2.Distance(playerPos, pumpPos) > 9f)
                    ctx.Navigation.NavigateTo(gc, BlightState.ToWorld(pumpPos));
                _huntTargetId = 0;
                _currentPatrolLane = -1;
                _returningToPump = false;
                StatusText = "PUMP DANGER — rushing back!";
                return;
            }

            var now = DateTime.Now;
            bool hasKnownMonsters = false;
            foreach (var cm in _blight.CachedMonsters.Values)
            {
                if (cm.AssumedAlive && (cm.IsVisible || (now - cm.LastSeen).TotalSeconds <= 3.0))
                { hasKnownMonsters = true; break; }
            }

            if (hasKnownMonsters)
            {
                _sweepMode = SweepMode.Hunt;
                _returningToPump = false;
                TickSweepHunt(ctx, gc, playerPos, pumpPos);
            }
            else
            {
                if (_sweepMode == SweepMode.Hunt)
                {
                    _sweepMode = SweepMode.Patrol;
                    _huntTargetId = 0;
                    _returningToPump = true;
                }
                TickSweepPatrol(ctx, gc, playerPos, pumpPos);
            }
        }

        /// <summary>
        /// Hunt mode with monster avoidance: don't path directly into large groups.
        /// Instead, approach gradually — get within a moderate distance and let minions clear.
        /// Only close the gap when few monsters remain.
        /// </summary>
        private void TickSweepHunt(BotContext ctx, GameController gc, Vector2 playerPos, Vector2 pumpPos)
        {
            var now = DateTime.Now;
            const double STALE_SECONDS = 3.0;

            if (_huntTargetId != 0)
            {
                if (!_blight.CachedMonsters.TryGetValue(_huntTargetId, out var target) || !target.AssumedAlive
                    || (!target.IsVisible && (now - target.LastSeen).TotalSeconds > STALE_SECONDS))
                    _huntTargetId = 0;
            }

            if (_huntTargetId == 0)
            {
                float bestPathDist = float.MaxValue;
                long bestId = 0;
                Vector2 bestPos = default;

                foreach (var cm in _blight.CachedMonsters.Values)
                {
                    if (!cm.AssumedAlive) continue;
                    if (!cm.IsVisible && (now - cm.LastSeen).TotalSeconds > STALE_SECONDS) continue;

                    float pathDist = _blight.LaneTracker.EstimatePathDistanceToPump(cm.Position, pumpPos);
                    if (pathDist < bestPathDist)
                    {
                        bestPathDist = pathDist;
                        bestId = cm.EntityId;
                        bestPos = cm.Position;
                    }
                }

                if (bestId == 0)
                {
                    StatusText = "Hunt: no monsters found";
                    return;
                }

                _huntTargetId = bestId;
                _huntTargetPos = bestPos;
            }

            var distToTarget = Vector2.Distance(playerPos, _huntTargetPos);

            // Count visible monsters near our target to gauge threat density
            int nearbyMonsterCount = 0;
            foreach (var cm in _blight.CachedMonsters.Values)
            {
                if (cm.AssumedAlive && cm.IsVisible &&
                    Vector2.Distance(cm.Position, _huntTargetPos) < 30f)
                    nearbyMonsterCount++;
            }

            // Avoidance: if large group (5+), keep distance and let minions engage
            // Only approach to within ~25 grid units when group is large
            float approachDistance = nearbyMonsterCount >= 5 ? 25f : 9f;

            if (distToTarget < approachDistance)
            {
                if (nearbyMonsterCount >= 5)
                {
                    // Large group — hold position, let minions do the work
                    ctx.Navigation.Stop(gc);
                    StatusText = $"Hunt: holding — {nearbyMonsterCount} enemies nearby ({_blight.AliveMonsterCount} alive)";
                }
                else
                {
                    StatusText = $"Hunt: at target — confirming kill ({_blight.AliveMonsterCount} alive)";
                    _huntTargetId = 0;
                }
                return;
            }

            // Navigate — aim for approach distance, not directly on top
            if (!ctx.Navigation.IsNavigating)
            {
                Vector2 navTarget;
                if (nearbyMonsterCount >= 5 && distToTarget > approachDistance)
                {
                    // Approach cautiously: aim for a point approachDistance away from target
                    var dir = Vector2.Normalize(_huntTargetPos - playerPos);
                    navTarget = _huntTargetPos - dir * approachDistance;
                }
                else
                {
                    navTarget = _huntTargetPos;
                }
                ctx.Navigation.NavigateTo(gc, BlightState.ToWorld(navTarget));
            }

            StatusText = $"Hunt: approaching (dist: {distToTarget:F0}, {nearbyMonsterCount} nearby, {_blight.AliveMonsterCount} alive)";
        }

        private void TickSweepPatrol(BotContext ctx, GameController gc, Vector2 playerPos, Vector2 pumpPos)
        {
            var leash = _settings.SweepPumpLeashRadius.Value;

            if (_returningToPump)
            {
                var distToPump = Vector2.Distance(playerPos, pumpPos);
                if (distToPump < 18f)
                {
                    _returningToPump = false;
                    ctx.Navigation.Stop(gc);
                }
                else
                {
                    if (!ctx.Navigation.IsNavigating)
                        ctx.Navigation.NavigateTo(gc, BlightState.ToWorld(pumpPos));
                    StatusText = $"Returning to pump (dist: {distToPump:F0})";
                    return;
                }
            }

            if (_currentPatrolLane < 0)
            {
                var laneOrder = _blight.LaneTracker.GetLanesOrderedByDanger();
                int nextLane = -1;
                foreach (var li in laneOrder)
                {
                    if (!_patrolledLanes.Contains(li))
                    {
                        nextLane = li;
                        break;
                    }
                }

                if (nextLane < 0)
                {
                    // All lanes patrolled but encounter not done — re-patrol to find remaining monsters
                    _patrolledLanes.Clear();
                    _returningToPump = true;
                    ctx.Navigation.Stop(gc);
                    StatusText = $"All lanes patrolled — restarting ({_blight.AliveMonsterCount} alive)";
                    return;
                }

                _currentPatrolLane = nextLane;
                _patrolWaypoints = _blight.LaneTracker.GetLaneWaypointsFromPump(nextLane, pumpPos);
                _patrolWaypointIndex = 0;
                _patrolLaneStartTime = DateTime.Now;
            }

            if ((DateTime.Now - _patrolLaneStartTime).TotalSeconds > _settings.SweepPatrolLaneTimeoutSeconds.Value)
            {
                _patrolledLanes.Add(_currentPatrolLane);
                _currentPatrolLane = -1;
                _returningToPump = true;
                ctx.Navigation.Stop(gc);
                StatusText = "Patrol lane timeout — returning";
                return;
            }

            if (_patrolWaypointIndex < _patrolWaypoints.Count)
            {
                var wp = _patrolWaypoints[_patrolWaypointIndex];
                var distToWp = Vector2.Distance(playerPos, wp);
                if (distToWp < 9f)
                {
                    _patrolWaypointIndex++;
                }
                else if (!ctx.Navigation.IsNavigating)
                {
                    ctx.Navigation.NavigateTo(gc, BlightState.ToWorld(wp));
                }

                StatusText = $"Patrol L{_currentPatrolLane} wp {_patrolWaypointIndex}/{_patrolWaypoints.Count}";
            }
            else
            {
                _patrolledLanes.Add(_currentPatrolLane);
                _currentPatrolLane = -1;
                _returningToPump = true;
                ctx.Navigation.Stop(gc);
            }
        }

        // =================================================================
        // Chest + Loot phase
        // =================================================================

        private DateTime _lastEmptyScanAt = DateTime.MinValue;
        private const float LootTimeoutSeconds = 120f;
        private const float EmptyGraceSeconds = 5f;

        private void EnterOpenChestsPhase()
        {
            _phase = BlightPhase.OpenChests;
            _phaseStartTime = DateTime.Now;
            _currentChestTarget = null;
            _lootTracker.Reset();
            _lastEmptyScanAt = DateTime.MinValue;
        }

        private void TickOpenChests(BotContext ctx, InteractionResult interactionResult)
        {
            // Handle completed loot pickup — record only on confirmed success
            _lootTracker.HandleResult(interactionResult, ctx);

            if (interactionResult == InteractionResult.Succeeded || interactionResult == InteractionResult.Failed)
                _currentChestTarget = null;

            if ((DateTime.Now - _phaseStartTime).TotalSeconds > LootTimeoutSeconds)
            {
                EnterExitMapPhase(ctx);
                StatusText = $"Chest+loot timeout — exiting map ({_lootTracker.PickupCount} items)";
                return;
            }

            if (ctx.Interaction.IsBusy) return;

            var gc = ctx.Game;
            var playerPos = gc.Player.GridPosNum;

            // Priority 1: Pick up visible loot (failed items filtered at scan time)
            ctx.Loot.Scan(gc);
            var best = ctx.Loot.GetBestCandidate();
            if (best != null)
            {
                _lastEmptyScanAt = DateTime.MinValue;
                var withinRadius = best.Distance <= ctx.Loot.LootRadius;
                ctx.Interaction.PickupGroundItem(best.Entity, ctx.Navigation,
                    requireProximity: !withinRadius);
                _lootTracker.SetPending(best.Entity.Id, best.ItemName, best.ChaosValue);
                StatusText = $"Picking up loot ({ctx.Loot.Candidates.Count} visible, {_lootTracker.PickupCount} picked, {_blight.ChestPositions.Count} chests left)";
                return;
            }

            // Priority 2: Open nearest visible chest
            Entity? nearestChest = null;
            float nearestDist = float.MaxValue;

            foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
            {
                if (entity.Type != EntityType.Chest || entity.IsOpened) continue;
                var chestPos = entity.GridPosNum;
                if (!_blight.ChestPositions.Contains(chestPos)) continue;
                var dist = Vector2.Distance(playerPos, chestPos);
                if (dist < nearestDist)
                {
                    nearestDist = dist;
                    nearestChest = entity;
                }
            }

            if (nearestChest != null)
            {
                _lastEmptyScanAt = DateTime.MinValue;
                ctx.Interaction.InteractWithEntity(nearestChest, ctx.Navigation);
                _currentChestTarget = nearestChest.GridPosNum;
                StatusText = $"Opening chest (dist: {nearestDist:F0}, {_blight.ChestPositions.Count} remaining)";
                return;
            }

            // Priority 3: Navigate to cached off-screen chest
            if (_blight.ChestPositions.Count > 0)
            {
                Vector2? nearestCachedChest = null;
                float bestDist = float.MaxValue;
                foreach (var pos in _blight.ChestPositions)
                {
                    var d = Vector2.Distance(playerPos, pos);
                    if (d < bestDist) { bestDist = d; nearestCachedChest = pos; }
                }

                if (nearestCachedChest.HasValue)
                {
                    if (bestDist < 25f)
                    {
                        _blight.ChestPositions.Remove(nearestCachedChest.Value);
                        StatusText = $"Stale chest removed (was at dist {bestDist:F0}, {_blight.ChestPositions.Count} remaining)";
                        return;
                    }

                    if (!ctx.Navigation.IsNavigating)
                    {
                        _lastEmptyScanAt = DateTime.MinValue;
                        ctx.Navigation.NavigateTo(gc, BlightState.ToWorld(nearestCachedChest.Value));
                        StatusText = $"Navigating to chest (dist: {bestDist:F0}, {_blight.ChestPositions.Count} remaining)";
                        return;
                    }
                }

                if (ctx.Navigation.IsNavigating)
                {
                    StatusText = $"Walking to chest area ({_blight.ChestPositions.Count} remaining)";
                    return;
                }
            }

            // Grace period
            if (_lastEmptyScanAt == DateTime.MinValue)
                _lastEmptyScanAt = DateTime.Now;

            var emptySince = (DateTime.Now - _lastEmptyScanAt).TotalSeconds;

            if (emptySince >= EmptyGraceSeconds)
            {
                ctx.Navigation.Stop(gc);
                EnterExitMapPhase(ctx);
                StatusText = $"Looting complete — exiting map ({_lootTracker.PickupCount} items)";
                return;
            }

            StatusText = $"Searching for remaining loot... ({_lootTracker.PickupCount} picked)";
        }

        // =================================================================
        // Exit Map — navigate to cached portal and click it
        // =================================================================

        private void EnterExitMapPhase(BotContext ctx)
        {
            _phase = BlightPhase.ExitMap;
            _phaseStartTime = DateTime.Now;
            _mapCompleted = true;
            _blight.MapComplete = true;
            ctx.LootTracker.RecordMapComplete();
            StatusText = "Exiting map via portal";
        }

        private void TickExitMap(BotContext ctx)
        {
            var gc = ctx.Game;

            // Already in hideout? Done.
            if (gc.Area.CurrentArea.IsHideout)
                return;

            if ((DateTime.Now - _phaseStartTime).TotalSeconds > 30)
            {
                _phase = BlightPhase.Done;
                StatusText = "Exit timeout — giving up";
                return;
            }

            if (!ModeHelpers.CanAct(_lastActionTime, MajorActionCooldownMs)) return;

            // Try to find live portal entity first
            Entity? portal = ModeHelpers.FindNearestPortal(gc);
            var playerPos = gc.Player.GridPosNum;

            if (portal != null)
            {
                var portalGridPos = portal.GridPosNum;
                var dist = Vector2.Distance(playerPos, portalGridPos);

                if (dist > 18f)
                {
                    if (!ctx.Navigation.IsNavigating)
                        ctx.Navigation.NavigateTo(gc, BlightState.ToWorld(portalGridPos));
                    StatusText = $"Walking to portal (dist: {dist:F0})";
                    return;
                }

                // Click portal
                ctx.Navigation.Stop(gc);
                ModeHelpers.ClickEntity(gc, portal, ref _lastActionTime);
                StatusText = "Clicking portal to exit";
                return;
            }

            // No live portal — use cached position
            if (_blight.PortalPosition.HasValue)
            {
                var cachedPos = _blight.PortalPosition.Value;
                var dist = Vector2.Distance(playerPos, cachedPos);

                if (dist > 18f)
                {
                    if (!ctx.Navigation.IsNavigating)
                        ctx.Navigation.NavigateTo(gc, BlightState.ToWorld(cachedPos));
                    StatusText = $"Walking to cached portal (dist: {dist:F0})";
                    return;
                }

                // We're close — portal entity should be visible now
                StatusText = "Near cached portal — waiting for entity to appear";
                return;
            }

            StatusText = "No portal found — waiting";
        }

        // =================================================================
        // Render
        // =================================================================

        public void Render(BotContext ctx)
        {
            if (ctx.Graphics == null) return;
            var gc = ctx.Game;
            var cam = gc.IngameState.Camera;
            var g = ctx.Graphics;

            // --- HUD ---
            var hudY = 100f;
            var hudX = 20f;
            var lineH = 16f;

            g.DrawText($"Phase: {_phase}", new Vector2(hudX, hudY), SharpDX.Color.White);
            hudY += lineH;
            g.DrawText(StatusText, new Vector2(hudX, hudY), SharpDX.Color.LightGreen);
            hudY += lineH;

            if (_blight.IsEncounterActive)
            {
                g.DrawText($"Timer: {_blight.CountdownText}", new Vector2(hudX, hudY), SharpDX.Color.Cyan);
                hudY += lineH;
            }

            if (_blight.Currency > 0)
            {
                g.DrawText($"Currency: {_blight.Currency:N0}", new Vector2(hudX, hudY), SharpDX.Color.Gold);
                hudY += lineH;
            }

            if (_blight.DeathCount > 0)
            {
                g.DrawText($"Deaths: {_blight.DeathCount}", new Vector2(hudX, hudY), SharpDX.Color.Red);
                hudY += lineH;
            }

            if (!string.IsNullOrEmpty(_blight.LaneDebug))
            {
                g.DrawText(_blight.LaneDebug, new Vector2(hudX, hudY), SharpDX.Color.Gray);
                hudY += lineH;
            }

            var towerStatus = _towerAction != null
                ? $"Tower: {_towerAction.CurrentPhase} — {_towerAction.Status}"
                : "";
            if (!string.IsNullOrEmpty(towerStatus))
            {
                g.DrawText(towerStatus, new Vector2(hudX, hudY), SharpDX.Color.Orange);
                hudY += lineH;
            }

            if (_phase == BlightPhase.OpenChests)
            {
                g.DrawText($"Loot: {ctx.Loot.LootableCount} visible, {_lootTracker.PickupCount} picked, {_blight.ChestPositions.Count} chests", new Vector2(hudX, hudY), SharpDX.Color.Gold);
                hudY += lineH;
            }

            if (ctx.Interaction.IsBusy)
            {
                g.DrawText($"Interact: {ctx.Interaction.Status}", new Vector2(hudX, hudY), SharpDX.Color.Yellow);
                hudY += lineH;
            }

            // --- World drawing (only in map) ---
            if (gc.Area.CurrentArea.IsHideout || gc.Area.CurrentArea.IsTown)
                return;

            var playerZ = gc.Player.PosNum.Z;

            // Pump + build radius
            if (_blight.PumpPosition.HasValue)
            {
                var pumpWorld = BlightState.ToWorld3(_blight.PumpPosition.Value, playerZ);
                g.DrawText("PUMP", cam.WorldToScreen(pumpWorld), SharpDX.Color.Yellow);
                g.DrawCircleInWorld(pumpWorld, 30f, SharpDX.Color.Yellow, 2f);

                float buildRadiusWorld = _settings.TowerBuildRadius.Value * Systems.Pathfinding.GridToWorld;
                g.DrawCircleInWorld(pumpWorld, buildRadiusWorld, new SharpDX.Color(255, 200, 0, 40), 1.5f);
            }

            // Active tower target
            if (_towerAction != null && !_towerAction.IsComplete)
            {
                var targetWorld = BlightState.ToWorld3(_towerAction.TargetGridPos, playerZ);
                var targetScreen = cam.WorldToScreen(targetWorld);
                g.DrawCircleInWorld(targetWorld, 25f, SharpDX.Color.Gold, 3f);
                g.DrawText("TARGET", targetScreen + new Vector2(-20, -25), SharpDX.Color.Gold);
            }

            // Cached portal
            if (_blight.PortalPosition.HasValue)
            {
                var portalWorld = BlightState.ToWorld3(_blight.PortalPosition.Value, playerZ);
                var portalScreen = cam.WorldToScreen(portalWorld);
                g.DrawText("PORTAL", portalScreen + new Vector2(-20, -15), SharpDX.Color.Aqua);
                g.DrawCircleInWorld(portalWorld, 20f, SharpDX.Color.Aqua, 1.5f);
            }

            // Chests
            foreach (var chestPos in _blight.ChestPositions)
            {
                var chestScreen = cam.WorldToScreen(BlightState.ToWorld3(chestPos, playerZ));
                g.DrawText("C", chestScreen, SharpDX.Color.Gold);
            }

            // Lanes
            var laneTracker = _blight.LaneTracker;
            if (laneTracker.HasLaneData)
            {
                for (int i = 0; i < laneTracker.Lanes.Count; i++)
                {
                    var lane = laneTracker.Lanes[i];
                    if (lane.Count == 0) continue;

                    var color = i == laneTracker.MostDangerousLane
                        ? SharpDX.Color.Red
                        : SharpDX.Color.LightGreen;

                    g.DrawText($"L{i}", cam.WorldToScreen(BlightState.ToWorld3(lane[0], playerZ)), color);
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
                    g.DrawLine(from, to, 1.5f, SharpDX.Color.CornflowerBlue);
                }
            }

            // Danger indicators
            if (_blight.PumpUnderAttack)
            {
                g.DrawText("PUMP UNDER ATTACK!", new Vector2(hudX, hudY), SharpDX.Color.Red);
                hudY += lineH;
            }
            g.DrawText($"Monsters: {_blight.AliveMonsterCount}", new Vector2(hudX, hudY), SharpDX.Color.Gray);
            hudY += lineH;

            if (_phase == BlightPhase.Sweep)
            {
                var sweepInfo = _sweepMode == SweepMode.Hunt
                    ? $"Sweep: HUNT ({_blight.AliveMonsterCount} alive)"
                    : $"Sweep: PATROL L{_currentPatrolLane} wp {_patrolWaypointIndex}/{_patrolWaypoints.Count} ({_patrolledLanes.Count}/{_blight.LaneTracker.Lanes.Count} done)";
                g.DrawText(sweepInfo, new Vector2(hudX, hudY), SharpDX.Color.Orange);
                hudY += lineH;
            }

            if (_phase == BlightPhase.OpenChests)
            {
                var candidates = ctx.Loot.Candidates;
                for (int i = 0; i < candidates.Count && i < 10; i++)
                {
                    var c = candidates[i];
                    var itemWorld = new Vector3(c.Entity.PosNum.X, c.Entity.PosNum.Y, c.Entity.PosNum.Z);
                    var itemScreen = cam.WorldToScreen(itemWorld);
                    var labelColor = i == 0 ? SharpDX.Color.Lime : SharpDX.Color.White;
                    g.DrawText($"[{i}] {c.ItemName} ({c.Distance:F0})", itemScreen + new Vector2(0, -20), labelColor);
                    if (i == 0)
                        g.DrawCircleInWorld(itemWorld, 15f, SharpDX.Color.Lime, 2f);
                }
            }
        }

        // =================================================================
        // Helpers
        // =================================================================

        private Entity? FindPumpEntity(GameController gc)
        {
            foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
            {
                if (entity.Type == EntityType.IngameIcon &&
                    entity.Path != null &&
                    entity.Path.EndsWith("/BlightPump"))
                    return entity;
            }
            return null;
        }

        private bool DoClick(Vector2 absPos)
        {
            if (!BotInput.CanAct) return false;
            BotInput.Click(absPos);
            _lastActionTime = DateTime.Now;
            return true;
        }

        private bool DoClickRelative(GameController gc, Vector2 windowRelativePos)
        {
            var windowRect = gc.Window.GetWindowRectangle();
            var absPos = new Vector2(windowRect.X + windowRelativePos.X, windowRect.Y + windowRelativePos.Y);
            return DoClick(absPos);
        }

    }

    public enum BlightPhase
    {
        Idle,

        // Hideout phases
        InHideout,
        StashItems,
        OpenMap,
        EnterPortal,   // re-enter map after death

        // Map phases
        FindPump,
        NavigateToPump,
        StartEncounter,
        FastForward,
        TowerManagement,
        WaitForCompletion,
        Sweep,
        OpenChests,
        ExitMap,
        Done,
    }

    public enum SweepMode
    {
        Hunt,
        Patrol,
    }
}
