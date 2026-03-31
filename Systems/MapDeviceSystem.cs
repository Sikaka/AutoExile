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

        // Atlas node selection state
        private bool _nodeSelected;
        private int _nodeClickAttempts;

        // Inventory fragment fallback
        private int _invOpenAttempts;
        private const int MaxInvOpenAttempts = 5;

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
            _phase = MapDevicePhase.NavigateToDevice;
            _phaseStartTime = DateTime.Now;
            _lastActionTime = DateTime.MinValue;
            _navAttempts = 0;
            _bestDistSeen = float.MaxValue;
            _nodeSelected = false;
            _nodeClickAttempts = 0;
            _invOpenAttempts = 0;
            _portalFirstSeenAt = null;
            Status = "Starting map creation";
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
                MapDevicePhase.SelectMap => TickSelectMap(gc),
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
                _phase = MapDevicePhase.SelectMap;
                _phaseStartTime = DateTime.Now;
                Status = "Atlas opened — selecting map";
                return MapDeviceResult.InProgress;
            }

            var device = FindMapDevice(gc);
            if (device == null)
            {
                Status = "Map device disappeared";
                _phase = MapDevicePhase.Idle;
                return MapDeviceResult.Failed;
            }

            // Click the device — bounds-based randomization to avoid overlapping entities
            if (!BotInput.ClickEntity(gc, device))
            {
                Status = "[Open] Device off screen or gate blocked";
                return MapDeviceResult.InProgress;
            }
            _lastActionTime = DateTime.Now;
            Status = "[Open] Clicking device";
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
                _phase = MapDevicePhase.Activate;
                _phaseStartTime = DateTime.Now;
                Status = "Map already in device — activating";
                return MapDeviceResult.InProgress;
            }

            // Two distinct flows:
            // A) Named map (mapping mode): must select atlas node first → device panel opens → Ctrl+click map
            // B) Auto-match (blight/simulacrum): right-click map in stash → game handles node selection + insertion
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

                // Inventory is open — find and right-click a matching fragment
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
                        BotInput.RightClick(absPos2);
                        _lastActionTime = DateTime.Now;
                        Status = "[Select] Right-clicking fragment from inventory";
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
                Status = $"[Select] No matching maps in stash ({checkedCount} items checked)";
                _phase = MapDevicePhase.Idle;
                return MapDeviceResult.Failed;
            }

            // Named map: Ctrl+click inserts into the already-selected node's device slot.
            // Auto-match: Right-click auto-selects the correct atlas node AND inserts.
            var rect = targetMap.GetClientRect();
            var windowRect = gc.Window.GetWindowRectangle();
            var clickPos = BotInput.RandomizeWithinRect(rect);
            var absPos = new Vector2(windowRect.X + clickPos.X, windowRect.Y + clickPos.Y);

            bool clicked = namedMapFlow
                ? BotInput.CtrlClick(absPos)
                : BotInput.RightClick(absPos);
            if (!clicked)
                return MapDeviceResult.InProgress; // gate blocked, retry next tick

            _lastActionTime = DateTime.Now;
            Status = namedMapFlow
                ? $"[Select] Ctrl+clicking {TargetMapName} map into device"
                : "[Select] Right-clicking map into device";

            // Re-enter this phase — IsMapInDevice check will advance us
            return MapDeviceResult.InProgress;
        }

        /// <summary>
        /// Find and click the atlas node for the target map name.
        /// Uses AtlasNodes file index + 2 to find the UI element.
        /// </summary>
        private MapDeviceResult TickSelectAtlasNode(GameController gc, Element atlas)
        {
            if (_nodeClickAttempts >= MaxClickAttempts)
            {
                Status = $"[Select] Failed to select {TargetMapName} after {MaxClickAttempts} attempts";
                _phase = MapDevicePhase.Idle;
                return MapDeviceResult.Failed;
            }

            // Find the atlas node index for the target map name
            var nodes = gc.Files?.AtlasNodes?.EntriesList;
            if (nodes == null || nodes.Count == 0)
            {
                Status = "[Select] AtlasNodes not loaded";
                return MapDeviceResult.InProgress;
            }

            int nodeIndex = -1;
            for (int i = 0; i < Math.Min(nodes.Count, 110); i++)
            {
                var name = nodes[i].Area?.Name;
                if (name != null && name.Equals(TargetMapName, StringComparison.OrdinalIgnoreCase))
                {
                    nodeIndex = i;
                    break;
                }
            }

            if (nodeIndex < 0)
            {
                Status = $"[Select] Map '{TargetMapName}' not found in AtlasNodes";
                _phase = MapDevicePhase.Idle;
                return MapDeviceResult.Failed;
            }

            // UI element index = file index + 2
            var canvas = atlas.GetChildAtIndex(0);
            if (canvas == null)
            {
                Status = "[Select] Atlas canvas not found";
                return MapDeviceResult.InProgress;
            }

            var uiIndex = nodeIndex + 2;
            if (uiIndex >= canvas.ChildCount)
            {
                Status = $"[Select] Atlas node UI index {uiIndex} out of range ({canvas.ChildCount} children)";
                _phase = MapDevicePhase.Idle;
                return MapDeviceResult.Failed;
            }

            var nodeElement = canvas.GetChildAtIndex(uiIndex);
            if (nodeElement == null)
            {
                Status = $"[Select] Atlas node element is null at index {uiIndex}";
                return MapDeviceResult.InProgress;
            }

            // Check if node is on screen
            var nodeRect = nodeElement.GetClientRect();
            var nodeCenter = new Vector2(nodeRect.Center.X, nodeRect.Center.Y);
            var windowRect = gc.Window.GetWindowRectangle();

            if (nodeCenter.X < 0 || nodeCenter.X > windowRect.Width ||
                nodeCenter.Y < 0 || nodeCenter.Y > windowRect.Height)
            {
                Status = $"[Select] {TargetMapName} node is off-screen — center atlas on the map first";
                _phase = MapDevicePhase.Idle;
                return MapDeviceResult.Failed;
            }

            var absPos = new Vector2(windowRect.X + nodeCenter.X, windowRect.Y + nodeCenter.Y);
            BotInput.Click(absPos);
            _lastActionTime = DateTime.Now;
            _nodeClickAttempts++;
            Status = $"[Select] Clicking {TargetMapName} node (attempt {_nodeClickAttempts})";
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
            if (stashBlocking || invBlocking)
            {
                BotInput.PressKey(Keys.Escape);
                _lastActionTime = DateTime.Now;
                Status = $"[Enter] Closing panels before portal click (stash={stashBlocking} inv={invBlocking})";
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
        SelectMap,
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
