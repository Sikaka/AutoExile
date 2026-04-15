using ExileCore;
using ExileCore.PoEMemory;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using System.Numerics;
using System.Windows.Forms;

namespace AutoExile.Systems
{
    /// <summary>
    /// Generic map device interaction: open device → find map in stash → insert → activate → enter portal.
    /// Modes provide a filter function to select which map type to run.
    /// </summary>
    public class MapDeviceSystem
    {
        private MapDevicePhase _phase = MapDevicePhase.Idle;
        private DateTime _phaseStartTime;
        private DateTime _lastActionTime;
        private Func<Element, bool>? _mapFilter;
        private Func<Entity, bool>? _cleanStorageFilter;
        private int _cleanStorageClicks;
        private string? _inventoryFragmentPath; // fallback: right-click from inventory if stash has none
        private const float ActionCooldownMs = 400;
        private const float BasePhaseTimeoutSeconds = 30f;
        private const float BasePortalWaitTimeoutSeconds = 10f;
        
        /// <summary>Extra seconds added to all server-response timeouts. Synced from settings.</summary>
        public float ExtraLatencySec { get; set; }
        
        /// <summary>Max click retry attempts. Synced from settings.</summary>
        public int MaxClickAttempts { get; set; } = 5;

        /// <summary>
        /// Grid distance considered "close enough" to interact with device/portal.
        /// Synced from InteractionSystem.InteractRadius (which comes from LootRadius setting).
        /// </summary>
        public float InteractRadius { get; set; } = 20f;

        /// <summary>InteractionSystem reference for portal clicking. Synced from BotCore.</summary>
        public InteractionSystem? Interaction { get; set; }

        /// <summary>
        /// Target map name to select in the atlas (e.g., "Mausoleum").
        /// When set, the system clicks the correct atlas node before selecting a map from stash.
        /// Strip any "★ " prefix before setting.
        /// </summary>
        public string? TargetMapName { get; set; }

        /// <summary>
        /// Minimum map tier to accept from the stash. 0 = any tier.
        /// </summary>
        public int MinMapTier { get; set; }

        // Navigation to device — track failed close-approach attempts
        private int _navAttempts;
        private float _bestDistSeen = float.MaxValue;

        /// <summary>
        /// Path substrings of scarabs to Ctrl+click from inventory into the map device.
        /// Set before calling Start(). Cleared after activation.
        /// </summary>
        public List<string> ScarabPaths { get; set; } = new();

        // ── Device storage state (populated by CheckStorage phase) ──
        /// <summary>Total slots in map device storage (Columns × Rows). -1 = not yet checked.</summary>
        public int StorageTotalSlots { get; private set; } = -1;
        /// <summary>Filled slots in map device storage.</summary>
        public int StorageFilledSlots { get; private set; }
        /// <summary>Empty slots = TotalSlots - FilledSlots. -1 if not checked.</summary>
        public int StorageEmptySlots => StorageTotalSlots < 0 ? -1 : StorageTotalSlots - StorageFilledSlots;

        // path substring and max count for StockStorage phase
        private string? _stockItemPath;
        private int _stockItemMinTier;
        private int _stockMaxItems;
        private int _stockItemsDeposited;

        /// <summary>
        /// When set, TickSelectMap will also check player inventory for a map matching this path
        /// and right-click it into the device if the atlas map stash is empty.
        /// Populated by HideoutFlow when AutoRestock pulls a map to inventory.
        /// </summary>
        public string? InventoryMapPath { get; set; }
        /// <summary>Minimum map tier when scanning player inventory for a restocked map. 0 = no filter.</summary>
        public int InventoryMapMinTier { get; set; }

        // Atlas node selection state
        private bool _nodeSelected;
        private int _nodeClickAttempts;
        private int _atlasDragAttempts;

        // Map Interaction State
        private bool _mapRightClicked;

        // Scarab insertion state
        private int _scarabInsertIndex;

        // Inventory fragment fallback
        private int _invOpenAttempts;
        private const int MaxInvOpenAttempts = 5;

        // Scroll of Wisdom identification state
        private bool _sowActivated; // true after right-clicking SoW, waiting to click the map

        // Portal spawn settle
        private DateTime? _portalFirstSeenAt;

        private bool CanAct() =>
            BotInput.CanAct && (DateTime.Now - _lastActionTime).TotalMilliseconds >= ActionCooldownMs;

        // UI element indices for atlas panel
        // Map stash: atlas[3][0][1] — children are InventoryItem elements
        // Device slots: atlas[7][0][2] — 6 slots, occupied slot has ChildCount==2, child[1] is the item
        // Activate button: atlas[7][0][3] — child[0].Text == "activate"
        // Map name text: atlas[7][0][1][0][0] — verifies correct node selected
        private static readonly int[] MapStashPath = { 3, 0, 1 };
        private static readonly int[] DeviceSlotsPath = { 7, 0, 2 };
        private static readonly int[] ActivateButtonPath = { 7, 0, 3 };
        private static readonly int[] MapNameTextPath = { 7, 0, 1, 0, 0 };

        public MapDevicePhase Phase => _phase;
        public string Status { get; private set; } = "";
        public bool IsBusy => _phase != MapDevicePhase.Idle;

        /// <summary>
        /// Start the map creation flow with a filter for which map to select.
        /// The filter receives each InventoryItem element from the map stash
        /// and should return true for the desired map type.
        /// </summary>
        public bool Start(Func<Element, bool> mapFilter, string? inventoryFragmentPath = null)
        {
            if (_phase != MapDevicePhase.Idle)
                return false;

            _mapFilter = mapFilter;
            _inventoryFragmentPath = inventoryFragmentPath;
            // Clear any leftover state from previous CheckStorage/CleanStorage/StockStorage operations
            // so TickOpenDevice routes to SelectMap, not back into a stale storage phase.
            _cleanStorageFilter = null;
            _stockItemPath = null;
            _stockItemsDeposited = 0;
            _phase = MapDevicePhase.NavigateToDevice;
            _phaseStartTime = DateTime.Now;
            _lastActionTime = DateTime.MinValue;
            _navAttempts = 0;
            _bestDistSeen = float.MaxValue;
            _nodeSelected = false;
            _nodeClickAttempts = 0;
            _atlasDragAttempts = 0;
            _mapRightClicked = false;
            _invOpenAttempts = 0;
            _scarabInsertIndex = 0;
            _sowActivated = false;
            _portalFirstSeenAt = null;
            Status = "Starting map creation";
            return true;
        }

        /// <summary>
        /// Navigate to device, open atlas, read storage slot counts.
        /// If <paramref name="cleanFilter"/> is provided, immediately pipeline into CleanStorage
        /// after reading (keeping the atlas open) instead of closing it. This lets HideoutFlow
        /// do Check + Clean in a single device visit instead of two.
        /// </summary>
        public bool StartCheckStorage(Func<Entity, bool>? cleanFilter = null)
        {
            if (_phase != MapDevicePhase.Idle) return false;
            _phase = MapDevicePhase.NavigateToDevice;
            _phaseStartTime = DateTime.Now;
            _lastActionTime = DateTime.MinValue;
            _navAttempts = 0;
            _bestDistSeen = float.MaxValue;
            // After NavigateToDevice→OpenDevice, we'll route to CheckStorage instead of SelectMap
            _mapFilter = _ => false; // dummy — CheckStorage won't use it
            _inventoryFragmentPath = null;
            // Pre-load clean filter so TickCheckStorage can pipeline directly to CleanStorage
            _cleanStorageFilter = cleanFilter;
            _cleanStorageClicks = 0;
            _invOpenAttempts = 0;
            Status = "Navigating to device for storage check";
            return true;
        }

