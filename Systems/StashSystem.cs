using ExileCore;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using Input = ExileCore.Input;
using SharpDX;
using System.Numerics;
using System.Windows.Forms;
using RectangleF = SharpDX.RectangleF;
using Vector2 = System.Numerics.Vector2;

namespace AutoExile.Systems
{
    /// <summary>
    /// Handles stashing inventory items in hideout.
    /// Flow: navigate to stash → click stash → Ctrl+click each item to transfer.
    /// Uses InventorySlotItems (same approach as AutoPOE) for reliable item detection and clicking.
    /// </summary>
    public class StashSystem
    {
        private StashPhase _phase = StashPhase.Idle;
        private DateTime _phaseStartTime;
        private DateTime _lastActionTime;
        private int _itemsStored;
        private int _attempts;
        private int _prevItemCount; // track item count to detect failed transfers
        private const float BasePhaseTimeoutSeconds = 30f;
        /// <summary>Extra seconds added to all server-response timeouts. Synced from settings.</summary>
        public float ExtraLatencySec { get; set; }
        private const int MaxAttempts = 50;
        /// <summary>
        /// Grid distance to consider "close enough" to interact with stash.
        /// Synced from InteractionSystem.InteractRadius (which comes from LootRadius setting).
        /// </summary>
        public float InteractRadius { get; set; } = 20f;
        private const int MaxConsecutiveFailures = 3;
        private int _consecutiveFailures;

        // Configurable cooldown — set from settings before ticking
        public int ActionCooldownMs { get; set; } = 450;

        /// <summary>Whether to apply incubators from stash to equipment after storing items.</summary>
        public bool ApplyIncubators { get; set; }

        /// <summary>
        /// Optional filter: return true to stash the item, false to keep it in inventory.
        /// When null, all items are stashed.
        /// </summary>
        public Func<ServerInventory.InventSlotItem, bool>? ItemFilter { get; set; }

        /// <summary>Tab name to switch to before storing items. Null = use current tab.</summary>
        public string? StoreTabName { get; set; }

        /// <summary>Tab name to switch to for withdrawing fragments. Null = skip withdraw.</summary>
        public string? WithdrawTabName { get; set; }

        /// <summary>Entity path substring of fragment to withdraw from stash.</summary>
        public string? WithdrawFragmentPath { get; set; }

        /// <summary>How many fragments to withdraw (ctrl+clicks). Each click withdraws one stack unit.</summary>
        public int WithdrawCount { get; set; }

        /// <summary>
        /// Multi-item withdrawal queue. Processed sequentially within the same stash
        /// session: switch to <see cref="WithdrawTabName"/> once, then for each entry
        /// find the matching item and ctrl+click <c>Count</c> times.
        ///
        /// When non-empty, this REPLACES the single-item <see cref="WithdrawFragmentPath"/>
        /// + <see cref="WithdrawCount"/> path. The single-item fields stay supported for
        /// existing Boss/Simulacrum callers; multi-item is for Wave Farming where one
        /// stash trip pulls scarabs + portal scrolls + maybe maps in one go.
        /// </summary>
        public List<(string PathSubstring, int Count)> WithdrawList { get; set; } = new();

        // Tab switching state
        private string? _pendingTabSwitch;
        private StashPhase _afterTabSwitch;
        private int _withdrawsRemaining;
        private int _withdrawListIndex; // which entry of WithdrawList we're processing

        // Incubator state
        private bool _cursorHasIncubator;
        private int _incubatorsApplied;

        public StashPhase Phase => _phase;
        public string Status { get; private set; } = "";
        public bool IsBusy => _phase != StashPhase.Idle;
        public int ItemsStored => _itemsStored;

        /// <summary>
        /// Begin a stash interaction. All configurable fields (store tab, withdraw tab,
        /// fragment path, count, filter) reset to the supplied arguments — they do NOT
        /// inherit from a previous Start(). This prevents callers (e.g. between-wave
        /// stashing) from accidentally re-running fragment withdrawal that was set up
        /// for a prior hideout flow.
        /// </summary>
        public bool Start(
            string? storeTabName = null,
            string? withdrawTabName = null,
            string? withdrawFragmentPath = null,
            int withdrawCount = 0,
            Func<ServerInventory.InventSlotItem, bool>? itemFilter = null,
            IReadOnlyList<(string PathSubstring, int Count)>? withdrawList = null)
        {
            if (_phase != StashPhase.Idle)
                return false;

            // Reset config — every Start() is a clean slate.
            StoreTabName         = storeTabName;
            WithdrawTabName      = withdrawTabName;
            WithdrawFragmentPath = withdrawFragmentPath;
            WithdrawCount        = withdrawCount;
            ItemFilter           = itemFilter;

            // Multi-item path: if a list is supplied, that wins over the single-item
            // fields. The single-item API is kept for Boss/Sim where fragmentPath/count
            // is the entire withdrawal — no need to construct a list for one item.
            WithdrawList.Clear();
            if (withdrawList != null && withdrawList.Count > 0)
                foreach (var w in withdrawList)
                    if (w.Count > 0 && !string.IsNullOrWhiteSpace(w.PathSubstring))
                        WithdrawList.Add((w.PathSubstring, w.Count));
            _withdrawListIndex = 0;

            _phase = StashPhase.NavigateToStash;
            _phaseStartTime = DateTime.Now;
            _lastActionTime = DateTime.MinValue;
            _itemsStored = 0;
            _attempts = 0;
            _prevItemCount = -1;
            _consecutiveFailures = 0;
            _cursorHasIncubator = false;
            _incubatorBatchRunning = false;
            _incubatorsApplied = 0;
            _pendingTabSwitch = null;
            _afterTabSwitch = StashPhase.Idle;

            _withdrawsRemaining = 0;
            Status = "Starting stash interaction";
            return true;
        }

