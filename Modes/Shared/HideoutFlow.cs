using ExileCore;
using ExileCore.PoEMemory;
using ExileCore.PoEMemory.MemoryObjects;
using AutoExile.Systems;
using System.Numerics;

namespace AutoExile.Modes.Shared
{
    /// <summary>
    /// Shared hideout flow: settle → stash → open map via MapDevice → enter portal.
    /// Used by BlightMode and SimulacrumMode to replace 5 duplicated hideout methods.
    /// </summary>
    public class HideoutFlow
    {
        private HideoutPhase _phase = HideoutPhase.Idle;
        private DateTime _phaseStartTime = DateTime.Now;
        private DateTime _lastActionTime = DateTime.MinValue;

        // Configuration set via Start()
        private Func<Element, bool>? _mapFilter;
        private Func<ServerInventory.InventSlotItem, bool>? _stashItemFilter;
        private string? _targetMapName;
        private int _minMapTier;

        private const float SettleSeconds = 3f;
        private const float BasePortalTimeoutSeconds = 15f;
        private const float MapDeviceRetrySeconds = 10f;
        private const float ActionCooldownMs = 500f;

        public string Status { get; private set; } = "";
        public bool IsActive => _phase != HideoutPhase.Idle;

        /// <summary>
        /// Start a full hideout flow: settle → stash → open map → enter portal.
        /// </summary>
        public void Start(Func<Element, bool> mapFilter,
            Func<ServerInventory.InventSlotItem, bool>? stashItemFilter = null,
            string? targetMapName = null, int minMapTier = 0)
        {
            _mapFilter = mapFilter;
            _stashItemFilter = stashItemFilter;
            _targetMapName = targetMapName;
            _minMapTier = minMapTier;
            _phase = HideoutPhase.Settle;
            _phaseStartTime = DateTime.Now;
            Status = "Hideout — settling";
        }

        /// <summary>
        /// Start portal re-entry flow (after death): find portal → navigate → click.
        /// </summary>
        public void StartPortalReentry()
        {
            _mapFilter = null;
            _phase = HideoutPhase.EnterPortal;
            _phaseStartTime = DateTime.Now;
            Status = "Re-entering map via portal";
        }

        /// <summary>
        /// Tick the hideout flow. Returns a signal for the mode to act on.
        /// </summary>
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
            _minMapTier = 0;
            Status = "";
        }

        // ── Phases ──

        private HideoutSignal TickSettle(BotContext ctx)
        {
            var elapsed = (DateTime.Now - _phaseStartTime).TotalSeconds;
            if (elapsed < SettleSeconds)
            {
                Status = $"Hideout — waiting for game state ({elapsed:F1}s)";
                return HideoutSignal.InProgress;
            }

            // Check if we have items to stash (respecting filter)
            if (StashSystem.HasStashableItems(ctx.Game, _stashItemFilter))
            {
                _phase = HideoutPhase.Stash;
                _phaseStartTime = DateTime.Now;
                ctx.Stash.ItemFilter = _stashItemFilter;
                ctx.Stash.Start();
                Status = "Stashing inventory items";
                return HideoutSignal.InProgress;
            }

            // No items — open map
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
                    Status = $"Stashed {ctx.Stash.ItemsStored} items — opening map";
                    _phase = HideoutPhase.OpenMap;
                    _phaseStartTime = DateTime.Now;
                    StartMapDevice(ctx);
                    break;
                case StashResult.Failed:
                    Status = $"Stash failed: {ctx.Stash.Status} — opening map anyway";
                    _phase = HideoutPhase.OpenMap;
                    _phaseStartTime = DateTime.Now;
                    StartMapDevice(ctx);
                    break;
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

            if (_mapFilter != null && !ctx.MapDevice.Start(_mapFilter))
                Status = $"MapDevice.Start failed (phase={ctx.MapDevice.Phase})";
        }

        private HideoutSignal TickOpenMap(BotContext ctx)
        {
            var result = ctx.MapDevice.Tick(ctx.Game, ctx.Navigation);

            switch (result)
            {
                case MapDeviceResult.Succeeded:
                    Status = "Map opened — entering";
                    // Area change will fire when player enters the portal
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

            // Close any open panels (stash/inventory) before clicking portal
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

            // Use InteractionSystem for portal clicking — handles navigation,
            // screen bounds, click verification, and retries automatically.
            // InteractionSystem is already ticked by the mode before this runs.
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
    }
}
