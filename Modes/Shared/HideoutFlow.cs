
using ExileCore;
using ExileCore.PoEMemory;
using ExileCore.PoEMemory.MemoryObjects;
using AutoExile.Systems;
using System.Numerics;

namespace AutoExile.Modes.Shared
{
    /// <summary>
    /// Shared hideout flow: settle → stash (clean inventory first) → open map via MapDevice → enter portal.
    /// Used by BlightMode and other modes to handle clean hideout cycling.
    /// </summary>
    public class HideoutFlow
    {
        private HideoutPhase _phase = HideoutPhase.Idle;
        private DateTime _phaseStartTime = DateTime.Now;
        private DateTime _lastActionTime = DateTime.MinValue;

        private Func<Element, bool>? _mapFilter;
        private Func<ServerInventory.InventSlotItem, bool>? _stashItemFilter;
        private string? _targetMapName;
        private string? _inventoryFragmentPath;
        private int _minMapTier;
        private int _stashItemThreshold;
        private string? _dumpTabName;
        private string? _resourceTabName;
        private string? _withdrawFragmentPath;
        private int _fragmentStock;
        private int _minFragments;

        private IReadOnlyList<(string PathSubstring, int Count)>? _withdrawList;
        private IReadOnlyList<string>? _scarabPaths;

        private const float BasePortalTimeoutSeconds = 15f;
        private const float MapDeviceRetrySeconds = 10f;
        private const float ActionCooldownMs = 500f;

        public string Status { get; private set; } = "";
        public bool IsActive => _phase != HideoutPhase.Idle;

        public void Start(Func<Element, bool> mapFilter,
            Func<ServerInventory.InventSlotItem, bool>? stashItemFilter = null,
            string? targetMapName = null, int minMapTier = 0,
            string? inventoryFragmentPath = null,
            int stashItemThreshold = 0,
            string? dumpTabName = null,
            string? resourceTabName = null,
            string? withdrawFragmentPath = null,
            int fragmentStock = 0,
            int minFragments = 1,
            IReadOnlyList<(string PathSubstring, int Count)>? withdrawList = null,
            IReadOnlyList<string>? scarabPaths = null)
        {
            _mapFilter = mapFilter;
            _stashItemFilter = stashItemFilter;
            _targetMapName = targetMapName;
            _inventoryFragmentPath = inventoryFragmentPath;
            _minMapTier = minMapTier;
            _stashItemThreshold = stashItemThreshold;
            _dumpTabName = dumpTabName;
            _resourceTabName = resourceTabName;
            _withdrawFragmentPath = withdrawFragmentPath;
            _fragmentStock = fragmentStock;
            _minFragments = minFragments;
            _withdrawList = withdrawList != null && withdrawList.Count > 0 ? withdrawList : null;
            _scarabPaths = scarabPaths != null && scarabPaths.Count > 0 ? scarabPaths : null;
            _phase = HideoutPhase.Settle;
            _phaseStartTime = DateTime.Now;
            Status = "Hideout — settling";
        }

        public void StartPortalReentry()
        {
            _mapFilter = null;
            _phase = HideoutPhase.EnterPortal;
            _phaseStartTime = DateTime.Now;
            Status = "Re-entering map via portal";
        }

        public HideoutSignal Tick(BotContext ctx)
        {
            switch (_phase)
            {
                case HideoutPhase.Settle:
                    return TickSettle(ctx);
                case HideoutPhase.Stash:
                    return TickStash(ctx);
                case HideoutPhase.OpenMap:
                    return TickOpenMap(ctx);
                case HideoutPhase.EnterPortal:
                    return TickEnterPortal(ctx);
                default:
                    return HideoutSignal.InProgress;
            }
        }

        public void Cancel()
        {
            _phase = HideoutPhase.Idle;
            _mapFilter = null;
            _stashItemFilter = null;
            _targetMapName = null;
            _inventoryFragmentPath = null;
            _minMapTier = 0;
            _stashItemThreshold = 0;
            _dumpTabName = null;
            _resourceTabName = null;
            _withdrawFragmentPath = null;
            _fragmentStock = 0;
            _minFragments = 1;
            _withdrawList = null;
            _scarabPaths = null;
            Status = "";
        }

