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
        private const float ActionCooldownMs = 400;
        private const float PhaseTimeoutSeconds = 30f;
        private const float PortalWaitTimeoutSeconds = 10f;

        // UI element indices for atlas panel
        // Map stash: atlas[3][0][1] — children are InventoryItem elements
        // Device slots: atlas[7][0][2] — 6 slots, occupied slot has ChildCount==2, child[1] is the item
        // Activate button: atlas[7][0][3] — child[0].Text == "activate"
        private static readonly int[] MapStashPath = { 3, 0, 1 };
        private static readonly int[] DeviceSlotsPath = { 7, 0, 2 };
        private static readonly int[] ActivateButtonPath = { 7, 0, 3 };

        public MapDevicePhase Phase => _phase;
        public string Status { get; private set; } = "";
        public bool IsBusy => _phase != MapDevicePhase.Idle;

        /// <summary>
        /// Start the map creation flow with a filter for which map to select.
        /// The filter receives each InventoryItem element from the map stash
        /// and should return true for the desired map type.
        /// </summary>
        public bool Start(Func<Element, bool> mapFilter)
        {
            if (_phase != MapDevicePhase.Idle)
                return false;

            _mapFilter = mapFilter;
            _phase = MapDevicePhase.NavigateToDevice;
            _phaseStartTime = DateTime.Now;
            _lastActionTime = DateTime.MinValue;
            Status = "Starting map creation";
            return true;
        }

        public void Cancel(GameController gc, NavigationSystem? nav = null)
        {
            nav?.Stop(gc);
            _phase = MapDevicePhase.Idle;
            _mapFilter = null;
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
            if (phaseElapsed > PhaseTimeoutSeconds
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

            if (dist < 8f) // ~80 world units / 10.88
            {
                // Close enough — switch to clicking the device
                nav.Stop(gc);
                _phase = MapDevicePhase.OpenDevice;
                _phaseStartTime = DateTime.Now;
                Status = "Near device — opening";
                return MapDeviceResult.InProgress;
            }

            // Navigate to device (NavigateTo expects world coordinates)
            if (!nav.IsNavigating)
            {
                var worldTarget = new Vector2(
                    deviceGrid.X * Pathfinding.GridToWorld,
                    deviceGrid.Y * Pathfinding.GridToWorld);
                var success = nav.NavigateTo(gc, worldTarget);
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

            // Click the device
            var screenPos = gc.IngameState.Camera.WorldToScreen(device.BoundsCenterPosNum);
            var windowRect = gc.Window.GetWindowRectangle();
            var absPos = new Vector2(windowRect.X + screenPos.X, windowRect.Y + screenPos.Y);

            BotInput.Click(absPos);
            _lastActionTime = DateTime.Now;
            Status = $"[Open] Clicking device at ({absPos.X:F0},{absPos.Y:F0})";
            return MapDeviceResult.InProgress;
        }

        // --- Phase: Find and right-click a map from the stash ---

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

            var mapStash = atlas.GetChildFromIndices(MapStashPath);
            if (mapStash == null)
            {
                Status = "Map stash panel not found";
                _phase = MapDevicePhase.Idle;
                return MapDeviceResult.Failed;
            }

            // Find a matching map
            Element? targetMap = null;
            for (int i = 0; i < mapStash.ChildCount; i++)
            {
                var item = mapStash.GetChildAtIndex(i);
                if (item == null || item.Type != ElementType.InventoryItem)
                    continue;
                if (_mapFilter != null && !_mapFilter(item))
                    continue;
                targetMap = item;
                break;
            }

            if (targetMap == null)
            {
                Status = $"[Select] No matching maps in stash ({mapStash.ChildCount} items checked)";
                _phase = MapDevicePhase.Idle;
                return MapDeviceResult.Failed;
            }

            // Right-click the map to insert it into the device
            var rect = targetMap.GetClientRect();
            var center = new Vector2(rect.Center.X, rect.Center.Y);
            var windowRect = gc.Window.GetWindowRectangle();
            var absPos = new Vector2(windowRect.X + center.X, windowRect.Y + center.Y);

            BotInput.RightClick(absPos);
            _lastActionTime = DateTime.Now;
            Status = "Inserting map into device";

            // After right-click, wait a moment then check if it landed in the device
            // We'll re-enter this phase and the IsMapInDevice check will advance us
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

            var rect = activateBtn.GetClientRect();
            var center = new Vector2(rect.Center.X, rect.Center.Y);
            var windowRect = gc.Window.GetWindowRectangle();
            var absPos = new Vector2(windowRect.X + center.X, windowRect.Y + center.Y);

            BotInput.Click(absPos);
            _lastActionTime = DateTime.Now;

            // Stay in Activate phase — next tick will detect atlas closing (lines above)
            // and transition to WaitForPortals only after verification
            Status = "Clicked activate — waiting for atlas to close";
            return MapDeviceResult.InProgress;
        }

        // --- Phase: Wait for portals to appear ---

        private MapDeviceResult TickWaitForPortals(GameController gc)
        {
            if ((DateTime.Now - _phaseStartTime).TotalSeconds > PortalWaitTimeoutSeconds)
            {
                Status = "Timed out waiting for portals";
                _phase = MapDevicePhase.Idle;
                return MapDeviceResult.Failed;
            }

            var portal = FindNearestPortal(gc);
            if (portal != null)
            {
                _phase = MapDevicePhase.EnterPortal;
                _phaseStartTime = DateTime.Now;
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
                _phase = MapDevicePhase.Idle;
                _mapFilter = null;
                Status = "Entering map";
                return MapDeviceResult.Succeeded;
            }

            // Check if we left hideout
            if (!gc.Area.CurrentArea.IsHideout)
            {
                _phase = MapDevicePhase.Idle;
                _mapFilter = null;
                Status = "Entered map";
                return MapDeviceResult.Succeeded;
            }

            var portal = FindNearestPortal(gc);
            if (portal == null)
            {
                Status = "Portal disappeared";
                _phase = MapDevicePhase.Idle;
                return MapDeviceResult.Failed;
            }

            var screenPos = gc.IngameState.Camera.WorldToScreen(portal.BoundsCenterPosNum);
            var windowRect = gc.Window.GetWindowRectangle();

            // Check if portal is on screen
            if (screenPos.X < 0 || screenPos.X > windowRect.Width ||
                screenPos.Y < 0 || screenPos.Y > windowRect.Height)
            {
                // Navigate closer
                var portalDist = Vector2.Distance(
                    new Vector2(gc.Player.GridPosNum.X, gc.Player.GridPosNum.Y),
                    new Vector2(portal.GridPosNum.X, portal.GridPosNum.Y));
                if (!nav.IsNavigating)
                {
                    var portalGrid = portal.GridPosNum;
                    var worldTarget = new Vector2(
                        portalGrid.X * Pathfinding.GridToWorld,
                        portalGrid.Y * Pathfinding.GridToWorld);
                    var success = nav.NavigateTo(gc, worldTarget);
                    if (!success && gc.Area.CurrentArea.IsHideout && BotInput.CanAct)
                    {
                        // Hideout decoration walls — direct walk toward portal
                        var portalScreen = gc.IngameState.Camera.WorldToScreen(portal.BoundsCenterPosNum);
                        var wr = gc.Window.GetWindowRectangle();
                        BotInput.CursorPressKey(
                            new Vector2(wr.X + portalScreen.X, wr.Y + portalScreen.Y),
                            nav.MoveKey);
                    }
                }
                Status = $"[Enter] Navigating to portal (dist: {portalDist:F0}, nav={nav.IsNavigating})";
                return MapDeviceResult.InProgress;
            }

            // Check if UI panels are blocking the click
            var stashBlocking = gc.IngameState.IngameUi.StashElement?.IsVisible == true;
            var invBlocking = gc.IngameState.IngameUi.InventoryPanel?.IsVisible == true;
            if (stashBlocking || invBlocking)
            {
                BotInput.PressKey(Keys.Escape);
                _lastActionTime = DateTime.Now;
                Status = $"[Enter] Closing panels before portal click (stash={stashBlocking} inv={invBlocking})";
                return MapDeviceResult.InProgress;
            }

            var absPos = new Vector2(windowRect.X + screenPos.X, windowRect.Y + screenPos.Y);
            var clicked = BotInput.Click(absPos);
            _lastActionTime = DateTime.Now;
            Status = $"[Enter] Clicking portal at ({absPos.X:F0},{absPos.Y:F0}) sent={clicked}";
            return MapDeviceResult.InProgress;
        }

        // --- Helpers ---

        private Entity? FindMapDevice(GameController gc)
        {
            foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
            {
                if (entity.Type == EntityType.IngameIcon &&
                    entity.Path != null &&
                    entity.Path.Contains("MappingDevice"))
                    return entity;
            }
            return null;
        }

        private Entity? FindNearestPortal(GameController gc)
        {
            Entity? best = null;
            float bestDist = float.MaxValue;

            foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
            {
                if (entity.Type != EntityType.TownPortal)
                    continue;
                if (!entity.IsTargetable)
                    continue;
                if (entity.DistancePlayer < bestDist)
                {
                    bestDist = entity.DistancePlayer;
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