        /// <summary>Navigate to device, open atlas, Ctrl+click matching inventory items into storage.</summary>
        public bool StartStockStorage(string itemPathSubstring, int minTier, int maxItems)
        {
            if (_phase != MapDevicePhase.Idle) return false;
            _stockItemPath = itemPathSubstring;
            _stockItemMinTier = minTier;
            _stockMaxItems = maxItems;
            _stockItemsDeposited = 0;
            _phase = MapDevicePhase.NavigateToDevice;
            _phaseStartTime = DateTime.Now;
            _lastActionTime = DateTime.MinValue;
            _navAttempts = 0;
            _bestDistSeen = float.MaxValue;
            _invOpenAttempts = 0;
            _mapFilter = _ => false;
            _inventoryFragmentPath = null;
            Status = "Navigating to device to stock storage";
            return true;
        }

        /// <summary>Navigate to device, open atlas, read storage items, and ctrl-click out any items failing the filter.</summary>
        public bool StartCleanStorage(Func<Entity, bool> validItemFilter)
        {
            if (_phase != MapDevicePhase.Idle) return false;
            _cleanStorageFilter = validItemFilter;
            _cleanStorageClicks = 0;
            _phase = MapDevicePhase.NavigateToDevice;
            _phaseStartTime = DateTime.Now;
            _lastActionTime = DateTime.MinValue;
            _navAttempts = 0;
            _bestDistSeen = float.MaxValue;
            _invOpenAttempts = 0;
            _stockItemPath = null;
            _mapFilter = _ => false;
            _inventoryFragmentPath = null;
            Status = "Navigating to device to clean storage";
            return true;
        }

        public void Cancel(GameController gc, NavigationSystem? nav = null)
        {
            nav?.Stop(gc);
            Interaction?.Cancel(gc);
            _phase = MapDevicePhase.Idle;
            _mapFilter = null;
            _inventoryFragmentPath = null;
            TargetMapName = null;
            MinMapTier = 0;
            Status = "Cancelled";
        }

        /// <summary>
        /// Tick the state machine. Call every frame while IsBusy.
        /// Returns result when complete or failed.
        /// </summary>
        public MapDeviceResult Tick(GameController gc, NavigationSystem nav)
        {
            if (_phase == MapDevicePhase.Idle)
                return MapDeviceResult.None;

            var phaseElapsed = (DateTime.Now - _phaseStartTime).TotalSeconds;

            // Phase timeout
            if (phaseElapsed > BasePhaseTimeoutSeconds + ExtraLatencySec
                && _phase != MapDevicePhase.WaitForPortals)
            {
                Status = $"TIMEOUT after {phaseElapsed:F0}s in {_phase} — last status: {Status}";
                _phase = MapDevicePhase.Idle;
                return MapDeviceResult.Failed;
            }

            // Action cooldown
            if ((DateTime.Now - _lastActionTime).TotalMilliseconds < ActionCooldownMs)
                return MapDeviceResult.InProgress;

            return _phase switch
            {
                MapDevicePhase.NavigateToDevice => TickNavigateToDevice(gc, nav),
                MapDevicePhase.OpenDevice => TickOpenDevice(gc),
                MapDevicePhase.CheckStorage => TickCheckStorage(gc),
                MapDevicePhase.CleanStorage => TickCleanStorage(gc),
                MapDevicePhase.StockStorage => TickStockStorage(gc),
                MapDevicePhase.SelectMap => TickSelectMap(gc),
                MapDevicePhase.InsertScarabs => TickInsertScarabs(gc),
                MapDevicePhase.Activate => TickActivate(gc),
                MapDevicePhase.WaitForPortals => TickWaitForPortals(gc),
                MapDevicePhase.EnterPortal => TickEnterPortal(gc, nav),
                _ => MapDeviceResult.InProgress
            };
        }

        // --- Phase: Navigate to map device ---

        private MapDeviceResult TickNavigateToDevice(GameController gc, NavigationSystem nav)
        {
            // Wait for stash/inventory panels to close before proceeding
            var stashVisible = gc.IngameState.IngameUi.StashElement?.IsVisible == true;
            var invVisible = gc.IngameState.IngameUi.InventoryPanel?.IsVisible == true;
            if (stashVisible || invVisible)
            {
                var sent = BotInput.PressKey(Keys.Escape);
                _lastActionTime = DateTime.Now;
                Status = $"[Nav] Closing panels (stash={stashVisible} inv={invVisible} sent={sent} canAct={BotInput.CanAct})";
                return MapDeviceResult.InProgress;
            }

            var device = FindMapDevice(gc);
            if (device == null)
            {
                // Grace period — entity list may not be populated yet on first frames
                if ((DateTime.Now - _phaseStartTime).TotalSeconds < 3)
                {
                    Status = $"[Nav] Searching for map device ({(DateTime.Now - _phaseStartTime).TotalSeconds:F0}s)...";
                    return MapDeviceResult.InProgress;
                }
                Status = "[Nav] Map device entity not found in entity list";
                _phase = MapDevicePhase.Idle;
                return MapDeviceResult.Failed;
            }

            // Check if atlas is already open
            var atlas = gc.IngameState.IngameUi.Atlas;
            if (atlas?.IsVisible == true)
            {
                _phase = MapDevicePhase.SelectMap;
                _phaseStartTime = DateTime.Now;
                Status = "Atlas already open — selecting map";
                return MapDeviceResult.InProgress;
            }

            // Distance check in grid units
            var playerGrid = gc.Player.GridPosNum;
            var deviceGrid = device.GridPosNum;
            var dist = Vector2.Distance(
                new Vector2(playerGrid.X, playerGrid.Y),
                new Vector2(deviceGrid.X, deviceGrid.Y));

            if (dist < _bestDistSeen)
                _bestDistSeen = dist;

            if (dist < InteractRadius)
            {
                // Close enough — switch to clicking the device
                nav.Stop(gc);
                _phase = MapDevicePhase.OpenDevice;
                _phaseStartTime = DateTime.Now;
                Status = "Near device — opening";
                return MapDeviceResult.InProgress;
            }

            if (!nav.IsNavigating)
            {
                _navAttempts++;

                // After first nav attempt completes, the device may be on unwalkable
                // terrain (e.g., hideout decorations). A* snaps to the nearest walkable
                // cell, so we can't get closer. Accept current position if within 2x radius.
                if (_navAttempts > 1 && _bestDistSeen < InteractRadius * 2)
                {
                    nav.Stop(gc);
                    _phase = MapDevicePhase.OpenDevice;
                    _phaseStartTime = DateTime.Now;
                    Status = $"Near device (best dist: {_bestDistSeen:F0}) — opening";
                    return MapDeviceResult.InProgress;
                }

                var gridTarget = new Vector2(deviceGrid.X, deviceGrid.Y);
                var success = nav.NavigateTo(gc, gridTarget);
                if (!success)
                {
                    // A* can't find a path — common in hideouts where decorations create
                    // fake walls on the pathfinding grid. Fall back to direct walk-toward:
                    // just aim cursor at the device and press move key.
                    if (gc.Area.CurrentArea.IsHideout && BotInput.CanAct)
                    {
                        var screenPos = gc.IngameState.Camera.WorldToScreen(device.BoundsCenterPosNum);
                        var windowRect = gc.Window.GetWindowRectangle();
                        var absPos = new Vector2(windowRect.X + screenPos.X, windowRect.Y + screenPos.Y);
                        BotInput.CursorPressKey(absPos, nav.MoveKey);
                        Status = $"[Nav] Direct walk to device — no A* path (dist: {dist:F0})";
                        return MapDeviceResult.InProgress;
                    }

                    Status = "No path to map device";
                    _phase = MapDevicePhase.Idle;
                    return MapDeviceResult.Failed;
                }
            }

            Status = $"[Nav] Walking to device (dist: {dist:F0}, nav={nav.IsNavigating})";
            return MapDeviceResult.InProgress;
        }