        public void Cancel(GameController gc, NavigationSystem? nav = null)
        {
            nav?.Stop(gc);
            _phase = StashPhase.Idle;
            ItemFilter = null;
            StoreTabName = null;
            WithdrawTabName = null;
            WithdrawFragmentPath = null;
            WithdrawCount = 0;
            WithdrawList.Clear();
            _withdrawListIndex = 0;
            _pendingTabSwitch = null;
            _incubatorBatchRunning = false;
            Status = "Cancelled";
        }

        /// <summary>
        /// Navigate to stash and open it — but don't deposit or withdraw anything.
        /// Returns Succeeded once the stash panel is open, Failed if unable to open.
        /// Used by StashIndexer to get the stash open before scanning.
        /// </summary>
        public StashResult TickOpenOnly(GameController gc, NavigationSystem nav)
        {
            if (gc.IngameState.IngameUi.StashElement?.IsVisible == true)
            {
                _phase = StashPhase.Idle;
                return StashResult.Succeeded;
            }

            if (_phase == StashPhase.Idle)
            {
                _phase = StashPhase.NavigateToStash;
                _phaseStartTime = DateTime.Now;
                _lastActionTime = DateTime.MinValue;
                // Clear all store/withdraw settings so nothing is deposited
                ItemFilter = _ => false;
                StoreTabName = null;
                WithdrawTabName = null;
                WithdrawFragmentPath = null;
                WithdrawCount = 0;
                WithdrawList.Clear();
                _withdrawListIndex = 0;
            }

            if ((DateTime.Now - _phaseStartTime).TotalSeconds > BasePhaseTimeoutSeconds + ExtraLatencySec)
            {
                _phase = StashPhase.Idle;
                return StashResult.Failed;
            }

            if ((DateTime.Now - _lastActionTime).TotalMilliseconds < ActionCooldownMs)
                return StashResult.InProgress;

            return _phase switch
            {
                StashPhase.NavigateToStash => TickNavigate(gc, nav),
                StashPhase.OpenStash => TickOpenStash(gc),
                _ => StashResult.InProgress,
            };
        }

        public StashResult Tick(GameController gc, NavigationSystem nav)
        {
            if (_phase == StashPhase.Idle)
                return StashResult.None;

            if ((DateTime.Now - _phaseStartTime).TotalSeconds > BasePhaseTimeoutSeconds + ExtraLatencySec)
            {
                Status = $"Timed out in phase: {_phase}";
                _phase = StashPhase.Idle;
                return StashResult.Failed;
            }

            if ((DateTime.Now - _lastActionTime).TotalMilliseconds < ActionCooldownMs)
                return StashResult.InProgress;

            return _phase switch
            {
                StashPhase.NavigateToStash => TickNavigate(gc, nav),
                StashPhase.OpenStash => TickOpenStash(gc),
                StashPhase.SwitchToWithdrawTab => TickSwitchTab(gc),
                StashPhase.WithdrawItems => TickWithdrawItems(gc),
                StashPhase.SwitchToStoreTab => TickSwitchTab(gc),
                StashPhase.StoreItems => TickStoreItems(gc),
                StashPhase.ApplyIncubators => TickApplyIncubators(gc),
                StashPhase.CloseStash => TickCloseStash(gc),
                _ => StashResult.InProgress
            };
        }

        private StashResult TickNavigate(GameController gc, NavigationSystem nav)
        {
            // Check if stash is already open
            if (gc.IngameState.IngameUi.StashElement?.IsVisible == true)
            {
                EnterFirstStashPhase(gc);
                Status = "Stash already open";
                return StashResult.InProgress;
            }

            var stash = FindStashEntity(gc);
            if (stash == null)
            {
                Status = "Stash not found";
                _phase = StashPhase.Idle;
                return StashResult.Failed;
            }

            // Distance check in grid units
            var playerGrid = gc.Player.GridPosNum;
            var stashGrid = stash.GridPosNum;
            var dist = Vector2.Distance(
                new Vector2(playerGrid.X, playerGrid.Y),
                new Vector2(stashGrid.X, stashGrid.Y));

            if (dist <= InteractRadius)
            {
                nav.Stop(gc);
                _phase = StashPhase.OpenStash;
                _phaseStartTime = DateTime.Now;
                Status = "Near stash — opening";
                return StashResult.InProgress;
            }

            if (!nav.IsNavigating)
            {
                var gridTarget = new Vector2(stashGrid.X, stashGrid.Y);
                if (!nav.NavigateTo(gc, gridTarget))
                {
                    // Hideout decorations create fake walls — direct walk toward stash
                    if (gc.Area.CurrentArea.IsHideout && BotInput.CanAct)
                    {
                        var screenPos = gc.IngameState.Camera.WorldToScreen(stash.BoundsCenterPosNum);
                        var windowRect = gc.Window.GetWindowRectangle();
                        var absPos = new Vector2(windowRect.X + screenPos.X, windowRect.Y + screenPos.Y);
                        if (BotInput.IsMovementActive && !BotInput.IsMovementSuspended)
                            BotInput.UpdateMovementCursor(absPos);
                        else
                            BotInput.StartMovement(absPos, nav.MoveKey);
                        Status = $"Direct walk to stash — no A* path (dist: {dist:F0})";
                        return StashResult.InProgress;
                    }

                    Status = "No path to stash";
                    _phase = StashPhase.Idle;
                    return StashResult.Failed;
                }
            }

            Status = $"Navigating to stash (dist: {dist:F0})";
            return StashResult.InProgress;
        }

