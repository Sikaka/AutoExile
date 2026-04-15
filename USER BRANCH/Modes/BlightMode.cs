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
        private DateTime _chestNavStartedAt = DateTime.MinValue;
        private const float ChestNavTimeoutSeconds = 30f;

        // Sweep state
        private bool _sweepWasSearching;
        private DateTime _sweepLastOutsidePumpAt = DateTime.MinValue;
        private DateTime _sweepLastMonsterSeenAt = DateTime.MinValue;
        private bool _sweepReturningToPump;
        // Combat stuck detection (same pattern as SimulacrumMode)
        private DateTime _sweepCombatEngageTime = DateTime.MinValue;
        private int _sweepCombatEngageCount;
        private const float SweepCombatStuckSeconds = 15f;

        // Pump click verification
        private int _pumpClickAttempts;
        private DateTime _lastPumpClickAt = DateTime.MinValue;
        private const int MaxPumpClickAttempts = 6;
        private const float PumpClickVerifyDelayMs = 1500f; // wait after click before retrying

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
            ModeHelpers.EnableCombatWithKiting(ctx, ctx.Settings.Blight.EnableKiting.Value);

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

                // Suppress combat repositioning during phases where BlightMode drives navigation.
                // Combat still scans threats and fires skills — just won't move the player.
                // Allow combat positioning in WaitForCompletion (no tower action) and Sweep when fighting.
                bool allowCombatMovement = ((_phase == BlightPhase.WaitForCompletion && _towerAction == null)
                    || (_phase == BlightPhase.Sweep && !_sweepReturningToPump))
                    && ctx.Combat.NearbyMonsterCount > 0;
                ctx.Combat.SuppressPositioning = !allowCombatMovement;

                // During active encounter phases, anchor combat to the defense point:
                // - DefenseAnchor: target scoring favors monsters closer to the pump hub
                // - LeashAnchor: positioning won't pull player beyond network bubble of pump hub
                bool inEncounterPhase = _phase is BlightPhase.TowerManagement or BlightPhase.WaitForCompletion or BlightPhase.Sweep;
                if (inEncounterPhase && _blight.DefensePosition.HasValue)
                {
                    ctx.Combat.Profile.DefenseAnchor = _blight.DefensePosition.Value;
                    ctx.Combat.Profile.LeashAnchor = _blight.DefensePosition.Value;
                    ctx.Combat.Profile.LeashRadius = Systems.Pathfinding.NetworkBubbleRadius;
                }
                else
                {
                    ctx.Combat.Profile.DefenseAnchor = null;
                    ctx.Combat.Profile.LeashAnchor = null;
                }

                ctx.Combat.Tick(ctx);
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
                var portalPos = _blight.PortalPosition; // preserve — portal doesn't move
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

        // =================================================================
        // Map phases
        // =================================================================

        private bool _nudgedForPump;

        private void TickFindPump(BotContext ctx)
        {
            var gc = ctx.Game;

            // Actively scan for pump each tick — EntityAdded events may not re-fire
            // on re-entry to the same map instance (e.g. after death via portal).
            if (!_blight.PumpPosition.HasValue)
                _blight.ScanForPump(gc);

            // Check encounter-active FIRST — on re-entry after death, the encounter
            // is already running. If pump is found + encounter active, skip straight
            // to the right phase (don't go through NavigateToPump → StartEncounter).
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

            // If pump not found after 2s, do a small movement to trigger entity loading
            // (portal can land right on the pump, which may not load until player moves)
            if (!_nudgedForPump && elapsed > 2)
            {
                _nudgedForPump = true;
                var playerGrid = gc.Player.GridPosNum;
                var nudgeTarget = new Vector2(playerGrid.X + 5, playerGrid.Y);
                ctx.Navigation.MoveToward(gc, nudgeTarget);
                StatusText = "Nudging to trigger entity loading...";
                return;
            }

            // After 5s, check for blight entities as evidence of an active encounter
            // (pump entity may not be in the entity list but towers/monsters are)
            if (elapsed > 5)
            {
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
            }

            StatusText = "Searching for blight pump...";

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
                // Require positive proof: pump StateMachine "activated > 0", or pump gone + monsters
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

                // False positive — reset and continue navigating
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

            // The ONLY way to advance: positive confirmation that pump StateMachine
            // has "activated > 0". Don't trust IsEncounterActive (non-targetable fallback
            // can false-positive). Don't trust "pump disappeared". Require hard proof.
            if (pump != null && IsPumpActivated(pump))
            {
                _phase = BlightPhase.FastForward;
                _phaseStartTime = DateTime.Now;
                _pumpClickAttempts = 0;
                StatusText = "Encounter confirmed (activated) — waiting for fast-forward";
                return;
            }

            // Secondary confirmation: pump entity gone + monsters spawning = encounter started
            // (pump entity can unload from entity list after activation)
            if (pump == null && _blight.IsEncounterActive && _blight.AliveMonsterCount > 5)
            {
                _phase = BlightPhase.FastForward;
                _phaseStartTime = DateTime.Now;
                _pumpClickAttempts = 0;
                StatusText = "Encounter confirmed (monsters spawning) — waiting for fast-forward";
                return;
            }

            // If IsEncounterActive got set but we can't confirm it, reset the false positive
            if (_blight.IsEncounterActive && pump != null && !IsPumpActivated(pump))
            {
                _blight.IsEncounterActive = false;
            }

            // After clicking, wait for verification delay before retrying
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

        /// <summary>
        /// Check if pump StateMachine has "activated > 0" — the definitive proof
        /// that the encounter has actually started. This is the ONLY reliable signal;
        /// IsTargetable changes and other fallbacks can false-positive.
        /// </summary>
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
                // Only advance on timeout if encounter is genuinely active (monsters, timer, etc.)
                if (_blight.IsEncounterActive)
                {
                    _blight.HasClickedFastForward = true;
                    _phase = BlightPhase.TowerManagement;
                    StatusText = "Fast-forward timeout — managing towers";
                }
                else
                {
                    // Encounter never actually started — go back to StartEncounter to retry
                    _phase = BlightPhase.StartEncounter;
                    _phaseStartTime = DateTime.Now;
                    _pumpClickAttempts = 0;
                    StatusText = "Fast-forward timeout but encounter not active — retrying pump";
                }
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
            if (ctx.Combat.NearbyMonsterCount > 0)
            {
                if (_towerAction != null)
                    CancelTowerAction(ctx);
                StatusText = $"Fighting — {ctx.Combat.NearbyMonsterCount} nearby, {_blight.AliveMonsterCount} alive";
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

            // Combat priority — cancel tower navigation if monsters are nearby.
            // Tower building shouldn't override combat; let CombatSystem handle threats first.
            if (_towerAction != null && ctx.Combat.NearbyMonsterCount > 0)
            {
                CancelTowerAction(ctx);
                StatusText = $"Fighting — {ctx.Combat.NearbyMonsterCount} nearby (tower action cancelled)";
                return;
            }

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

            // Don't start new tower actions if pump is under attack — return to defend
            if (_blight.PumpUnderAttack)
            {
                TickSafetyPosition(ctx);
                StatusText = $"Pump under attack — defending ({_blight.AliveMonsterCount} monsters)";
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
            if (!_blight.DefensePosition.HasValue) return;
            var gc = ctx.Game;
            var playerPos = gc.Player.GridPosNum;
            var defensePos = _blight.DefensePosition.Value;
            var distToDefense = Vector2.Distance(playerPos, defensePos);

            // Safety radius — stay reasonably close to the defense point (lane hub)
            float safetyRadius = 30f;

            if (distToDefense > safetyRadius && !ctx.Navigation.IsNavigating)
            {
                // Move back toward defense point (but not on top of it)
                var dir = Vector2.Normalize(defensePos - playerPos);
                var targetPos = defensePos - dir * 10f; // stand 10 grid units from hub
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
        // Sweep — hunt cached monsters, explore for stragglers, return to pump periodically
        // =================================================================

        private void EnterSweepPhase()
        {
            _phase = BlightPhase.Sweep;
            _phaseStartTime = DateTime.Now;
            _sweepWasSearching = false;
            _sweepLastOutsidePumpAt = DateTime.MinValue;
            _sweepLastMonsterSeenAt = DateTime.Now;
            _sweepReturningToPump = false;
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

            var gc = ctx.Game;
            var playerPos = gc.Player.GridPosNum;
            var defensePos = _blight.DefensePosition ?? playerPos;
            var distToDefense = Vector2.Distance(playerPos, defensePos);
            var now = DateTime.Now;

            var pumpRadius = _settings.SweepPumpRadius.Value;
            var returnSeconds = _settings.SweepPumpReturnSeconds.Value;

            // --- Track pump proximity timer ---
            // Reset timer when inside pump radius, start/continue when outside
            if (distToDefense <= pumpRadius)
            {
                _sweepLastOutsidePumpAt = DateTime.MinValue;
                _sweepReturningToPump = false;
            }
            else if (_sweepLastOutsidePumpAt == DateTime.MinValue)
            {
                _sweepLastOutsidePumpAt = now;
            }

            // --- Forced return to pump ---
            // Been outside pump radius too long — return to refresh state machine and check threats
            bool awayTooLong = _sweepLastOutsidePumpAt != DateTime.MinValue
                && (now - _sweepLastOutsidePumpAt).TotalSeconds > returnSeconds;

            if (_sweepReturningToPump || awayTooLong)
            {
                _sweepReturningToPump = true;
                if (distToDefense < 18f)
                {
                    // Arrived at pump — reset and resume sweep
                    _sweepReturningToPump = false;
                    _sweepLastOutsidePumpAt = DateTime.MinValue;
                    ctx.Navigation.Stop(gc);
                    // Reset exploration so we re-sweep from pump outward
                    if (ctx.Exploration.IsInitialized)
                        ctx.Exploration.ResetSeen();
                    _sweepWasSearching = false;
                    StatusText = "Returned to pump — resuming sweep";
                    return;
                }

                if (!ctx.Navigation.IsNavigating)
                    ctx.Navigation.NavigateTo(gc, defensePos);
                StatusText = $"Returning to defense point (dist: {distToDefense:F0})";
                return;
            }

            // --- No-monster timeout ---
            // If no monsters found for SweepTimeoutSeconds, give up
            if ((now - _sweepLastMonsterSeenAt).TotalSeconds > _settings.SweepTimeoutSeconds.Value)
            {
                ctx.Navigation.Stop(gc);
                EnterOpenChestsPhase();
                StatusText = $"Sweep timeout — no monsters for {_settings.SweepTimeoutSeconds.Value:F0}s";
                return;
            }

            // --- Priority 1: Fight nearby monsters ---
            if (ctx.Combat.NearbyMonsterCount > 0)
            {
                _sweepLastMonsterSeenAt = now;

                // Combat stuck detection: if monster count isn't decreasing, move on
                if (_sweepCombatEngageTime == DateTime.MinValue || ctx.Combat.NearbyMonsterCount < _sweepCombatEngageCount)
                {
                    _sweepCombatEngageTime = now;
                    _sweepCombatEngageCount = ctx.Combat.NearbyMonsterCount;
                }

                var combatElapsed = (now - _sweepCombatEngageTime).TotalSeconds;
                if (combatElapsed > SweepCombatStuckSeconds)
                {
                    // Stuck fighting same monsters too long — explore elsewhere
                    _sweepCombatEngageTime = DateTime.MinValue;
                    _sweepCombatEngageCount = 0;
                    if (!_sweepWasSearching)
                    {
                        _sweepWasSearching = true;
                        if (ctx.Exploration.IsInitialized)
                            ctx.Exploration.ResetSeen();
                    }
                    StatusText = $"Combat stuck ({combatElapsed:F0}s) — moving on ({ctx.Combat.NearbyMonsterCount} unreachable)";
                    TickSweepExplore(ctx, playerPos, defensePos);
                }
                else
                {
                    _sweepWasSearching = false;
                    // CombatSystem handles fighting + positioning (SuppressPositioning = false above)
                    StatusText = $"Sweep: fighting ({ctx.Combat.NearbyMonsterCount} nearby, {ctx.Combat.CachedMonsterCount} total)";
                }
                return;
            }

            // --- Priority 2: Chase cached distant monsters (closest to pump first) ---
            if (ctx.Combat.CachedMonsterCount > 0)
            {
                _sweepLastMonsterSeenAt = now;

                // Transition from fighting → searching: reset exploration
                if (!_sweepWasSearching)
                {
                    _sweepWasSearching = true;
                    if (ctx.Exploration.IsInitialized)
                        ctx.Exploration.ResetSeen();
                }
                _sweepCombatEngageTime = DateTime.MinValue;
                _sweepCombatEngageCount = 0;

                // Find the monster closest to defense point (biggest threat).
                // The return-to-pump timer (SweepPumpReturnSeconds) prevents staying away too long.
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

            // --- Priority 3: Explore for stragglers ---
            if (!_sweepWasSearching)
            {
                _sweepWasSearching = true;
                if (ctx.Exploration.IsInitialized)
                    ctx.Exploration.ResetSeen();
            }
            _sweepCombatEngageTime = DateTime.MinValue;
            _sweepCombatEngageCount = 0;

            TickSweepExplore(ctx, playerPos, defensePos);
        }

        /// <summary>
        /// Explore the map to find remaining monsters. Falls back to orbiting the defense point
        /// when exploration is exhausted.
        /// </summary>
        private void TickSweepExplore(BotContext ctx, Vector2 playerPos, Vector2 defensePos)
        {
            var gc = ctx.Game;

            // Let current navigation finish before picking a new target
            if (ctx.Navigation.IsNavigating)
            {
                StatusText = $"Sweep: searching for monsters ({ctx.Combat.CachedMonsterCount} alive)";
                return;
            }

            // Try exploration target. The return-to-pump timer (SweepPumpReturnSeconds)
            // ensures we don't stay away from the defense point for too long.
            if (ctx.Exploration.IsInitialized)
            {
                var target = ctx.Exploration.GetNextExplorationTarget(playerPos);
                if (target.HasValue)
                {
                    ctx.Navigation.NavigateTo(gc, target.Value);
                    StatusText = $"Sweep: exploring for monsters ({ctx.Combat.CachedMonsterCount} alive)";
                    return;
                }
            }

            // Exploration exhausted — orbit the defense point to find stragglers
            if (_blight.DefensePosition.HasValue)
            {
                var distOrbit = Vector2.Distance(playerPos, defensePos);
                if (distOrbit > 60f)
                {
                    ctx.Navigation.NavigateTo(gc, defensePos);
                    StatusText = $"Sweep: returning to defense point (exploration exhausted, dist: {distOrbit:F0})";
                    return;
                }
                if (distOrbit < 30f)
                {
                    var angle = (float)(DateTime.Now.Ticks % 6283) / 1000f;
                    var orbitTarget = defensePos + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * 50f;
                    ctx.Navigation.NavigateTo(gc, orbitTarget);
                    StatusText = $"Sweep: orbiting defense point ({ctx.Combat.CachedMonsterCount} alive)";
                    return;
                }
            }

            StatusText = $"Sweep: searching (no targets, {ctx.Combat.CachedMonsterCount} alive)";
        }

        /// <summary>
        /// Find the alive hostile monster closest to the defense point (biggest threat).
        /// Uses OnlyValidEntities (entity list), not blight-specific cache.
        /// </summary>
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
                var withinRadius = best.Distance <= ctx.Interaction.InteractRadius;
                ctx.Interaction.PickupGroundItem(best.Entity, ctx.Navigation,
                    requireProximity: !withinRadius);
                _lootTracker.SetPending(best.Entity.Id, best.ItemName, best.ChaosValue);
                StatusText = $"Picking up loot ({ctx.Loot.Candidates.Count} visible, {_lootTracker.PickupCount} picked, {_blight.ChestPositions.Count} chests left)";
                return;
            }

            // Priority 2: Open nearest visible chest (don't require cache membership —
            // if it's visible, unopened, and a chest entity, open it)
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
                        // Close enough but no entity found — stale cache entry
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
                            // Can't path to this chest — remove it and try another next tick
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
                    // Timeout individual chest navigation — if stuck or path too long, skip it
                    if (_chestNavStartedAt != DateTime.MinValue
                        && (DateTime.Now - _chestNavStartedAt).TotalSeconds > ChestNavTimeoutSeconds)
                    {
                        ctx.Navigation.Stop(gc);
                        if (_currentChestTarget.HasValue)
                            _blight.ChestPositions.Remove(_currentChestTarget.Value);
                        else if (_blight.ChestPositions.Count > 0)
                        {
                            // Remove the nearest cached chest we were heading to
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

            if ((DateTime.Now - _phaseStartTime).TotalSeconds > 60)
            {
                _phase = BlightPhase.Done;
                StatusText = "Exit timeout — giving up";
                return;
            }

            // Let InteractionSystem handle clicking if it's busy (retries, UI blocking, etc.)
            if (ctx.Interaction.IsBusy)
            {
                StatusText = $"Clicking portal to exit";
                return;
            }

            // Try to find live portal entity first
            Entity? portal = ModeHelpers.FindNearestPortal(gc);
            var playerPos = gc.Player.GridPosNum;

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

                // Use InteractionSystem for click verification and UI blocking
                ctx.Navigation.Stop(gc);
                ctx.Interaction.InteractWithEntity(portal, ctx.Navigation);
                StatusText = "Clicking portal to exit";
                return;
            }

            // No live portal — navigate to cached position to bring it into entity range
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

            // Pump entity (clickable)
            if (_blight.PumpPosition.HasValue)
            {
                var pumpWorld = Systems.Pathfinding.GridToWorld3D(gc, _blight.PumpPosition.Value);
                g.DrawText("PUMP", cam.WorldToScreen(pumpWorld), SharpDX.Color.Yellow);
                g.DrawCircleInWorld(pumpWorld, 30f, SharpDX.Color.Yellow, 2f);

                float buildRadiusWorld = _settings.TowerBuildRadius.Value * Systems.Pathfinding.GridToWorld;
                g.DrawCircleInWorld(pumpWorld, buildRadiusWorld, new SharpDX.Color(255, 200, 0, 40), 1.5f);
            }

            // Defense point (lane hub — where monsters converge)
            if (_blight.DefensePosition.HasValue && _blight.DefensePosition != _blight.PumpPosition)
            {
                var defWorld = Systems.Pathfinding.GridToWorld3D(gc, _blight.DefensePosition.Value);
                g.DrawText("DEFEND", cam.WorldToScreen(defWorld), SharpDX.Color.Cyan);
                g.DrawCircleInWorld(defWorld, 30f, SharpDX.Color.Cyan, 2f);
            }

            // Active tower target
            if (_towerAction != null && !_towerAction.IsComplete)
            {
                var targetWorld = Systems.Pathfinding.GridToWorld3D(gc, _towerAction.TargetGridPos);
                var targetScreen = cam.WorldToScreen(targetWorld);
                g.DrawCircleInWorld(targetWorld, 25f, SharpDX.Color.Gold, 3f);
                g.DrawText("TARGET", targetScreen + new Vector2(-20, -25), SharpDX.Color.Gold);
            }

            // Cached portal
            if (_blight.PortalPosition.HasValue)
            {
                var portalWorld = Systems.Pathfinding.GridToWorld3D(gc, _blight.PortalPosition.Value);
                var portalScreen = cam.WorldToScreen(portalWorld);
                g.DrawText("PORTAL", portalScreen + new Vector2(-20, -15), SharpDX.Color.Aqua);
                g.DrawCircleInWorld(portalWorld, 20f, SharpDX.Color.Aqua, 1.5f);
            }

            // Chests
            foreach (var chestPos in _blight.ChestPositions)
            {
                g.DrawText("C", Systems.Pathfinding.GridToScreen(gc, chestPos), SharpDX.Color.Gold);
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

                    g.DrawText($"L{i}", Systems.Pathfinding.GridToScreen(gc, lane[0]), color);
                }
            }

            // Navigation path
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
                var awayTime = _sweepLastOutsidePumpAt != DateTime.MinValue
                    ? $", away {(DateTime.Now - _sweepLastOutsidePumpAt).TotalSeconds:F0}s/{_settings.SweepPumpReturnSeconds.Value:F0}s"
                    : "";
                var noMonsterTime = (DateTime.Now - _sweepLastMonsterSeenAt).TotalSeconds;
                var sweepInfo = $"Sweep: {ctx.Combat.NearbyMonsterCount} nearby, {ctx.Combat.CachedMonsterCount} cached{awayTime}"
                    + (noMonsterTime > 5 ? $", no monsters {noMonsterTime:F0}s" : "");
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

}