        // --- Phase: Click to open the atlas panel ---

        private MapDeviceResult TickOpenDevice(GameController gc)
        {
            var atlas = gc.IngameState.IngameUi.Atlas;
            if (atlas?.IsVisible == true)
            {
                // Route to the appropriate next phase based on what started us
                if (_cleanStorageFilter != null)
                    _phase = MapDevicePhase.CleanStorage;
                else if (_stockItemPath != null && _stockItemsDeposited == 0 && _phase != MapDevicePhase.StockStorage)
                    _phase = MapDevicePhase.StockStorage;
                else if (StorageTotalSlots < 0) // haven't checked storage yet
                    _phase = MapDevicePhase.CheckStorage;
                else
                    _phase = MapDevicePhase.SelectMap;
                _phaseStartTime = DateTime.Now;
                Status = $"Atlas opened — going to {_phase}";
                return MapDeviceResult.InProgress;
            }

            var device = FindMapDevice(gc);
            if (device == null)
            {
                Status = "Map device disappeared";
                _phase = MapDevicePhase.Idle;
                return MapDeviceResult.Failed;
            }

            // Click the device strictly at its center to guarantee we don't accidentally click orbiting portals
            var windowRect = gc.Window.GetWindowRectangle();
            var screenCenter = gc.IngameState.Camera.WorldToScreen(device.BoundsCenterPosNum);
            var clickRect = new SharpDX.RectangleF(windowRect.X + screenCenter.X - 10, windowRect.Y + screenCenter.Y - 10, 20, 20);
            var pos = BotInput.RandomizeWithinRect(clickRect);
            
            if (!BotInput.Click(pos))
            {
                Status = "[Open] Device off screen or gate blocked";
                return MapDeviceResult.InProgress;
            }
            _lastActionTime = DateTime.Now;
            Status = "[Open] Clicking device explicitly at center bounds";
            return MapDeviceResult.InProgress;
        }

        // --- Phase: Select map node + insert map from stash ---

