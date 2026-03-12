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

        private const float SettleSeconds = 3f;
        private const float PortalTimeoutSeconds = 15f;
        private const float MapDeviceRetrySeconds = 10f;
        private const float ActionCooldownMs = 500f;

        public string Status { get; private set; } = "";
        public bool IsActive => _phase != HideoutPhase.Idle;

        /// <summary>
        /// Start a full hideout flow: settle → stash → open map → enter portal.
        /// </summary>
        public void Start(Func<Element, bool> mapFilter)
        {
            _mapFilter = mapFilter;
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

            // Check if we have items to stash
            if (ModeHelpers.HasInventoryItems(ctx.Game))
            {
                _phase = HideoutPhase.Stash;
                _phaseStartTime = DateTime.Now;
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

            if ((DateTime.Now - _phaseStartTime).TotalSeconds > PortalTimeoutSeconds)
            {
                Status = "No portal found";
                _phase = HideoutPhase.Idle;
                return HideoutSignal.PortalTimeout;
            }

            if (!ModeHelpers.CanAct(_lastActionTime, ActionCooldownMs))
                return HideoutSignal.InProgress;

            var portal = ModeHelpers.FindNearestPortal(gc);
            if (portal == null)
            {
                Status = "Looking for portal to re-enter...";
                return HideoutSignal.InProgress;
            }

            var playerGridPos = new Vector2(gc.Player.GridPosNum.X, gc.Player.GridPosNum.Y);
            var portalGridPos = new Vector2(portal.GridPosNum.X, portal.GridPosNum.Y);
            var dist = Vector2.Distance(playerGridPos, portalGridPos);

            if (dist > 8f)
            {
                if (!ctx.Navigation.IsNavigating)
                    ctx.Navigation.NavigateTo(gc, portalGridPos * Systems.Pathfinding.GridToWorld);
                Status = $"Walking to portal (dist: {dist:F0})";
                return HideoutSignal.InProgress;
            }

            ModeHelpers.ClickEntity(gc, portal, ref _lastActionTime);
            Status = "Clicking portal to re-enter map";
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
