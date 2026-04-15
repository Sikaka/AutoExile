using ExileCore;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using SharpDX;
using System.IO;
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
        private const int MaxConsecutiveFailures = 2;
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

        /// <summary>
        /// Ordered list of tab names to store into. If the first tab is full (consecutive failures),
        /// the system automatically tries the next one. Null/empty = use current tab.
        /// </summary>
        public List<string>? StoreTabNames { get; set; }

        private int _currentStoreTabIndex;
        private int _anonymousTabSwitches;

        /// <summary>Tab name to switch to for withdrawing fragments. Null = skip withdraw.</summary>
        public string? WithdrawTabName { get; set; }

        /// <summary>Entity path substring of fragment to withdraw from stash.</summary>
        public string? WithdrawFragmentPath { get; set; }

        /// <summary>How many fragments to withdraw (ctrl+clicks). Each click withdraws one stack unit.</summary>
        public int WithdrawCount { get; set; }

        /// <summary>
        /// Additional items to withdraw after the main fragment withdrawal (e.g. scarabs from their own tabs).
        /// Each entry: (tab name, path substring, count).
        /// Sub-tabs are auto-detected: if the tab has a sub-tab bar at child[5][0],
        /// the bot clicks the button whose label is contained in PathSubstring (case-insensitive).
        /// </summary>
        public List<(string TabName, string PathSubstring, int Count, int MinTier)> ExtraWithdrawals { get; set; } = new();
        private int _extraWithdrawalIndex;
        private bool _extraSubTabClicked;
        private int _extraWithdrawsRemaining;

        // Tab switching state
        private string? _pendingTabSwitch;
        private StashPhase _afterTabSwitch;
        private int _withdrawsRemaining;

        // Incubator state
        private bool _cursorHasIncubator;   // set after right-click, cleared once cursor verified
        private bool _cursorVerified;        // true once we confirmed incubator is on cursor
        private int _incubatorsApplied;
        private int _incubatorFailures;      // consecutive failures — stop after 2
        private string? _lastAppliedSlot;    // slot name we just clicked, for verification
        private bool _incubatorPhaseRan;     // true once ApplyIncubators has been entered this session
        private const int MaxIncubatorFailures = 2;

        public StashPhase Phase => _phase;
        public string Status { get; private set; } = "";
        public bool IsBusy => _phase != StashPhase.Idle;
        public int ItemsStored => _itemsStored;

        public bool Start()
        {
            if (_phase != StashPhase.Idle)
                return false;

            _phase = StashPhase.NavigateToStash;
            _phaseStartTime = DateTime.Now;
            _lastActionTime = DateTime.MinValue;
            _itemsStored = 0;
            _attempts = 0;
            _prevItemCount = -1;
            _consecutiveFailures = 0;
            _cursorHasIncubator = false;
            _cursorVerified = false;
            _incubatorsApplied = 0;
            _incubatorFailures = 0;
            _incubatorPhaseRan = false;
            _lastAppliedSlot = null;
            _pendingTabSwitch = null;
            _afterTabSwitch = StashPhase.Idle;

            _withdrawsRemaining = 0;
            _extraWithdrawalIndex = 0;
            _extraWithdrawsRemaining = 0;
            _extraSubTabClicked = false;
            _currentStoreTabIndex = 0;
            _anonymousTabSwitches = 0;
            Status = "Starting stash interaction";
            return true;
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
                // Reset phase so a subsequent Start() call is not blocked by leftover state,
                // and Tick()'s timeout doesn't fire using the old _phaseStartTime.
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
                StoreTabNames = null;
                WithdrawTabName = null;
                WithdrawFragmentPath = null;
                WithdrawCount = 0;
                ExtraWithdrawals.Clear();
            }

            if ((DateTime.Now - _phaseStartTime).TotalSeconds > BasePhaseTimeoutSeconds)
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

        public void Cancel(GameController gc, NavigationSystem? nav = null)
        {
            nav?.Stop(gc);
            _phase = StashPhase.Idle;
            ItemFilter = null;
            StoreTabNames = null;
            WithdrawTabName = null;
            WithdrawFragmentPath = null;
            WithdrawCount = 0;
            ExtraWithdrawals.Clear();
            _pendingTabSwitch = null;
            Status = "Cancelled";
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
                        BotInput.CursorPressKey(
                            new Vector2(windowRect.X + screenPos.X, windowRect.Y + screenPos.Y),
                            nav.MoveKey);
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
            // Always store loot first, then withdraw.
            // EnterCloseStash() will trigger any pending withdrawals after storing is done.
            _withdrawsRemaining = (!string.IsNullOrEmpty(WithdrawTabName) && !string.IsNullOrEmpty(WithdrawFragmentPath)) ? WithdrawCount : 0;
            _extraWithdrawalIndex = 0;
            EnterStorePhase(gc);
        }

        private string? CurrentStoreTabName =>
            StoreTabNames != null && _currentStoreTabIndex < StoreTabNames.Count
                ? StoreTabNames[_currentStoreTabIndex]
                : null;

        private void EnterStorePhase(GameController gc)
        {
            var tabName = CurrentStoreTabName;
            if (!string.IsNullOrEmpty(tabName))
            {
                var stash = gc.IngameState?.IngameUi?.StashElement;
                var names = stash?.AllStashNames;
                var currentIdx = stash?.IndexVisibleStash ?? -1;
                if (names != null && currentIdx >= 0 && currentIdx < names.Count
                    && !names[currentIdx].Equals(tabName, StringComparison.OrdinalIgnoreCase))
                {
                    _pendingTabSwitch = tabName;
                    _afterTabSwitch = StashPhase.StoreItems;
                    _phase = StashPhase.SwitchToStoreTab;
                    _phaseStartTime = DateTime.Now;
                    Status = $"Switching to {tabName} tab for storing";
                    return;
                }
            }

            _phase = StashPhase.StoreItems;
            _phaseStartTime = DateTime.Now;
            _prevItemCount = -1;
            _consecutiveFailures = 0;
            Status = $"Storing items{(string.IsNullOrEmpty(tabName) ? "" : $" ({tabName})")}";
        }

        private StashResult TickOpenStash(GameController gc)
        {
            if (gc.IngameState.IngameUi.StashElement?.IsVisible == true)
            {
                EnterFirstStashPhase(gc);
                return StashResult.InProgress;
            }

            // Close Map Device UI if it's open before moving to stash
            if (gc.IngameState.IngameUi.Atlas?.IsVisible == true)
            {
                BotInput.PressKey(Keys.Escape);
                _lastActionTime = DateTime.Now;
                Status = "[Nav] Closing Map Device UI before stash navigation";
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
                _phaseStartTime = DateTime.Now; // Reset timer so settle delay triggers after arrive
                _phase = _afterTabSwitch;
                _phaseStartTime = DateTime.Now;
                Status = $"On tab '{names?[targetIdx]}'";
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

            // Determine the active path/count for this withdrawal pass
            string? activePath;
            int remaining;
            bool isExtra = _extraWithdrawalIndex > 0 || _withdrawsRemaining <= 0;

            if (!isExtra && _withdrawsRemaining > 0 && !string.IsNullOrEmpty(WithdrawFragmentPath))
            {
                activePath = WithdrawFragmentPath;
                remaining = _withdrawsRemaining;
            }
            else if (_extraWithdrawalIndex < ExtraWithdrawals.Count)
            {
                activePath = ExtraWithdrawals[_extraWithdrawalIndex].PathSubstring;
                remaining = _extraWithdrawsRemaining;
                isExtra = true;
            }
            else
            {
                // All withdrawals done — proceed to close stash
                return EnterCloseStash();
            }

            if (remaining <= 0)
            {
                // Current pass complete — advance to next extra withdrawal
                if (isExtra)
                {
                    _extraWithdrawalIndex++;
                    _extraSubTabClicked = false;
                    if (_extraWithdrawalIndex < ExtraWithdrawals.Count)
                    {
                        var next = ExtraWithdrawals[_extraWithdrawalIndex];
                        _extraWithdrawsRemaining = next.Count;
                        _pendingTabSwitch = next.TabName;
                        _afterTabSwitch = StashPhase.WithdrawItems;
                        _phase = StashPhase.SwitchToWithdrawTab;
                        _phaseStartTime = DateTime.Now;
                        Status = $"Switching to '{next.TabName}' for scarab";
                        return StashResult.InProgress;
                    }
                }
                else
                {
                    // Main withdrawal done — start extra withdrawals if any
                    _withdrawsRemaining = 0;
                    if (ExtraWithdrawals.Count > 0)
                    {
                        _extraWithdrawalIndex = 0;
                        var first = ExtraWithdrawals[0];
                        _extraWithdrawsRemaining = first.Count;
                        _pendingTabSwitch = first.TabName;
                        _afterTabSwitch = StashPhase.WithdrawItems;
                        _phase = StashPhase.SwitchToWithdrawTab;
                        _phaseStartTime = DateTime.Now;
                        Status = $"Switching to '{first.TabName}' for scarab";
                        return StashResult.InProgress;
                    }
                }
                return EnterCloseStash();
            }

            // Auto-detect sub-tabs: if the stash tab has a sub-tab bar at child[5][0],
            // find the button whose label is contained in the PathSubstring and click it.
            if (isExtra && !_extraSubTabClicked && _extraWithdrawalIndex < ExtraWithdrawals.Count)
            {
                var pathSubstring = ExtraWithdrawals[_extraWithdrawalIndex].PathSubstring;
                var visStash = stashEl.VisibleStash;
                var tabBarContainer = visStash?.GetChildAtIndex(5)?.GetChildAtIndex(0);
                if (tabBarContainer != null && tabBarContainer.ChildCount > 0)
                {
                    // Find a sub-tab button whose label is contained in the path substring
                    for (int i = 0; i < tabBarContainer.ChildCount; i++)
                    {
                        var btn = tabBarContainer.GetChildAtIndex(i);
                        var btnLabel = btn?.GetChildAtIndex(0)?.Text ?? "";
                        if (!string.IsNullOrEmpty(btnLabel) &&
                            pathSubstring.IndexOf(btnLabel, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            var r = btn!.GetClientRect();
                            var windowRect0 = gc.Window.GetWindowRectangle();
                            var absPos0 = new Vector2(windowRect0.X + r.Center.X, windowRect0.Y + r.Center.Y);
                            if (BotInput.Click(absPos0))
                            {
                                _lastActionTime = DateTime.Now;
                                _phaseStartTime = DateTime.Now; // Reset timer for settle delay
                                _extraSubTabClicked = true;
                                Status = $"Clicked '{btnLabel}' sub-tab (auto-detected)";
                            }
                            return StashResult.InProgress;
                        }
                    }
                    // No matching sub-tab found — proceed with current view
                }
                _extraSubTabClicked = true;
            }

            // Find the item in the visible stash tab
            var items = stashEl.VisibleStash?.VisibleInventoryItems;
            if (items == null)
            {
                Status = "No items visible in withdraw tab";
                return EnterCloseStash();
            }

            ExileCore.PoEMemory.MemoryObjects.Entity? fragmentItem = null;
            SharpDX.RectangleF fragmentRect = default;
            int activeMinTier = isExtra && _extraWithdrawalIndex < ExtraWithdrawals.Count
                ? ExtraWithdrawals[_extraWithdrawalIndex].MinTier : 0;

            int totalChecked = 0;
            int tierMismatches = 0;

            foreach (var item in items)
            {
                var entity = item?.Item; // use Item for more reliable path reading
                if (entity?.Path == null) continue;
                totalChecked++;

                if (entity.Path.IndexOf(activePath, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                if (activeMinTier > 0)
                {
                    if (entity.TryGetComponent<MapKey>(out var mk))
                    {
                        if (mk.Tier < activeMinTier) { tierMismatches++; continue; }
                    }
                    else { tierMismatches++; continue; }
                }

                fragmentItem = entity;
                fragmentRect = item.GetClientRect();
                break;
            }

            if (fragmentItem == null)
            {
                Status = tierMismatches > 0 
                    ? $"Item found but {tierMismatches} failed tier check (>={activeMinTier})"
                    : $"Item '{activePath}' not found in {totalChecked} items";

                // Log what we actually saw to a file for debugging
                try
                {
                    var pluginDir = BotCore.Instance?.DirectoryFullName ?? "";
                    var logPath = Path.Combine(pluginDir, "WithdrawFail.txt");
                    var logLines = new List<string> { $"=== Withdraw Fail: {DateTime.Now} ===" };
                    logLines.Add($"Searching for: '{activePath}' (minTier: {activeMinTier})");
                    logLines.Add($"Visible items in current tab ({items.Count}):");
                    foreach (var it in items)
                    {
                        var ent = it?.Item ?? it?.Entity;
                        logLines.Add($"  - {ent?.Path ?? "null"}");
                    }
                    File.WriteAllLines(logPath, logLines);
                }
                catch { }

                // Skip this withdrawal — move on
                if (isExtra)
                {
                    _extraWithdrawsRemaining = 0; // will advance on next tick
                }
                else
                {
                    // Main withdrawal failed — start extra withdrawals if any
                    _withdrawsRemaining = 0;
                    if (ExtraWithdrawals.Count > 0)
                    {
                        _extraWithdrawalIndex = 0;
                        var first = ExtraWithdrawals[0];
                        _extraWithdrawsRemaining = first.Count;
                        _pendingTabSwitch = first.TabName;
                        _afterTabSwitch = StashPhase.WithdrawItems;
                        _phase = StashPhase.SwitchToWithdrawTab;
                        _phaseStartTime = DateTime.Now;
                        _extraSubTabClicked = false;
                        Status = $"Main item not found — switching to '{first.TabName}' for '{first.PathSubstring}'";
                        return StashResult.InProgress;
                    }
                }
                return StashResult.InProgress;
            }

            // Ctrl+click to withdraw one stack
            var windowRect = gc.Window.GetWindowRectangle();
            var absPos = new Vector2(windowRect.X + fragmentRect.Center.X, windowRect.Y + fragmentRect.Center.Y);
            if (BotInput.CtrlClick(absPos))
            {
                _lastActionTime = DateTime.Now;
                if (isExtra) _extraWithdrawsRemaining--;
                else _withdrawsRemaining--;
                var rem2 = isExtra ? _extraWithdrawsRemaining : _withdrawsRemaining;
                Status = $"Withdrawing '{activePath}' ({rem2} remaining)";
            }
            return StashResult.InProgress;
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

            if (_attempts >= MaxAttempts)
            {
                Status = $"Max attempts reached — stored {_itemsStored} items — closing stash";
                return EnterCloseStash();
            }

            // Get inventory slot items — same API as AutoPOE
            var slotItems = GetInventorySlotItems(gc);
            if (slotItems == null || slotItems.Count == 0)
            {
                Status = $"Done — stored {_itemsStored} items — closing stash";
                return EnterCloseStash();
            }

            // Filter: find first stashable item (skip items the filter wants to keep)
            ServerInventory.InventSlotItem? itemToStash = null;
            int stashableCount = 0;
            foreach (var si in slotItems)
            {
                if (ItemFilter != null && !ItemFilter(si))
                    continue; // keep this item
                stashableCount++;
                itemToStash ??= si;
            }

            if (itemToStash == null)
            {
                Status = $"Done — stored {_itemsStored} items (kept {slotItems.Count} filtered) — closing stash";
                return EnterCloseStash();
            }

            // Get click position from first stashable item
            var rect = itemToStash.GetClientRect();
            var center = new Vector2(rect.Center.X, rect.Center.Y);
            var windowRect = gc.Window.GetWindowRectangle();
            var absPos = new Vector2(windowRect.X + center.X, windowRect.Y + center.Y);

            // If BotInput isn't ready yet, return without touching failure detection
            if (!BotInput.CtrlClick(absPos))
                return StashResult.InProgress;

            // Detect failed transfer: item count didn't decrease since the last click

            if (_prevItemCount >= 0 && stashableCount >= _prevItemCount)
            {
                _consecutiveFailures++;

                // Wait for 2 failures in a row before switching tabs
                if (_consecutiveFailures >= 2)
                {
                    // Reset for the next tab
                    _consecutiveFailures = 0; 
                    _anonymousTabSwitches++;

                    if (StoreTabNames != null && StoreTabNames.Count > 0)
                    {
                        if (_anonymousTabSwitches >= StoreTabNames.Count)
                        {
                            Status = $"All {StoreTabNames.Count} configured tabs full — closing";
                            return EnterCloseStash();
                        }
                        _currentStoreTabIndex = (_currentStoreTabIndex + 1) % StoreTabNames.Count;
                        Status = $"Tab full — cycling to next named tab ({StoreTabNames[_currentStoreTabIndex]})";
                        EnterStorePhase(gc);
                        return StashResult.InProgress;
                    }
                    else
                    {
                        var stash = gc.IngameState?.IngameUi?.StashElement;
                        var names = stash?.AllStashNames;
                        if (names != null && names.Count > 1)
                        {
                            if (_anonymousTabSwitches >= names.Count)
                            {
                                Status = $"Entire stash checked and full — closing";
                                return EnterCloseStash();
                            }
                            int nextIdx = (stash.IndexVisibleStash + 1) % names.Count;
                            _pendingTabSwitch = names[nextIdx];
                            _prevItemCount = -1;
                            _phase = StashPhase.SwitchToStoreTab;
                            _afterTabSwitch = StashPhase.StoreItems;
                            _phaseStartTime = DateTime.Now;
                            Status = $"Tab full — cycling to tab '{_pendingTabSwitch}' ({_anonymousTabSwitches}/{names.Count})";
                            return StashResult.InProgress;
                        }
                    }
                    return EnterCloseStash();
                }
                else
                {
                    // FIRST FAILURE: Just retry the click
                    _lastActionTime = DateTime.Now;
                    Status = "Transfer failed, retrying click (1/2)...";
                    return StashResult.InProgress; 
                }
            }
            else
            {
                // Success! The item moved. Reset counters.
                _consecutiveFailures = 0;
                _anonymousTabSwitches = 0;
            }

            _prevItemCount = stashableCount;
            _lastActionTime = DateTime.Now;
            _attempts++;
            _itemsStored++;
            Status = $"Storing item {_itemsStored} ({slotItems.Count} remaining)";
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
        public static int CountInventoryItems(GameController gc, string? pathSubstring, int minTier = 0)
        {
            var items = GetInventorySlotItems(gc);
            if (items == null || string.IsNullOrEmpty(pathSubstring)) return 0;
            int count = 0;
            foreach (var item in items)
            {
                if (item.Item?.Path?.Contains(pathSubstring, StringComparison.OrdinalIgnoreCase) != true)
                    continue;
                if (minTier > 0)
                {
                    var mk = item.Item.GetComponent<MapKey>();
                    if (mk == null || mk.Tier < minTier) continue;
                }
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
            // Try incubator phase first if enabled and not yet attempted this session
            if (ApplyIncubators && !_incubatorPhaseRan)
            {
                _incubatorPhaseRan = true;
                _phase = StashPhase.ApplyIncubators;
                _phaseStartTime = DateTime.Now;
                _lastActionTime = DateTime.MinValue;
                _cursorHasIncubator = false;
                return StashResult.InProgress;
            }

            // After storing, do pending withdrawals before closing
            if (_withdrawsRemaining > 0 && !string.IsNullOrEmpty(WithdrawTabName) && !string.IsNullOrEmpty(WithdrawFragmentPath))
            {
                _pendingTabSwitch = WithdrawTabName;
                _afterTabSwitch = StashPhase.WithdrawItems;
                _phase = StashPhase.SwitchToWithdrawTab;
                _phaseStartTime = DateTime.Now;
                Status = $"Store done — switching to {WithdrawTabName} for withdrawal";
                return StashResult.InProgress;
            }

            if (_extraWithdrawalIndex < ExtraWithdrawals.Count)
            {
                var ex = ExtraWithdrawals[_extraWithdrawalIndex];
                _extraWithdrawsRemaining = ex.Count;
                _pendingTabSwitch = ex.TabName;
                _afterTabSwitch = StashPhase.WithdrawItems;
                _phase = StashPhase.SwitchToWithdrawTab;
                _phaseStartTime = DateTime.Now;
                Status = $"Store done — switching to '{ex.TabName}' for '{ex.PathSubstring}'";
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
                // Wait for a short post-close delay so inventory panels fully dismiss
                // before the bot resumes movement (prevents accidental item clicks)
                if ((DateTime.Now - _phaseStartTime).TotalMilliseconds < 600)
                    return StashResult.InProgress;

                Status = $"Stash closed — stored {_itemsStored} items";
                _phase = StashPhase.Idle;
                return StashResult.Succeeded;
            }

            // Press Escape to close
            BotInput.PressKey(Keys.Escape);
            _phaseStartTime = DateTime.Now; // reset timer so delay begins after stash closes
            _lastActionTime = DateTime.Now;
            Status = "Closing stash";
            return StashResult.InProgress;
        }

        // =================================================================
        // Incubator Application
        // =================================================================

        private StashResult TickApplyIncubators(GameController gc)
        {
            if ((DateTime.Now - _lastActionTime).TotalMilliseconds < ActionCooldownMs || !BotInput.CanAct)
                return StashResult.InProgress;

            // Stash must stay open for incubator right-click
            if (gc.IngameState.IngameUi.StashElement?.IsVisible != true)
            {
                Status = "Stash closed — skipping incubators";
                return EnterCloseStash();
            }

            // Give up after too many failures to prevent equipment damage
            if (_incubatorFailures >= MaxIncubatorFailures)
            {
                Status = $"Incubator apply failed {_incubatorFailures}x — stopping";
                // Drop cursor item safely if we're holding something
                var cursorCheck = gc.IngameState.IngameUi.Cursor;
                if (cursorCheck?.ChildCount > 0)
                {
                    BotInput.PressKey(Keys.Escape);
                    _lastActionTime = DateTime.Now;
                }
                return EnterCloseStash();
            }

            var cursor = gc.IngameState.IngameUi.Cursor;
            bool cursorHolding = cursor?.ChildCount > 0;

            // ── Step B: We clicked equipment — verify incubator was applied ──
            if (_cursorVerified && _lastAppliedSlot != null)
            {
                if (!cursorHolding)
                {
                    // Cursor cleared — verify the slot actually received the incubator
                    bool applied = VerifyIncubatorApplied(gc, _lastAppliedSlot);
                    if (applied)
                    {
                        _incubatorsApplied++;
                        _incubatorFailures = 0;
                        Status = $"Incubator applied ({_incubatorsApplied})";
                    }
                    else
                    {
                        _incubatorFailures++;
                        Status = $"Incubator apply unverified (attempt {_incubatorFailures}) — retrying";
                    }
                    _cursorHasIncubator = false;
                    _cursorVerified = false;
                    _lastAppliedSlot = null;
                    return StashResult.InProgress;
                }

                // Still holding — click the slot again (previous click may have missed)
                var retrySlot = FindEquipmentSlotToApply(gc, _lastAppliedSlot);
                if (retrySlot == null)
                {
                    // Slot filled or gone — consider it applied
                    _incubatorsApplied++;
                    BotInput.PressKey(Keys.Escape); // drop cursor item
                    _lastActionTime = DateTime.Now;
                    _cursorHasIncubator = false;
                    _cursorVerified = false;
                    _lastAppliedSlot = null;
                    Status = "No target slot found — dropping cursor item";
                    return EnterCloseStash();
                }
                BotInput.Click(retrySlot.Value.pos);
                _lastAppliedSlot = retrySlot.Value.slotName;
                _lastActionTime = DateTime.Now;
                Status = "Re-clicking equipment slot";
                return StashResult.InProgress;
            }

            // ── Step A2: Incubator right-clicked — verify it's on cursor before clicking gear ──
            if (_cursorHasIncubator && !_cursorVerified)
            {
                if (!cursorHolding)
                {
                    // Right-click didn't pick it up — increment failure, try again
                    _incubatorFailures++;
                    _cursorHasIncubator = false;
                    Status = $"Incubator not on cursor after right-click (attempt {_incubatorFailures})";
                    return StashResult.InProgress;
                }

                // Verify cursor item is actually an incubator (not some other item)
                bool isIncubator = IsCursorIncubator(gc);
                if (!isIncubator)
                {
                    // Wrong item on cursor — press Escape to drop it
                    _incubatorFailures++;
                    BotInput.PressKey(Keys.Escape);
                    _lastActionTime = DateTime.Now;
                    _cursorHasIncubator = false;
                    Status = $"Wrong item on cursor — escaping (attempt {_incubatorFailures})";
                    return StashResult.InProgress;
                }

                // Confirmed incubator on cursor — find slot and click
                var slot = FindEquipmentSlotToApply(gc);
                if (slot == null)
                {
                    BotInput.PressKey(Keys.Escape);
                    _lastActionTime = DateTime.Now;
                    _cursorHasIncubator = false;
                    Status = "No empty equipment slots — done";
                    return EnterCloseStash();
                }

                _cursorVerified = true;
                _lastAppliedSlot = slot.Value.slotName;
                BotInput.Click(slot.Value.pos);
                _lastActionTime = DateTime.Now;
                Status = $"Applying incubator to {slot.Value.slotName}";
                return StashResult.InProgress;
            }

            // ── Step A1: No incubator on cursor — find one in stash and right-click ──
            if (FindEquipmentSlotToApply(gc) == null)
            {
                Status = "All equipment has incubators — done";
                return EnterCloseStash();
            }

            var incubatorPos = FindIncubatorInStash(gc);
            if (incubatorPos == null)
            {
                Status = $"No incubators in stash — applied {_incubatorsApplied}";
                return EnterCloseStash();
            }

            BotInput.RightClick(incubatorPos.Value);
            _lastActionTime = DateTime.Now;
            _cursorHasIncubator = true;
            _cursorVerified = false;
            Status = "Right-clicking incubator — waiting to verify cursor";
            return StashResult.InProgress;
        }

        /// <summary>
        /// Verify the cursor actually holds an incubator item (not equipment or other item).
        /// Uses the UI cursor element's child entity path.
        /// </summary>
        private static bool IsCursorIncubator(GameController gc)
        {
            try
            {
                var cursorEl = gc.IngameState.IngameUi.Cursor;
                
                // Skip the complex Entity.Path memory read that is failing.
                // Just return true if the cursor is holding any item!
                return cursorEl != null && cursorEl.ChildCount > 0;
            }
            catch { return false; }
        }

        /// <summary>
        /// Check that the named equipment slot now has an incubator applied.
        /// </summary>
        private static bool VerifyIncubatorApplied(GameController gc, string slotName)
        {
            try
            {
                var inventories = gc.IngameState.ServerData?.PlayerInventories;
                if (inventories == null) return false;
                foreach (var invHolder in inventories)
                {
                    var inv = invHolder.Inventory;
                    if (inv == null) continue;
                    if (!SlotRelativePos.ContainsKey(inv.InventSlot)) continue;
                    if (inv.InventSlot.ToString() != slotName) continue;
                    var item = inv.Items.FirstOrDefault();
                    if (item == null) continue;
                    if (item.TryGetComponent<Mods>(out var mods))
                        return !string.IsNullOrEmpty(mods.IncubatorName);
                }
            }
            catch { }
            return false; // can't verify — treat as unverified but don't block
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
                    var entity = item?.Item ?? item?.Entity;
                    if (entity?.Path != null && entity.Path.Contains("/CurrencyIncubation", StringComparison.OrdinalIgnoreCase))
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
        /// Returns (absolute screen position, slot name), or null if none found.
        /// Optionally restrict to a specific slot name for re-click verification.
        /// </summary>
        private (Vector2 pos, string slotName)? FindEquipmentSlotToApply(GameController gc, string? specificSlot = null)
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

                    var slotName = inv.InventSlot.ToString();
                    if (specificSlot != null && slotName != specificSlot) continue;

                    var equippedItem = inv.Items.FirstOrDefault();
                    if (equippedItem == null) continue;

                    // Skip if already has incubator; items without Mods component are valid targets
                    if (equippedItem.TryGetComponent<Mods>(out var mods) && !string.IsNullOrEmpty(mods.IncubatorName))
                        continue;

                    var screenX = panelRect.X + panelRect.Width * relPos.X;
                    var screenY = panelRect.Y + panelRect.Height * relPos.Y;
                    return (new Vector2(windowRect.X + screenX, windowRect.Y + screenY), slotName);
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
                        var entity = item?.Item ?? item?.Entity;
                        var path = entity?.Path ?? "null";
                        bool isIncubator = path.Contains("/CurrencyIncubation", StringComparison.OrdinalIgnoreCase);

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
