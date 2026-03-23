using ExileCore;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using System.Numerics;
using System.Windows.Forms;

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
        private const float PhaseTimeoutSeconds = 30f;
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

        // Incubator state
        private bool _cursorHasIncubator;
        private int _incubatorsApplied;

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
            _incubatorsApplied = 0;
            Status = "Starting stash interaction";
            return true;
        }

        public void Cancel(GameController gc, NavigationSystem? nav = null)
        {
            nav?.Stop(gc);
            _phase = StashPhase.Idle;
            ItemFilter = null;
            Status = "Cancelled";
        }

        public StashResult Tick(GameController gc, NavigationSystem nav)
        {
            if (_phase == StashPhase.Idle)
                return StashResult.None;

            if ((DateTime.Now - _phaseStartTime).TotalSeconds > PhaseTimeoutSeconds)
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
                _phase = StashPhase.StoreItems;
                _phaseStartTime = DateTime.Now;
                Status = "Stash already open — storing items";
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

        private StashResult TickOpenStash(GameController gc)
        {
            if (gc.IngameState.IngameUi.StashElement?.IsVisible == true)
            {
                _phase = StashPhase.StoreItems;
                _phaseStartTime = DateTime.Now;
                Status = "Stash opened — storing items";
                return StashResult.InProgress;
            }

            var stash = FindStashEntity(gc);
            if (stash == null)
            {
                Status = "Stash entity gone";
                _phase = StashPhase.Idle;
                return StashResult.Failed;
            }

            var screenPos = gc.IngameState.Camera.WorldToScreen(stash.BoundsCenterPosNum);
            var windowRect = gc.Window.GetWindowRectangle();
            var absPos = new Vector2(windowRect.X + screenPos.X, windowRect.Y + screenPos.Y);

            BotInput.Click(absPos);
            _lastActionTime = DateTime.Now;
            Status = "Clicking stash";
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

            // Detect failed transfer: if stashable count didn't decrease since last click
            if (_prevItemCount >= 0 && stashableCount >= _prevItemCount)
            {
                _consecutiveFailures++;
                if (_consecutiveFailures >= MaxConsecutiveFailures)
                {
                    Status = $"Too many failed transfers — stored {_itemsStored} items — closing stash";
                    return EnterCloseStash();
                }
            }
            else
            {
                _consecutiveFailures = 0;
            }

            _prevItemCount = stashableCount;

            // Get click position from first stashable item
            var item = itemToStash;
            var rect = item.GetClientRect();
            var center = new Vector2(rect.Center.X, rect.Center.Y);
            var windowRect = gc.Window.GetWindowRectangle();
            var absPos = new Vector2(windowRect.X + center.X, windowRect.Y + center.Y);

            BotInput.CtrlClick(absPos);
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

            var cursor = gc.IngameState.IngameUi.Cursor;
            bool cursorHolding = cursor?.ChildCount > 0;

            // Step 2: Cursor has incubator → click equipment slot
            if (_cursorHasIncubator)
            {
                if (!cursorHolding)
                {
                    // Incubator was applied (cursor cleared)
                    _incubatorsApplied++;
                    _cursorHasIncubator = false;
                    Status = $"Incubator applied ({_incubatorsApplied})";
                    // Try next incubator on next tick
                    return StashResult.InProgress;
                }

                // Find an equipment slot without incubator and click it
                var slotPos = FindEquipmentSlotToApply(gc);
                if (slotPos == null)
                {
                    // No empty slots — right-click to drop cursor item back
                    BotInput.PressKey(Keys.Escape);
                    _lastActionTime = DateTime.Now;
                    _cursorHasIncubator = false;
                    Status = "No empty equipment slots — done";
                    _phase = StashPhase.CloseStash;
                    _phaseStartTime = DateTime.Now;
                    return StashResult.InProgress;
                }

                BotInput.Click(slotPos.Value);
                _lastActionTime = DateTime.Now;
                Status = "Clicking equipment slot";
                return StashResult.InProgress;
            }

            // Step 1: Find incubator in stash and right-click it
            // First check if there are any equipment slots available
            if (FindEquipmentSlotToApply(gc) == null)
            {
                Status = "All equipment has incubators — skipping";
                _phase = StashPhase.CloseStash;
                _phaseStartTime = DateTime.Now;
                return StashResult.InProgress;
            }

            var incubatorPos = FindIncubatorInStash(gc);
            if (incubatorPos == null)
            {
                Status = $"No incubators in stash — applied {_incubatorsApplied}";
                _phase = StashPhase.CloseStash;
                _phaseStartTime = DateTime.Now;
                return StashResult.InProgress;
            }

            BotInput.RightClick(incubatorPos.Value);
            _lastActionTime = DateTime.Now;
            _cursorHasIncubator = true;
            Status = "Right-clicking incubator";
            return StashResult.InProgress;
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

        // Fixed window-relative equipment slot positions (same as AutoPOE).
        // These are for 2560x1440. TODO: scale by resolution if needed.
        private static readonly Dictionary<InventorySlotE, Vector2> EquipmentSlotPositions = new()
        {
            { InventorySlotE.Helm1, new Vector2(1585, 165) },
            { InventorySlotE.BodyArmour1, new Vector2(1587, 296) },
            { InventorySlotE.Weapon1, new Vector2(1369, 233) },
            { InventorySlotE.Offhand1, new Vector2(1784, 232) },
            { InventorySlotE.Amulet1, new Vector2(1690, 245) },
            { InventorySlotE.Ring1, new Vector2(1480, 300) },
            { InventorySlotE.Ring2, new Vector2(1687, 306) },
            { InventorySlotE.Gloves1, new Vector2(1453, 398) },
            { InventorySlotE.Boots1, new Vector2(1719, 391) },
            { InventorySlotE.Belt1, new Vector2(1584, 421) },
        };

        /// <summary>
        /// Find an equipped item that doesn't have an incubator applied.
        /// Returns absolute screen position using fixed offsets (same approach as AutoPOE).
        /// Uses ServerData to check incubator presence, fixed positions for clicking.
        /// </summary>
        private Vector2? FindEquipmentSlotToApply(GameController gc)
        {
            try
            {
                var inventories = gc.IngameState.ServerData?.PlayerInventories;
                if (inventories == null) return null;

                var windowRect = gc.Window.GetWindowRectangle();

                foreach (var invHolder in inventories)
                {
                    var inv = invHolder.Inventory;
                    if (inv == null) continue;

                    // Only check equipment slots we have positions for
                    if (!EquipmentSlotPositions.ContainsKey(inv.InventSlot))
                        continue;

                    // Must have exactly 1 item (equipped)
                    if (inv.Items.Count != 1) continue;

                    var equippedItem = inv.Items.FirstOrDefault();
                    if (equippedItem == null) continue;

                    // Check if item already has an incubator
                    if (equippedItem.TryGetComponent<Mods>(out var mods))
                    {
                        if (!string.IsNullOrEmpty(mods.IncubatorName))
                            continue; // already has incubator
                    }

                    // Use fixed screen position for this slot
                    var slotPos = EquipmentSlotPositions[inv.InventSlot];
                    return new Vector2(windowRect.X + slotPos.X, windowRect.Y + slotPos.Y);
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
    }

    public enum StashPhase
    {
        Idle,
        NavigateToStash,
        OpenStash,
        StoreItems,
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