        /// <summary>Decide what to do first once stash is open.</summary>
        private void EnterFirstStashPhase(GameController gc)
        {
            // Multi-item path takes precedence — wave farming withdraws scarabs +
            // portal scrolls + maybe maps in one stash trip.
            if (!string.IsNullOrEmpty(WithdrawTabName) && WithdrawList.Count > 0)
            {
                _pendingTabSwitch = WithdrawTabName;
                _afterTabSwitch = StashPhase.WithdrawItems;
                _phase = StashPhase.SwitchToWithdrawTab;
                _phaseStartTime = DateTime.Now;
                _withdrawListIndex = 0;
                _withdrawsRemaining = WithdrawList[0].Count;
                Status = $"Switching to {WithdrawTabName} tab for {WithdrawList.Count} items";
                return;
            }

            // Single-item path — Boss/Sim fragment withdrawal.
            if (!string.IsNullOrEmpty(WithdrawTabName) && !string.IsNullOrEmpty(WithdrawFragmentPath) && WithdrawCount > 0)
            {
                _pendingTabSwitch = WithdrawTabName;
                _afterTabSwitch = StashPhase.WithdrawItems;

                _phase = StashPhase.SwitchToWithdrawTab;
                _phaseStartTime = DateTime.Now;
                _withdrawsRemaining = WithdrawCount;
                Status = $"Switching to {WithdrawTabName} tab for fragments";
                return;
            }

            EnterStorePhase(gc);
        }

        private void EnterStorePhase(GameController gc)
        {
            // Switch to store tab if configured and not already on it
            if (!string.IsNullOrEmpty(StoreTabName))
            {
                var stash = gc.IngameState?.IngameUi?.StashElement;
                var names = stash?.AllStashNames;
                var currentIdx = stash?.IndexVisibleStash ?? -1;
                if (names != null && currentIdx >= 0 && currentIdx < names.Count
                    && !names[currentIdx].Equals(StoreTabName, StringComparison.OrdinalIgnoreCase))
                {
                    _pendingTabSwitch = StoreTabName;
                    _afterTabSwitch = StashPhase.StoreItems;
        
                    _phase = StashPhase.SwitchToStoreTab;
                    _phaseStartTime = DateTime.Now;
                    Status = $"Switching to {StoreTabName} tab for storing";
                    return;
                }
            }

            _phase = StashPhase.StoreItems;
            _phaseStartTime = DateTime.Now;
            Status = "Storing items";
        }

        private StashResult TickOpenStash(GameController gc)
        {
            if (gc.IngameState.IngameUi.StashElement?.IsVisible == true)
            {
                EnterFirstStashPhase(gc);
                return StashResult.InProgress;
            }

            var stash = FindStashEntity(gc);
            if (stash == null)
            {
                Status = "Stash entity gone";
                _phase = StashPhase.Idle;
                return StashResult.Failed;
            }

            if (!BotInput.ClickEntity(gc, stash))
            {
                Status = "Stash off screen or gate blocked";
                return StashResult.InProgress;
            }
            _lastActionTime = DateTime.Now;
            Status = "Clicking stash";
            return StashResult.InProgress;
        }

        private const float TabSwitchSettleMs = 200f;

