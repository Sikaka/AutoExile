using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Windows.Forms;
using ExileCore;
using ExileCore.PoEMemory;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.Elements;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using ImGuiNET;

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
        private string? _inventoryFragmentPath;
        private const float ActionCooldownMs = 400;
        private const float BasePhaseTimeoutSeconds = 30f;
        private const float BasePortalWaitTimeoutSeconds = 10f;

        public float ExtraLatencySec { get; set; }
        public int MaxClickAttempts { get; set; } = 5;
        public float InteractRadius { get; set; } = 20f;
        public InteractionSystem? Interaction { get; set; }
        public string? TargetMapName { get; set; }
        public int MinMapTier { get; set; }
        public bool ForceCtrlClick { get; set; }
        public IReadOnlyList<string>? ScarabPaths { get; set; }

        private int _navAttempts;
        private float _bestDistSeen = float.MaxValue;
        private bool _nodeSelected;
        private int _nodeClickAttempts;
        private int _invOpenAttempts;
        private const int MaxInvOpenAttempts = 5;
        private const int InsertSettleMs = 800;
        private DateTime? _portalFirstSeenAt;

        private bool CanAct() =>
            BotInput.CanAct && (DateTime.Now - _lastActionTime).TotalMilliseconds >= ActionCooldownMs;

        private static readonly int[] MapStashPath = { 3, 0, 1 };
        private static readonly int[] DeviceSlotsPath = { 7, 0, 2 };
        private static readonly int[] ActivateButtonPath = { 7, 0, 3 };
        private static readonly int[] MapNameTextPath = { 7, 0, 1, 0, 0 };

        public MapDevicePhase Phase => _phase;
        public string Status { get; private set; } = "";
        public bool IsBusy => _phase != MapDevicePhase.Idle;

        public bool Start(Func<Element, bool> mapFilter, string? inventoryFragmentPath = null,
            IReadOnlyList<string>? scarabPaths = null)
        {
            if (_phase != MapDevicePhase.Idle)
                return false;

            _mapFilter = mapFilter;
            _inventoryFragmentPath = inventoryFragmentPath;
            ScarabPaths = scarabPaths;
            _phase = MapDevicePhase.NavigateToDevice;
            _phaseStartTime = DateTime.Now;
            _lastActionTime = DateTime.MinValue;
            _navAttempts = 0;
            _bestDistSeen = float.MaxValue;
            _nodeSelected = false;
            _nodeClickAttempts = 0;
            _invOpenAttempts = 0;
            _portalFirstSeenAt = null;

            if (string.IsNullOrEmpty(TargetMapName))
            {
                if (_mapFilter == IsBlightRavagedMap || _inventoryFragmentPath == StashSystem.BlightRavagedMapIdentifier ||
                    _mapFilter == IsBlightedMap || _inventoryFragmentPath == StashSystem.BlightMapIdentifier)
                {
                    TargetMapName = "Blighted Lands";
                }
            }

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
            ScarabPaths = null;
            TargetMapName = null;
            MinMapTier = 0;
            Status = "Cancelled";
        }

        public MapDeviceResult Tick(GameController gc, NavigationSystem nav)
        {
            if (_phase == MapDevicePhase.Idle)
                return MapDeviceResult.None;

            var phaseElapsed = (DateTime.Now - _phaseStartTime).TotalSeconds;

            if (phaseElapsed > BasePhaseTimeoutSeconds + ExtraLatencySec
                && _phase != MapDevicePhase.WaitForPortals)
            {
                Status = $"TIMEOUT after {phaseElapsed:F0}s in {_phase} — last status: {Status}";
                _phase = MapDevicePhase.Idle;
                return MapDeviceResult.Failed;
            }

            if ((DateTime.Now - _lastActionTime).TotalMilliseconds < ActionCooldownMs)
                return MapDeviceResult.InProgress;

            return _phase switch
            {
                MapDevicePhase.NavigateToDevice => TickNavigateToDevice(gc, nav),
                MapDevicePhase.OpenDevice => TickOpenDevice(gc),
                MapDevicePhase.SelectMap => TickSelectMap(gc),
                MapDevicePhase.InsertScarabs => TickInsertScarabs(gc),
                MapDevicePhase.Activate => TickActivate(gc),
                MapDevicePhase.WaitForPortals => TickWaitForPortals(gc),
                MapDevicePhase.EnterPortal => TickEnterPortal(gc, nav),
                _ => MapDeviceResult.InProgress
            };
        }

        private MapDeviceResult TickNavigateToDevice(GameController gc, NavigationSystem nav)
        {
            var stashVisible = gc.IngameState.IngameUi.StashElement?.IsVisible == true;
            var invVisible = gc.IngameState.IngameUi.InventoryPanel?.IsVisible == true;
            if (stashVisible || invVisible)
            {
                var sent = BotInput.PressKey(Keys.Escape);
                _lastActionTime = DateTime.Now;
                Status = $"[Nav] Closing panels (stash={stashVisible} inv={invVisible})";
                return MapDeviceResult.InProgress;
            }

            var device = FindMapDevice(gc);
            if (device == null)
            {
                if ((DateTime.Now - _phaseStartTime).TotalSeconds < 3)
                {
                    Status = $"[Nav] Searching for map device ({(DateTime.Now - _phaseStartTime).TotalSeconds:F0}s)...";
                    return MapDeviceResult.InProgress;
                }
                Status = "[Nav] Map device entity not found in entity list";
                _phase = MapDevicePhase.Idle;
                return MapDeviceResult.Failed;
            }

            var atlas = gc.IngameState.IngameUi.Atlas;
            if (atlas?.IsVisible == true)
            {
                _phase = MapDevicePhase.SelectMap;
                _phaseStartTime = DateTime.Now;
                Status = "Atlas already open — selecting map";
                return MapDeviceResult.InProgress;
            }

            var playerGrid = gc.Player.GridPosNum;
            var deviceGrid = device.GridPosNum;
            var dist = Vector2.Distance(
                new Vector2(playerGrid.X, playerGrid.Y),
                new Vector2(deviceGrid.X, deviceGrid.Y));

            if (dist < _bestDistSeen)
                _bestDistSeen = dist;

            if (dist < InteractRadius)
            {
                nav.Stop(gc);
                _phase = MapDevicePhase.OpenDevice;
                _phaseStartTime = DateTime.Now;
                Status = "Near device — opening";
                return MapDeviceResult.InProgress;
            }

            if (!nav.IsNavigating)
            {
                _navAttempts++;

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
                    if (gc.Area.CurrentArea.IsHideout && BotInput.CanAct)
                    {
                        var screenPos = gc.IngameState.Camera.WorldToScreen(device.BoundsCenterPosNum);
                        var windowRect = gc.Window.GetWindowRectangle();
                        var absPos = new Vector2(windowRect.X + screenPos.X, windowRect.Y + screenPos.Y);
                        if (BotInput.IsMovementActive && !BotInput.IsMovementSuspended)
                            BotInput.UpdateMovementCursor(absPos);
                        else
                            BotInput.StartMovement(absPos, nav.MoveKey);
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

            if (!BotInput.ClickEntity(gc, device))
            {
                Status = "[Open] Device off screen or gate blocked";
                return MapDeviceResult.InProgress;
            }
            _lastActionTime = DateTime.Now;
            Status = "[Open] Clicking device";
            return MapDeviceResult.InProgress;
        }

        private MapDeviceResult TickSelectMap(GameController gc)
        {
            var atlas = gc.IngameState.IngameUi.Atlas;
            var invPanel = gc.IngameState.IngameUi.InventoryPanel;

            bool isSimulacrum = (_inventoryFragmentPath != null && _inventoryFragmentPath.Contains("CurrencyAfflictionFragment", StringComparison.OrdinalIgnoreCase))
                || _mapFilter == IsSimulacrum;

            // ── Dedicated Simulacrum Flow: Right-click directly from inventory ──
            if (isSimulacrum)
            {
                if (!CanAct()) return MapDeviceResult.InProgress;

                // Ensure inventory panel is open
                if (invPanel == null || !invPanel.IsVisible)
                {
                    if (_invOpenAttempts > MaxInvOpenAttempts)
                    {
                        Status = "[Simulacrum] Failed to open inventory panel";
                        _phase = MapDevicePhase.Idle;
                        return MapDeviceResult.Failed;
                    }

                    if (BotInput.PressKey(Keys.I))
                    {
                        _invOpenAttempts++;
                        _lastActionTime = DateTime.Now;
                        Status = $"[Simulacrum] Opening inventory (attempt {_invOpenAttempts})...";
                    }
                    return MapDeviceResult.InProgress;
                }

                var invItems = gc.IngameState.ServerData?.PlayerInventories?[0]?.Inventory?.InventorySlotItems;
                if (invItems != null)
                {
                    foreach (var slotItem in invItems)
                    {
                        var item = slotItem.Item;
                        if (item?.Path == null) continue;

                        if (item.Path.Contains("CurrencyAfflictionFragment", StringComparison.OrdinalIgnoreCase))
                        {
                            var windowRect = gc.Window.GetWindowRectangle();
                            var slotRect = slotItem.GetClientRect();
                            var absPos = new Vector2(windowRect.X + slotRect.Center.X, windowRect.Y + slotRect.Center.Y);

                            if (BotInput.RightClick(absPos))
                            {
                                _lastActionTime = DateTime.Now;
                                _phase = MapDevicePhase.WaitForPortals;
                                _phaseStartTime = DateTime.Now;
                                Status = "[Simulacrum] Right-clicked Simulacrum in inventory — waiting for portals";
                                return MapDeviceResult.InProgress;
                            }
                        }
                    }
                }

                Status = "[Simulacrum] No Simulacrum found in inventory to right-click";
                _phase = MapDevicePhase.Idle;
                return MapDeviceResult.Failed;
            }

            // ── Standard Map / Blight Flow ──
            if (atlas?.IsVisible != true)
            {
                Status = "Atlas closed unexpectedly";
                _phase = MapDevicePhase.Idle;
                return MapDeviceResult.Failed;
            }

            if (IsActivateButtonReady(atlas))
            {
                _phase = NextPhaseAfterMapLoaded();
                _phaseStartTime = DateTime.Now;
                Status = "Activate button ready — " + (_phase == MapDevicePhase.InsertScarabs
                    ? "inserting scarabs"
                    : "going straight to activate");
                return MapDeviceResult.InProgress;
            }

            if (IsMapInDevice(atlas))
            {
                _phase = NextPhaseAfterMapLoaded();
                _phaseStartTime = DateTime.Now;
                Status = _phase == MapDevicePhase.InsertScarabs
                    ? "Map in device — inserting scarabs"
                    : "Map already in device — activating";
                return MapDeviceResult.InProgress;
            }

            if ((DateTime.Now - _lastActionTime).TotalMilliseconds < InsertSettleMs)
            {
                Status = $"[Select] Waiting {InsertSettleMs}ms for device to update after click";
                return MapDeviceResult.InProgress;
            }

            bool namedMapFlow = !string.IsNullOrEmpty(TargetMapName);

            if (!namedMapFlow && ForceCtrlClick)
            {
                Status = "[Select] No map name configured — select a map in farming settings";
                _phase = MapDevicePhase.Idle;
                return MapDeviceResult.Failed;
            }

            if (namedMapFlow)
            {
                var devicePanel = atlas.GetChildAtIndex(7);
                bool devicePanelVisible = devicePanel?.IsVisible == true;

                if (!devicePanelVisible)
                {
                    return TickSelectAtlasNode(gc, atlas);
                }

                if (!_nodeSelected)
                {
                    var nameEl = atlas.GetChildFromIndices(MapNameTextPath);
                    var expectedName = StripMapPrefix(TargetMapName);
                    if (nameEl?.Text != null &&
                        (nameEl.Text.Equals(expectedName, StringComparison.OrdinalIgnoreCase) ||
                         nameEl.Text.Contains("Blight", StringComparison.OrdinalIgnoreCase)))
                    {
                        _nodeSelected = true;
                        Status = $"[Select] {TargetMapName} confirmed selected";
                    }
                    else
                    {
                        if ((DateTime.Now - _lastActionTime).TotalSeconds < 2.0)
                        {
                            Status = $"[Select] Device panel open, waiting for name to update (got: {nameEl?.Text ?? "null"})";
                            return MapDeviceResult.InProgress;
                        }
                        return TickSelectAtlasNode(gc, atlas);
                    }
                }
            }

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

            if (targetMap == null && (_inventoryFragmentPath != null || _mapFilter != null))
            {
                if (!CanAct()) return MapDeviceResult.InProgress;

                if (invPanel == null || !invPanel.IsVisible)
                {
                    if (namedMapFlow && _invOpenAttempts >= 2)
                    {
                        return TickSelectAtlasNode(gc, atlas);
                    }

                    if (_invOpenAttempts > MaxInvOpenAttempts)
                    {
                        Status = "[Select] Failed to open inventory panel";
                        _phase = MapDevicePhase.Idle;
                        return MapDeviceResult.Failed;
                    }

                    if (BotInput.PressKey(Keys.I))
                    {
                        _invOpenAttempts++;
                        _lastActionTime = DateTime.Now;
                        Status = $"[Select] Opening inventory (attempt {_invOpenAttempts})...";
                    }
                    return MapDeviceResult.InProgress;
                }

                bool foundAny = false;
                var invItems = gc.IngameState.ServerData?.PlayerInventories?[0]?.Inventory?.InventorySlotItems;
                if (invItems != null)
                {
                    foreach (var slotItem in invItems)
                    {
                        var item = slotItem.Item;
                        if (item == null) continue;

                        bool match = false;
                        if (_inventoryFragmentPath == StashSystem.BlightMapIdentifier || _mapFilter == IsBlightedMap)
                        {
                            match = StashSystem.IsBlightMapEntity(item, ravagedOnly: false);
                        }
                        else if (_inventoryFragmentPath == StashSystem.BlightRavagedMapIdentifier || _mapFilter == IsBlightRavagedMap)
                        {
                            match = StashSystem.IsBlightMapEntity(item, ravagedOnly: true);
                        }
                        else if (_inventoryFragmentPath != null && item.Path != null)
                        {
                            match = item.Path.Contains(_inventoryFragmentPath, StringComparison.OrdinalIgnoreCase);
                        }

                        if (!match) continue;

                        foundAny = true;
                        var windowRect2 = gc.Window.GetWindowRectangle();
                        var slotRect = slotItem.GetClientRect();
                        var absPos2 = new Vector2(windowRect2.X + slotRect.Center.X,
                            windowRect2.Y + slotRect.Center.Y);

                        bool inserted = namedMapFlow
                            ? BotInput.CtrlClick(absPos2)
                            : BotInput.RightClick(absPos2);
                        if (inserted)
                        {
                            _lastActionTime = DateTime.Now;
                            Status = namedMapFlow
                                ? $"[Select] Ctrl+clicking inventory map into {TargetMapName} slot"
                                : "[Select] Right-clicking map/fragment from inventory";
                        }
                        return MapDeviceResult.InProgress;
                    }
                }

                if (!foundAny && _inventoryFragmentPath != null)
                {
                    Status = $"[Select] No matching map in stash or inventory";
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

            var rect = targetMap.GetClientRect();
            var windowRect3 = gc.Window.GetWindowRectangle();
            var clickPos = BotInput.RandomizeWithinRect(rect);
            var absPos3 = new Vector2(windowRect3.X + clickPos.X, windowRect3.Y + clickPos.Y);

            bool useCtrlClick = namedMapFlow || ForceCtrlClick;
            bool clicked = useCtrlClick
                ? BotInput.CtrlClick(absPos3)
                : BotInput.RightClick(absPos3);
            if (!clicked)
                return MapDeviceResult.InProgress;

            _lastActionTime = DateTime.Now;
            Status = useCtrlClick
                ? $"[Select] Ctrl+clicking map into device"
                : "[Select] Right-clicking fragment into device";

            return MapDeviceResult.InProgress;
        }

        private DateTime _lastSearchPastedAt = DateTime.MinValue;
        private string _lastSearchedMapName = "";

        private MapDeviceResult TickSelectAtlasNode(GameController gc, Element atlas)
        {
            if (_nodeClickAttempts >= MaxClickAttempts)
            {
                Status = $"[Select] Failed to select {TargetMapName} after {MaxClickAttempts} attempts";
                _phase = MapDevicePhase.Idle;
                return MapDeviceResult.Failed;
            }

            if (!CanAct()) return MapDeviceResult.InProgress;

            var cleanTargetName = StripMapPrefix(TargetMapName) ?? "";
            var canvas = atlas.GetChildAtIndex(0);
            if (canvas == null)
            {
                Status = "[Select] Atlas canvas not found";
                return MapDeviceResult.InProgress;
            }

            var windowRect = gc.Window.GetWindowRectangle();
            var targetNode = FindTargetMapNode(canvas, cleanTargetName);

            if (targetNode != null)
            {
                var rect = targetNode.GetClientRect();
                var clickPos = new Vector2(windowRect.X + rect.Center.X, windowRect.Y + rect.Center.Y);

                if (rect.Center.X > 20 && rect.Center.X < windowRect.Width - 20 &&
                    rect.Center.Y > 20 && rect.Center.Y < windowRect.Height - 50)
                {
                    if (BotInput.Click(clickPos))
                    {
                        _lastActionTime = DateTime.Now;
                        _nodeClickAttempts++;
                        Status = $"[Select] Clicked '{cleanTargetName}' node (attempt {_nodeClickAttempts})";
                    }
                    return MapDeviceResult.InProgress;
                }
            }

            if (_lastSearchedMapName != cleanTargetName || (DateTime.Now - _lastSearchPastedAt).TotalSeconds > 4.0)
            {
                try
                {
                    ImGui.SetClipboardText(cleanTargetName);

                    // Ctrl + F
                    ExileCore.Input.KeyDown(Keys.ControlKey);
                    ExileCore.Input.KeyDown(Keys.F);
                    ExileCore.Input.KeyUp(Keys.F);

                    // Ctrl + A
                    ExileCore.Input.KeyDown(Keys.A);
                    ExileCore.Input.KeyUp(Keys.A);

                    // Ctrl + V
                    ExileCore.Input.KeyDown(Keys.V);
                    ExileCore.Input.KeyUp(Keys.V);

                    // Release Control
                    ExileCore.Input.KeyUp(Keys.ControlKey);

                    // Enter
                    ExileCore.Input.KeyDown(Keys.Enter);
                    ExileCore.Input.KeyUp(Keys.Enter);

                    _lastSearchedMapName = cleanTargetName;
                    _lastSearchPastedAt = DateTime.Now;
                    _lastActionTime = DateTime.Now;
                    Status = $"[Select] Searched Atlas for '{cleanTargetName}'";
                    return MapDeviceResult.InProgress;
                }
                catch (Exception ex)
                {
                    Status = $"[Select] Search error: {ex.Message}";
                    return MapDeviceResult.InProgress;
                }
            }

            Status = $"[Select] Searching Atlas for '{cleanTargetName}'...";
            return MapDeviceResult.InProgress;
        }

        private static Element? FindTargetMapNode(Element canvas, string targetMapName)
        {
            if (canvas == null) return null;

            var cleanNameNoSpaces = targetMapName.Replace(" ", "");

            for (int i = 0; i < canvas.ChildCount; i++)
            {
                var child = canvas.GetChildAtIndex(i);
                if (child == null || !child.IsVisible) continue;

                if (child.Tooltip != null && TooltipMatchesMap(child.Tooltip, targetMapName))
                {
                    return child;
                }

                try
                {
                    var entity = child.Entity;
                    if (entity != null)
                    {
                        if (!string.IsNullOrEmpty(entity.RenderName) &&
                            entity.RenderName.Contains(targetMapName, StringComparison.OrdinalIgnoreCase))
                        {
                            return child;
                        }
                        if (!string.IsNullOrEmpty(entity.Path) &&
                            entity.Path.Contains(cleanNameNoSpaces, StringComparison.OrdinalIgnoreCase))
                        {
                            return child;
                        }
                    }
                }
                catch { }
            }

            return null;
        }

        private static bool TooltipMatchesMap(Element? tooltip, string targetMapName)
        {
            if (tooltip == null) return false;

            if (!string.IsNullOrEmpty(tooltip.Text) &&
                tooltip.Text.Contains(targetMapName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            for (int i = 0; i < tooltip.ChildCount; i++)
            {
                var child = tooltip.GetChildAtIndex(i);
                if (child != null && TooltipMatchesMap(child, targetMapName))
                {
                    return true;
                }
            }

            return false;
        }

        private MapDeviceResult TickActivate(GameController gc)
        {
            var atlas = gc.IngameState.IngameUi.Atlas;
            if (atlas?.IsVisible != true)
            {
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

            if (!activateBtn.IsActive)
            {
                Status = "Activate button greyed out — no map loaded; returning to map selection";
                _phase = MapDevicePhase.SelectMap;
                _phaseStartTime = DateTime.Now;
                return MapDeviceResult.InProgress;
            }

            if (!BotInput.ClickLabel(gc, activateBtn.GetClientRect()))
            {
                Status = "[Activate] Waiting for input gate";
                return MapDeviceResult.InProgress;
            }
            _lastActionTime = DateTime.Now;
            Status = "Clicked activate — waiting for atlas to close";
            return MapDeviceResult.InProgress;
        }

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

        private MapDeviceResult TickEnterPortal(GameController gc, NavigationSystem nav)
        {
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

            var stashBlocking = gc.IngameState.IngameUi.StashElement?.IsVisible == true;
            var invBlocking = gc.IngameState.IngameUi.InventoryPanel?.IsVisible == true;
            if (stashBlocking || invBlocking)
            {
                BotInput.PressKey(Keys.Escape);
                _lastActionTime = DateTime.Now;
                Status = $"[Enter] Closing panels before portal click (stash={stashBlocking} inv={invBlocking})";
                return MapDeviceResult.InProgress;
            }

            if (Interaction != null)
            {
                if (Interaction.IsBusy)
                {
                    Status = $"[Enter] {Interaction.Status}";
                    return MapDeviceResult.InProgress;
                }

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

            Status = "[Enter] No InteractionSystem available";
            _phase = MapDevicePhase.Idle;
            return MapDeviceResult.Failed;
        }

        private Entity? FindMapDevice(GameController gc)
        {
            Entity? fallback = null;

            try
            {
                foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
                {
                    if (!entity.IsTargetable)
                        continue;

                    if (entity.RenderName == "Map Device")
                        return entity;

                    if (fallback == null && entity.Type == EntityType.IngameIcon &&
                        entity.Path != null && entity.Path.Contains("MappingDevice"))
                        fallback = entity;
                }
            }
            catch (IndexOutOfRangeException)
            {
                return null;
            }

            return fallback;
        }

        private Entity? FindNearestPortal(GameController gc) =>
            Modes.Shared.ModeHelpers.FindNearestPortal(gc);

        private bool IsMapInDevice(Element atlas)
        {
            var slots = atlas.GetChildFromIndices(DeviceSlotsPath);
            if (slots == null) return false;

            var slot0 = slots.GetChildAtIndex(0);
            return slot0 != null && slot0.ChildCount >= 2;
        }

        private bool IsActivateButtonReady(Element atlas)
        {
            var btn = atlas.GetChildFromIndices(ActivateButtonPath);
            return btn != null && btn.IsVisible && btn.IsActive;
        }

        private static string? StripMapPrefix(string? name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            return name.TrimStart('★', ' ', '\u00a0').Trim();
        }

        private MapDevicePhase NextPhaseAfterMapLoaded()
        {
            return ScarabPaths != null && ScarabPaths.Count > 0
                ? MapDevicePhase.InsertScarabs
                : MapDevicePhase.Activate;
        }

        private int CountEmptyScarabSlots(Element atlas)
        {
            var slots = atlas.GetChildFromIndices(DeviceSlotsPath);
            if (slots == null) return 0;
            int empty = 0;
            for (int i = 1; i <= 5; i++)
            {
                var s = slots.GetChildAtIndex(i);
                if (s != null && s.IsVisible && s.ChildCount < 2) empty++;
            }
            return empty;
        }

        private MapDeviceResult TickInsertScarabs(GameController gc)
        {
            var atlas = gc.IngameState.IngameUi.Atlas;
            if (atlas?.IsVisible != true)
            {
                _phase = MapDevicePhase.Activate;
                _phaseStartTime = DateTime.Now;
                Status = "[Scarabs] Atlas closed — proceeding to activate";
                return MapDeviceResult.InProgress;
            }

            if (ScarabPaths == null || ScarabPaths.Count == 0)
            {
                _phase = MapDevicePhase.Activate;
                _phaseStartTime = DateTime.Now;
                return MapDeviceResult.InProgress;
            }

            if ((DateTime.Now - _lastActionTime).TotalMilliseconds < InsertSettleMs)
            {
                Status = $"[Scarabs] Waiting {InsertSettleMs}ms for slot to update";
                return MapDeviceResult.InProgress;
            }

            int empty = CountEmptyScarabSlots(atlas);
            if (empty <= 0)
            {
                _phase = MapDevicePhase.Activate;
                _phaseStartTime = DateTime.Now;
                Status = "[Scarabs] All slots full — activating";
                return MapDeviceResult.InProgress;
            }

            var invItems = gc.IngameState.ServerData?.PlayerInventories?[0]?.Inventory?.InventorySlotItems;
            if (invItems == null)
            {
                _phase = MapDevicePhase.Activate;
                _phaseStartTime = DateTime.Now;
                Status = "[Scarabs] Inventory not readable — activating";
                return MapDeviceResult.InProgress;
            }

            ExileCore.PoEMemory.MemoryObjects.ServerInventory.InventSlotItem? targetSlot = null;
            string? matchedPath = null;
            foreach (var slotItem in invItems)
            {
                var path = slotItem.Item?.Path;
                if (string.IsNullOrEmpty(path)) continue;
                foreach (var sp in ScarabPaths)
                {
                    if (path.Contains(sp, StringComparison.OrdinalIgnoreCase))
                    {
                        targetSlot = slotItem;
                        matchedPath = sp;
                        break;
                    }
                }
                if (targetSlot != null) break;
            }

            if (targetSlot == null)
            {
                _phase = MapDevicePhase.Activate;
                _phaseStartTime = DateTime.Now;
                Status = $"[Scarabs] No more matching scarabs in inventory ({empty} slots empty) — activating";
                return MapDeviceResult.InProgress;
            }

            if (!CanAct()) return MapDeviceResult.InProgress;

            var rect = targetSlot.GetClientRect();
            var windowRect = gc.Window.GetWindowRectangle();
            var absPos = new Vector2(windowRect.X + rect.Center.X, windowRect.Y + rect.Center.Y);

            if (BotInput.CtrlClick(absPos))
            {
                _lastActionTime = DateTime.Now;
                Status = $"[Scarabs] Inserting '{matchedPath}' (empty slots: {empty})";
            }
            return MapDeviceResult.InProgress;
        }

        public static bool IsBlightedMap(Element item)
        {
            var entity = item.Entity;
            if (entity == null) return false;
            if (!entity.Path?.Contains("Maps/") == true) return false;
            if (!entity.TryGetComponent<Mods>(out var mods) || mods.ItemMods == null) return false;
            return mods.ItemMods.Any(m => m.RawName == "InfectedMap") &&
                   !mods.ItemMods.Any(m => m.RawName.StartsWith("UberInfectedMap"));
        }

        public static bool IsBlightRavagedMap(Element item)
        {
            var entity = item.Entity;
            if (entity == null) return false;
            if (!entity.Path?.Contains("Maps/") == true) return false;
            if (!entity.TryGetComponent<Mods>(out var mods) || mods.ItemMods == null) return false;
            return mods.ItemMods.Any(m => m.RawName.StartsWith("UberInfectedMap"));
        }

        public static bool IsAnyBlightMap(Element item)
        {
            return IsBlightedMap(item) || IsBlightRavagedMap(item);
        }

        public static bool IsSimulacrum(Element item)
        {
            var entity = item.Entity;
            if (entity == null || entity.Path == null) return false;
            return entity.Path.Contains("CurrencyAfflictionFragment", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsStandardMap(Element item)
        {
            var entity = item.Entity;
            if (entity == null) return false;
            if (!entity.Path?.Contains("Maps/MapKey") == true) return false;
            if (!entity.TryGetComponent<Mods>(out var mods)) return true;
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