        private HideoutSignal TickSettle(BotContext ctx)
        {
            var elapsed = (DateTime.Now - _phaseStartTime).TotalSeconds;
            if (elapsed < ctx.Settings.AreaSettleSeconds.Value)
            {
                Status = $"Hideout — waiting for game state ({elapsed:F1}s)";
                return HideoutSignal.InProgress;
            }

            // Always prioritise checking for loot/stashable items to clean inventory first
            int lootItems = StashSystem.CountNonMatchingItems(ctx.Game, _withdrawFragmentPath);
            bool hasStashables = StashSystem.HasStashableItems(ctx.Game, _stashItemFilter);
            bool needStore = hasStashables && (_stashItemThreshold <= 0 || lootItems >= _stashItemThreshold);

            // Multi-item path
            List<(string PathSubstring, int Count)>? activeWithdrawList = null;
            int totalNeededFromList = 0;
            if (_withdrawList != null && !string.IsNullOrWhiteSpace(_resourceTabName))
            {
                activeWithdrawList = new List<(string, int)>();
                foreach (var (path, target) in _withdrawList)
                {
                    int have;
                    if (path == StashSystem.BlightMapIdentifier)
                        have = StashSystem.CountBlightMaps(ctx.Game, ravagedOnly: false);
                    else if (path == StashSystem.BlightRavagedMapIdentifier)
                        have = StashSystem.CountBlightMaps(ctx.Game, ravagedOnly: true);
                    else
                        have = StashSystem.CountInventoryItems(ctx.Game, path);

                    var need = target - have;
                    if (need > 0)
                    {
                        activeWithdrawList.Add((path, need));
                        totalNeededFromList += need;
                    }
                }
                if (activeWithdrawList.Count == 0) activeWithdrawList = null;
            }

            // Single-item path
            int fragmentsInInventory;
            if (_withdrawFragmentPath == StashSystem.BlightMapIdentifier)
                fragmentsInInventory = StashSystem.CountBlightMaps(ctx.Game, ravagedOnly: false);
            else if (_withdrawFragmentPath == StashSystem.BlightRavagedMapIdentifier)
                fragmentsInInventory = StashSystem.CountBlightMaps(ctx.Game, ravagedOnly: true);
            else
                fragmentsInInventory = StashSystem.CountInventoryItems(ctx.Game, _withdrawFragmentPath);

            bool usesFragments = !string.IsNullOrEmpty(_withdrawFragmentPath);
            int minNeeded = _minFragments > 0 ? _minFragments : 1;
            bool canWithdraw = usesFragments
                && !string.IsNullOrEmpty(_resourceTabName)
                && _fragmentStock > 0;
            bool needSingleWithdraw = canWithdraw && fragmentsInInventory < minNeeded;
            int withdrawNeeded = needSingleWithdraw ? _fragmentStock : 0;
            bool needMultiWithdraw = activeWithdrawList != null;
            bool needWithdraw = needSingleWithdraw || needMultiWithdraw;

            // Prioritise cleaning the inventory first if any loot exists
            if (needStore || needWithdraw)
            {
                _phase = HideoutPhase.Stash;
                _phaseStartTime = DateTime.Now;
                ctx.Stash.Start(
                    storeTabName:         needStore    ? _dumpTabName          : null,
                    withdrawTabName:      needWithdraw ? _resourceTabName      : null,
                    withdrawFragmentPath: needMultiWithdraw ? null : (needSingleWithdraw ? _withdrawFragmentPath : null),
                    withdrawCount:        needMultiWithdraw ? 0    : withdrawNeeded,
                    itemFilter:           needStore ? _stashItemFilter : (_ => false),
                    withdrawList:         activeWithdrawList);

                var parts = new List<string>();
                if (needStore) parts.Add($"cleaning inventory ({lootItems} items)");
                if (needSingleWithdraw) parts.Add($"withdraw {withdrawNeeded} items");
                if (needMultiWithdraw)  parts.Add($"withdraw {totalNeededFromList} items");
                Status = string.Join(" & ", parts);
                return HideoutSignal.InProgress;
            }

            if (usesFragments && fragmentsInInventory < minNeeded && !canWithdraw)
            {
                Status = "No maps/fragments in inventory";
                _phase = HideoutPhase.Idle;
                return HideoutSignal.NoFragments;
            }

            // No stashing needed — proceed to map device
            _phase = HideoutPhase.OpenMap;
            _phaseStartTime = DateTime.Now;
            StartMapDevice(ctx);
            return HideoutSignal.InProgress;
        }