        private StashResult TickSwitchTab(GameController gc)
        {
            var stashEl = gc.IngameState.IngameUi.StashElement;
            if (stashEl?.IsVisible != true)
            {
                Status = "Stash closed during tab switch";
                _phase = StashPhase.Idle;
                return StashResult.Failed;
            }

            if (string.IsNullOrEmpty(_pendingTabSwitch))
            {
                _phase = _afterTabSwitch;
                _phaseStartTime = DateTime.Now;
                return StashResult.InProgress;
            }

            // Find target tab index by name
            var names = stashEl.AllStashNames;
            int targetIdx = -1;
            if (names != null)
            {
                for (int i = 0; i < names.Count; i++)
                {
                    if (names[i].Equals(_pendingTabSwitch, StringComparison.OrdinalIgnoreCase))
                    {
                        targetIdx = i;
                        break;
                    }
                }
            }

            if (targetIdx < 0)
            {
                Status = $"Tab '{_pendingTabSwitch}' not found";
                _phase = StashPhase.Idle;
                return StashResult.Failed;
            }

            var currentIdx = stashEl.IndexVisibleStash;

            // Already on the right tab?
            if (currentIdx == targetIdx)
            {
                _pendingTabSwitch = null;
                _phase = _afterTabSwitch;
                _phaseStartTime = DateTime.Now;
                Status = $"On tab '{names[targetIdx]}'";
                return StashResult.InProgress;
            }

            // Tab switch timeout
            if ((DateTime.Now - _phaseStartTime).TotalSeconds > 15)
            {
                Status = $"Tab switch timeout — wanted '{_pendingTabSwitch}' (on {currentIdx})";
                _phase = StashPhase.Idle;
                return StashResult.Failed;
            }

            // Settle delay after each arrow press
            if ((DateTime.Now - _lastActionTime).TotalMilliseconds < TabSwitchSettleMs)
                return StashResult.InProgress;

            // Press left or right arrow to move toward target tab
            if (!BotInput.CanAct) return StashResult.InProgress;

            var key = currentIdx < targetIdx ? Keys.Right : Keys.Left;
            BotInput.PressKey(key);
            _lastActionTime = DateTime.Now;
            var delta = targetIdx - currentIdx;
            Status = $"Switching tab → '{_pendingTabSwitch}' ({Math.Abs(delta)} away)";
            return StashResult.InProgress;
        }

        private StashResult TickWithdrawItems(GameController gc)
        {
            var stashEl = gc.IngameState.IngameUi.StashElement;
            if (stashEl?.IsVisible != true)
            {
                Status = "Stash closed during withdraw";
                _phase = StashPhase.Idle;
                return StashResult.Failed;
            }

            // Wait for any in-flight batch to complete before evaluating again.
            // The batch holds Ctrl down across all clicks; we don't want to start a
            // second batch while one is mid-flight or it'll race with Ctrl release.
            if (BotInput.IsBatchRunning)
                return StashResult.InProgress;

            // Resolve the current target — either WithdrawList[index] (multi-item)
            // or the legacy single-item path. The two are mutually exclusive at Start().
            string? currentPath;
            int wantTotal;
            if (WithdrawList.Count > 0)
            {
                if (_withdrawListIndex >= WithdrawList.Count)
                {
                    EnterStorePhase(gc);
                    return StashResult.InProgress;
                }
                currentPath = WithdrawList[_withdrawListIndex].PathSubstring;
                wantTotal   = WithdrawList[_withdrawListIndex].Count;
            }
            else
            {
                currentPath = WithdrawFragmentPath;
                wantTotal   = WithdrawCount;
            }

            if (wantTotal <= 0 || string.IsNullOrEmpty(currentPath))
            {
                AdvanceOrFinishWithdraw(gc);
                return StashResult.InProgress;
            }

            // Inventory-aware stopping — bail when we have enough. Handles BOTH
            // stackable items (one ctrl+click transfers the whole stack) and
            // non-stackable items (one ctrl+click per slot).
            int haveInInv = CountInventoryItems(gc, currentPath);
            if (haveInInv >= wantTotal)
            {
                AdvanceOrFinishWithdraw(gc);
                return StashResult.InProgress;
            }

            // Settle window after each batch — gives the UI time to update so
            // CountInventoryItems reflects the just-transferred items before we
            // launch another batch.
            if ((DateTime.Now - _lastActionTime).TotalMilliseconds < ActionCooldownMs)
                return StashResult.InProgress;

            // Find ALL matching items in the visible stash tab. Batch-clicking ONE
            // position N times only works for stacks; for non-stackable items
            // (maps) each one occupies a different slot and we need to click each.
            var items = stashEl.VisibleStash?.VisibleInventoryItems;
            if (items == null)
            {
                Status = "No items visible in withdraw tab";
                AdvanceOrFinishWithdraw(gc);
                return StashResult.InProgress;
            }

            var windowRect = gc.Window.GetWindowRectangle();
            var positions = new List<Vector2>();
            foreach (var item in items)
            {
                var entity = item.Entity;
                if (entity?.Path?.Contains(currentPath, StringComparison.OrdinalIgnoreCase) == true)
                {
                    var rect = item.GetClientRect();
                    positions.Add(new Vector2(
                        windowRect.X + rect.Center.X,
                        windowRect.Y + rect.Center.Y));
                }
            }

            if (positions.Count == 0)
            {
                Status = $"'{currentPath}' not found in tab ({haveInInv}/{wantTotal} have) — skipping";
                AdvanceOrFinishWithdraw(gc);
                return StashResult.InProgress;
            }

            Status = $"Withdrawing '{currentPath}' ({haveInInv}/{wantTotal}, {positions.Count} stash slots)";
            // CtrlClickBatch holds Ctrl down across every click in one async pass,
            // then releases. Items get transferred (stacks fully, single items one-per-click).
            // After the batch completes, IsBatchRunning flips back to false; the
            // settle window above lets inventory state catch up before we re-evaluate.
            BotInput.CtrlClickBatch(positions);
            _lastActionTime = DateTime.Now;
            return StashResult.InProgress;
        }