        private MapDeviceResult TickSelectMap(GameController gc)
        {
            var atlas = gc.IngameState.IngameUi.Atlas;
            if (atlas?.IsVisible != true)
            {
                Status = "Atlas closed unexpectedly";
                _phase = MapDevicePhase.Idle;
                return MapDeviceResult.Failed;
            }

            // Check if a map is already in the device
            if (IsMapInDevice(atlas))
            {
                if (ScarabPaths.Count > 0)
                {
                    _scarabInsertIndex = 0;
                    _phase = MapDevicePhase.InsertScarabs;
                    _phaseStartTime = DateTime.Now;
                    Status = "Map in device — inserting scarabs";
                }
                else
                {
                    _phase = MapDevicePhase.Activate;
                    _phaseStartTime = DateTime.Now;
                    Status = "Map already in device — activating";
                }
                return MapDeviceResult.InProgress;
            }

            // Two distinct flows:
            // A) Named map (mapping mode): must select atlas node first → device panel opens → Ctrl+click map
            // B) Auto-match (blight/simulacrum): right-click map in stash → ctrl+click to insert
            bool namedMapFlow = !string.IsNullOrEmpty(TargetMapName);

            if (namedMapFlow)
            {
                // Device panel must be visible before we can Ctrl+click insert
                var devicePanel = atlas.GetChildAtIndex(7);
                bool devicePanelVisible = devicePanel?.IsVisible == true;

                if (!devicePanelVisible)
                {
                    // Click the atlas node to open the device panel for this map
                    return TickSelectAtlasNode(gc, atlas);
                }

                // Device panel is open — verify correct map is selected
                if (!_nodeSelected)
                {
                    var nameEl = atlas.GetChildFromIndices(MapNameTextPath);
                    if (nameEl?.Text != null &&
                        nameEl.Text.Equals(TargetMapName, StringComparison.OrdinalIgnoreCase))
                    {
                        _nodeSelected = true;
                        Status = $"[Select] {TargetMapName} confirmed selected";
                    }
                    else
                    {
                        // Wrong map selected — click the correct node
                        return TickSelectAtlasNode(gc, atlas);
                    }
                }
            }

            // Find a matching map from the stash and insert it
            var mapStash = atlas.GetChildFromIndices(MapStashPath);
            if (mapStash == null)
            {
                Status = "[Select] Map stash panel not found";
                _phase = MapDevicePhase.Idle;
                return MapDeviceResult.Failed;
            }

            Element? targetMap = null;
            int checkedCount = 0;
            for (int i = 0; i < mapStash.ChildCount; i++)
            {
                var item = mapStash.GetChildAtIndex(i);
                if (item == null || item.Type != ElementType.InventoryItem)
                    continue;
                checkedCount++;

                if (_mapFilter != null && !_mapFilter(item))
                    continue;

                targetMap = item;
                break;
            }

            // Fallback: check player inventory for right-clickable fragments (boss invitations, etc.)
            if (targetMap == null && _inventoryFragmentPath != null)
            {
                if (!CanAct()) return MapDeviceResult.InProgress;

                // Ensure inventory panel is open
                var invPanel = gc.IngameState.IngameUi.InventoryPanel;
                if (invPanel == null || !invPanel.IsVisible)
                {
                    _invOpenAttempts++;
                    if (_invOpenAttempts > MaxInvOpenAttempts)
                    {
                        Status = "[Select] Failed to open inventory panel";
                        _phase = MapDevicePhase.Idle;
                        return MapDeviceResult.Failed;
                    }
                    BotInput.PressKey(System.Windows.Forms.Keys.I);
                    _lastActionTime = DateTime.Now;
                    Status = $"[Select] Opening inventory (attempt {_invOpenAttempts})...";
                    return MapDeviceResult.InProgress;
                }

                // Inventory is open — find and process a matching fragment
                bool foundAny = false;
                var invItems = gc.IngameState.ServerData?.PlayerInventories?[0]?.Inventory?.InventorySlotItems;
                if (invItems != null)
                {
                    foreach (var slotItem in invItems)
                    {
                        var item = slotItem.Item;
                        if (item?.Path == null) continue;
                        if (!item.Path.Contains(_inventoryFragmentPath, StringComparison.OrdinalIgnoreCase))
                            continue;

                        foundAny = true;
                        var windowRect2 = gc.Window.GetWindowRectangle();
                        var slotRect = slotItem.GetClientRect();
                        var absPos2 = new Vector2(windowRect2.X + slotRect.Center.X,
                            windowRect2.Y + slotRect.Center.Y);
                            
                        if (!_mapRightClicked)
                        {
                            BotInput.RightClick(absPos2);
                            _mapRightClicked = true;
                            _lastActionTime = DateTime.Now;
                            Status = "[Select] Right-clicking fragment from inventory";
                        }
                        else
                        {
                            BotInput.CtrlClick(absPos2);
                            _lastActionTime = DateTime.Now;
                            Status = "[Select] Ctrl-clicking fragment into device";
                        }
                        return MapDeviceResult.InProgress;
                    }
                }

                if (!foundAny)
                {
                    Status = $"[Select] No fragments in stash or inventory — out of {_inventoryFragmentPath}";
                    _phase = MapDevicePhase.Idle;
                    return MapDeviceResult.Failed;
                }
            }

            if (targetMap == null)
            {
                // Fallback: check player inventory for a restocked map (AutoRestock placed it there)
                if (!string.IsNullOrEmpty(InventoryMapPath))
                {
                    var inv = gc.IngameState?.ServerData?.PlayerInventories;
                    if (inv != null && inv.Count > 0)
                    {
                        foreach (var slot in inv[0].Inventory.InventorySlotItems)
                        {
                            var item = slot?.Item;
                            if (item?.Path == null) continue;
                            if (!item.Path.Contains(InventoryMapPath, StringComparison.OrdinalIgnoreCase))
                                continue;
                            if (InventoryMapMinTier > 0)
                            {
                                var mk = item.GetComponent<MapKey>();
                                if (mk == null || mk.Tier < InventoryMapMinTier) continue;
                            }

                            // Open inventory if needed
                            if (gc.IngameState?.IngameUi?.InventoryPanel?.IsVisible != true)
                            {
                                if (++_invOpenAttempts > MaxInvOpenAttempts)
                                {
                                    Status = "[Select] Inventory won't open for restock map";
                                    _phase = MapDevicePhase.Idle;
                                    return MapDeviceResult.Failed;
                                }
                                BotInput.PressKey(System.Windows.Forms.Keys.I);
                                _lastActionTime = DateTime.Now;
                                return MapDeviceResult.InProgress;
                            }
                            _invOpenAttempts = 0;

                            if (!CanAct()) return MapDeviceResult.InProgress;
                            var windowRect3 = gc.Window.GetWindowRectangle();
                            var slotRect3 = slot.GetClientRect();
                            var absPos3 = new Vector2(windowRect3.X + slotRect3.Center.X, windowRect3.Y + slotRect3.Center.Y);

                            // If map is unidentified and SoW not yet activated, use SoW first
                            var mapMods = item.GetComponent<Mods>();
                            if (mapMods?.Identified == false && !_sowActivated)
                            {
                                // Find a Scroll of Wisdom in inventory
                                var sowSlot = inv[0].Inventory.InventorySlotItems
                                    .FirstOrDefault(s => s.Item?.Path?.Contains("CurrencyIdentification", StringComparison.OrdinalIgnoreCase) == true);
                                if (sowSlot == null)
                                {
                                    Status = "[Select] Map is unidentified but no Scroll of Wisdom found";
                                    _phase = MapDevicePhase.Idle;
                                    return MapDeviceResult.Failed;
                                }
                                var sowRect = sowSlot.GetClientRect();
                                var sowPos = new Vector2(windowRect3.X + sowRect.Center.X, windowRect3.Y + sowRect.Center.Y);
                                BotInput.RightClick(sowPos);
                                _sowActivated = true;
                                _lastActionTime = DateTime.Now;
                                Status = "[Select] Activating Scroll of Wisdom to identify map";
                                return MapDeviceResult.InProgress;
                            }

                            if (_sowActivated)
                            {
                                // SoW cursor is active — left-click the map to identify it
                                BotInput.Click(absPos3);
                                _sowActivated = false;
                                _lastActionTime = DateTime.Now;
                                Status = "[Select] Identifying map with Scroll of Wisdom";
                                return MapDeviceResult.InProgress;
                            }

                            BotInput.RightClick(absPos3);
                            _mapRightClicked = true;
                            _lastActionTime = DateTime.Now;
                            Status = "[Select] Right-clicking restocked map from inventory";
                            return MapDeviceResult.InProgress;
                        }
                    }
                }

                Status = $"[Select] No matching maps in stash ({checkedCount} items checked)";
                _phase = MapDevicePhase.Idle;
                return MapDeviceResult.Failed;
            }

            // Named map: Ctrl+click inserts into the already-selected node's device slot.
            // Auto-match: Right-click once, then Ctrl+click.
            var rect = targetMap.GetClientRect();
            var windowRect = gc.Window.GetWindowRectangle();
            var clickPos = BotInput.RandomizeWithinRect(rect);
            var absPos = new Vector2(windowRect.X + clickPos.X, windowRect.Y + clickPos.Y);

            bool clicked = false;
            if (namedMapFlow)
            {
                clicked = BotInput.CtrlClick(absPos);
                if (clicked)
                {
                    _lastActionTime = DateTime.Now;
                    Status = $"[Select] Ctrl+clicking {TargetMapName} map into device";
                }
            }
            else
            {
                if (!_mapRightClicked)
                {
                    clicked = BotInput.RightClick(absPos);
                    if (clicked)
                    {
                        _mapRightClicked = true;
                        _lastActionTime = DateTime.Now;
                        Status = "[Select] Right-clicking item first";
                    }
                }
                else
                {
                    clicked = BotInput.CtrlClick(absPos);
                    if (clicked)
                    {
                        _lastActionTime = DateTime.Now;
                        Status = "[Select] Ctrl-clicking item into device";
                    }
                }
            }
            
            if (!clicked)
                return MapDeviceResult.InProgress; // gate blocked, retry next tick

            // Re-enter this phase — IsMapInDevice check will advance us
            return MapDeviceResult.InProgress;
        }

