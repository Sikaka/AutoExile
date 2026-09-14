using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ExileCore;
using ExileCore.PoEMemory;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.Elements;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using AutoExile.Systems;
using AutoExile.Modes.Shared;

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
        private DateTime _chestNavStartedAt = DateTime.MinValue;
        private const float ChestNavTimeoutSeconds = 30f;
        private readonly HashSet<int> _lootedLaneIndices = new();
        private int _currentLootLaneIndex;
        private DateTime _chestOpenedDelayUntil = DateTime.MinValue;
        private const float LootEndpointSkipRadius = 65f;

        // Sweep state
        private enum SweepSubPhase { PatrolLaneOutward, ReturnToPump }
        private SweepSubPhase _sweepSubPhase = SweepSubPhase.PatrolLaneOutward;
        private int _currentPatrolLaneIndex;
        private readonly HashSet<int> _sweptLaneIndices = new();
        private DateTime _lanePatrolStartedAt = DateTime.MinValue;
        private const float LanePatrolTimeoutSeconds = 25f;
        private const float EndpointOverlapRadius = 40f;
        private DateTime _sweepCombatEngageTime = DateTime.MinValue;
        private int _sweepCombatEngageCount;
        private const float SweepCombatStuckSeconds = 15f;

        // Pump click verification
        private int _pumpClickAttempts;
        private DateTime _lastPumpClickAt = DateTime.MinValue;
        private const int MaxPumpClickAttempts = 6;
        private const float PumpClickVerifyDelayMs = 1500f;

        // Action cooldown for major actions (pump click, fast-forward)
        private const float MajorActionCooldownMs = 500f;

        // Hideout/loop tracking
        private bool _mapCompleted;
        private string _lastMapAreaName = "";
        private const int MaxDeaths = 5;

        // Public for ImGui display
        public BlightState State => _blight;
        public BlightPhase Phase => _phase;
        public string StatusText { get; private set; } = "";
        public string TowerActionStatus => _towerAction != null
            ? $"{_towerAction.CurrentPhase}: {_towerAction.Status}"
            : "";

        private void StartHideoutFlow(BotContext ctx)
        {
            bool ravaged = _settings.RunBlightRavaged.Value;
            string mapIdentifier = ravaged ? StashSystem.BlightRavagedMapIdentifier : StashSystem.BlightMapIdentifier;
            string targetMapName = "Blighted Lands";

            _hideoutFlow.Start(
                mapFilter: ravaged ? MapDeviceSystem.IsBlightRavagedMap : MapDeviceSystem.IsBlightedMap,
                stashItemFilter: item => !StashSystem.IsBlightMapEntity(item.Item, ravagedOnly: ravaged),
                targetMapName: targetMapName,
                inventoryFragmentPath: mapIdentifier,
                dumpTabName: ctx.Settings.Stash.DumpTabName.Value,
                resourceTabName: _settings.BlightMapTabName.Value,
                withdrawFragmentPath: mapIdentifier,
                fragmentStock: _settings.BlightMapStock.Value,
                minFragments: 1,
                stashItemThreshold: 0
            );
        }

        public void OnEnter(BotContext ctx)
        {
            _settings = ctx.Settings.Blight;
            _mapCompleted = false;
            _lastMapAreaName = "";

            ModeHelpers.EnableDefaultCombat(ctx);

            var gc = ctx.Game;
            if (gc.Area.CurrentArea.IsHideout || gc.Area.CurrentArea.IsTown)
            {
                _phase = BlightPhase.InHideout;
                _phaseStartTime = DateTime.Now;
                StartHideoutFlow(ctx);
                StatusText = "In hideout — preparing";
            }
            else
            {
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

            var currentArea = gc.Area?.CurrentArea?.Name ?? "";
            if (!string.IsNullOrEmpty(currentArea) && currentArea != _lastMapAreaName)
            {
                OnAreaChanged(ctx, currentArea);
                _lastMapAreaName = currentArea;
            }

            if (gc.Area?.CurrentArea != null && !gc.Area.CurrentArea.IsHideout && !gc.Area.CurrentArea.IsTown)
            {
                _blight.Tick(gc);

                bool allowCombatMovement = ((_phase == BlightPhase.WaitForCompletion && !_settings.StandAtTower.Value && _towerAction == null)
                    || (_phase == BlightPhase.Sweep && _sweepSubPhase != SweepSubPhase.ReturnToPump))
                    && ctx.Combat.NearbyMonsterCount > 0;
                ctx.Combat.SuppressPositioning = !allowCombatMovement;

                bool inEncounterPhase = _phase is BlightPhase.TowerManagement or BlightPhase.WaitForCompletion or BlightPhase.Sweep;
                if (inEncounterPhase && _blight.DefensePosition.HasValue)
                {
                    ctx.Combat.Profile.DefenseAnchor = _blight.DefensePosition.Value;
                    ctx.Combat.Profile.LeashAnchor = _blight.DefensePosition.Value;
                    ctx.Combat.Profile.LeashRadius = (_settings.StandAtTower.Value && _phase != BlightPhase.Sweep)
                        ? _settings.SweepPumpRadius.Value
                        : Systems.Pathfinding.NetworkBubbleRadius;
                }
                else
                {
                    ctx.Combat.Profile.DefenseAnchor = null;
                    ctx.Combat.Profile.LeashAnchor = null;
                }

                ctx.Combat.Tick(ctx);
            }

            var interactionResult = ctx.Interaction.Tick(gc);

            switch (_phase)
            {
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
                        StartHideoutFlow(ctx);
                        StatusText = "No portal found — starting new map";
                    }
                    else if (hideoutSignal == HideoutSignal.NoFragments)
                    {
                        StatusText = "Out of Blighted Maps in stash tab and inventory — stopped";
                        _phase = BlightPhase.Done;
                    }
                    break;

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

        private void OnAreaChanged(BotContext ctx, string newArea)
        {
            var gc = ctx.Game;

            ModeHelpers.CancelAllSystems(ctx);
            _hideoutFlow.Cancel();

            if (gc.Area.CurrentArea.IsHideout || gc.Area.CurrentArea.IsTown)
            {
                if (_mapCompleted)
                {
                    _phase = BlightPhase.InHideout;
                    _phaseStartTime = DateTime.Now;
                    _mapCompleted = false;
                    StartHideoutFlow(ctx);
                    StatusText = "Back in hideout — starting new map";
                }
                else if (_blight.DeathCount > 0 && _blight.DeathCount < MaxDeaths)
                {
                    _phase = BlightPhase.EnterPortal;
                    _phaseStartTime = DateTime.Now;
                    _hideoutFlow.StartPortalReentry();
                    StatusText = $"Revived (death {_blight.DeathCount}) — re-entering map";
                }
                else if (_blight.DeathCount >= MaxDeaths)
                {
                    _blight.Reset();
                    _phase = BlightPhase.InHideout;
                    _phaseStartTime = DateTime.Now;
                    StartHideoutFlow(ctx);
                    StatusText = "Too many deaths — starting new map";
                }
                else
                {
                    _phase = BlightPhase.InHideout;
                    _phaseStartTime = DateTime.Now;
                    StartHideoutFlow(ctx);
                }
            }
            else
            {
                var deathCount = _blight.DeathCount;
                var portalPos = _blight.PortalPosition;
                _blight.Reset();
                _blight.DeathCount = deathCount;
                _blight.PortalPosition = portalPos;
                _blight.InitializeFromCurrentEntities(gc);
                _phase = BlightPhase.FindPump;
                _phaseStartTime = DateTime.Now;
                _towerAction = null;
                _nudgedForPump = false;
                StatusText = "Entered map — finding pump";
            }
        }

        private bool _nudgedForPump;

        private void TickFindPump(BotContext ctx)
        {
            var gc = ctx.Game;

            if (!_blight.PumpPosition.HasValue)
                _blight.ScanForPump(gc);

            if (_blight.IsEncounterActive)
            {
                _phase = _blight.IsTimerDone ? BlightPhase.WaitForCompletion : BlightPhase.TowerManagement;
                _phaseStartTime = DateTime.Now;
                _nudgedForPump = false;
                StatusText = "Encounter already active — resuming";
                return;
            }

            if (_blight.PumpPosition.HasValue)
            {
                _phase = BlightPhase.NavigateToPump;
                _phaseStartTime = DateTime.Now;
                _nudgedForPump = false;
                StatusText = "Pump found — navigating";
                return;
            }

            var elapsed = (DateTime.Now - _phaseStartTime).TotalSeconds;

            if (!_nudgedForPump && elapsed > 2)
            {
                _nudgedForPump = true;
                var playerGrid = gc.Player.GridPosNum;
                var nudgeTarget = new Vector2(playerGrid.X + 5, playerGrid.Y);
                ctx.Navigation.MoveToward(gc, nudgeTarget);
                StatusText = "Nudging to trigger entity loading...";
                return;
            }

            StatusText = $"Searching for blight pump ({elapsed:F0}s)...";

            if (elapsed > 30)
            {
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
                var pump = FindPumpEntity(ctx.Game);
                bool confirmed = (pump != null && IsPumpActivated(pump))
                    || (pump == null && _blight.AliveMonsterCount > 5);

                if (confirmed)
                {
                    ctx.Navigation.Stop(ctx.Game);
                    _phase = BlightPhase.TowerManagement;
                    _phaseStartTime = DateTime.Now;
                    StatusText = "Encounter confirmed — managing towers";
                    return;
                }

                _blight.IsEncounterActive = false;
            }

            var playerPos = ctx.Game.Player.GridPosNum;
            var dist = Vector2.Distance(playerPos, _blight.PumpPosition.Value);

            if (dist < 18f)
            {
                ctx.Navigation.Stop(ctx.Game);
                _phase = BlightPhase.StartEncounter;
                _phaseStartTime = DateTime.Now;
                _pumpClickAttempts = 0;
                StatusText = "Near pump — starting encounter";
                return;
            }

            if (!ctx.Navigation.IsNavigating)
            {
                var success = ctx.Navigation.NavigateTo(ctx.Game, _blight.PumpPosition.Value);
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
            var gc = ctx.Game;
            Entity? pump = FindPumpEntity(gc);

            if (pump != null && IsPumpActivated(pump))
            {
                _phase = BlightPhase.FastForward;
                _phaseStartTime = DateTime.Now;
                _pumpClickAttempts = 0;
                StatusText = "Encounter confirmed (activated) — waiting for fast-forward";
                return;
            }

            if (pump == null && _blight.IsEncounterActive && _blight.AliveMonsterCount > 5)
            {
                _phase = BlightPhase.FastForward;
                _phaseStartTime = DateTime.Now;
                _pumpClickAttempts = 0;
                StatusText = "Encounter confirmed (monsters spawning) — waiting for fast-forward";
                return;
            }

            if (_blight.IsEncounterActive && pump != null && !IsPumpActivated(pump))
            {
                _blight.IsEncounterActive = false;
            }

            if (_pumpClickAttempts > 0)
            {
                var msSinceClick = (DateTime.Now - _lastPumpClickAt).TotalMilliseconds;
                if (msSinceClick < PumpClickVerifyDelayMs)
                {
                    StatusText = $"Verifying pump click ({_pumpClickAttempts}/{MaxPumpClickAttempts}, {msSinceClick:F0}ms)...";
                    return;
                }
            }

            if (_pumpClickAttempts >= MaxPumpClickAttempts)
            {
                StatusText = $"Failed to start encounter after {MaxPumpClickAttempts} click attempts";
                _phase = BlightPhase.Done;
                return;
            }

            if (!ModeHelpers.CanAct(_lastActionTime, MajorActionCooldownMs)) return;

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
            _pumpClickAttempts++;
            _lastPumpClickAt = DateTime.Now;
            StatusText = $"Clicking pump to start encounter (attempt {_pumpClickAttempts}/{MaxPumpClickAttempts})";

            if ((DateTime.Now - _phaseStartTime).TotalSeconds > 30)
            {
                StatusText = "Timeout starting encounter";
                _phase = BlightPhase.Done;
            }
        }

        private static bool IsPumpActivated(Entity pump)
        {
            if (!pump.TryGetComponent<StateMachine>(out var states))
                return false;
            foreach (var s in states.States)
            {
                if (s.Name == "activated" && s.Value > 0)
                    return true;
            }
            return false;
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

            if ((DateTime.Now - _phaseStartTime).TotalSeconds < ctx.Settings.AreaSettleSeconds.Value)
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
                if (_blight.IsEncounterActive)
                {
                    _blight.HasClickedFastForward = true;
                    _phase = BlightPhase.TowerManagement;
                    StatusText = "Fast-forward timeout — managing towers";
                }
                else
                {
                    _phase = BlightPhase.StartEncounter;
                    _phaseStartTime = DateTime.Now;
                    _pumpClickAttempts = 0;
                    StatusText = "Fast-forward timeout but encounter not active — retrying pump";
                }
            }
        }

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

            if (!_blight.IsTimerDone)
            {
                _phase = BlightPhase.TowerManagement;
                _phaseStartTime = DateTime.Now;
                StatusText = $"Timer still running ({_blight.CountdownText}) — defending pump";
                return;
            }

            var elapsedAfterTimer = (DateTime.Now - _phaseStartTime).TotalSeconds;
            var sweepDelay = _settings.SweepDelayAfterTimerSeconds.Value;

            if (_settings.StandAtTower.Value)
            {
                if (_blight.AliveMonsterCount > 0 && ctx.Combat.NearbyMonsterCount > 0)
                {
                    TickSafetyPosition(ctx);
                    StatusText = $"Standing at tower — defending ({_blight.AliveMonsterCount} alive, {ctx.Combat.NearbyMonsterCount} nearby)";
                    return;
                }
            }

            if (elapsedAfterTimer > sweepDelay)
            {
                CancelTowerAction(ctx);
                EnterSweepPhase();
                StatusText = "Timer finished & delay passed — sweeping remaining monsters";
                return;
            }

            if (ctx.Combat.NearbyMonsterCount > 0)
            {
                if (_towerAction != null)
                    CancelTowerAction(ctx);
                StatusText = $"Fighting — {ctx.Combat.NearbyMonsterCount} nearby, {_blight.AliveMonsterCount} alive";
            }
            else
            {
                TickTowerLoop(ctx);
                StatusText = $"Waiting — {_blight.AliveMonsterCount} monsters alive ({sweepDelay - elapsedAfterTimer:F0}s until sweep)";
            }
        }

        private void TickTowerLoop(BotContext ctx)
        {
            var gc = ctx.Game;

            if (ctx.Interaction.IsBusy)
                return;

            if (_towerAction != null && ctx.Combat.NearbyMonsterCount > 0)
            {
                CancelTowerAction(ctx);
                StatusText = $"Fighting — {ctx.Combat.NearbyMonsterCount} nearby (tower action cancelled)";
                return;
            }

            if (_settings.DontBuildTowers.Value)
            {
                if (_towerAction != null)
                    CancelTowerAction(ctx);

                TickSafetyPosition(ctx);
                StatusText = "Towers disabled";
                return;
            }

            if (_settings.StandAtTower.Value)
            {
                TickSafetyPosition(ctx);
            }

            if (_towerAction != null)
            {
                if (_settings.StandAtTower.Value && _blight.DefensePosition.HasValue)
                {
                    var distFromTower = Vector2.Distance(_towerAction.TargetGridPos, _blight.DefensePosition.Value);
                    if (distFromTower > _settings.TowerApproachDistance.Value + 15f)
                    {
                        CancelTowerAction(ctx);
                        StatusText = "Standing at tower — distant tower build cancelled";
                        return;
                    }
                }

                _towerAction.Tick(gc);
                if (_towerAction.IsComplete)
                {
                    if (_towerAction.Succeeded)
                    {
                        StatusText = _towerAction.Status;
                        _towerAction = null;
                        if (!TryStartTowerAction(ctx, TowerAction.ActionType.Upgrade))
                        {
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

            if ((DateTime.Now - _lastTowerActionEndAt).TotalMilliseconds < _settings.TowerBuildCooldownMs.Value)
            {
                TickSafetyPosition(ctx);
                StatusText = "Tower cooldown";
                return;
            }

            if (_blight.PumpUnderAttack)
            {
                TickSafetyPosition(ctx);
                StatusText = $"Pump under attack — defending ({_blight.AliveMonsterCount} monsters)";
                return;
            }

            if (!TryStartTowerAction(ctx, TowerAction.ActionType.Upgrade))
                TryStartTowerAction(ctx, TowerAction.ActionType.Build);

            if (_towerAction == null)
            {
                TickSafetyPosition(ctx);
                StatusText = "No tower actions";
                _lastTowerActionEndAt = DateTime.Now;
            }
        }

        private void TickSafetyPosition(BotContext ctx)
        {
            if (!_blight.DefensePosition.HasValue) return;
            var gc = ctx.Game;
            var playerPos = gc.Player.GridPosNum;
            var defensePos = _blight.DefensePosition.Value;
            var distToDefense = Vector2.Distance(playerPos, defensePos);

            float safetyRadius = 30f;

            if (distToDefense > safetyRadius && !ctx.Navigation.IsNavigating)
            {
                var dir = Vector2.Normalize(defensePos - playerPos);
                var targetPos = defensePos - dir * 10f;
                ctx.Navigation.NavigateTo(gc, targetPos);
            }
        }

        private bool TryStartTowerAction(BotContext ctx, TowerAction.ActionType type)
        {
            var action = new TowerAction(type, _blight, _settings, ctx.Navigation);
            action.ExtraLatencySec = ctx.Settings.ExtraLatencyMs.Value / 1000f;
            action.Tick(ctx.Game);
            if (action.CurrentPhase == TowerAction.Phase.Failed)
                return false;

            if (_settings.StandAtTower.Value && _blight.DefensePosition.HasValue)
            {
                var dist = Vector2.Distance(action.TargetGridPos, _blight.DefensePosition.Value);
                if (dist > _settings.TowerApproachDistance.Value + 15f)
                {
                    action.Cancel(ctx.Game);
                    return false;
                }
            }

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

        private void EnterSweepPhase()
        {
            _phase = BlightPhase.Sweep;
            _phaseStartTime = DateTime.Now;
            _sweepSubPhase = SweepSubPhase.PatrolLaneOutward;
            _currentPatrolLaneIndex = 0;
            _sweptLaneIndices.Clear();
            _lanePatrolStartedAt = DateTime.Now;
            _sweepCombatEngageTime = DateTime.MinValue;
            _sweepCombatEngageCount = 0;
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

            if (!_blight.IsTimerDone)
            {
                ctx.Navigation.Stop(ctx.Game);
                _phase = BlightPhase.TowerManagement;
                _phaseStartTime = DateTime.Now;
                StatusText = $"Timer still active ({_blight.CountdownText}) — returning to defend pump";
                return;
            }

            var gc = ctx.Game;
            var playerPos = gc.Player.GridPosNum;
            var defensePos = _blight.DefensePosition ?? playerPos;
            var now = DateTime.Now;

            MarkLanesNearPlayerAsSwept(playerPos, defensePos);

            if (ctx.Combat.NearbyMonsterCount > 0)
            {
                if (_sweepCombatEngageTime == DateTime.MinValue || ctx.Combat.NearbyMonsterCount < _sweepCombatEngageCount)
                {
                    _sweepCombatEngageTime = now;
                    _sweepCombatEngageCount = ctx.Combat.NearbyMonsterCount;
                }

                var combatElapsed = (now - _sweepCombatEngageTime).TotalSeconds;
                if (combatElapsed > SweepCombatStuckSeconds)
                {
                    _sweepCombatEngageTime = DateTime.MinValue;
                    _sweepCombatEngageCount = 0;
                    StatusText = $"Combat stuck ({combatElapsed:F0}s) — resuming lane sweep";
                    TickSweepExplore(ctx, playerPos, defensePos);
                }
                else
                {
                    StatusText = $"Sweep: fighting ({ctx.Combat.NearbyMonsterCount} nearby, {ctx.Combat.CachedMonsterCount} total)";
                }
                return;
            }

            if (ctx.Combat.CachedMonsterCount > 0)
            {
                _sweepCombatEngageTime = DateTime.MinValue;
                _sweepCombatEngageCount = 0;

                var nearestToPumpPos = FindMonsterClosestToDefense(gc, defensePos, ctx.Combat.BlacklistedEnemies);
                if (nearestToPumpPos.HasValue)
                {
                    var monsterDist = Vector2.Distance(playerPos, nearestToPumpPos.Value);
                    if (monsterDist > 20f && !ctx.Navigation.IsNavigating)
                        ctx.Navigation.NavigateTo(gc, nearestToPumpPos.Value);
                    StatusText = $"Sweep: chasing monster near pump (dist: {monsterDist:F0}, {ctx.Combat.CachedMonsterCount} alive)";
                    return;
                }
            }

            _sweepCombatEngageTime = DateTime.MinValue;
            _sweepCombatEngageCount = 0;

            TickSweepExplore(ctx, playerPos, defensePos);
        }

        private void TickSweepExplore(BotContext ctx, Vector2 playerPos, Vector2 defensePos)
        {
            var gc = ctx.Game;
            var now = DateTime.Now;
            var laneTracker = _blight.LaneTracker;

            // If no lane data, or if ALL known lanes were already swept and encounter is still active,
            // fall back to exploration to find undiscovered lanes / distant stragglers.
            bool allLanesSwept = laneTracker.HasLaneData && _sweptLaneIndices.Count >= laneTracker.Lanes.Count;

            if (!laneTracker.HasLaneData || laneTracker.Lanes.Count == 0 || allLanesSwept)
            {
                if (ctx.Exploration.IsInitialized)
                {
                    if (ctx.Exploration.ActiveBlobCoverage >= 0.95f)
                    {
                        ctx.Exploration.SeenRadiusOverride = 40;
                        ctx.Exploration.ResetSeen();
                    }

                    var target = ctx.Exploration.GetNextExplorationTarget(playerPos);
                    if (target.HasValue)
                    {
                        ctx.Navigation.NavigateTo(gc, target.Value);
                        StatusText = $"Sweep: exploring unvisited map area for stragglers ({ctx.Combat.CachedMonsterCount} alive)";
                        return;
                    }
                }

                // If exploration returned no target, loop lane patrol again
                if (allLanesSwept)
                {
                    _sweptLaneIndices.Clear();
                }
            }

            bool laneTimedOut = _lanePatrolStartedAt != DateTime.MinValue
                && (now - _lanePatrolStartedAt).TotalSeconds > LanePatrolTimeoutSeconds;

            if (_sweepSubPhase == SweepSubPhase.PatrolLaneOutward)
            {
                while (_sweptLaneIndices.Contains(_currentPatrolLaneIndex) && _sweptLaneIndices.Count < laneTracker.Lanes.Count)
                {
                    _currentPatrolLaneIndex = (_currentPatrolLaneIndex + 1) % laneTracker.Lanes.Count;
                }

                if (_currentPatrolLaneIndex >= laneTracker.Lanes.Count)
                    _currentPatrolLaneIndex = 0;

                var lane = laneTracker.Lanes[_currentPatrolLaneIndex];
                var furthestEndpoint = GetLaneFurthestEndpoint(lane, defensePos);
                var distToEndpoint = Vector2.Distance(playerPos, furthestEndpoint);

                if (distToEndpoint < 25f || laneTimedOut)
                {
                    MarkLanesNearPositionAsSwept(furthestEndpoint, defensePos);
                    _sweepSubPhase = SweepSubPhase.ReturnToPump;
                    _lanePatrolStartedAt = now;
                    ctx.Navigation.Stop(gc);
                    StatusText = $"Sweep: reached lane {_currentPatrolLaneIndex + 1} portal — returning to pump";
                    return;
                }

                if (!ctx.Navigation.IsNavigating || Vector2.Distance(ctx.Navigation.Destination ?? Vector2.Zero, furthestEndpoint) > 20f)
                {
                    var pathFound = ctx.Navigation.NavigateTo(gc, furthestEndpoint);
                    if (!pathFound)
                    {
                        var walkableEndpoint = ctx.Navigation.FindNearestWalkable(gc, furthestEndpoint, 20);
                        if (walkableEndpoint.HasValue)
                            pathFound = ctx.Navigation.NavigateTo(gc, walkableEndpoint.Value);
                    }

                    if (!pathFound)
                    {
                        _sweptLaneIndices.Add(_currentPatrolLaneIndex);
                        _sweepSubPhase = SweepSubPhase.ReturnToPump;
                        _lanePatrolStartedAt = now;
                        StatusText = $"Sweep: lane {_currentPatrolLaneIndex + 1} unreachable — returning to pump";
                        return;
                    }
                }
                StatusText = $"Sweep: traversing lane {_currentPatrolLaneIndex + 1}/{laneTracker.Lanes.Count} to portal (dist: {distToEndpoint:F0})";
            }
            else if (_sweepSubPhase == SweepSubPhase.ReturnToPump)
            {
                var distToPump = Vector2.Distance(playerPos, defensePos);

                if (distToPump < 20f || laneTimedOut)
                {
                    _currentPatrolLaneIndex = (_currentPatrolLaneIndex + 1) % laneTracker.Lanes.Count;
                    _sweepSubPhase = SweepSubPhase.PatrolLaneOutward;
                    _lanePatrolStartedAt = now;
                    ctx.Navigation.Stop(gc);
                    StatusText = "Sweep: defended pump — traversing next lane";
                    return;
                }

                if (!ctx.Navigation.IsNavigating)
                {
                    ctx.Navigation.NavigateTo(gc, defensePos);
                }
                StatusText = $"Sweep: returning to defend pump (dist: {distToPump:F0})";
            }
        }

        private static Vector2 GetLaneFurthestEndpoint(List<Vector2> lane, Vector2 defensePos)
        {
            if (lane.Count == 0) return defensePos;
            Vector2 furthest = lane[0];
            float maxD = 0f;
            foreach (var wp in lane)
            {
                float d = Vector2.Distance(wp, defensePos);
                if (d > maxD)
                {
                    maxD = d;
                    furthest = wp;
                }
            }
            return furthest;
        }

        private void MarkLanesNearPlayerAsSwept(Vector2 playerPos, Vector2 defensePos)
        {
            MarkLanesNearPositionAsSwept(playerPos, defensePos);
        }

        private void MarkLanesNearPositionAsSwept(Vector2 position, Vector2 defensePos)
        {
            var laneTracker = _blight.LaneTracker;
            if (!laneTracker.HasLaneData) return;

            for (int i = 0; i < laneTracker.Lanes.Count; i++)
            {
                if (_sweptLaneIndices.Contains(i)) continue;
                var endpoint = GetLaneFurthestEndpoint(laneTracker.Lanes[i], defensePos);
                if (Vector2.Distance(position, endpoint) <= EndpointOverlapRadius)
                {
                    _sweptLaneIndices.Add(i);
                }
            }
        }

        private static Vector2? FindMonsterClosestToDefense(GameController gc, Vector2 defensePos, HashSet<string> enemyBlacklist)
        {
            float bestDist = float.MaxValue;
            Vector2? bestPos = null;

            foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
            {
                if (entity.Type != EntityType.Monster || !entity.IsHostile) continue;
                if (!entity.IsAlive || !entity.IsTargetable) continue;
                if (enemyBlacklist.Count > 0 && !string.IsNullOrEmpty(entity.RenderName) &&
                    enemyBlacklist.Contains(entity.RenderName)) continue;

                var dist = Vector2.Distance(entity.GridPosNum, defensePos);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    bestPos = entity.GridPosNum;
                }
            }

            return bestPos;
        }

        private DateTime _lastEmptyScanAt = DateTime.MinValue;
        private const float LootTimeoutSeconds = 180f;
        private const float EmptyGraceSeconds = 5f;

        private void EnterOpenChestsPhase()
        {
            _phase = BlightPhase.OpenChests;
            _phaseStartTime = DateTime.Now;
            _currentChestTarget = null;
            _chestOpenedDelayUntil = DateTime.MinValue;
            _lootTracker.Reset();
            _lastEmptyScanAt = DateTime.MinValue;
            _lootedLaneIndices.Clear();
            _currentLootLaneIndex = 0;
        }

        private void TickOpenChests(BotContext ctx, InteractionResult interactionResult)
        {
            _lootTracker.HandleResult(interactionResult, ctx);

            if (interactionResult == InteractionResult.Succeeded || interactionResult == InteractionResult.Failed)
            {
                if (_currentChestTarget.HasValue && interactionResult == InteractionResult.Succeeded)
                {
                    // Wait 200ms for chest contents to spawn on the ground before scanning
                    _chestOpenedDelayUntil = DateTime.Now.AddMilliseconds(200);
                }
                _currentChestTarget = null;
            }

            if ((DateTime.Now - _phaseStartTime).TotalSeconds > LootTimeoutSeconds)
            {
                EnterExitMapPhase(ctx);
                StatusText = $"Chest+loot timeout — exiting map ({_lootTracker.PickupCount} items)";
                return;
            }

            // Delay scan right after opening a chest to let ground entities and labels spawn
            if (DateTime.Now < _chestOpenedDelayUntil)
            {
                StatusText = "Waiting for chest loot to drop...";
                return;
            }

            if (ctx.Interaction.IsBusy) return;

            var gc = ctx.Game;
            var playerPos = gc.Player.GridPosNum;
            var defensePos = _blight.DefensePosition ?? playerPos;

            // Issue 1: Skip any lane endpoints we coincidentally walked near
            MarkLanesNearPositionAsLooted(playerPos, defensePos);

            // 1. Pick up ground loot
            ctx.Loot.Scan(gc);
            var best = ctx.Loot.GetBestCandidate();
            if (best != null)
            {
                _lastEmptyScanAt = DateTime.MinValue;
                var withinRadius = best.Distance <= ctx.Interaction.InteractRadius;
                ctx.Interaction.PickupGroundItem(best.Entity, ctx.Navigation,
                    requireProximity: !withinRadius);
                _lootTracker.SetPending(best.Entity.Id, best.ItemName, best.ChaosValue);
                StatusText = $"Picking up loot ({ctx.Loot.Candidates.Count} visible, {_lootTracker.PickupCount} picked, {_blight.ChestPositions.Count} chests left)";
                return;
            }

            // 2. Open nearest visible unopened chest
            Entity? nearestChest = null;
            float nearestDist = float.MaxValue;

            foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
            {
                if (entity.Type != EntityType.Chest || entity.IsOpened) continue;
                var dist = Vector2.Distance(playerPos, entity.GridPosNum);
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

            // 3. Navigate to cached chest positions
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
                        var pathFound = ctx.Navigation.NavigateTo(gc, nearestCachedChest.Value);
                        if (!pathFound)
                        {
                            _blight.ChestPositions.Remove(nearestCachedChest.Value);
                            StatusText = $"No path to chest (dist: {bestDist:F0}) — removed, {_blight.ChestPositions.Count} remaining";
                            return;
                        }
                        _chestNavStartedAt = DateTime.Now;
                        StatusText = $"Navigating to chest (dist: {bestDist:F0}, {_blight.ChestPositions.Count} remaining)";
                        return;
                    }
                }

                if (ctx.Navigation.IsNavigating)
                {
                    if (_chestNavStartedAt != DateTime.MinValue
                        && (DateTime.Now - _chestNavStartedAt).TotalSeconds > ChestNavTimeoutSeconds)
                    {
                        ctx.Navigation.Stop(gc);
                        if (_currentChestTarget.HasValue)
                            _blight.ChestPositions.Remove(_currentChestTarget.Value);
                        else if (_blight.ChestPositions.Count > 0)
                        {
                            Vector2? nearest = null;
                            float nd = float.MaxValue;
                            foreach (var p in _blight.ChestPositions)
                            {
                                var d = Vector2.Distance(playerPos, p);
                                if (d < nd) { nd = d; nearest = p; }
                            }
                            if (nearest.HasValue)
                                _blight.ChestPositions.Remove(nearest.Value);
                        }
                        _chestNavStartedAt = DateTime.MinValue;
                        StatusText = $"Chest nav timeout — skipping, {_blight.ChestPositions.Count} remaining";
                        return;
                    }
                    StatusText = $"Walking to chest area ({_blight.ChestPositions.Count} remaining)";
                    return;
                }
            }

            // 4. Visit unvisited lane endpoints (where Blight reward chests spawn)
            var laneTracker = _blight.LaneTracker;
            if (laneTracker.HasLaneData && _lootedLaneIndices.Count < laneTracker.Lanes.Count)
            {
                // Pick the closest unlooted lane endpoint instead of strictly sequential cycling
                int bestLaneIdx = -1;
                float bestEndpointDist = float.MaxValue;
                Vector2 bestEndpoint = Vector2.Zero;

                for (int i = 0; i < laneTracker.Lanes.Count; i++)
                {
                    if (_lootedLaneIndices.Contains(i)) continue;
                    var ep = GetLaneFurthestEndpoint(laneTracker.Lanes[i], defensePos);
                    var d = Vector2.Distance(playerPos, ep);
                    if (d < bestEndpointDist)
                    {
                        bestEndpointDist = d;
                        bestLaneIdx = i;
                        bestEndpoint = ep;
                    }
                }

                if (bestLaneIdx != -1)
                {
                    _currentLootLaneIndex = bestLaneIdx;

                    if (bestEndpointDist < 25f)
                    {
                        MarkLanesNearPositionAsLooted(bestEndpoint, defensePos);
                        _lastEmptyScanAt = DateTime.MinValue;
                        return;
                    }

                    if (!ctx.Navigation.IsNavigating)
                    {
                        _lastEmptyScanAt = DateTime.MinValue;
                        var pathFound = ctx.Navigation.NavigateTo(gc, bestEndpoint);
                        if (!pathFound)
                        {
                            var walkable = ctx.Navigation.FindNearestWalkable(gc, bestEndpoint, 20);
                            if (walkable.HasValue)
                                pathFound = ctx.Navigation.NavigateTo(gc, walkable.Value);
                        }

                        if (!pathFound)
                        {
                            _lootedLaneIndices.Add(bestLaneIdx);
                            return;
                        }
                    }

                    StatusText = $"Visiting lane {bestLaneIdx + 1}/{laneTracker.Lanes.Count} endpoint for chests (dist: {bestEndpointDist:F0})";
                    return;
                }
            }

            // 5. If everything is checked and no loot visible, count grace period before exit
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

        private void EnterExitMapPhase(BotContext ctx)
        {
            _phase = BlightPhase.ExitMap;
            _phaseStartTime = DateTime.Now;
            _lastActionTime = DateTime.MinValue;
            _mapCompleted = true;
            _blight.MapComplete = true;
            ctx.LootTracker.RecordMapComplete();
            ctx.Interaction.Cancel(ctx.Game);
            ctx.Navigation.Stop(ctx.Game);

            StatusText = "Exiting map via portal";
        }

        private void TickExitMap(BotContext ctx)
        {
            var gc = ctx.Game;

            if (gc.IsLoading || gc.Area.CurrentArea.IsHideout || gc.Area.CurrentArea.IsTown)
            {
                StatusText = "Loading hideout...";
                return;
            }

            if ((DateTime.Now - _phaseStartTime).TotalSeconds > 60)
            {
                _phase = BlightPhase.Done;
                StatusText = "Exit timeout — giving up";
                return;
            }

            if (!ModeHelpers.CanAct(_lastActionTime, MajorActionCooldownMs))
                return;

            if (gc.IngameState.IngameUi.StashElement?.IsVisible == true ||
                gc.IngameState.IngameUi.InventoryPanel?.IsVisible == true)
            {
                BotInput.PressKey(System.Windows.Forms.Keys.Escape);
                _lastActionTime = DateTime.Now;
                StatusText = "Closing panels before exit";
                return;
            }

            var playerPos = gc.Player.GridPosNum;

            Entity? portal = ModeHelpers.FindNearestPortal(gc);

            if (portal != null)
            {
                var portalGridPos = portal.GridPosNum;
                var dist = Vector2.Distance(playerPos, portalGridPos);

                if (dist > ctx.Interaction.InteractRadius)
                {
                    if (!ctx.Navigation.IsNavigating)
                        ctx.Navigation.NavigateTo(gc, portalGridPos);
                    StatusText = $"Walking to portal (dist: {dist:F0})";
                    return;
                }

                if (ctx.Navigation.IsNavigating)
                    ctx.Navigation.Stop(gc);

                ModeHelpers.ClickEntity(gc, portal, ref _lastActionTime);
                StatusText = "Clicking portal to exit";
                return;
            }

            if (_blight.PortalPosition.HasValue)
            {
                var cachedPos = _blight.PortalPosition.Value;
                var dist = Vector2.Distance(playerPos, cachedPos);

                if (dist > ctx.Interaction.InteractRadius)
                {
                    if (!ctx.Navigation.IsNavigating)
                        ctx.Navigation.NavigateTo(gc, cachedPos);
                    StatusText = $"Walking to cached portal (dist: {dist:F0})";
                    return;
                }

                StatusText = "Near cached portal — waiting for entity to appear";
                return;
            }

            StatusText = "No portal found — waiting";
        }

        public void Render(BotContext ctx)
        {
            if (ctx.Graphics == null) return;
            var gc = ctx.Game;
            var cam = gc.IngameState.Camera;
            var g = ctx.Graphics;

            var hudY = 200f;
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

            if (gc.Area.CurrentArea.IsHideout || gc.Area.CurrentArea.IsTown)
                return;

            if (_blight.PumpPosition.HasValue)
            {
                var pumpWorld = Systems.Pathfinding.GridToWorld3D(gc, _blight.PumpPosition.Value);
                g.DrawText("PUMP", cam.WorldToScreen(pumpWorld), SharpDX.Color.Yellow);
                g.DrawCircleInWorld(pumpWorld, 30f, SharpDX.Color.Yellow, 2f);

                float buildRadiusWorld = _settings.TowerBuildRadius.Value * Systems.Pathfinding.GridToWorld;
                g.DrawCircleInWorld(pumpWorld, buildRadiusWorld, new SharpDX.Color(255, 200, 0, 40), 1.5f);
            }

            if (_blight.DefensePosition.HasValue && _blight.DefensePosition != _blight.PumpPosition)
            {
                var defWorld = Systems.Pathfinding.GridToWorld3D(gc, _blight.DefensePosition.Value);
                g.DrawText("DEFEND", cam.WorldToScreen(defWorld), SharpDX.Color.Cyan);
                g.DrawCircleInWorld(defWorld, 30f, SharpDX.Color.Cyan, 2f);
            }

            if (_towerAction != null && !_towerAction.IsComplete)
            {
                var targetWorld = Systems.Pathfinding.GridToWorld3D(gc, _towerAction.TargetGridPos);
                var targetScreen = cam.WorldToScreen(targetWorld);
                g.DrawCircleInWorld(targetWorld, 25f, SharpDX.Color.Gold, 3f);
                g.DrawText("TARGET", targetScreen + new Vector2(-20, -25), SharpDX.Color.Gold);
            }

            if (_blight.PortalPosition.HasValue)
            {
                var portalWorld = Systems.Pathfinding.GridToWorld3D(gc, _blight.PortalPosition.Value);
                var portalScreen = cam.WorldToScreen(portalWorld);
                g.DrawText("PORTAL", portalScreen + new Vector2(-20, -15), SharpDX.Color.Aqua);
                g.DrawCircleInWorld(portalWorld, 20f, SharpDX.Color.Aqua, 1.5f);
            }

            foreach (var chestPos in _blight.ChestPositions)
            {
                g.DrawText("C", Systems.Pathfinding.GridToScreen(gc, chestPos), SharpDX.Color.Gold);
            }

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

                    g.DrawText($"L{i}", Systems.Pathfinding.GridToScreen(gc, lane[0]), color);
                }
            }

            if (ctx.Navigation.IsNavigating)
            {
                var path = ctx.Navigation.CurrentNavPath;
                for (int i = ctx.Navigation.CurrentWaypointIndex; i < path.Count - 1; i++)
                {
                    var from = Systems.Pathfinding.GridToScreen(gc, path[i].Position);
                    var to = Systems.Pathfinding.GridToScreen(gc, path[i + 1].Position);
                    g.DrawLine(from, to, 1.5f, SharpDX.Color.CornflowerBlue);
                }
            }

            if (_blight.PumpUnderAttack)
            {
                g.DrawText("PUMP UNDER ATTACK!", new Vector2(hudX, hudY), SharpDX.Color.Red);
                hudY += lineH;
            }
            g.DrawText($"Monsters: {_blight.AliveMonsterCount}", new Vector2(hudX, hudY), SharpDX.Color.Gray);
            hudY += lineH;

            if (_phase == BlightPhase.Sweep)
            {
                var laneCount = _blight.LaneTracker.Lanes.Count;
                var sweepInfo = $"Sweep: {_sweepSubPhase} | Lane {_currentPatrolLaneIndex + 1}/{laneCount} ({_sweptLaneIndices.Count} swept) | {ctx.Combat.NearbyMonsterCount} nearby";
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

        private void MarkLanesNearPositionAsLooted(Vector2 position, Vector2 defensePos, float radius = LootEndpointSkipRadius)
        {
            var laneTracker = _blight.LaneTracker;
            if (!laneTracker.HasLaneData) return;

            for (int i = 0; i < laneTracker.Lanes.Count; i++)
            {
                if (_lootedLaneIndices.Contains(i)) continue;
                var endpoint = GetLaneFurthestEndpoint(laneTracker.Lanes[i], defensePos);
                if (Vector2.Distance(position, endpoint) <= radius)
                {
                    _lootedLaneIndices.Add(i);
                }
            }
        }
    }

    public enum BlightPhase
    {
        Idle,
        InHideout,
        StashItems,
        OpenMap,
        EnterPortal,
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
}