        /// <summary>
        /// After a withdrawal batch completes (or the item wasn't found), move to
        /// the next entry in <see cref="WithdrawList"/>, or transition to storing
        /// items if we've exhausted the list.
        /// </summary>
        private void AdvanceOrFinishWithdraw(GameController gc)
        {
            if (WithdrawList.Count > 0)
            {
                _withdrawListIndex++;
                if (_withdrawListIndex < WithdrawList.Count)
                {
                    _withdrawsRemaining = WithdrawList[_withdrawListIndex].Count;
                    Status = $"Next withdrawal: {WithdrawList[_withdrawListIndex].PathSubstring}";
                    return; // stay in WithdrawItems phase, next tick processes the next item
                }
            }
            EnterStorePhase(gc);
        }

        private StashResult TickStoreItems(GameController gc)
        {
            // Check stash is still open
            if (gc.IngameState.IngameUi.StashElement?.IsVisible != true)
            {
                Status = "Stash closed unexpectedly";
                _phase = StashPhase.Idle;
                return _itemsStored > 0 ? StashResult.Succeeded : StashResult.Failed;
            }

            // If a batch is running, wait for it to complete
            if (BotInput.IsBatchRunning)
                return StashResult.InProgress;

            // If we already ran a batch, we're done — close stash
            if (_itemsStored > 0)
            {
                Status = $"Done — stored {_itemsStored} items — closing stash";
                return EnterCloseStash();
            }

            // Get inventory slot items — same API as AutoPOE
            var slotItems = GetInventorySlotItems(gc);
            if (slotItems == null || slotItems.Count == 0)
            {
                Status = $"Done — nothing to store — closing stash";
                return EnterCloseStash();
            }

            // Build list of all stashable item positions
            var windowRect = gc.Window.GetWindowRectangle();
            var positions = new List<Vector2>();
            foreach (var si in slotItems)
            {
                if (ItemFilter != null && !ItemFilter(si))
                    continue; // keep this item
                var rect = si.GetClientRect();
                var center = new Vector2(rect.Center.X, rect.Center.Y);
                positions.Add(new Vector2(windowRect.X + center.X, windowRect.Y + center.Y));
            }

            if (positions.Count == 0)
            {
                Status = $"Done — stored {_itemsStored} items (kept {slotItems.Count} filtered) — closing stash";
                return EnterCloseStash();
            }

            // Fire the batch — single async sequence: hold Ctrl → click all → release Ctrl
            Status = $"Storing {positions.Count} items...";
            BotInput.CtrlClickBatch(positions, count =>
            {
                _itemsStored = count;
            });
            return StashResult.InProgress;
        }

        /// <summary>
        /// Get player inventory slot items via server data — same approach as AutoPOE.
        /// Returns items with clickable GetClientRect() positions.
        /// </summary>
        public static IList<ServerInventory.InventSlotItem>? GetInventorySlotItems(GameController gc)
        {
            try
            {
                var playerInv = gc.IngameState.ServerData?.PlayerInventories;
                if (playerInv == null || playerInv.Count == 0) return null;
                var inv = playerInv[0].Inventory;
                return inv?.InventorySlotItems;
            }
            catch { return null; }
        }

        /// <summary>
        /// Check if player has any inventory items (static helper for use by modes).
        /// </summary>
        public static bool HasInventoryItems(GameController gc)
        {
            var items = GetInventorySlotItems(gc);
            return items != null && items.Count > 0;
        }

        /// <summary>
        /// Count inventory items matching a path substring.
        /// </summary>
        public static int CountInventoryItems(GameController gc, string? pathSubstring)
        {
            var items = GetInventorySlotItems(gc);
            if (items == null || string.IsNullOrEmpty(pathSubstring)) return 0;
            int count = 0;
            foreach (var item in items)
            {
                if (item.Item?.Path?.Contains(pathSubstring, StringComparison.OrdinalIgnoreCase) == true)
                    count++;
            }
            return count;
        }

        /// <summary>
        /// Count inventory items that do NOT match a path substring (i.e., non-fragment loot items).
        /// </summary>
        public static int CountNonMatchingItems(GameController gc, string? pathSubstring)
        {
            var items = GetInventorySlotItems(gc);
            if (items == null) return 0;
            if (string.IsNullOrEmpty(pathSubstring)) return items.Count;
            int count = 0;
            foreach (var item in items)
            {
                if (item.Item?.Path?.Contains(pathSubstring, StringComparison.OrdinalIgnoreCase) != true)
                    count++;
            }
            return count;
        }

        /// <summary>
        /// Check if player has any stashable inventory items (respecting a filter).
        /// Returns true only if at least one item passes the filter (should be stashed).
        /// </summary>
        public static bool HasStashableItems(GameController gc, Func<ServerInventory.InventSlotItem, bool>? filter)
        {
            var items = GetInventorySlotItems(gc);
            if (items == null || items.Count == 0) return false;
            if (filter == null) return true;
            foreach (var item in items)
            {
                if (filter(item)) return true;
            }
            return false;
        }

        private StashResult EnterCloseStash()
        {
            // Try incubator phase first if enabled and stash is still open
            if (ApplyIncubators && _incubatorsApplied == 0)
            {
                _phase = StashPhase.ApplyIncubators;
                _phaseStartTime = DateTime.Now;
                _lastActionTime = DateTime.MinValue;
                _cursorHasIncubator = false;
                return StashResult.InProgress;
            }

            _phase = StashPhase.CloseStash;
            _phaseStartTime = DateTime.Now;
            _lastActionTime = DateTime.MinValue;
            return StashResult.InProgress;
        }