        /// <summary>
        /// Find and click the atlas node for the target map name.
        /// Canvas child layout: [0..2] = background layers, [3..N] = atlas nodes in
        /// the same order as gc.Files.AtlasNodes.EntriesList. So canvasIndex = fileIndex + 3.
        /// </summary>
        private MapDeviceResult TickSelectAtlasNode(GameController gc, Element atlas)
        {
            if (_nodeClickAttempts >= MaxClickAttempts)
            {
                Status = $"[Select] Failed to select {TargetMapName} after {MaxClickAttempts} attempts";
                _phase = MapDevicePhase.Idle;
                return MapDeviceResult.Failed;
            }

            // Find node in file list
            var nodes = gc.Files.AtlasNodes.EntriesList;
            int nodeIndex = -1;
            for (int i = 0; i < nodes.Count; i++)
            {
                if (nodes[i].Area?.Name?.Equals(TargetMapName, StringComparison.OrdinalIgnoreCase) == true)
                {
                    nodeIndex = i;
                    break;
                }
            }

            if (nodeIndex < 0)
            {
                Status = $"[Select] '{TargetMapName}' not found in AtlasNodes ({nodes.Count} entries)";
                _phase = MapDevicePhase.Idle;
                return MapDeviceResult.Failed;
            }

            var canvas = atlas.GetChildAtIndex(0);
            if (canvas == null)
            {
                Status = "[Select] Atlas canvas not found";
                return MapDeviceResult.InProgress;
            }

            // 3 background layers precede the node elements
            int canvasIndex = nodeIndex + 3;
            if (canvasIndex >= canvas.ChildCount)
            {
                Status = $"[Select] Canvas index {canvasIndex} out of range ({canvas.ChildCount} children)";
                _phase = MapDevicePhase.Idle;
                return MapDeviceResult.Failed;
            }

            var nodeElement = canvas.GetChildAtIndex(canvasIndex);
            if (nodeElement == null)
            {
                Status = $"[Select] Canvas child {canvasIndex} is null";
                return MapDeviceResult.InProgress;
            }

            var windowRect = gc.Window.GetWindowRectangle();
            var nodeRect = nodeElement.GetClientRect();
            var nodeCenter = new Vector2(nodeRect.Center.X, nodeRect.Center.Y);

            // Safe zone: avoid left/right HUD (~200px) and top/bottom edges (~80px)
            const float HudMarginX = 200f;
            const float HudMarginY = 80f;
            float safeLeft   = HudMarginX;
            float safeRight  = windowRect.Width  - HudMarginX;
            float safeTop    = HudMarginY;
            float safeBottom = windowRect.Height - HudMarginY;

            bool nodeOffScreen = nodeCenter.X < 0 || nodeCenter.X > windowRect.Width ||
                                 nodeCenter.Y < 0 || nodeCenter.Y > windowRect.Height;
            bool nodeInSafeZone = !nodeOffScreen &&
                                  nodeCenter.X >= safeLeft && nodeCenter.X <= safeRight &&
                                  nodeCenter.Y >= safeTop  && nodeCenter.Y <= safeBottom;

            if (!nodeInSafeZone)
            {
                if (_atlasDragAttempts >= 3)
                {
                    Status = $"[Select] {TargetMapName} node unreachable after {_atlasDragAttempts} drag attempts";
                    _phase = MapDevicePhase.Idle;
                    return MapDeviceResult.Failed;
                }

                if (!CanAct()) return MapDeviceResult.InProgress;

                // Drag the atlas so the node moves toward screen center.
                // We hold the mouse at the atlas center and pull toward the offset required.
                var screenCenter = new Vector2(windowRect.Width / 2f, windowRect.Height / 2f);
                // Clamp target to safe zone
                var clampedTarget = new Vector2(
                    Math.Clamp(nodeCenter.X, safeLeft, safeRight),
                    Math.Clamp(nodeCenter.Y, safeTop,  safeBottom));
                // Drag offset needed (where we want to drag FROM, i.e. atlas center)
                var dragFrom = new Vector2(windowRect.X + screenCenter.X, windowRect.Y + screenCenter.Y);
                // Drag TO moves in the opposite direction of the offset from desired to actual
                var delta = clampedTarget - nodeCenter; // how far node needs to move
                var dragTo = new Vector2(windowRect.X + screenCenter.X - delta.X,
                                         windowRect.Y + screenCenter.Y - delta.Y);

                _atlasDragAttempts++;
                Status = $"[Select] Dragging atlas toward {TargetMapName} (attempt {_atlasDragAttempts}/3)";
                BotInput.DragAtlas(dragFrom, dragTo);
                _lastActionTime = DateTime.Now;
                return MapDeviceResult.InProgress;
            }

            _atlasDragAttempts = 0; // node is in safe zone — reset drag counter
            var absPos = new Vector2(windowRect.X + nodeCenter.X, windowRect.Y + nodeCenter.Y);
            BotInput.Click(absPos);
            _lastActionTime = DateTime.Now;
            _nodeClickAttempts++;
            Status = $"[Select] Clicking {TargetMapName} node (fileIdx={nodeIndex} canvasIdx={canvasIndex}, attempt {_nodeClickAttempts})";
            return MapDeviceResult.InProgress;
        }

        // --- Phase: Insert scarabs from inventory into device ---

        private MapDeviceResult TickInsertScarabs(GameController gc)
        {
            // Done — all scarabs processed (or no scarabs)
            if (_scarabInsertIndex >= ScarabPaths.Count)
            {
                _phase = MapDevicePhase.Activate;
                _phaseStartTime = DateTime.Now;
                Status = "Scarabs inserted — activating";
                return MapDeviceResult.InProgress;
            }

            var atlas = gc.IngameState.IngameUi.Atlas;
            if (atlas?.IsVisible != true)
            {
                Status = "Atlas closed during scarab insertion";
                _phase = MapDevicePhase.Idle;
                return MapDeviceResult.Failed;
            }

            var pathSubstring = ScarabPaths[_scarabInsertIndex];

            // Look in the atlas device stash first, then fall back to player inventory
            var mapStash = atlas.GetChildFromIndices(MapStashPath);
            if (mapStash != null)
            {
                for (int i = 0; i < mapStash.ChildCount; i++)
                {
                    var item = mapStash.GetChildAtIndex(i);
                    if (item == null || item.Type != ElementType.InventoryItem) continue;
                    var entity = item.Entity;
                    if (entity?.Path?.Contains(pathSubstring, StringComparison.OrdinalIgnoreCase) != true) continue;

                    var rect = item.GetClientRect();
                    var windowRect = gc.Window.GetWindowRectangle();
                    var pos = BotInput.RandomizeWithinRect(rect);
                    var absPos = new Vector2(windowRect.X + pos.X, windowRect.Y + pos.Y);
                    if (BotInput.CtrlClick(absPos))
                    {
                        _lastActionTime = DateTime.Now;
                        _scarabInsertIndex++;
                        Status = $"[Scarab] Inserted '{pathSubstring}' from device stash ({_scarabInsertIndex}/{ScarabPaths.Count})";
                    }
                    return MapDeviceResult.InProgress;
                }
            }

            // Fall back to player inventory
            var invItems = gc.IngameState.ServerData?.PlayerInventories?[0]?.Inventory?.InventorySlotItems;
            if (invItems != null)
            {
                foreach (var slotItem in invItems)
                {
                    var entity = slotItem.Item;
                    if (entity?.Path?.Contains(pathSubstring, StringComparison.OrdinalIgnoreCase) != true) continue;

                    var windowRect2 = gc.Window.GetWindowRectangle();
                    var slotRect = slotItem.GetClientRect();
                    var absPos2 = new Vector2(windowRect2.X + slotRect.Center.X, windowRect2.Y + slotRect.Center.Y);
                    if (BotInput.CtrlClick(absPos2))
                    {
                        _lastActionTime = DateTime.Now;
                        _scarabInsertIndex++;
                        Status = $"[Scarab] Inserted '{pathSubstring}' from inventory ({_scarabInsertIndex}/{ScarabPaths.Count})";
                    }
                    return MapDeviceResult.InProgress;
                }
            }

            // Scarab not found — skip it and continue
            Status = $"[Scarab] '{pathSubstring}' not found — skipping";
            _scarabInsertIndex++;
            return MapDeviceResult.InProgress;
        }