        private HideoutSignal TickStash(BotContext ctx)
        {
            var result = ctx.Stash.Tick(ctx.Game, ctx.Navigation);

            switch (result)
            {
                case StashResult.Succeeded:
                case StashResult.Failed:
                {
                    if (!string.IsNullOrEmpty(_withdrawFragmentPath) && !string.IsNullOrEmpty(_resourceTabName))
                    {
                        int frags;
                        if (_withdrawFragmentPath == StashSystem.BlightMapIdentifier)
                            frags = StashSystem.CountBlightMaps(ctx.Game, ravagedOnly: false);
                        else if (_withdrawFragmentPath == StashSystem.BlightRavagedMapIdentifier)
                            frags = StashSystem.CountBlightMaps(ctx.Game, ravagedOnly: true);
                        else
                            frags = StashSystem.CountInventoryItems(ctx.Game, _withdrawFragmentPath);

                        int needed = _minFragments > 0 ? _minFragments : 1;
                        if (frags < needed)
                        {
                            Status = $"Not enough maps/fragments ({frags}/{needed}) — stopping";
                            _phase = HideoutPhase.Idle;
                            return HideoutSignal.NoFragments;
                        }
                    }

                    Status = result == StashResult.Succeeded
                        ? $"Stash cleaned ({ctx.Stash.ItemsStored} stored) — opening map"
                        : $"Stash status: {ctx.Stash.Status} — opening map";
                    _phase = HideoutPhase.OpenMap;
                    _phaseStartTime = DateTime.Now;
                    StartMapDevice(ctx);
                    break;
                }
                default:
                    Status = $"Stashing: {ctx.Stash.Status}";
                    break;
            }
            return HideoutSignal.InProgress;
        }

        private void StartMapDevice(BotContext ctx)
        {
            if (ctx.MapDevice.IsBusy)
                ctx.MapDevice.Cancel(ctx.Game, ctx.Navigation);

            ctx.MapDevice.TargetMapName = _targetMapName;
            ctx.MapDevice.MinMapTier = _minMapTier;

            if (_mapFilter != null && !ctx.MapDevice.Start(_mapFilter, _inventoryFragmentPath, _scarabPaths))
                Status = $"MapDevice.Start failed (phase={ctx.MapDevice.Phase})";
        }

        private HideoutSignal TickOpenMap(BotContext ctx)
        {
            var result = ctx.MapDevice.Tick(ctx.Game, ctx.Navigation);

            switch (result)
            {
                case MapDeviceResult.Succeeded:
                    Status = "Map opened — entering";
                    break;
                case MapDeviceResult.Failed:
                    Status = $"Map device failed: {ctx.MapDevice.Status}";
                    if ((DateTime.Now - _phaseStartTime).TotalSeconds > MapDeviceRetrySeconds)
                    {
                        _phaseStartTime = DateTime.Now;
                        StartMapDevice(ctx);
                    }
                    break;
                default:
                    Status = $"Map device: {ctx.MapDevice.Status}";
                    break;
            }
            return HideoutSignal.InProgress;
        }

        private HideoutSignal TickEnterPortal(BotContext ctx)
        {
            var gc = ctx.Game;

            if (!gc.Area.CurrentArea.IsHideout)
                return HideoutSignal.InProgress;

            if ((DateTime.Now - _phaseStartTime).TotalSeconds > BasePortalTimeoutSeconds + ctx.Settings.ExtraLatencyMs.Value / 1000f)
            {
                Status = "No portal found";
                ctx.Interaction.Cancel(gc);
                _phase = HideoutPhase.Idle;
                return HideoutSignal.PortalTimeout;
            }

            if (gc.IngameState.IngameUi.StashElement?.IsVisible == true ||
                gc.IngameState.IngameUi.InventoryPanel?.IsVisible == true)
            {
                if (ModeHelpers.CanAct(_lastActionTime, ActionCooldownMs))
                {
                    BotInput.PressKey(System.Windows.Forms.Keys.Escape);
                    _lastActionTime = DateTime.Now;
                    Status = "Closing panels before portal";
                }
                return HideoutSignal.InProgress;
            }

            if (ctx.Interaction.IsBusy)
            {
                Status = $"Entering portal: {ctx.Interaction.Status}";
                return HideoutSignal.InProgress;
            }

            var portal = ModeHelpers.FindNearestPortal(gc);
            if (portal == null)
            {
                Status = "Looking for portal to re-enter...";
                return HideoutSignal.InProgress;
            }

            ctx.Interaction.InteractWithEntity(portal, ctx.Navigation, requireProximity: true);
            Status = "Interacting with portal";
            return HideoutSignal.InProgress;
        }

        private enum HideoutPhase
        {
            Idle,
            Settle,
            Stash,
            OpenMap,
            EnterPortal,
        }
    }

    public enum HideoutSignal
    {
        InProgress,
        PortalTimeout,
        NoFragments,
    }
}