        private StashResult TickCloseStash(GameController gc)
        {
            // Already closed?
            if (gc.IngameState.IngameUi.StashElement?.IsVisible != true)
            {
                Status = $"Stash closed — stored {_itemsStored} items";
                _phase = StashPhase.Idle;
                return StashResult.Succeeded;
            }

            // Press Escape to close
            BotInput.PressKey(Keys.Escape);
            _lastActionTime = DateTime.Now;
            Status = "Closing stash";
            return StashResult.InProgress;
        }

        // =================================================================
        // Incubator Application
        // =================================================================

        /// <summary>Whether the incubator batch is currently running.</summary>
        private bool _incubatorBatchRunning;

        private StashResult TickApplyIncubators(GameController gc)
        {
            // Stash must stay open for incubator right-click
            if (gc.IngameState.IngameUi.StashElement?.IsVisible != true)
            {
                Status = "Stash closed — skipping incubators";
                _phase = StashPhase.CloseStash;
                _phaseStartTime = DateTime.Now;
                return StashResult.InProgress;
            }

            // Wait for batch to finish
            if (_incubatorBatchRunning)
                return StashResult.InProgress;

            // If batch already ran, move to close
            if (_incubatorsApplied > 0)
            {
                Status = $"Applied {_incubatorsApplied} incubators — closing stash";
                _phase = StashPhase.CloseStash;
                _phaseStartTime = DateTime.Now;
                return StashResult.InProgress;
            }

            // Check we have both incubators and empty equipment slots
            if (FindEquipmentSlotToApply(gc) == null)
            {
                Status = "All equipment has incubators — skipping";
                _phase = StashPhase.CloseStash;
                _phaseStartTime = DateTime.Now;
                return StashResult.InProgress;
            }

            if (FindIncubatorInStash(gc) == null)
            {
                Status = "No incubators in stash";
                _phase = StashPhase.CloseStash;
                _phaseStartTime = DateTime.Now;
                return StashResult.InProgress;
            }

            // Launch the async batch
            _incubatorBatchRunning = true;
            Status = "Applying incubators...";
            _ = DoIncubatorBatch(gc);
            return StashResult.InProgress;
        }

        private async Task DoIncubatorBatch(GameController gc)
        {
            const int maxRetries = 3;
            const int maxIncubators = 10; // safety limit
            int applied = 0;

            try
            {
                for (int i = 0; i < maxIncubators; i++)
                {
                    // Find an incubator in stash
                    var incubatorPos = FindIncubatorInStash(gc);
                    if (incubatorPos == null) break;

                    // Find an equipment slot without incubator
                    var slotPos = FindEquipmentSlotToApply(gc);
                    if (slotPos == null) break;

                    // Right-click the incubator to pick it up
                    await BotInput.MoveCursorToPublic(incubatorPos.Value);
                    await Task.Delay(BotInput.RandSettle());
                    Input.RightDown();
                    BotInput.MarkInputEventPublic("RightDown", "incubator-pickup");
                    await Task.Delay(BotInput.RandHold());
                    Input.RightUp();
                    BotInput.MarkInputEventPublic("RightUp", "incubator-pickup");

                    // Wait and verify cursor has item
                    await Task.Delay(150);
                    bool picked = false;
                    for (int retry = 0; retry < maxRetries; retry++)
                    {
                        try { picked = gc.IngameState.IngameUi.Cursor?.ChildCount > 0; } catch { }
                        if (picked) break;
                        await Task.Delay(100);
                    }
                    if (!picked) break; // failed to pick up

                    // Click the equipment slot to apply
                    await BotInput.MoveCursorToPublic(slotPos.Value);
                    await Task.Delay(BotInput.RandSettle());
                    Input.LeftDown();
                    BotInput.MarkInputEventPublic("LeftDown", "incubator-apply");
                    await Task.Delay(BotInput.RandHold());
                    Input.LeftUp();
                    BotInput.MarkInputEventPublic("LeftUp", "incubator-apply");

                    // Wait and verify cursor is empty (incubator was applied)
                    await Task.Delay(150);
                    bool cleared = false;
                    for (int retry = 0; retry < maxRetries; retry++)
                    {
                        try { cleared = gc.IngameState.IngameUi.Cursor?.ChildCount == 0; } catch { }
                        if (cleared) break;
                        await Task.Delay(100);
                    }

                    if (cleared)
                    {
                        applied++;
                        Status = $"Applied incubator {applied}";
                    }
                    else
                    {
                        // Cursor still has item — press Escape to drop it
                        Input.KeyDown(Keys.Escape);
                        BotInput.MarkInputEventPublic("KeyDown", "Escape incubator-cancel");
                        await Task.Delay(BotInput.RandHold());
                        Input.KeyUp(Keys.Escape);
                        BotInput.MarkInputEventPublic("KeyUp", "Escape incubator-cancel");
                        await Task.Delay(100);
                        break;
                    }

                    // Short delay between incubators
                    await Task.Delay(BotInput.ActionCooldownMs);
                }
            }
            catch { }
            finally
            {
                _incubatorsApplied = applied;
                _incubatorBatchRunning = false;
                BotInput.NextActionAt = DateTime.Now.AddMilliseconds(BotInput.ActionCooldownMs);
            }
        }