        // --- Phase: Click activate ---

        private MapDeviceResult TickActivate(GameController gc)
        {
            var atlas = gc.IngameState.IngameUi.Atlas;
            if (atlas?.IsVisible != true)
            {
                // Atlas closed — portals may have spawned
                _phase = MapDevicePhase.WaitForPortals;
                _phaseStartTime = DateTime.Now;
                Status = "Atlas closed — waiting for portals";
                return MapDeviceResult.InProgress;
            }

            var activateBtn = atlas.GetChildFromIndices(ActivateButtonPath);
            if (activateBtn == null || !activateBtn.IsVisible)
            {
                Status = "Activate button not found";
                return MapDeviceResult.InProgress;
            }

            BotInput.ClickLabel(gc, activateBtn.GetClientRect());
            _lastActionTime = DateTime.Now;

            // Stay in Activate phase — next tick will detect atlas closing (lines above)
            // and transition to WaitForPortals only after verification
            Status = "Clicked activate — waiting for atlas to close";
            return MapDeviceResult.InProgress;
        }

        // --- Phase: Wait for portals to appear ---

        private MapDeviceResult TickWaitForPortals(GameController gc)
        {
            if ((DateTime.Now - _phaseStartTime).TotalSeconds > BasePortalWaitTimeoutSeconds + ExtraLatencySec)
            {
                Status = "Timed out waiting for portals";
                _phase = MapDevicePhase.Idle;
                return MapDeviceResult.Failed;
            }

            var portal = FindNearestPortal(gc);
            if (portal != null)
            {
                // Wait 1s after first portal appears for all 6 to spawn,
                // so we can pick the best (southmost) one
                if (!_portalFirstSeenAt.HasValue)
                {
                    _portalFirstSeenAt = DateTime.Now;
                    Status = "Portals appearing — waiting for all to spawn...";
                    return MapDeviceResult.InProgress;
                }
                if ((DateTime.Now - _portalFirstSeenAt.Value).TotalMilliseconds < 1000)
                {
                    Status = "Portals appearing — waiting for all to spawn...";
                    return MapDeviceResult.InProgress;
                }

                _phase = MapDevicePhase.EnterPortal;
                _phaseStartTime = DateTime.Now;
                _portalFirstSeenAt = null;
                Status = "Portals found — entering";
                return MapDeviceResult.InProgress;
            }

            Status = "Waiting for portals...";
            return MapDeviceResult.InProgress;
        }

        // --- Phase: Click a portal to enter the map ---

        private MapDeviceResult TickEnterPortal(GameController gc, NavigationSystem nav)
        {
            // Check if we're loading (means we entered)
            if (gc.IsLoading)
            {
                Interaction?.Cancel(gc);
                _phase = MapDevicePhase.Idle;
                _mapFilter = null;
                TargetMapName = null;
                MinMapTier = 0;
                Status = "Entering map";
                return MapDeviceResult.Succeeded;
            }

            // Check if we left hideout
            if (!gc.Area.CurrentArea.IsHideout)
            {
                Interaction?.Cancel(gc);
                _phase = MapDevicePhase.Idle;
                _mapFilter = null;
                TargetMapName = null;
                MinMapTier = 0;
                Status = "Entered map";
                return MapDeviceResult.Succeeded;
            }

            // Close UI panels that block portal clicks
            var stashBlocking = gc.IngameState.IngameUi.StashElement?.IsVisible == true;
            var invBlocking = gc.IngameState.IngameUi.InventoryPanel?.IsVisible == true;
            var atlasBlocking = gc.IngameState.IngameUi.Atlas?.IsVisible == true;
            if (stashBlocking || invBlocking || atlasBlocking)
            {
                BotInput.PressKey(Keys.Escape);
                _lastActionTime = DateTime.Now;
                Status = $"[Enter] Closing panels before portal click (stash={stashBlocking} inv={invBlocking} atlas={atlasBlocking})";
                return MapDeviceResult.InProgress;
            }

            // Delegate to InteractionSystem — handles navigate, proximity, click, verify.
            // InteractionSystem is already ticked by the mode each frame; we just
            // start the interaction and monitor IsBusy / status.
            if (Interaction != null)
            {
                if (Interaction.IsBusy)
                {
                    Status = $"[Enter] {Interaction.Status}";
                    // Area change / loading detection above handles success
                    return MapDeviceResult.InProgress;
                }

                // Interaction finished without area change — it either failed or
                // succeeded but the entity vanished (portal consumed). Check if
                // we're still in hideout — if so, retry with fresh portal reference.
                var portal = FindNearestPortal(gc);
                if (portal == null)
                {
                    Status = "Portal disappeared";
                    _phase = MapDevicePhase.Idle;
                    return MapDeviceResult.Failed;
                }

                Interaction.InteractWithEntity(portal, nav, requireProximity: true);
                Status = "[Enter] Interacting with portal";
                return MapDeviceResult.InProgress;
            }

            // Fallback if Interaction not wired (shouldn't happen)
            Status = "[Enter] No InteractionSystem available";
            _phase = MapDevicePhase.Idle;
            return MapDeviceResult.Failed;
        }

        // --- Helpers ---

        private Entity? FindMapDevice(GameController gc)
        {
            Entity? fallback = null;

            try
            {
                foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
                {
                    if (!entity.IsTargetable)
                        continue;

                    // Primary: RenderName exactly "Map Device" — works for standard and variant devices
                    // (variant decorative piece is "Map Device 1", so exact match avoids it)
                    if (entity.RenderName == "Map Device")
                        return entity;

                    // Fallback: standard map device by path (legacy detection)
                    if (fallback == null && entity.Type == EntityType.IngameIcon &&
                        entity.Path != null && entity.Path.Contains("MappingDevice"))
                        fallback = entity;
                }
            }
            catch (IndexOutOfRangeException)
            {
                // ExileCore entity component dictionary can race during entity load/unload — retry next tick
                return null;
            }

            return fallback;
        }

        private Entity? FindNearestPortal(GameController gc)
        {
            // Prefer lowest grid Y (south on screen / behind map device in isometric view).
            Entity? best = null;
            float bestY = float.MaxValue;

            foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
            {
                if (entity.Type != EntityType.TownPortal)
                    continue;
                if (!entity.IsTargetable)
                    continue;
                if (entity.GridPosNum.Y < bestY)
                {
                    bestY = entity.GridPosNum.Y;
                    best = entity;
                }
            }
            return best;
        }

        // --- Phase: Read device storage slot counts then close atlas ---

