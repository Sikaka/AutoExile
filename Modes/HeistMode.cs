using ExileCore;
using ExileCore.PoEMemory;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.Elements;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using AutoExile.Systems;
using AutoExile.Modes.Shared;
using System.Linq;
using System.Numerics;
using System.Windows.Forms;
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
        private const int RepathCooldownMs = 1250;
        private const float MajorActionCooldownMs = 500f;

        // Harbour & Adiyah interaction state
        private int _adiyahClickAttempts;
        private int _contractInsertAttempts;
        private int _signClickAttempts;

        // Companion wait tracking
        private long _waitingOnEntityId;
        private DateTime _companionWaitStart = DateTime.MinValue;
        private DateTime _lastCompanionClickTime = DateTime.MinValue;
        private int _companionClickAttempts;
        private HeistPhase _returnPhaseAfterDoor;

        // Loot
        private readonly LootPickupTracker _lootTracker = new();
        private DateTime _lastLootScanTime = DateTime.MinValue;
        private DateTime _chestLootWindowEnd = DateTime.MinValue;

        // Curio display evaluation
        private List<CurioDisplayInfo> _curioDisplays = new();
        private DateTime _lastCurioScanTime = DateTime.MinValue;

        // Navigation / exploration tracking
        private long _pendingInteractionEntityId;
        private int _lastStuckCount;
        private int _lastRouteIndex = -1;
        private Vector2? _currentExploreTarget;
        private readonly HashSet<Vector2> _visitedPathNodes = new();

        private string? _heistLogPath;

        private void HeistLog(string msg)
        {
            try
            {
                if (string.IsNullOrEmpty(_heistLogPath))
                {
                    var dir = Path.GetDirectoryName(typeof(HeistMode).Assembly.Location) ?? ".";
                    _heistLogPath = Path.Combine(dir, "heist_debug.log");
                }
                var line = $"[{DateTime.Now:HH:mm:ss.fff}] [Phase:{_phase}] {msg}\n";
                File.AppendAllText(_heistLogPath, line);
            }
            catch { }
        }

        public void OnEnter(BotContext ctx)
        {
            _state.Reset();
            _lootTracker.Reset();
            _visitedPathNodes.Clear();
            _currentExploreTarget = null;
            _status = "Heist mode entered";
            ctx.Loot.IgnoreQuestItems = false;

            // Initialize/reset heist_debug.log on session start
            try
            {
                var dir = Path.GetDirectoryName(typeof(HeistMode).Assembly.Location) ?? ".";
                _heistLogPath = Path.Combine(dir, "heist_debug.log");
                File.WriteAllText(_heistLogPath, $"=== HEIST LOG STARTED {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===\n");
            }
            catch { }

            var gc = ctx.Game;
            var currentArea = gc.Area?.CurrentArea?.Name ?? "";
            _lastAreaName = currentArea;

            if (currentArea == "The Rogue Harbour" || gc.Area?.CurrentArea?.IsHideout == true || gc.Area?.CurrentArea?.IsTown == true)
            {
                _phase = HeistPhase.InHarbour;
                _phaseStartTime = DateTime.Now;
                _status = "In Rogue Harbour — preparing contract";
                HeistLog("Started in Rogue Harbour");
            }
            else
            {
                ModeHelpers.EnableDefaultCombat(ctx);
                _phase = HeistPhase.Initializing;
                _phaseStartTime = DateTime.Now;
                _status = "In contract — initializing";
                HeistLog($"Started in contract: {currentArea}");
            }
        }

        public void OnExit()
        {
            _phase = HeistPhase.Idle;
            _state.Reset();
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

            var currentArea = gc.Area?.CurrentArea?.Name ?? "";
            if (!string.IsNullOrEmpty(currentArea) && currentArea != _lastAreaName)
            {
                OnAreaChanged(ctx, currentArea);
                _lastAreaName = currentArea;
            }

            var isHideout = gc.Area?.CurrentArea?.IsHideout == true;
            var isTown = gc.Area?.CurrentArea?.IsTown == true;
            var isRogueHarbour = currentArea == "The Rogue Harbour";
            bool inContract = !isHideout && !isTown && !isRogueHarbour;

            if (inContract)
            {
                ctx.Navigation.WalkOnly = false;

                if (!ctx.Combat.Profile.Enabled)
                    ModeHelpers.EnableDefaultCombat(ctx);

                ctx.Combat.SuppressPositioning = ctx.Interaction.IsBusy;
                ctx.Combat.SuppressTargetedSkills = ctx.Interaction.IsBusy && ctx.Combat.NearbyMonsterCount == 0;
                ctx.Combat.Tick(ctx);

                var interactionResult = ctx.Interaction.Tick(gc);
                _lootTracker.HandleResult(interactionResult, ctx);

                if (_pendingInteractionEntityId != 0 && interactionResult != InteractionResult.None && interactionResult != InteractionResult.InProgress)
                {
                    if (interactionResult == InteractionResult.Succeeded)
                        _state.OpenedEntities.Add(_pendingInteractionEntityId);
                    _pendingInteractionEntityId = 0;
                }

                _state.Tick(gc);

                if ((DateTime.Now - _lastCurioScanTime).TotalSeconds > 2)
                {
                    _lastCurioScanTime = DateTime.Now;
                    ScanCurioDisplays(gc);
                }
            }
            else
            {
                ctx.Navigation.WalkOnly = true;
            }

            // Phase State Machine
            switch (_phase)
            {
                case HeistPhase.InHarbour:
                    TickInHarbour(ctx, gc);
                    break;
                case HeistPhase.StashItems:
                    TickStashItems(ctx, gc);
                    break;
                case HeistPhase.OpenAdiyah:
                    TickOpenAdiyah(ctx, gc);
                    break;
                case HeistPhase.InsertContract:
                    TickInsertContract(ctx, gc);
                    break;
                case HeistPhase.SelectRogue:
                    TickSelectRogue(ctx, gc);
                    break;
                case HeistPhase.SignContract:
                    TickSignContract(ctx, gc);
                    break;
                case HeistPhase.WaitForPortal:
                    TickWaitForPortal(ctx, gc);
                    break;
                case HeistPhase.EnterPortal:
                    TickEnterPortal(ctx, gc);
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
                    _status = "Heist cycle complete";
                    Decision = "done";
                    break;
                case HeistPhase.Idle:
                    if (isRogueHarbour)
                    {
                        _phase = HeistPhase.InHarbour;
                        _phaseStartTime = DateTime.Now;
                    }
                    else
                    {
                        _status = "Idle";
                        Decision = "idle";
                    }
                    break;
            }
        }

        private void OnAreaChanged(BotContext ctx, string newArea)
        {
            ModeHelpers.CancelAllSystems(ctx);
            _state.OnAreaChanged();
            _lootTracker.ResetCount();
            ctx.Loot.ClearFailed();
            _waitingOnEntityId = 0;
            _pendingInteractionEntityId = 0;
            _visitedPathNodes.Clear();
            _currentExploreTarget = null;
            _adiyahClickAttempts = 0;
            _contractInsertAttempts = 0;
            _signClickAttempts = 0;

            if (newArea == "The Rogue Harbour" || ctx.Game.Area?.CurrentArea?.IsHideout == true || ctx.Game.Area?.CurrentArea?.IsTown == true)
            {
                // If an existing contract portal is still open after death, re-enter it
                var existingPortal = FindHeistPortal(ctx.Game);
                if (existingPortal != null)
                {
                    _phase = HeistPhase.EnterPortal;
                    _phaseStartTime = DateTime.Now;
                    _status = "Contract portal still open — re-entering after death";
                    Decision = "reenter_portal";
                }
                else
                {
                    _phase = HeistPhase.InHarbour;
                    _phaseStartTime = DateTime.Now;
                    _status = "Returned to Harbour — checking inventory";
                    Decision = "harbour_entered";
                }
            }
            else
            {
                // In contract: re-enable combat system
                ModeHelpers.EnableDefaultCombat(ctx);
                _phase = HeistPhase.Initializing;
                _phaseStartTime = DateTime.Now;
                _status = "Entered contract — initializing";
                Decision = "contract_entered";
            }
        }

        // =====================================================================
        // Rogue Harbour Automation: Stash -> Adiyah -> Contract -> Portal
        // =====================================================================

        private void TickInHarbour(BotContext ctx, GameController gc)
        {
            // Close any open inventory panels
            if (gc.IngameState.IngameUi.InventoryPanel?.IsVisible == true)
                BotInput.PressKey(Keys.Escape);

            // Step 1: If an open portal exists, re-enter it immediately
            var existingPortal = FindHeistPortal(gc);
            if (existingPortal != null)
            {
                _phase = HeistPhase.EnterPortal;
                _phaseStartTime = DateTime.Now;
                _status = "Open contract portal found — entering";
                Decision = "enter_existing_portal";
                return;
            }

            // Step 2: Check if contract board is already open on screen
            var openPanel = FindHeistContractPanel(gc);
            if (openPanel != null)
            {
                ctx.Navigation.Stop(gc);
                _phase = HeistPhase.InsertContract;
                _phaseStartTime = DateTime.Now;
                _status = "Contract UI open — preparing contract";
                return;
            }

            // Step 3: Check if we need to stash loot
            int stashableCount = 0;
            var slotItems = StashSystem.GetInventorySlotItems(gc);
            if (slotItems != null)
            {
                foreach (var item in slotItems)
                    if (StashFilterKeepContractsAndMarkers(item)) stashableCount++;
            }

            if (stashableCount >= ctx.Settings.Run.StashItemThreshold.Value)
            {
                _phase = HeistPhase.StashItems;
                _phaseStartTime = DateTime.Now;
                _status = $"Stashing loot ({stashableCount} items)";
                return;
            }

            // Step 4: Check if we have a contract in inventory
            var contract = FindContractInInventory(gc);
            if (contract == null)
            {
                _status = "No Heist Contracts found in inventory — stopping";
                Decision = "no_contracts";
                _phase = HeistPhase.Done;
                return;
            }

            // Step 5: Go to Adiyah
            _phase = HeistPhase.OpenAdiyah;
            _phaseStartTime = DateTime.Now;
            _adiyahClickAttempts = 0;
            _status = "Heading to Adiyah";
            Decision = "goto_adiyah";
        }

        private void TickStashItems(BotContext ctx, GameController gc)
        {
            if (!ctx.Stash.IsBusy)
            {
                var dumpTab = ctx.Settings.Stash.DumpTabName.Value;
                ctx.Stash.Start(
                    storeTabName: string.IsNullOrWhiteSpace(dumpTab) ? null : dumpTab,
                    itemFilter: StashFilterKeepContractsAndMarkers);
            }

            var result = ctx.Stash.Tick(gc, ctx.Navigation);
            if (result == StashResult.Succeeded || result == StashResult.Failed)
            {
                _phase = HeistPhase.InHarbour;
                _phaseStartTime = DateTime.Now;
            }
            _status = $"Stashing items: {ctx.Stash.Status}";
        }

        private void TickOpenAdiyah(BotContext ctx, GameController gc)
        {
            // Check if Adiyah's window is open
            if (IsHeistWindowOpen(gc))
            {
                ctx.Navigation.Stop(gc);
                _phase = HeistPhase.InsertContract;
                _phaseStartTime = DateTime.Now;
                _contractInsertAttempts = 0;
                _status = "Adiyah UI open — inserting contract";
                return;
            }

            var adiyah = FindAdiyah(gc);
            if (adiyah == null)
            {
                _status = "Searching for Adiyah...";
                return;
            }

            var dist = Vector2.Distance(gc.Player.GridPosNum, adiyah.GridPosNum);
            if (dist > 25f)
            {
                if (!ctx.Navigation.IsNavigating)
                    ctx.Navigation.NavigateTo(gc, adiyah.GridPosNum);
                _status = $"Walking to Adiyah (dist: {dist:F0})";
                return;
            }

            if (!ModeHelpers.CanAct(_lastActionTime, MajorActionCooldownMs)) return;

            ctx.Navigation.Stop(gc);

            // Compute exact target on Adiyah
            var pos = adiyah.BoundsCenterPosNum;
            if (pos == Vector3.Zero || float.IsNaN(pos.X))
                pos = adiyah.PosNum;

            pos.Z -= 15f; // Target upper body

            var camera = gc.IngameState.Camera;
            var screenPos = camera.WorldToScreen(pos);
            var windowRect = gc.Window.GetWindowRectangle();
            var absPos = new Vector2(windowRect.X + screenPos.X, windowRect.Y + screenPos.Y);

            if (BotInput.CtrlClick(absPos))
            {
                _lastActionTime = DateTime.Now;
                _adiyahClickAttempts++;
                _status = $"Ctrl+clicking Adiyah (attempt {_adiyahClickAttempts})";
            }
        }

        private void TickInsertContract(BotContext ctx, GameController gc)
        {
            if (!IsHeistWindowOpen(gc))
            {
                _phase = HeistPhase.OpenAdiyah;
                return;
            }

            // If the board has expanded to show details / "SIGN CONTRACT", contract is socketed
            if (IsContractSocketed(gc))
            {
                _phase = HeistPhase.SelectRogue;
                _phaseStartTime = DateTime.Now;
                _status = "Contract socketed — selecting rogue";
                HeistLog("Contract socketed successfully -> SelectRogue");
                return;
            }

            if (!ModeHelpers.CanAct(_lastActionTime, MajorActionCooldownMs)) return;

            // Safety: If cursor is currently holding an item, press Escape to drop it back to inventory
            try
            {
                if (gc.IngameState.IngameUi.Cursor?.ChildCount > 0)
                {
                    BotInput.PressKey(Keys.Escape);
                    _lastActionTime = DateTime.Now;
                    _status = "Clearing item from cursor...";
                    return;
                }
            }
            catch { }

            var contract = FindContractInInventory(gc);
            if (contract == null)
            {
                _status = "No contracts in inventory";
                _phase = HeistPhase.Done;
                return;
            }

            // Guaranteed Ctrl+Click: calculate absolute screen coordinates
            var rect = contract.GetClientRect();
            var windowRect = gc.Window.GetWindowRectangle();
            var absPos = new Vector2(windowRect.X + rect.Center.X, windowRect.Y + rect.Center.Y);

            // Send discrete Ctrl+Click with explicit keydown
            ExileCore.Input.KeyDown(Keys.LControlKey);
            ExileCore.Input.SetCursorPos(absPos);
            BotInput.CtrlClick(absPos);
            ExileCore.Input.KeyUp(Keys.LControlKey);

            _lastActionTime = DateTime.Now;
            _contractInsertAttempts++;
            _status = "Ctrl+clicking contract into socket...";
            HeistLog($"Ctrl+clicked contract in inventory (attempt {_contractInsertAttempts})");
        }

        private void TickSelectRogue(BotContext ctx, GameController gc)
        {
            if (!IsHeistWindowOpen(gc))
            {
                _phase = HeistPhase.OpenAdiyah;
                return;
            }

            var ui = gc.IngameState?.IngameUi;
            var p105 = ui?.GetChildAtIndex(105);
            if (p105 == null) return;

            if (!ModeHelpers.CanAct(_lastActionTime, MajorActionCooldownMs)) return;

            var windowRect = gc.Window.GetWindowRectangle();

            // Step 1: If the Rogue list popup is open (105->2->1->0->2->0), click the first rogue
            var firstRogue = FindRogueSelectionList(gc);
            if (firstRogue != null && firstRogue.IsVisible)
            {
                var r = firstRogue.GetClientRect();
                var clickPos = new Vector2(windowRect.X + r.Center.X, windowRect.Y + r.Center.Y);
                BotInput.Click(clickPos);
                _lastActionTime = DateTime.Now;
                _status = "Selected first rogue member";
                _phase = HeistPhase.SignContract;
                _phaseStartTime = DateTime.Now;
                return;
            }

            // Step 2: Click the team slot button (105->2->0->0->2->1) to open the rogue list
            var slotBtn = FindRogueSlotButton(p105);
            if (slotBtn != null && slotBtn.IsVisible)
            {
                var r = slotBtn.GetClientRect();
                var clickPos = new Vector2(windowRect.X + r.Center.X, windowRect.Y + r.Center.Y);
                BotInput.Click(clickPos);
                _lastActionTime = DateTime.Now;
                _status = "Clicked team slot — opening rogue list";
                return;
            }

            _status = "Waiting for team slot button...";
        }

        private void TickSignContract(BotContext ctx, GameController gc)
        {
            var ui = gc.IngameState?.IngameUi;

            // Once the window closes, the contract has been signed
            if (!IsHeistWindowOpen(gc) || ui == null || ui.ChildCount <= 105)
            {
                _phase = HeistPhase.WaitForPortal;
                _phaseStartTime = DateTime.Now;
                _status = "Contract signed — waiting for portal";
                return;
            }

            var p105 = ui.GetChildAtIndex(105);
            if (p105 == null || !p105.IsVisible) return;

            if (!ModeHelpers.CanAct(_lastActionTime, MajorActionCooldownMs)) return;

            // Click the SIGN CONTRACT button (105->2->2->0->0)
            var signBtn = FindSignContractButton(p105);
            if (signBtn != null && signBtn.IsVisible)
            {
                var r = signBtn.GetClientRect();
                var windowRect = gc.Window.GetWindowRectangle();
                var clickPos = new Vector2(windowRect.X + r.Center.X, windowRect.Y + r.Center.Y);

                BotInput.Click(clickPos);
                _lastActionTime = DateTime.Now;
                _signClickAttempts++;
                _status = $"Clicking SIGN CONTRACT (attempt {_signClickAttempts})";
                return;
            }

            if ((DateTime.Now - _phaseStartTime).TotalSeconds > 10)
            {
                _phase = HeistPhase.WaitForPortal;
                _phaseStartTime = DateTime.Now;
            }
        }

        private void TickWaitForPortal(BotContext ctx, GameController gc)
        {
            var elapsed = (DateTime.Now - _phaseStartTime).TotalSeconds;

            // Wait 2.5 seconds for the portal opening animation to complete
            if (elapsed < 2.5f)
            {
                _status = $"Waiting for portal to open ({2.5f - elapsed:F1}s)...";
                return;
            }

            var portal = FindHeistPortal(gc);
            if (portal != null)
            {
                _phase = HeistPhase.EnterPortal;
                _phaseStartTime = DateTime.Now;
                _status = $"Heist portal found ({portal.RenderName ?? "Portal"}) — entering";
                return;
            }

            if (elapsed > 12)
            {
                _phase = HeistPhase.InHarbour;
                _status = "Portal wait timeout — retrying";
            }
            else
            {
                _status = "Looking for Heist portal near Adiyah...";
            }
        }

        private void TickEnterPortal(BotContext ctx, GameController gc)
        {
            if (gc.IsLoading || gc.Area?.CurrentArea?.Name != "The Rogue Harbour")
            {
                _status = "Entering contract...";
                return;
            }

            if (!ModeHelpers.CanAct(_lastActionTime, MajorActionCooldownMs)) return;

            var portal = FindHeistPortal(gc);
            if (portal == null)
            {
                _phase = HeistPhase.WaitForPortal;
                _phaseStartTime = DateTime.Now;
                return;
            }

            var dist = Vector2.Distance(gc.Player.GridPosNum, portal.GridPosNum);
            if (dist > ctx.Interaction.InteractRadius)
            {
                if (!ctx.Navigation.IsNavigating)
                    ctx.Navigation.NavigateTo(gc, portal.GridPosNum);
                _status = $"Walking to portal (dist: {dist:F0})";
                return;
            }

            ctx.Navigation.Stop(gc);
            ModeHelpers.ClickEntity(gc, portal, ref _lastActionTime);
            _status = $"Clicking portal '{portal.RenderName ?? "Portal"}' to enter Heist";
        }

        // =====================================================================
        // In-Contract Execution: Infiltrate -> Curio -> Escape -> Exit
        // =====================================================================

        private void TickInitializing(BotContext ctx, GameController gc)
        {
            if ((DateTime.Now - _phaseStartTime).TotalSeconds < ctx.Settings.AreaSettleSeconds.Value)
            {
                _status = "Initializing... waiting for area settle";
                Decision = "init_wait";
                return;
            }

            // Step 1: Find entrance transition (live entities or TileEntities)
            var entrance = FindHeistEntranceTransition(gc);
            if (entrance != null)
            {
                var shortName = entrance.RenderName ?? entrance.Path.Split('/').LastOrDefault() ?? "Entrance";
                var dist = Vector2.Distance(gc.Player.GridPosNum, entrance.GridPosNum);

                if (!ctx.Interaction.IsBusy)
                {
                    ctx.Interaction.InteractWithEntity(entrance, ctx.Navigation, requireProximity: true);
                    _status = $"Entering facility via {shortName} (dist: {dist:F0})...";
                    Decision = "enter_facility_transition";
                    HeistLog($"Found entrance transition: {entrance.Path} (RenderName='{entrance.RenderName}', Type={entrance.Type}, dist={dist:F0}) -> moving to click");
                }
                else
                {
                    _status = $"Walking to entrance transition (dist: {dist:F0})...";
                }
                return; // Stay in Initializing until through
            }

            // Step 2: Once inside the facility, initialize route and start infiltration
            HeistLog("No entrance transition found in staging — initializing route for main facility");
            _state.Initialize(gc);
            _state.BuildRoute(ctx.Settings.Heist);
            ModeHelpers.EnableDefaultCombat(ctx);

            var routeDesc = string.Join(" → ", _state.PlannedRoute.Select(t => t.Label));
            ctx.Log($"Heist initialized: {_state.Status}");
            ctx.Log($"Route ({_state.PlannedRoute.Count} targets): {routeDesc}");
            HeistLog($"Inside facility — route: {routeDesc}");

            // Mid-lockdown check
            if (!_state.IsAlertPanelVisible && _state.FindCurioEntity(gc) == null && _state.CompanionEntityId != 0)
            {
                _state.ForceLockdown();
                _phase = HeistPhase.Escaping;
                _phaseStartTime = DateTime.Now;
                _status = "Lockdown detected (mid-start)";
                Decision = "init_lockdown";
                return;
            }

            _phase = HeistPhase.Infiltrating;
            _phaseStartTime = DateTime.Now;
            _status = "Infiltrating";
            Decision = "infiltrating";
        }

        private void TickInfiltrating(BotContext ctx, GameController gc)
        {
            if (_state.IsLockdown)
            {
                ctx.Navigation.Stop(gc);
                _visitedPathNodes.Clear();
                _currentExploreTarget = null;
                _phase = HeistPhase.GrabCurio;
                _phaseStartTime = DateTime.Now;
                Decision = "lockdown_detected";
                HeistLog("Lockdown started — grabbing curio before escape");
                return;
            }

            var playerGrid = gc.Player.GridPosNum;

            var curio = _state.FindCurioEntity(gc);
            if (curio != null && curio.DistancePlayer < 25)
            {
                ctx.Navigation.Stop(gc);
                _phase = HeistPhase.GrabCurio;
                _phaseStartTime = DateTime.Now;
                Decision = "at_curio";
                HeistLog("Curio room reached");
                return;
            }

            var currentRouteTarget = _state.CurrentTarget;
            bool nearChestTarget = currentRouteTarget != null && currentRouteTarget.Type == RouteTargetType.RewardChest && Vector2.Distance(playerGrid, currentRouteTarget.GridPos) < 40;

            // Priority 1: Clear nearby active monsters
            if (ctx.Combat.InCombat && ctx.Combat.NearbyMonsterCount > 0 && !nearChestTarget)
            {
                if (ctx.Navigation.IsNavigating)
                    ctx.Navigation.Stop(gc);

                _status = $"Clearing enemies ({ctx.Combat.NearbyMonsterCount} nearby)...";
                Decision = "clearing_pack";

                // Log enemy breakdown every 2.5s while actively engaged
                if ((DateTime.Now - _lastActionTime).TotalSeconds > 2.5)
                {
                    LogNearbyEnemiesDebug(gc);
                    _lastActionTime = DateTime.Now;
                }
                return;
            }

            // Priority 2: Loot ground items only when clear of threats
            if ((DateTime.Now - _lastLootScanTime).TotalMilliseconds > 500 && !ctx.Interaction.IsBusy)
            {
                _lastLootScanTime = DateTime.Now;
                TryPickupLoot(ctx, gc);
            }

            if (DateTime.Now < _chestLootWindowEnd)
            {
                _status = "Looting chest drops...";
                Decision = "chest_loot_window";
                return;
            }

            // Priority 3: Check for blocking doors in the corridor
            if (!ctx.Interaction.IsBusy && !nearChestTarget)
            {
                var blockingDoor = FindBlockingDoor(gc, playerGrid);
                if (blockingDoor != null)
                {
                    StartDoorInteraction(ctx, gc, blockingDoor, HeistPhase.Infiltrating);
                    return;
                }
            }

            // Priority 4: Route navigation & chest interaction
            while (_state.CurrentRouteIndex < _state.PlannedRoute.Count)
            {
                var t = _state.PlannedRoute[_state.CurrentRouteIndex];
                if (t.Reached || t.Skipped) { _state.CurrentRouteIndex++; continue; }
                if (t.Type == RouteTargetType.RewardChest && _state.OpenedEntities.Contains(t.EntityId)) { t.Reached = true; _state.CurrentRouteIndex++; continue; }
                break;
            }

            if (_state.CurrentRouteIndex != _lastRouteIndex)
            {
                _lastStuckCount = ctx.Navigation.StuckRecoveries;
                _lastRouteIndex = _state.CurrentRouteIndex;
            }

            var target = _state.CurrentTarget;

            if (target != null && target.Type == RouteTargetType.RewardChest)
            {
                if (_state.AlertPercent > ctx.Settings.Heist.AlertThreshold.Value)
                {
                    target.Skipped = true;
                    _status = $"Skipping {target.Label} — alert {_state.AlertPercent:F0}%";
                    Decision = "skip_chest_alert";
                    HeistLog($"Skipping chest {target.Label} — alert { _state.AlertPercent:F0}%");
                    return;
                }

                var distToChest = Vector2.Distance(playerGrid, target.GridPos);
                if (distToChest < 35)
                {
                    Entity? chestEntity = null;
                    float bestChestDist = 25f;
                    foreach (var e in gc.EntityListWrapper.OnlyValidEntities)
                    {
                        if (e?.Path == null || !e.IsTargetable || !e.Path.Contains("HeistChest")) continue;
                        if (e.Id == target.EntityId) { chestEntity = e; break; }
                        var d = Vector2.Distance(e.GridPosNum, target.GridPos);
                        if (d < bestChestDist)
                        {
                            var ch = e.GetComponent<Chest>();
                            if (ch?.IsOpened != true) { bestChestDist = d; chestEntity = e; }
                        }
                    }

                    if (chestEntity != null)
                    {
                        var chest = chestEntity.GetComponent<Chest>();
                        if (chest?.IsOpened != true && !ctx.Interaction.IsBusy)
                        {
                            StartChestInteraction(ctx, gc, chestEntity);
                            return;
                        }
                    }
                    else if ((DateTime.Now - _phaseStartTime).TotalSeconds > 10)
                    {
                        target.Reached = true;
                        return;
                    }
                }

                NavigateToRouteTarget(ctx, gc, playerGrid, target);
            }
            else if (target != null && target.Type == RouteTargetType.Curio)
            {
                var curioEntity = _state.FindCurioEntity(gc);
                if (curioEntity != null) target.GridPos = curioEntity.GridPosNum;
                NavigateToRouteTarget(ctx, gc, playerGrid, target);
            }
            else
            {
                FallbackExplore(ctx, gc, playerGrid);
            }
        }

        private void TickAtDoor(BotContext ctx, GameController gc)
        {
            var elapsed = (DateTime.Now - _companionWaitStart).TotalSeconds;
            var settings = ctx.Settings.Heist;

            Entity? doorEntity = null;
            foreach (var e in gc.EntityListWrapper.OnlyValidEntities)
                if (e.Id == _waitingOnEntityId) { doorEntity = e; break; }

            if (doorEntity == null || !doorEntity.IsTargetable)
            {
                _state.OpenedEntities.Add(_waitingOnEntityId);
                _waitingOnEntityId = 0;
                _phase = _returnPhaseAfterDoor;
                _phaseStartTime = DateTime.Now;
                HeistLog($"Door {_waitingOnEntityId} opened successfully");
                return;
            }

            var distToDoor = doorEntity.DistancePlayer;
            bool isClickDoor = doorEntity.Path == "Metadata/MiscellaneousObjects/Door" || doorEntity.Path?.Contains("Door_Basic") == true;

            if (isClickDoor)
            {
                if (distToDoor > 40)
                {
                    if (!ctx.Navigation.IsNavigating)
                    {
                        var nearWalkable = ctx.Navigation.FindNearestWalkable(gc, doorEntity.GridPosNum, 20);
                        if (nearWalkable.HasValue) ctx.Navigation.NavigateTo(gc, nearWalkable.Value);
                    }
                    return;
                }

                if (ctx.Navigation.IsNavigating) ctx.Navigation.Stop(gc);

                if (!ctx.Interaction.IsBusy && (DateTime.Now - _lastCompanionClickTime).TotalSeconds > 2)
                {
                    ctx.Interaction.InteractWithEntity(doorEntity, ctx.Navigation, requireProximity: false);
                    _lastCompanionClickTime = DateTime.Now;
                }

                if (elapsed > 30)
                {
                    _waitingOnEntityId = 0;
                    _phase = _returnPhaseAfterDoor;
                    _phaseStartTime = DateTime.Now;
                }
                return;
            }

            var sm2 = doorEntity.GetComponent<StateMachine>();
            var locked = HeistState.GetStateValue(sm2, "heist_locked");

            if (locked == 0 || !doorEntity.IsTargetable)
            {
                _state.OpenedEntities.Add(_waitingOnEntityId);
                _waitingOnEntityId = 0;
                _phase = _returnPhaseAfterDoor;
                _phaseStartTime = DateTime.Now;
                return;
            }

            if (distToDoor > 30)
            {
                if (!ctx.Navigation.IsNavigating)
                {
                    var dir = Vector2.Normalize(doorEntity.GridPosNum - gc.Player.GridPosNum);
                    var approachTarget = doorEntity.GridPosNum - dir * 25;
                    var nearWalkable = ctx.Navigation.FindNearestWalkable(gc, approachTarget, 15);
                    if (nearWalkable.HasValue) ctx.Navigation.NavigateTo(gc, nearWalkable.Value);
                }
                return;
            }

            var doorAccepted = locked < 2;
            var companionWorking = _state.CompanionLockPickProgress > 0 || _state.CompanionIsBusy || doorAccepted;

            if (!companionWorking)
            {
                var timeSinceClick = (DateTime.Now - _lastCompanionClickTime).TotalSeconds;
                var retryDelay = _companionClickAttempts == 0 ? 0.3 : 1.5;
                if (timeSinceClick > retryDelay)
                {
                    var sent = BotInput.PressKeyOverlay(settings.CompanionInteractKey);
                    if (sent)
                    {
                        _lastCompanionClickTime = DateTime.Now;
                        _companionClickAttempts++;
                    }
                }
                if (elapsed > settings.CompanionRetryDelay.Value)
                    _companionWaitStart = DateTime.Now;
            }

            if (elapsed > settings.CompanionWaitTimeout.Value)
            {
                HeistLog($"Companion door timeout on {_waitingOnEntityId}");
                _waitingOnEntityId = 0;
                _phase = _returnPhaseAfterDoor;
                _phaseStartTime = DateTime.Now;
            }
        }

        private void TickAtChest(BotContext ctx, GameController gc)
        {
            var elapsed = (DateTime.Now - _companionWaitStart).TotalSeconds;
            var settings = ctx.Settings.Heist;

            Entity? chestEntity = null;
            foreach (var e in gc.EntityListWrapper.OnlyValidEntities)
                if (e.Id == _waitingOnEntityId) { chestEntity = e; break; }

            if (chestEntity == null || !chestEntity.IsTargetable || chestEntity.GetComponent<Chest>()?.IsOpened == true)
            {
                _state.OpenedEntities.Add(_waitingOnEntityId);
                _waitingOnEntityId = 0;
                _chestLootWindowEnd = DateTime.Now.AddSeconds(3);
                _phase = HeistPhase.Infiltrating;
                _phaseStartTime = DateTime.Now;
                Decision = "chest_opened";
                HeistLog($"Chest {_waitingOnEntityId} opened successfully");
                return;
            }

            var distToChest = chestEntity.DistancePlayer;
            if (distToChest > 35)
            {
                if (!ctx.Navigation.IsNavigating)
                    ctx.Navigation.MoveToward(gc, chestEntity.GridPosNum);
                return;
            }

            var sm = chestEntity.GetComponent<StateMachine>();
            var heistLocked = HeistState.GetStateValue(sm, "heist_locked");
            var chestAccepted = heistLocked > 0 && heistLocked < 2;
            var companionWorking = _state.CompanionLockPickProgress > 0 || _state.CompanionIsBusy || chestAccepted;

            if (heistLocked > 0 && !companionWorking)
            {
                var timeSinceClick = (DateTime.Now - _lastCompanionClickTime).TotalSeconds;
                var retryDelay = _companionClickAttempts == 0 ? 0.3 : 1.5;
                if (timeSinceClick > retryDelay)
                {
                    var sent = BotInput.PressKeyOverlay(settings.CompanionInteractKey);
                    if (sent)
                    {
                        _lastCompanionClickTime = DateTime.Now;
                        _companionClickAttempts++;
                    }
                }
                if (elapsed > settings.CompanionRetryDelay.Value)
                    _companionWaitStart = DateTime.Now;
            }
            else if (heistLocked <= 0 && !ctx.Interaction.IsBusy)
            {
                ctx.Interaction.InteractWithEntity(chestEntity, ctx.Navigation, requireProximity: false);
            }

            if (elapsed > settings.CompanionWaitTimeout.Value)
            {
                HeistLog($"Chest interaction timeout on {_waitingOnEntityId}");
                _state.OpenedEntities.Add(_waitingOnEntityId);
                _waitingOnEntityId = 0;
                _phase = HeistPhase.Infiltrating;
                _phaseStartTime = DateTime.Now;
            }
        }

        private void TickGrabCurio(BotContext ctx, GameController gc)
        {
            if (_state.IsLockdown)
            {
                var timeSinceLockdown = (DateTime.Now - _phaseStartTime).TotalSeconds;
                if ((DateTime.Now - _lastLootScanTime).TotalMilliseconds > 500 && !ctx.Interaction.IsBusy)
                {
                    _lastLootScanTime = DateTime.Now;
                    TryPickupLoot(ctx, gc);
                }

                if (timeSinceLockdown > 3 && !ctx.Interaction.IsBusy && !_lootTracker.HasPending)
                {
                    ctx.Loot.Scan(gc);
                    var (hasLoot, _) = ctx.Loot.PickupNext(ctx.Interaction, ctx.Navigation);
                    if (!ctx.Interaction.IsBusy)
                    {
                        ctx.Navigation.Stop(gc);
                        _phase = HeistPhase.Escaping;
                        _phaseStartTime = DateTime.Now;
                        return;
                    }
                }
                return;
            }

            var curio = _state.FindCurioEntity(gc);
            if (curio != null && !ctx.Interaction.IsBusy)
            {
                ctx.Interaction.InteractWithEntity(curio, ctx.Navigation, requireProximity: true);
                return;
            }

            if (curio == null && (DateTime.Now - _phaseStartTime).TotalSeconds > 30)
            {
                _phase = HeistPhase.Escaping;
                _phaseStartTime = DateTime.Now;
            }
        }

        private void TickEscaping(BotContext ctx, GameController gc)
        {
            var playerGrid = gc.Player.GridPosNum;

            // Priority 1: Clear enemies blocking our escape path
            if (ctx.Combat.InCombat && ctx.Combat.NearbyMonsterCount > 0)
            {
                if (ctx.Navigation.IsNavigating)
                    ctx.Navigation.Stop(gc);

                _status = $"Clearing escape path ({ctx.Combat.NearbyMonsterCount} nearby)...";
                Decision = "escape_combat";
                HeistLog($"Fighting {ctx.Combat.NearbyMonsterCount} enemies in escape path (Target: {ctx.Combat.BestTarget?.RenderName})");
                return;
            }

            // Priority 2: Check for re-locked doors blocking the path
            var blockingDoor = FindBlockingDoor(gc, playerGrid);
            if (blockingDoor != null && !ctx.Interaction.IsBusy)
            {
                ctx.Navigation.Stop(gc);
                HeistLog($"Found blocking door {blockingDoor.Id} during escape — opening");
                StartDoorInteraction(ctx, gc, blockingDoor, HeistPhase.Escaping);
                return;
            }

            // Priority 3: Check for stuck navigation
            if (ctx.Navigation.IsNavigating)
            {
                var stuckDelta = ctx.Navigation.StuckRecoveries - _lastStuckCount;
                if (stuckDelta >= 2)
                {
                    ctx.Navigation.Stop(gc);
                    var nextDoor = FindNextLockedDoor(gc, playerGrid);
                    if (nextDoor != null)
                    {
                        HeistLog($"Stuck navigation — diverting to door {nextDoor.Id}");
                        StartDoorInteraction(ctx, gc, nextDoor, HeistPhase.Escaping);
                        return;
                    }
                    _lastStuckCount = ctx.Navigation.StuckRecoveries;
                }
            }

            if (_state.ExitPosition == null)
                _state.ScanForExit(gc);

            if (_state.ExitPosition != null)
            {
                var distToExit = Vector2.Distance(playerGrid, _state.ExitPosition.Value);
                if (distToExit < 20)
                {
                    _phase = HeistPhase.ExitingMap;
                    _phaseStartTime = DateTime.Now;
                    HeistLog("Arrived at escape exit — transitioning to ExitingMap");
                    return;
                }

                if (!ctx.Navigation.IsNavigating)
                {
                    _lastStuckCount = ctx.Navigation.StuckRecoveries;
                    if (!ctx.Navigation.NavigateTo(gc, _state.ExitPosition.Value))
                    {
                        // Direct A* blocked by closed doors — search wider for the next door
                        var nextDoor = FindNextLockedDoor(gc, playerGrid);
                        if (nextDoor != null)
                        {
                            HeistLog($"A* to exit blocked — heading to next locked door {nextDoor.Id}");
                            StartDoorInteraction(ctx, gc, nextDoor, HeistPhase.Escaping);
                            return;
                        }

                        // Fallback: move directly in the direction of the exit
                        HeistLog($"A* blocked and no door visible — moving toward exit");
                        ctx.Navigation.MoveToward(gc, _state.ExitPosition.Value);
                    }
                }
                _status = $"Escaping — dist to exit: {distToExit:F0}";
            }
            else if (!ctx.Navigation.IsNavigating)
            {
                var nextDoor = FindNextLockedDoor(gc, playerGrid);
                if (nextDoor != null)
                {
                    HeistLog($"Searching for exit — opening door {nextDoor.Id}");
                    StartDoorInteraction(ctx, gc, nextDoor, HeistPhase.Escaping);
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
                return;
            }

            if ((DateTime.Now - _phaseStartTime).TotalSeconds > 15)
            {
                if (_state.ExitPosition != null)
                    ctx.Navigation.NavigateTo(gc, _state.ExitPosition.Value);
                _phaseStartTime = DateTime.Now;
            }
        }

        // =====================================================================
        // Helpers
        // =====================================================================

        private bool IsHeistWindowOpen(GameController gc)
        {
            var ui = gc.IngameState?.IngameUi;
            if (ui == null || ui.ChildCount <= 105) return false;

            var p105 = ui.GetChildAtIndex(105);
            return p105 != null && p105.IsVisible && p105.GetClientRect().Width > 300;
        }

        private Element? FindHeistContractPanel(GameController gc)
        {
            var ui = gc.IngameState?.IngameUi;
            if (ui == null) return null;

            if (ui.ChildCount > 105)
            {
                var p105 = ui.GetChildAtIndex(105);
                if (p105 != null && p105.IsVisible && p105.GetClientRect().Width > 400)
                    return p105;
            }

            for (int i = 0; i < ui.ChildCount; i++)
            {
                var child = ui.GetChildAtIndex(i);
                if (child == null || !child.IsVisible) continue;
                var rect = child.GetClientRect();
                if (rect.Width < 400 || rect.Height < 300) continue;

                if (FindElementByText(child, "Contract Details") != null ||
                    FindElementByText(child, "SIGN CONTRACT") != null ||
                    FindElementByText(child, "The Ring's Cut") != null)
                {
                    return child;
                }
            }

            return null;
        }

        private bool IsContractSocketed(GameController gc)
        {
            var ui = gc.IngameState?.IngameUi;
            if (ui == null || ui.ChildCount <= 105) return false;

            var p105 = ui.GetChildAtIndex(105);
            if (p105 == null || !p105.IsVisible || p105.ChildCount <= 2) return false;

            // [105][2] is the expanded details board which only becomes visible when a contract is socketed
            var child2 = p105.GetChildAtIndex(2);
            return child2 != null && child2.IsVisible;
        }

        private Entity? FindAdiyah(GameController gc)
        {
            foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
            {
                if (entity.IsTargetable && entity.Path != null &&
                    (entity.Path.Contains("HeistPortalNPC") || entity.Path.Contains("NPC/League/Heist/Adiyah") || entity.RenderName == "Adiyah, the Wayfinder"))
                {
                    return entity;
                }
            }
            return null;
        }

        private Entity? FindHeistPortal(GameController gc)
        {
            var adiyah = FindAdiyah(gc);
            var adiyahPos = adiyah?.GridPosNum ?? gc.Player.GridPosNum;

            Entity? bestPortal = null;
            float bestDist = 40f; // Adiyah's portal is always within 40 units of her

            foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
            {
                if (!entity.IsTargetable || entity.Path == null) continue;

                // Explicitly ignore Planning Room and Harbour zone transitions
                var renderName = entity.RenderName ?? "";
                if (renderName.Equals("Planning Room", StringComparison.OrdinalIgnoreCase) ||
                    renderName.Equals("The Rogue Harbour", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Match MissionEntryPortal, HeistPortal, or portals spawned near Adiyah
                bool isHeistPortal = entity.Path.Contains("MissionEntryPortal", StringComparison.OrdinalIgnoreCase)
                    || entity.Path.Contains("HeistPortal", StringComparison.OrdinalIgnoreCase)
                    || entity.Path.Contains("Heist/Objects", StringComparison.OrdinalIgnoreCase)
                    || entity.Type == EntityType.TownPortal
                    || (entity.Type == EntityType.AreaTransition && entity.Path.Contains("Portal", StringComparison.OrdinalIgnoreCase));

                if (isHeistPortal)
                {
                    var distToAdiyah = Vector2.Distance(entity.GridPosNum, adiyahPos);
                    if (distToAdiyah < bestDist)
                    {
                        bestDist = distToAdiyah;
                        bestPortal = entity;
                    }
                }
            }

            return bestPortal;
        }

        private static ServerInventory.InventSlotItem? FindContractInInventory(GameController gc)
        {
            var invItems = StashSystem.GetInventorySlotItems(gc);
            if (invItems == null) return null;

            foreach (var slotItem in invItems)
            {
                var item = slotItem.Item;
                if (item?.Path == null) continue;
                if (item.Path.Contains("Items/Heist/HeistContract") || item.Path.Contains("HeistContract"))
                    return slotItem;
            }
            return null;
        }

        private static bool StashFilterKeepContractsAndMarkers(ServerInventory.InventSlotItem item)
        {
            var path = item.Item?.Path;
            if (path == null) return true;
            if (path.Contains("HeistContract") || path.Contains("CurrencyHeistCoinage")) return false;
            return true;
        }

        private Element? FindRogueSlotButton(Element panel105)
        {
            if (panel105 == null || !panel105.IsVisible) return null;

            // Safe navigation of: 105->2->0->0->2->1->0
            var c2 = SafeGetChild(panel105, 2);
            var c0 = SafeGetChild(c2, 0);
            var c0_2 = SafeGetChild(c0, 0);
            var c2_2 = SafeGetChild(c0_2, 2);
            var c1 = SafeGetChild(c2_2, 1);
            var btn = SafeGetChild(c1, 0) ?? c1;

            if (btn != null && btn.IsVisible && btn.GetClientRect().Width > 30)
                return btn;

            return null;
        }

        private Element? FindRogueSelectionList(GameController gc)
        {
            var ui = gc.IngameState?.IngameUi;
            if (ui == null) return null;

            var p105 = SafeGetChild(ui, 105);
            if (p105 == null || !p105.IsVisible) return null;

            // Safe navigation of: 105->2->1->0->2->0->0
            var c2 = SafeGetChild(p105, 2);
            var c1 = SafeGetChild(c2, 1);
            var c0 = SafeGetChild(c1, 0);
            var c2_2 = SafeGetChild(c0, 2);
            var c0_2 = SafeGetChild(c2_2, 0);
            var rogueBtn = SafeGetChild(c0_2, 0) ?? c0_2;

            if (rogueBtn != null && rogueBtn.IsVisible && rogueBtn.GetClientRect().Width > 30)
                return rogueBtn;

            return null;
        }

        private Element? FindSignContractButton(Element panel105)
        {
            if (panel105 == null || !panel105.IsVisible) return null;

            // Safe navigation of: 105->2->2->0->0
            var c2 = SafeGetChild(panel105, 2);
            var c2_2 = SafeGetChild(c2, 2);
            var c0 = SafeGetChild(c2_2, 0);
            var btn = SafeGetChild(c0, 0) ?? c0 ?? c2_2;

            if (btn != null && btn.IsVisible && btn.GetClientRect().Width > 30)
                return btn;

            return null;
        }

        private static Element? FindElementByText(Element parent, string text, bool checkVisibility = true)
        {
            if (parent == null) return null;
            if (checkVisibility && !parent.IsVisible) return null;

            if (parent.Text != null && parent.Text.Contains(text, StringComparison.OrdinalIgnoreCase))
                return parent;

            for (int i = 0; i < parent.ChildCount; i++)
            {
                var child = parent.GetChildAtIndex(i);
                if (child == null) continue;
                var res = FindElementByText(child, text, checkVisibility);
                if (res != null) return res;
            }
            return null;
        }

        private void StartDoorInteraction(BotContext ctx, GameController gc, Entity door, HeistPhase returnPhase)
        {
            _waitingOnEntityId = door.Id;
            _companionWaitStart = DateTime.Now;
            _lastCompanionClickTime = DateTime.MinValue;
            _companionClickAttempts = 0;
            _returnPhaseAfterDoor = returnPhase;
            _phase = HeistPhase.AtDoor;
            _phaseStartTime = DateTime.Now;

            bool isClickDoor = door.Path == "Metadata/MiscellaneousObjects/Door" || door.Path?.Contains("Door_Basic") == true;

            if (isClickDoor)
            {
                if (!ctx.Interaction.IsBusy && door.DistancePlayer < 40)
                {
                    ctx.Interaction.InteractWithEntity(door, ctx.Navigation, requireProximity: false);
                    _lastCompanionClickTime = DateTime.Now;
                }
            }
            else if (door.DistancePlayer < 45)
            {
                // Send V concurrently without stopping movement or interrupting skill channeling
                var sent = BotInput.PressKeyOverlay(ctx.Settings.Heist.CompanionInteractKey);
                if (sent) _lastCompanionClickTime = DateTime.Now;
                HeistLog($"Triggered companion (V) overlay for door {door.Id} (dist: {door.DistancePlayer:F0})");
            }
        }

        private void StartChestInteraction(BotContext ctx, GameController gc, Entity chest)
        {
            _waitingOnEntityId = chest.Id;
            _companionWaitStart = DateTime.Now;
            _lastCompanionClickTime = DateTime.MinValue;
            _companionClickAttempts = 0;
            _phase = HeistPhase.AtChest;
            _phaseStartTime = DateTime.Now;

            var sm = chest.GetComponent<StateMachine>();
            var heistLocked = HeistState.GetStateValue(sm, "heist_locked");
            if (heistLocked > 0)
            {
                // Send V concurrently without stopping movement
                var sent = BotInput.PressKeyOverlay(ctx.Settings.Heist.CompanionInteractKey);
                if (sent) _lastCompanionClickTime = DateTime.Now;
                HeistLog($"Triggered companion (V) overlay for chest {chest.Id} (dist: {chest.DistancePlayer:F0})");
            }
        }

        private void NavigateToRouteTarget(BotContext ctx, GameController gc, Vector2 playerGrid, RouteTarget target)
        {
            if (ctx.Navigation.IsNavigating)
            {
                var stuckDelta = ctx.Navigation.StuckRecoveries - _lastStuckCount;
                if (stuckDelta >= 5)
                {
                    target.Skipped = true;
                    ctx.Navigation.Stop(gc);
                    return;
                }

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
                if ((DateTime.Now - _lastRepathTime).TotalMilliseconds < RepathCooldownMs) return;

                var nearbyDoor = FindNextLockedDoor(gc, playerGrid);
                if (nearbyDoor != null)
                {
                    _lastRepathTime = DateTime.Now;
                    StartDoorInteraction(ctx, gc, nearbyDoor, HeistPhase.Infiltrating);
                    return;
                }

                if (!ctx.Navigation.NavigateTo(gc, target.GridPos))
                {
                    var stepNode = FindPathNodeToward(gc, playerGrid, target.GridPos);
                    if (stepNode.HasValue) ctx.Navigation.NavigateTo(gc, stepNode.Value);
                }
                _lastRepathTime = DateTime.Now;
            }
            else
            {
                ctx.Navigation.UpdateDestination(gc, target.GridPos, 12);
            }
        }

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
                }
            }
            else if (!ctx.Navigation.IsNavigating)
            {
                if ((DateTime.Now - _lastRepathTime).TotalMilliseconds < RepathCooldownMs) return;

                var bestNode = FindNextPathNode(gc, playerGrid);
                if (bestNode.HasValue)
                {
                    if (ctx.Navigation.NavigateTo(gc, bestNode.Value))
                    {
                        _currentExploreTarget = bestNode.Value;
                        _lastStuckCount = ctx.Navigation.StuckRecoveries;
                    }
                    else
                    {
                        _visitedPathNodes.Add(bestNode.Value);
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
                            if (nearWalkable.HasValue) ctx.Navigation.NavigateTo(gc, nearWalkable.Value);
                        }
                    }
                }
            }
        }

        private Vector2? FindNextPathNode(GameController gc, Vector2 playerGrid)
        {
            var exitPos = _state.ExitPosition ?? playerGrid;
            Vector2? best = null;
            float bestDistFromExit = 0;

            foreach (var nodeGrid in _state.PathNodes)
            {
                if (_visitedPathNodes.Contains(nodeGrid)) continue;
                var distToPlayer = Vector2.Distance(playerGrid, nodeGrid);
                if (distToPlayer < 15 || distToPlayer > Pathfinding.NetworkBubbleRadius) continue;

                var distFromExit = Vector2.Distance(nodeGrid, exitPos);
                if (distFromExit > bestDistFromExit)
                {
                    bestDistFromExit = distFromExit;
                    best = nodeGrid;
                }
            }
            return best;
        }

        private Entity? FindNextLockedDoor(GameController gc, Vector2 playerGrid)
        {
            Entity? nearest = null;
            float nearestDist = float.MaxValue;

            foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
            {
                if (entity?.Path == null || !entity.IsTargetable || _state.OpenedEntities.Contains(entity.Id)) continue;
                bool isDoor = false;

                if (entity.Path == "Metadata/MiscellaneousObjects/Door") isDoor = true;
                else if (entity.Path.Contains("Door_Basic") && HeistState.GetStateValue(entity.GetComponent<StateMachine>(), "open") == 0) isDoor = true;
                else if ((entity.Path.Contains("Door_NPC") && !entity.Path.Contains("Alternate")) || entity.Path.Contains("Vault"))
                {
                    if (HeistState.GetStateValue(entity.GetComponent<StateMachine>(), "heist_locked") > 0)
                    {
                        isDoor = true;
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

        private Vector2? FindPathNodeToward(GameController gc, Vector2 playerGrid, Vector2 target)
        {
            var playerDistToTarget = Vector2.Distance(playerGrid, target);
            Vector2? best = null;
            float bestDist = float.MaxValue;

            foreach (var nodeGrid in _state.PathNodes)
            {
                if (Vector2.Distance(nodeGrid, target) >= playerDistToTarget) continue;
                var distToPlayer = Vector2.Distance(playerGrid, nodeGrid);
                if (distToPlayer < 15 || distToPlayer > Pathfinding.NetworkBubbleRadius) continue;

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
                if (entity?.Path == null || !entity.IsTargetable || entity.DistancePlayer > 50) continue;
                bool isDoor = false;

                if (entity.Path == "Metadata/MiscellaneousObjects/Door") isDoor = true;
                else if (entity.Path.Contains("Door_Basic") && HeistState.GetStateValue(entity.GetComponent<StateMachine>(), "open") == 0) isDoor = true;
                else if ((entity.Path.Contains("Door_NPC") && !entity.Path.Contains("Alternate")) || entity.Path.Contains("Vault"))
                {
                    if (HeistState.GetStateValue(entity.GetComponent<StateMachine>(), "heist_locked") > 0)
                    {
                        isDoor = true;
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

        private void TryPickupLoot(BotContext ctx, GameController gc)
        {
            if (_lootTracker.HasPending || ctx.Interaction.IsBusy) return;

            // Priority: never pick up loot while enemies are nearby or actively engaged
            if (ctx.Combat.InCombat || ctx.Combat.NearbyMonsterCount > 0) return;

            ctx.Loot.Scan(gc);
            var (wasInRadius, candidate) = ctx.Loot.PickupNext(ctx.Interaction, ctx.Navigation);
            if (candidate != null && ctx.Interaction.IsBusy)
                _lootTracker.SetPending(candidate.Entity.Id, candidate.ItemName, candidate.ChaosValue);
        }

        // =====================================================================
        // Rendering
        // =====================================================================


        public void Render(BotContext ctx)
        {
            if (ctx.Graphics == null) return;
            var g = ctx.Graphics;
            var gc = ctx.Game;
            if (!gc.InGame) return;

            var cam = gc.IngameState.Camera;
            var hudX = 20f;
            var hudY = 200f;
            var lineH = 16f;

            void DrawTextWithBg(string text, Vector2 pos, SharpDX.Color textColor, SharpDX.Color? bgColor = null)
            {
                var bg = bgColor ?? new SharpDX.Color(0, 0, 0, 210);
                var textSize = text.Length * 7.2f + 10f;
                g.DrawBox(new SharpDX.RectangleF(pos.X - 2, pos.Y - 1, textSize, lineH), bg);
                g.DrawText(text, pos, textColor);
            }

            DrawTextWithBg("=== COMBAT & HEIST DEBUG ===", new Vector2(hudX, hudY), SharpDX.Color.Cyan, new SharpDX.Color(0, 20, 40, 240));
            hudY += lineH + 2;

            var area = gc.Area?.CurrentArea;
            var areaName = area?.Name ?? "NULL";
            bool isTown = area?.IsTown == true;
            bool isHideout = area?.IsHideout == true;
            bool isRogueHarbour = areaName == "The Rogue Harbour";
            bool isSafeZone = isTown || isHideout || isRogueHarbour;

            DrawTextWithBg($"Area: \"{areaName}\" | Phase: {_phase}", new Vector2(hudX, hudY), SharpDX.Color.White);
            hudY += lineH;

            // Combat State
            var c = ctx.Combat;
            var combatColor = c.InCombat ? SharpDX.Color.Red : SharpDX.Color.Gray;
            DrawTextWithBg($"Combat: InCombat={c.InCombat} | Nearby={c.NearbyMonsterCount} | Target={c.BestTarget?.RenderName ?? "(none)"}",
                new Vector2(hudX, hudY), combatColor);
            hudY += lineH;

            DrawTextWithBg($"Action: \"{c.LastAction}\" | SkillAction: \"{c.LastSkillAction}\"",
                new Vector2(hudX, hudY), SharpDX.Color.Yellow);
            hudY += lineH;

            DrawTextWithBg($"InputGate: CanAct={BotInput.CanAct} | MoveActive={BotInput.IsMovementActive} | MoveKey={ctx.Navigation.MoveKey}",
                new Vector2(hudX, hudY), BotInput.CanAct ? SharpDX.Color.LimeGreen : SharpDX.Color.OrangeRed);
            hudY += lineH;

            DrawTextWithBg($"Status: {_status}", new Vector2(hudX, hudY), SharpDX.Color.Orange);
            hudY += lineH;

            if (isSafeZone)
                return;

            // Visual overlay for detected entrance transition in staging room
            if (_phase == HeistPhase.Initializing)
            {
                var entrance = FindHeistEntranceTransition(gc);
                if (entrance != null)
                {
                    var eWorld = Pathfinding.GridToWorld3D(gc, entrance.GridPosNum);
                    var eScreen = cam.WorldToScreen(eWorld);
                    g.DrawCircleInWorld(eWorld, 35f, SharpDX.Color.LimeGreen, 2.5f);
                    g.DrawText($"ENTRANCE: {entrance.RenderName ?? entrance.Path.Split('/').LastOrDefault()}", eScreen + new Vector2(-40, -20), SharpDX.Color.LimeGreen);
                }
            }

            // Alert bar (in contract)
            if (_state.IsAlertPanelVisible || _state.AlertPercent > 0)
            {
                var barWidth = 200f;
                var barHeight = 14f;
                var alertPct = _state.AlertPercent / 100f;
                var alertColor = _state.IsLockdown ? new SharpDX.Color(255, 0, 0, 200) : new SharpDX.Color(200, 200, 0, 200);
                g.DrawBox(new SharpDX.RectangleF(hudX, hudY, barWidth, barHeight), new SharpDX.Color(40, 40, 40, 200));
                g.DrawBox(new SharpDX.RectangleF(hudX, hudY, barWidth * Math.Min(alertPct, 1f), barHeight), alertColor);
                g.DrawText(_state.IsLockdown ? "LOCKDOWN" : $"Alert: {_state.AlertPercent:F0}%", new Vector2(hudX + barWidth + 8, hudY - 1), SharpDX.Color.White);
                hudY += barHeight + 4;
            }

            // In-contract route and markers overlay
            if (ctx.Navigation.IsNavigating)
            {
                var path = ctx.Navigation.CurrentNavPath;
                for (int i = ctx.Navigation.CurrentWaypointIndex; i < path.Count - 1; i++)
                {
                    var from = Pathfinding.GridToScreen(gc, path[i].Position);
                    var to = Pathfinding.GridToScreen(gc, path[i + 1].Position);
                    g.DrawLine(from, to, 2f, SharpDX.Color.Orange);
                }
            }

            for (int i = 0; i < _state.PlannedRoute.Count; i++)
            {
                var rt = _state.PlannedRoute[i];
                var rtWorld = Pathfinding.GridToWorld3D(gc, rt.GridPos);
                var rtScreen = cam.WorldToScreen(rtWorld);
                var color = rt.Reached ? SharpDX.Color.DarkGray : SharpDX.Color.Gold;
                g.DrawCircleInWorld(rtWorld, 25f, color, 1.5f);
                g.DrawText($"{i + 1}:{rt.Label}", rtScreen + new Vector2(-20, -25), color);
            }

            RenderMinimapRoute(gc);
        }

        private void RenderMinimapRoute(GameController gc)
        {
            if (_state.PlannedRoute.Count == 0) return;
            try
            {
                var largeMap = gc.IngameState.IngameUi.Map.LargeMap.AsObject<ExileCore.PoEMemory.Elements.SubMap>();
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
                ImGuiNET.ImGui.Begin("heist_route_overlay", ImGuiNET.ImGuiWindowFlags.NoDecoration | ImGuiNET.ImGuiWindowFlags.NoInputs | ImGuiNET.ImGuiWindowFlags.NoMove | ImGuiNET.ImGuiWindowFlags.NoBackground);

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
                    uint fill = rt.Reached ? ImGuiNET.ImGui.ColorConvertFloat4ToU32(new Vector4(0f, 0.7f, 0f, 0.5f)) : ImGuiNET.ImGui.ColorConvertFloat4ToU32(new Vector4(0.9f, 0.7f, 0f, 0.5f));
                    var center = ToMap(rt.GridPos);
                    dl.AddRectFilled(center - new Vector2(boxHalf, boxHalf), center + new Vector2(boxHalf, boxHalf), fill, 3f);
                    dl.AddText(center - new Vector2(4, 6), white, (i + 1).ToString());
                }
                ImGuiNET.ImGui.End();
            }
            catch { }
        }

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

        private void ScanCurioDisplays(GameController gc)
        {
            _curioDisplays.Clear();
            foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
            {
                if (entity?.Path == null || !entity.Path.Contains("HeistChestPrimaryTarget")) continue;
                var info = new CurioDisplayInfo { EntityId = entity.Id, GridPos = entity.GridPosNum };
                var chest = entity.GetComponent<Chest>();
                info.IsOpened = chest?.IsOpened == true || !entity.IsTargetable;
                _curioDisplays.Add(info);
            }
        }

        private const float GridToWorldMultiplier = 250f / 23f;
        private const double CameraAngle = 38.7 * Math.PI / 180;
        private static readonly float CamCos = (float)Math.Cos(CameraAngle);
        private static readonly float CamSin = (float)Math.Sin(CameraAngle);

        private static Vector2 GridDeltaToMap(Vector2 delta, float deltaZ, float mapScale)
        {
            deltaZ /= GridToWorldMultiplier;
            return mapScale * new Vector2((delta.X - delta.Y) * CamCos, (deltaZ - (delta.X + delta.Y)) * CamSin);
        }

        private static float GetTerrainHeight(float[][]? heightData, Vector2 pos)
        {
            if (heightData == null) return 0f;
            int x = (int)pos.X, y = (int)pos.Y;
            if (y >= 0 && y < heightData.Length && x >= 0 && x < heightData[y].Length)
                return heightData[y][x];
            return 0f;
        }

        private static Element? SafeGetChild(Element? parent, int index)
        {
            if (parent == null || index < 0 || index >= (int)parent.ChildCount) return null;
            return parent.GetChildAtIndex(index);
        }

        private void LogNearbyEnemiesDebug(GameController gc)
        {
            try
            {
                var playerPos = gc.Player.GridPosNum;
                var enemies = gc.EntityListWrapper.OnlyValidEntities
                    .Where(e => e.Type == EntityType.Monster && e.IsHostile && e.IsAlive && e.DistancePlayer < 100)
                    .OrderBy(e => e.DistancePlayer)
                    .ToList();

                if (enemies.Count == 0) return;

                var lines = new List<string> { $"--- ENEMY THREAT BREAKDOWN ({enemies.Count} nearby) ---" };
                foreach (var e in enemies)
                {
                    var life = e.GetComponent<Life>();
                    var hp = life != null ? $"{life.CurHP}/{life.MaxHP}" : "no-life";
                    var name = !string.IsNullOrEmpty(e.RenderName) ? e.RenderName : (e.Path?.Split('/').LastOrDefault() ?? "?");
                    var targetable = e.IsTargetable;
                    lines.Add($"  - [{e.Rarity}] {name} (Id={e.Id}, Dist={e.DistancePlayer:F0}, HP={hp}, Targetable={targetable}, Path={e.Path})");
                }
                HeistLog(string.Join("\n", lines));
            }
            catch { }
        }

        private Entity? FindHeistEntranceTransition(GameController gc)
        {
            var playerPos = gc.Player.GridPosNum;
            Entity? best = null;
            float bestDist = 120f; // Staging entrance is always within 120 units

            foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
            {
                if (!entity.IsTargetable || entity.Path == null) continue;

                var renderName = entity.RenderName ?? "";
                var path = entity.Path;

                // Ignore return portals, Harbour transitions, and Escape Route transitions
                if (entity.Type == EntityType.TownPortal ||
                    renderName.Equals("The Rogue Harbour", StringComparison.OrdinalIgnoreCase) ||
                    renderName.Contains("Escape Route", StringComparison.OrdinalIgnoreCase) ||
                    path.Contains("MissionExitPortal", StringComparison.OrdinalIgnoreCase) ||
                    path.Contains("heist_exit_portal", StringComparison.OrdinalIgnoreCase) ||
                    path.Contains("AdiyahPortal", StringComparison.OrdinalIgnoreCase) ||
                    path.Contains("HeistEscapeRoute", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Match staging entrance transitions (bulkhead doors, pipes, grates)
                bool isTransition = entity.Type == EntityType.AreaTransition
                    || path.Contains("SewersGrate", StringComparison.OrdinalIgnoreCase)
                    || path.Contains("AreaTransition", StringComparison.OrdinalIgnoreCase)
                    || path.Contains("aqueduct_sewer_entrance", StringComparison.OrdinalIgnoreCase)
                    || path.Contains("slum_sewer_entrance", StringComparison.OrdinalIgnoreCase)
                    || path.Contains("garden_wall_entrance", StringComparison.OrdinalIgnoreCase)
                    || path.Contains("slaveden_IronCageOpened", StringComparison.OrdinalIgnoreCase)
                    || path.Contains("templar_to_innocents", StringComparison.OrdinalIgnoreCase)
                    || path.Contains("templar_oriath_transition", StringComparison.OrdinalIgnoreCase)
                    || path.Contains("HeistEntranceTransition", StringComparison.OrdinalIgnoreCase);

                if (isTransition)
                {
                    var dist = Vector2.Distance(entity.GridPosNum, playerPos);
                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        best = entity;
                    }
                }
            }

            return best;
        }
    }

    public enum HeistPhase
    {
        Idle,

        // Harbour & Adiyah Automation
        InHarbour,
        StashItems,
        OpenAdiyah,
        InsertContract,
        SelectRogue,
        SignContract,
        WaitForPortal,
        EnterPortal,

        // In-Contract
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