        /// <summary>
        /// Find an incubator item in the currently visible stash tab.
        /// Returns absolute screen position for clicking, or null if none found.
        /// </summary>
        private Vector2? FindIncubatorInStash(GameController gc)
        {
            try
            {
                var items = gc.IngameState.IngameUi.StashElement.VisibleStash?.VisibleInventoryItems;
                if (items == null) return null;

                foreach (var item in items)
                {
                    if (item.Entity?.Path != null && item.Entity.Path.Contains("/CurrencyIncubation"))
                    {
                        var rect = item.GetClientRect();
                        var windowRect = gc.Window.GetWindowRectangle();
                        return new Vector2(windowRect.X + rect.Center.X, windowRect.Y + rect.Center.Y);
                    }
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Equipment slot positions as fractions of the InventoryPanel rect.
        /// Derived from live UI measurements — independent of UI element child indices,
        /// which can be stale/wrong after zone loads until the panel is cycled.
        /// </summary>
        private static readonly Dictionary<InventorySlotE, (float X, float Y)> SlotRelativePos = new()
        {
            { InventorySlotE.Helm1,       (0.5000f, 0.1486f) },
            { InventorySlotE.Amulet1,     (0.6541f, 0.2213f) },
            { InventorySlotE.Offhand1,    (0.8098f, 0.2093f) },
            { InventorySlotE.Weapon1,     (0.1887f, 0.2093f) },
            { InventorySlotE.BodyArmour1, (0.5000f, 0.2819f) },
            { InventorySlotE.Ring1,       (0.3444f, 0.2824f) },
            { InventorySlotE.Ring2,       (0.6541f, 0.2824f) },
            { InventorySlotE.Gloves1,     (0.3045f, 0.3671f) },
            { InventorySlotE.Belt1,       (0.5000f, 0.3917f) },
            { InventorySlotE.Boots1,      (0.6940f, 0.3671f) },
        };

        /// <summary>
        /// Find an equipped item that doesn't have an incubator applied.
        /// Uses ServerData to identify which slots need incubators, then computes
        /// click position from panel-relative coordinates (no UI element index lookup).
        /// </summary>
        private Vector2? FindEquipmentSlotToApply(GameController gc)
        {
            try
            {
                var inventories = gc.IngameState.ServerData?.PlayerInventories;
                if (inventories == null) return null;

                var panel = gc.IngameState.IngameUi.InventoryPanel;
                if (panel == null || !panel.IsVisible) return null;

                var panelRect = panel.GetClientRect();
                var windowRect = gc.Window.GetWindowRectangle();

                foreach (var invHolder in inventories)
                {
                    var inv = invHolder.Inventory;
                    if (inv == null || !SlotRelativePos.TryGetValue(inv.InventSlot, out var relPos)) continue;
                    if (inv.Items.Count != 1) continue;

                    var equippedItem = inv.Items.FirstOrDefault();
                    if (equippedItem == null) continue;

                    if (equippedItem.TryGetComponent<Mods>(out var mods))
                    {
                        if (!string.IsNullOrEmpty(mods.IncubatorName))
                            continue; // already has incubator
                    }

                    // Compute absolute click position from panel-relative coordinates
                    var screenX = panelRect.X + panelRect.Width * relPos.X;
                    var screenY = panelRect.Y + panelRect.Height * relPos.Y;
                    return new Vector2(windowRect.X + screenX, windowRect.Y + screenY);
                }
            }
            catch { }
            return null;
        }

        private Entity? FindStashEntity(GameController gc)
        {
            foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
            {
                if (entity.Type == EntityType.Stash && entity.IsTargetable)
                    return entity;
            }
            return null;
        }

        /// <summary>
        /// Debug overlay: draws rects around stash items and equipment slots with index IDs.
        /// Call from BotCore.Render() when stash is open to diagnose incubator click targets.
        /// </summary>
        public void RenderDebugIncubators(ExileCore.Graphics g, GameController gc)
        {
            try
            {
                var windowRect = gc.Window.GetWindowRectangle();
                float yOffset = 200f;

                // === Stash items ===
                var stashItems = gc.IngameState.IngameUi.StashElement?.VisibleStash?.VisibleInventoryItems;
                if (stashItems != null)
                {
                    g.DrawText("=== STASH ITEMS ===", new Vector2(10, yOffset), SharpDX.Color.Cyan);
                    yOffset += 18;

                    int idx = 0;
                    foreach (var item in stashItems)
                    {
                        var rect = item.GetClientRect();
                        var path = item.Entity?.Path ?? "null";
                        bool isIncubator = path.Contains("/CurrencyIncubation");

                        // Draw rect outline on-screen (client-relative, no window offset needed for DrawBox)
                        var drawRect = new RectangleF(rect.X, rect.Y, rect.Width, rect.Height);
                        var color = isIncubator ? SharpDX.Color.LimeGreen : new SharpDX.Color(255, 255, 255, 60);
                        g.DrawBox(drawRect, new SharpDX.Color(color.R, color.G, color.B, (byte)40));
                        // Border
                        g.DrawBox(new RectangleF(rect.X, rect.Y, rect.Width, 1), color);
                        g.DrawBox(new RectangleF(rect.X, rect.Y + rect.Height - 1, rect.Width, 1), color);
                        g.DrawBox(new RectangleF(rect.X, rect.Y, 1, rect.Height), color);
                        g.DrawBox(new RectangleF(rect.X + rect.Width - 1, rect.Y, 1, rect.Height), color);

                        // Index label on the item
                        g.DrawText($"{idx}", new Vector2(rect.X + 2, rect.Y + 2), SharpDX.Color.Yellow);

                        // Absolute click position (what we'd actually click)
                        var absX = windowRect.X + rect.Center.X;
                        var absY = windowRect.Y + rect.Center.Y;

                        // Short path name
                        var shortPath = path.Length > 40 ? "..." + path.Substring(path.Length - 37) : path;

                        // Side panel text
                        var label = isIncubator ? "[INCUB] " : "";
                        g.DrawText($"#{idx} {label}{shortPath}  rect=({rect.X:F0},{rect.Y:F0},{rect.Width:F0},{rect.Height:F0})  abs=({absX:F0},{absY:F0})",
                            new Vector2(10, yOffset), isIncubator ? SharpDX.Color.LimeGreen : SharpDX.Color.White);
                        yOffset += 15;
                        idx++;
                    }
                }

                // === Equipment slots (panel-relative positions) ===
                yOffset += 10;
                g.DrawText("=== EQUIPMENT SLOTS (panel-relative) ===", new Vector2(10, yOffset), SharpDX.Color.Cyan);
                yOffset += 18;

                var inventories = gc.IngameState.ServerData?.PlayerInventories;
                var panel = gc.IngameState.IngameUi.InventoryPanel;

                if (inventories != null && panel != null)
                {
                    var panelRect = panel.GetClientRect();
                    g.DrawText($"Panel rect=({panelRect.X:F0},{panelRect.Y:F0},{panelRect.Width:F0},{panelRect.Height:F0})", new Vector2(10, yOffset), SharpDX.Color.Gray);
                    yOffset += 15;

                    foreach (var kvp in SlotRelativePos)
                    {
                        var slotEnum = kvp.Key;
                        var relPos = kvp.Value;

                        // Find the inventory for this slot
                        string incubName = "?";
                        bool hasItem = false;
                        foreach (var invHolder in inventories)
                        {
                            var inv = invHolder.Inventory;
                            if (inv?.InventSlot != slotEnum) continue;
                            hasItem = inv.Items.Count > 0;
                            if (hasItem)
                            {
                                var equippedItem = inv.Items.FirstOrDefault();
                                if (equippedItem?.TryGetComponent<Mods>(out var mods) == true)
                                    incubName = string.IsNullOrEmpty(mods.IncubatorName) ? "NONE" : mods.IncubatorName;
                                else
                                    incubName = "no-mods";
                            }
                            else
                            {
                                incubName = "empty";
                            }
                            break;
                        }

                        // Compute screen position from panel-relative coords
                        var screenX = panelRect.X + panelRect.Width * relPos.X;
                        var screenY = panelRect.Y + panelRect.Height * relPos.Y;
                        var hasIncub = incubName != "NONE" && incubName != "empty" && incubName != "no-mods" && incubName != "?";
                        var slotColor = !hasItem ? SharpDX.Color.Gray
                            : hasIncub ? SharpDX.Color.Orange
                            : SharpDX.Color.LimeGreen;

                        // Draw crosshair at computed click position
                        const float crossSize = 8f;
                        g.DrawBox(new RectangleF(screenX - crossSize, screenY, crossSize * 2, 1), slotColor);
                        g.DrawBox(new RectangleF(screenX, screenY - crossSize, 1, crossSize * 2), slotColor);

                        // Label at position
                        g.DrawText(slotEnum.ToString().Replace("1", ""), new Vector2(screenX + 4, screenY - 8), SharpDX.Color.Yellow);

                        var absPos = new Vector2(windowRect.X + screenX, windowRect.Y + screenY);
                        g.DrawText($"{slotEnum} rel=({relPos.X:F3},{relPos.Y:F3}) incub={incubName}  abs=({absPos.X:F0},{absPos.Y:F0})",
                            new Vector2(10, yOffset), slotColor);
                        yOffset += 15;
                    }
                }
                else
                {
                    g.DrawText("InventoryPanel or ServerData not available", new Vector2(10, yOffset), SharpDX.Color.Red);
                    yOffset += 15;
                }
            }
            catch (Exception ex)
            {
                g.DrawText($"IncubatorDebug error: {ex.Message}", new Vector2(10, 200), SharpDX.Color.Red);
            }
        }
    }

    public enum StashPhase
    {
        Idle,
        NavigateToStash,
        OpenStash,
        SwitchToStoreTab,
        StoreItems,
        SwitchToWithdrawTab,
        WithdrawItems,
        ApplyIncubators,
        CloseStash,
    }

    public enum StashResult
    {
        None,
        InProgress,
        Succeeded,
        Failed,
    }
}