        /// <summary>
        /// Ensures the map device storage panel is expanded (visible).
        /// The panel can be collapsed via a left-side toggle button (atlas[3]).
        /// When collapsed, MapDeviceStorage.IsVisible == false and its rect is off-screen left.
        /// Returns true if storage is already visible; false if the toggle was clicked (wait next tick).
        /// </summary>
        private bool EnsureStorageVisible(GameController gc)
        {
            var atlas = gc.IngameState.IngameUi.Atlas;
            var atlasPanel = atlas as ExileCore.PoEMemory.Elements.AtlasPanel;
            var deviceStorage = atlasPanel?.MapDeviceStorage;
            if (deviceStorage == null || deviceStorage.IsVisible)
                return true; // already visible (or no storage panel on this device)

            // Storage is minimized — click the toggle strip (atlas[3]) to expand it.
            // atlas[3] is the narrow visible strip on the left side; its GetClientRect() is correct.
            var toggleEl = atlas?.GetChildAtIndex(3);
            if (toggleEl == null || !toggleEl.IsVisible)
                return true; // no toggle found — assume no storage, proceed

            if (!CanAct()) return false;
            var windowRect = gc.Window.GetWindowRectangle();
            var toggleRect = toggleEl.GetClientRect();
            var clickPos = new Vector2(
                windowRect.X + toggleRect.Center.X,
                windowRect.Y + toggleRect.Center.Y);
            BotInput.Click(clickPos);
            _lastActionTime = DateTime.Now;
            Status = "[Storage] Clicking toggle to expand map device storage panel";
            return false;
        }

        private MapDeviceResult TickCheckStorage(GameController gc)
        {
            var atlas = gc.IngameState.IngameUi.Atlas;

            // If atlas is closed:
            if (atlas?.IsVisible != true)
            {
                if (StorageTotalSlots >= 0)
                {
                    // Read succeeded and atlas is now closed (normal close path)
                    _phase = MapDevicePhase.Idle;
                    Status = $"Storage checked: {StorageFilledSlots}/{StorageTotalSlots} filled, {StorageEmptySlots} empty";
                    return MapDeviceResult.Succeeded;
                }
                Status = "Atlas closed before storage could be read";
                _phase = MapDevicePhase.Idle;
                return MapDeviceResult.Failed;
            }

            // Expand storage panel if it is currently minimized
            if (!EnsureStorageVisible(gc))
                return MapDeviceResult.InProgress;

            // Read values from atlas storage panel
            var atlasPanel = atlas as ExileCore.PoEMemory.Elements.AtlasPanel;
            var sinv = atlasPanel?.MapDeviceStorage?.ServerInventory;
            if (sinv != null)
            {
                StorageTotalSlots = sinv.Columns * sinv.Rows;
                StorageFilledSlots = sinv.InventorySlotItems?.Count ?? 0;
            }

            // If a clean filter was pre-loaded by StartCheckStorage, pipeline directly into
            // CleanStorage — atlas stays open, saving one full device navigate+open cycle.
            if (_cleanStorageFilter != null)
            {
                _phase = MapDevicePhase.CleanStorage;
                _phaseStartTime = DateTime.Now;
                Status = $"Storage read ({StorageFilledSlots}/{StorageTotalSlots}) — pipelining into clean";
                return MapDeviceResult.InProgress;
            }

            // No clean needed — close atlas and report success
            BotInput.PressKey(Keys.Escape);
            _lastActionTime = DateTime.Now;
            Status = $"Storage read ({StorageFilledSlots}/{StorageTotalSlots}) — waiting for atlas to close";
            return MapDeviceResult.InProgress;
        }

        // --- Phase: Ctrl+click matching inventory items into device storage ---

        private MapDeviceResult TickStockStorage(GameController gc)
        {
            var atlas = gc.IngameState.IngameUi.Atlas;
            if (atlas?.IsVisible != true)
            {
                Status = "Atlas closed during StockStorage";
                _phase = MapDevicePhase.Idle;
                return MapDeviceResult.Failed;
            }

            if (string.IsNullOrEmpty(_stockItemPath))
            {
                _phase = MapDevicePhase.Idle;
                return MapDeviceResult.Succeeded;
            }

            // Done when we've deposited all we wanted, or storage is full
            if (_stockItemsDeposited >= _stockMaxItems || StorageEmptySlots == 0)
            {
                BotInput.PressKey(Keys.Escape);
                _lastActionTime = DateTime.Now;
                _phase = MapDevicePhase.Idle;
                Status = $"Stocked {_stockItemsDeposited} items into device storage";
                _stockItemPath = null;
                return MapDeviceResult.Succeeded;
            }

            // Ensure inventory panel is open — ctrl-click positions require it to be visible
            var invPanelStock = gc.IngameState.IngameUi.InventoryPanel;
            if (invPanelStock == null || !invPanelStock.IsVisible)
            {
                if (!CanAct()) return MapDeviceResult.InProgress;
                _invOpenAttempts++;
                if (_invOpenAttempts > MaxInvOpenAttempts)
                {
                    Status = "[StockStorage] Failed to open inventory panel";
                    BotInput.PressKey(Keys.Escape);
                    _lastActionTime = DateTime.Now;
                    _phase = MapDevicePhase.Idle;
                    return MapDeviceResult.Failed;
                }
                BotInput.PressKey(Keys.I);
                _lastActionTime = DateTime.Now;
                Status = $"[StockStorage] Opening inventory (attempt {_invOpenAttempts})...";
                return MapDeviceResult.InProgress;
            }

            // Find matching item in player inventory and Ctrl+click it into storage
            var invItems = gc.IngameState.ServerData?.PlayerInventories?[0]?.Inventory?.InventorySlotItems;
            if (invItems != null)
            {
                foreach (var slotItem in invItems)
                {
                    var entity = slotItem.Item;
                    if (entity?.Path?.Contains(_stockItemPath, StringComparison.OrdinalIgnoreCase) != true)
                        continue;

                    // Tier filter for maps
                    if (_stockItemMinTier > 0)
                    {
                        if (!entity.TryGetComponent<MapKey>(out var mk) || mk.Tier < _stockItemMinTier)
                            continue;
                    }

                    var windowRect = gc.Window.GetWindowRectangle();
                    var slotRect = slotItem.GetClientRect();
                    var absPos = new Vector2(windowRect.X + slotRect.Center.X, windowRect.Y + slotRect.Center.Y);
                    if (BotInput.CtrlClick(absPos))
                    {
                        _stockItemsDeposited++;
                        StorageFilledSlots++;
                        _lastActionTime = DateTime.Now;
                        Status = $"Stocked item {_stockItemsDeposited}/{_stockMaxItems} into storage";
                    }
                    return MapDeviceResult.InProgress;
                }
            }

            // No more matching items in inventory — done
            BotInput.PressKey(Keys.Escape);
            _lastActionTime = DateTime.Now;
            _phase = MapDevicePhase.Idle;
            Status = $"No more items to stock ({_stockItemsDeposited} deposited)";
            _stockItemPath = null;
            return MapDeviceResult.Succeeded;
        }

        // --- Phase: Ctrl+click unexpected items from device storage back into inventory ---

        private MapDeviceResult TickCleanStorage(GameController gc)
        {
            var atlas = gc.IngameState.IngameUi.Atlas;
            if (atlas?.IsVisible != true)
            {
                Status = "Atlas closed during CleanStorage";
                _phase = MapDevicePhase.Idle;
                return MapDeviceResult.Failed;
            }

            // Expand storage panel if it is currently minimized
            if (!EnsureStorageVisible(gc))
                return MapDeviceResult.InProgress;

            // Ensure inventory panel is open — ctrl-click positions require it to be visible
            var invPanelStock = gc.IngameState.IngameUi.InventoryPanel;
            if (invPanelStock == null || !invPanelStock.IsVisible)
            {
                if (!CanAct()) return MapDeviceResult.InProgress;
                _invOpenAttempts++;
                if (_invOpenAttempts > MaxInvOpenAttempts)
                {
                    Status = "[CleanStorage] Failed to open inventory panel";
                    BotInput.PressKey(System.Windows.Forms.Keys.Escape);
                    _lastActionTime = DateTime.Now;
                    _phase = MapDevicePhase.Idle;
                    return MapDeviceResult.Failed;
                }
                BotInput.PressKey(System.Windows.Forms.Keys.I);
                _lastActionTime = DateTime.Now;
                Status = $"[CleanStorage] Opening inventory (attempt {_invOpenAttempts})...";
                return MapDeviceResult.InProgress;
            }

            var atlasPanel = atlas as ExileCore.PoEMemory.Elements.AtlasPanel;
            var sinv = atlasPanel?.MapDeviceStorage?.ServerInventory;

            if (sinv == null || sinv.InventorySlotItems == null)
            {
                // No device storage panel (e.g. boss encounters). Close the atlas and succeed —
                // there is nothing to clean. Do not return Failed here or _cleanStorageFilter
                // would persist and corrupt the next MapDeviceSystem.Start() call.
                BotInput.PressKey(Keys.Escape);
                _lastActionTime = DateTime.Now;
                _cleanStorageFilter = null;
                _phase = MapDevicePhase.Idle;
                Status = "No device storage panel — skipping clean";
                return MapDeviceResult.Succeeded;
            }

            // Find an item that fails the filter.
            // Use VisibleInventoryItems on the storage element — these are real UI items
            // with valid GetClientRect(), like stash items. ServerInventory slot items
            // have no screen position and DeviceSlotsPath only covers the 6 activation slots.
            var atlasPanel2 = atlas as ExileCore.PoEMemory.Elements.AtlasPanel;
            var storageUiItems = atlasPanel2?.MapDeviceStorage?.VisibleInventoryItems;
            if (storageUiItems == null || storageUiItems.Count == 0)
            {
                // No UI items visible — storage may be empty, treat as done
                BotInput.PressKey(System.Windows.Forms.Keys.Escape);
                _lastActionTime = DateTime.Now;
                _phase = MapDevicePhase.Idle;
                _cleanStorageFilter = null;
                Status = $"CleanStorage done ({_cleanStorageClicks} items removed)";
                return MapDeviceResult.Succeeded;
            }

            foreach (var uiItem in storageUiItems)
            {
                var entity = uiItem?.Item;
                if (entity == null) continue;

                if (_cleanStorageFilter != null && !_cleanStorageFilter(entity))
                {
                    var windowRect = gc.Window.GetWindowRectangle();
                    var slotRect = uiItem.GetClientRect();
                    var absPos = new Vector2(windowRect.X + slotRect.Center.X, windowRect.Y + slotRect.Center.Y);
                    if (BotInput.CtrlClick(absPos))
                    {
                        _cleanStorageClicks++;
                        if (StorageFilledSlots > 0) StorageFilledSlots--;
                        _lastActionTime = DateTime.Now;
                        Status = $"CleanStorage: removed invalid item #{_cleanStorageClicks}";
                    }
                    return MapDeviceResult.InProgress;
                }
            }

            // No invalid items left
            BotInput.PressKey(System.Windows.Forms.Keys.Escape);
            _lastActionTime = DateTime.Now;
            _phase = MapDevicePhase.Idle;
            Status = $"CleanStorage done ({_cleanStorageClicks} items removed)";
            _cleanStorageFilter = null;
            return MapDeviceResult.Succeeded;
        }

        private bool IsMapInDevice(Element atlas)
        {
            var slots = atlas.GetChildFromIndices(DeviceSlotsPath);
            if (slots == null) return false;

            // Slot 0 has ChildCount==2 when occupied (child[1] is the InventoryItem)
            var slot0 = slots.GetChildAtIndex(0);
            return slot0 != null && slot0.ChildCount >= 2;
        }

        // --- Static map filter helpers ---

        /// <summary>
        /// Filter for blighted maps (has InfectedMap mod, NOT UberInfectedMap).
        /// </summary>
        public static bool IsBlightedMap(Element item)
        {
            var entity = item.Entity;
            if (entity == null) return false;
            if (!entity.Path?.Contains("Maps/") == true) return false;
            if (!entity.TryGetComponent<Mods>(out var mods)) return false;
            return mods.ItemMods?.Any(m => m.RawName == "InfectedMap") == true;
        }

        /// <summary>
        /// Filter for blight-ravaged maps (has UberInfectedMap mod).
        /// </summary>
        public static bool IsBlightRavagedMap(Element item)
        {
            var entity = item.Entity;
            if (entity == null) return false;
            if (!entity.Path?.Contains("Maps/") == true) return false;
            if (!entity.TryGetComponent<Mods>(out var mods)) return false;
            return mods.ItemMods?.Any(m => m.RawName.StartsWith("UberInfectedMap")) == true;
        }

        /// <summary>
        /// Filter for any blighted or blight-ravaged map.
        /// </summary>
        public static bool IsAnyBlightMap(Element item)
        {
            return IsBlightedMap(item) || IsBlightRavagedMap(item);
        }

        /// <summary>
        /// Filter for simulacrum fragments.
        /// </summary>
        public static bool IsSimulacrum(Element item)
        {
            var entity = item.Entity;
            if (entity == null) return false;
            return entity.Path?.EndsWith("CurrencyAfflictionFragment") == true;
        }

        /// <summary>
        /// Filter for any standard map (not blighted, not simulacrum).
        /// </summary>
        public static bool IsStandardMap(Element item)
        {
            var entity = item.Entity;
            if (entity == null) return false;
            if (!entity.Path?.Contains("Maps/MapKey") == true) return false;
            if (!entity.TryGetComponent<Mods>(out var mods)) return true; // no mods = normal map
            var modNames = mods.ItemMods;
            if (modNames == null) return true;
            return !modNames.Any(m => m.RawName == "InfectedMap" || m.RawName.StartsWith("UberInfectedMap"));
        }
    }

    public enum MapDevicePhase
    {
        Idle,
        NavigateToDevice,
        OpenDevice,
        CheckStorage,   // open atlas, read storage state, close atlas
        CleanStorage,   // open atlas, ctrl+click mismatched items to inventory
        StockStorage,   // open atlas, ctrl+click matching inventory items into storage
        SelectMap,
        InsertScarabs,
        Activate,
        WaitForPortals,
        EnterPortal,
    }

    public enum MapDeviceResult
    {
        None,
        InProgress,
        Succeeded,
        Failed,
    }
}