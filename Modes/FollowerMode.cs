using ExileCore;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using AutoExile.Systems;
using System.Numerics;
using System.Windows.Forms;
using Input = ExileCore.Input;

namespace AutoExile.Modes
{
    public class FollowerMode : IBotMode
    {
        public string Name => "Follower";

        // Configuration
        public string LeaderName { get; set; } = "";
        public float FollowDistance { get; set; } = 28f; // grid units — start moving when leader is farther
        public float StopDistance { get; set; } = 14f; // grid units — stop when this close
        public float TeleportDetectDistance { get; set; } = 138f; // grid units — leader moved this far in one tick = teleport
        public bool FollowThroughTransitions { get; set; } = true;

        // Skill keys for buffs/debuffs (empty = don't cast)
        public Keys[] BuffSkillKeys { get; set; } = Array.Empty<Keys>();
        public float BuffCastInterval { get; set; } = 3.0f; // seconds between buff casts

        // State
        private FollowerState _state = FollowerState.SearchingForLeader;
        private string _status = "";
        private Vector2 _lastLeaderPos;
        private bool _hasLastLeaderPos;
        private DateTime _lastBuffCast = DateTime.MinValue;
        private DateTime _transitionClickTime = DateTime.MinValue;
        private const int TransitionCooldownMs = 3000; // wait after clicking transition
        private Entity? _targetTransition; // transition we're navigating to
        private const int InputIntervalMs = 50;
        private DateTime _lastInputTime = DateTime.MinValue;

        public void OnEnter(BotContext ctx)
        {
            _state = FollowerState.SearchingForLeader;
            _hasLastLeaderPos = false;
            _targetTransition = null;
            _status = string.IsNullOrEmpty(LeaderName)
                ? "No leader name set — configure in settings"
                : $"Searching for leader: {LeaderName}";
            ctx.Log($"Follower mode active — leader: {LeaderName}");
        }

        public void OnExit()
        {
            _state = FollowerState.SearchingForLeader;
            _hasLastLeaderPos = false;
            _targetTransition = null;
        }

        public void Tick(BotContext ctx)
        {
            if (string.IsNullOrEmpty(LeaderName))
            {
                _status = "No leader name configured";
                return;
            }

            var gc = ctx.Game;

            // Handle loading screens
            if (gc.IsLoading)
            {
                _state = FollowerState.WaitingForLoad;
                _status = "Loading...";
                ctx.Navigation.Stop(gc);
                return;
            }

            if (_state == FollowerState.WaitingForLoad)
            {
                // Just finished loading — search for leader again
                _state = FollowerState.SearchingForLeader;
                _hasLastLeaderPos = false;
            }

            // Try to find the leader entity
            var leader = FindLeader(gc);

            if (leader != null)
            {
                HandleLeaderVisible(ctx, gc, leader);
            }
            else
            {
                HandleLeaderMissing(ctx, gc);
            }
        }

        private void HandleLeaderVisible(BotContext ctx, GameController gc, Entity leader)
        {
            var playerGridPos = GetPlayerGrid(gc);
            var leaderGridPos = new Vector2(leader.GridPosNum.X, leader.GridPosNum.Y);

            // Check for teleport — leader moved impossibly far in one tick
            if (_hasLastLeaderPos && FollowThroughTransitions)
            {
                var leaderMoved = Vector2.Distance(_lastLeaderPos, leaderGridPos);
                if (leaderMoved > TeleportDetectDistance)
                {
                    // Leader teleported — find transition near their OLD position
                    ctx.Log($"Leader teleported ({leaderMoved:F0} grid units) — looking for transition");
                    var transition = FindNearestTransition(gc, _lastLeaderPos);
                    if (transition != null)
                    {
                        _targetTransition = transition;
                        _state = FollowerState.NavigatingToTransition;
                        ctx.Navigation.Stop(gc);
                        var transGridPos = new Vector2(transition.GridPosNum.X, transition.GridPosNum.Y);
                        ctx.Navigation.NavigateTo(gc, transGridPos * Pathfinding.GridToWorld, maxNodes: 200000);
                        _status = $"Leader teleported — heading to transition";
                    }
                }
            }

            _lastLeaderPos = leaderGridPos;
            _hasLastLeaderPos = true;

            // If we're chasing a transition but leader is visible and close, cancel that
            if (_state == FollowerState.NavigatingToTransition || _state == FollowerState.ClickingTransition)
            {
                var distToLeader = Vector2.Distance(playerGridPos, leaderGridPos);
                if (distToLeader < FollowDistance)
                {
                    _state = FollowerState.Following;
                    _targetTransition = null;
                    ctx.Navigation.Stop(gc);
                }
                else
                {
                    // Still following transition path
                    return;
                }
            }

            // Normal following
            var dist = Vector2.Distance(playerGridPos, leaderGridPos);

            if (dist > FollowDistance)
            {
                // Too far — pathfind to leader
                var leaderWorldPos = leaderGridPos * Pathfinding.GridToWorld;
                if (!ctx.Navigation.IsNavigating ||
                    Vector2.Distance(ctx.Navigation.Destination ?? Vector2.Zero, leaderWorldPos) > FollowDistance * Pathfinding.GridToWorld)
                {
                    ctx.Navigation.NavigateTo(gc, leaderWorldPos);
                }
                _state = FollowerState.Following;
                _status = $"Following {LeaderName} (dist: {dist:F0})";
            }
            else if (dist < StopDistance)
            {
                // Close enough — stop and idle
                if (ctx.Navigation.IsNavigating)
                    ctx.Navigation.Stop(gc);
                _state = FollowerState.NearLeader;
                _status = $"Near {LeaderName} (dist: {dist:F0})";
            }
            else
            {
                _state = FollowerState.Following;
                _status = $"Following {LeaderName} (dist: {dist:F0})";
            }

            // Cast buffs when near leader
            TryCastBuffs(gc, dist);
        }

        private void HandleLeaderMissing(BotContext ctx, GameController gc)
        {
            switch (_state)
            {
                case FollowerState.NavigatingToTransition:
                    // We're heading to a transition — check if we've arrived
                    if (!ctx.Navigation.IsNavigating && _targetTransition != null)
                    {
                        _state = FollowerState.ClickingTransition;
                    }
                    _status = "Leader gone — navigating to transition";
                    break;

                case FollowerState.ClickingTransition:
                    // Click the transition
                    if (_targetTransition != null)
                    {
                        ClickEntity(gc, _targetTransition);
                        _transitionClickTime = DateTime.Now;
                        _state = FollowerState.WaitingForLoad;
                        _status = "Clicking transition...";
                    }
                    else
                    {
                        _state = FollowerState.SearchingForLeader;
                    }
                    break;

                case FollowerState.SearchingForLeader:
                    // Leader not in zone — look for a transition to follow through
                    if (FollowThroughTransitions && _hasLastLeaderPos)
                    {
                        var transition = FindNearestTransition(gc, _lastLeaderPos);
                        if (transition != null)
                        {
                            _targetTransition = transition;
                            _state = FollowerState.NavigatingToTransition;
                            var transGridPos = new Vector2(transition.GridPosNum.X, transition.GridPosNum.Y);
                            ctx.Navigation.NavigateTo(gc, transGridPos * Pathfinding.GridToWorld, maxNodes: 200000);
                            _status = "Leader not found — heading to nearest transition";
                        }
                        else
                        {
                            _status = $"Searching for {LeaderName}...";
                        }
                    }
                    else
                    {
                        _status = $"Searching for {LeaderName}...";
                    }
                    break;

                default:
                    // Leader was visible but now gone
                    if (_hasLastLeaderPos && FollowThroughTransitions)
                    {
                        var transition = FindNearestTransition(gc, _lastLeaderPos);
                        if (transition != null)
                        {
                            _targetTransition = transition;
                            _state = FollowerState.NavigatingToTransition;
                            ctx.Navigation.Stop(gc);
                            var transGridPos = new Vector2(transition.GridPosNum.X, transition.GridPosNum.Y);
                            ctx.Navigation.NavigateTo(gc, transGridPos * Pathfinding.GridToWorld, maxNodes: 200000);
                            _status = "Leader disappeared — heading to transition";
                            ctx.Log("Leader disappeared — following through transition");
                        }
                        else
                        {
                            _state = FollowerState.SearchingForLeader;
                            _status = $"Leader gone — no transition found near last position";
                        }
                    }
                    else
                    {
                        _state = FollowerState.SearchingForLeader;
                        _status = $"Searching for {LeaderName}...";
                    }
                    break;
            }
        }

        private Entity? FindLeader(GameController gc)
        {
            foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
            {
                if (entity.Type != EntityType.Player)
                    continue;
                if (entity.RenderName == LeaderName)
                    return entity;
            }
            return null;
        }

        private Entity? FindNearestTransition(GameController gc, Vector2 nearGridPos)
        {
            Entity? best = null;
            float bestDist = float.MaxValue;

            foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
            {
                if (entity.Type != EntityType.AreaTransition &&
                    entity.Type != EntityType.TownPortal &&
                    entity.Type != EntityType.Portal)
                    continue;

                if (!entity.IsTargetable)
                    continue;

                var entityGridPos = new Vector2(entity.GridPosNum.X, entity.GridPosNum.Y);
                var dist = Vector2.Distance(nearGridPos, entityGridPos);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = entity;
                }
            }

            return best;
        }

        private void ClickEntity(GameController gc, Entity entity)
        {
            if ((DateTime.Now - _lastInputTime).TotalMilliseconds < InputIntervalMs)
                return;

            var screenPos = gc.IngameState.Camera.WorldToScreen(entity.BoundsCenterPosNum);
            var windowRect = gc.Window.GetWindowRectangle();

            if (screenPos.X > 0 && screenPos.X < windowRect.Width &&
                screenPos.Y > 0 && screenPos.Y < windowRect.Height)
            {
                var absPos = new Vector2(windowRect.X + screenPos.X, windowRect.Y + screenPos.Y);
                BotInput.Click(absPos);
                _lastInputTime = DateTime.Now;
            }
        }

        private void TryCastBuffs(GameController gc, float distToLeader)
        {
            if (BuffSkillKeys.Length == 0)
                return;

            if (distToLeader > FollowDistance)
                return; // don't cast buffs when too far

            if ((DateTime.Now - _lastBuffCast).TotalSeconds < BuffCastInterval)
                return;

            if ((DateTime.Now - _lastInputTime).TotalMilliseconds < InputIntervalMs)
                return;

            // Cast next buff skill
            var keyIndex = (int)((DateTime.Now.Ticks / TimeSpan.TicksPerSecond) % BuffSkillKeys.Length);
            var key = BuffSkillKeys[keyIndex];

            Input.KeyDown(key);
            Input.KeyUp(key);
            _lastBuffCast = DateTime.Now;
            _lastInputTime = DateTime.Now;
        }

        private static Vector2 GetPlayerGrid(GameController gc)
        {
            var pos = gc.Player.GridPosNum;
            return new Vector2(pos.X, pos.Y);
        }

        public void Render(BotContext ctx)
        {
            var gfx = ctx.Graphics;
            if (gfx == null) return;

            var yOffset = 100f;

            // Status
            var color = _state switch
            {
                FollowerState.NearLeader => SharpDX.Color.LimeGreen,
                FollowerState.Following => SharpDX.Color.Yellow,
                FollowerState.NavigatingToTransition or FollowerState.ClickingTransition => SharpDX.Color.Orange,
                FollowerState.SearchingForLeader => SharpDX.Color.Red,
                _ => SharpDX.Color.White
            };

            gfx.DrawText($"[Follower] {_status}", new Vector2(100, yOffset), color);
            yOffset += 20;

            gfx.DrawText($"State: {_state} | Leader: {LeaderName}", new Vector2(100, yOffset), SharpDX.Color.Gray);
            yOffset += 20;

            // Draw leader marker if visible
            var leader = FindLeader(ctx.Game);
            if (leader != null)
            {
                var camera = ctx.Game.IngameState.Camera;
                var leaderScreen = camera.WorldToScreen(leader.BoundsCenterPosNum);
                var windowRect = ctx.Game.Window.GetWindowRectangle();

                if (leaderScreen.X > 0 && leaderScreen.X < windowRect.Width &&
                    leaderScreen.Y > 0 && leaderScreen.Y < windowRect.Height)
                {
                    var ls = new Vector2(leaderScreen.X, leaderScreen.Y);
                    gfx.DrawLine(ls + new Vector2(-10, -10), ls + new Vector2(10, 10), 2, SharpDX.Color.Cyan);
                    gfx.DrawLine(ls + new Vector2(10, -10), ls + new Vector2(-10, 10), 2, SharpDX.Color.Cyan);
                    gfx.DrawText("LEADER", ls + new Vector2(12, -8), SharpDX.Color.Cyan);
                }
            }

            // Draw nav path if navigating
            if (ctx.Navigation.IsNavigating && ctx.Navigation.CurrentNavPath.Count > 1)
            {
                var camera = ctx.Game.IngameState.Camera;
                var playerZ = ctx.Game.Player.PosNum.Z;
                var path = ctx.Navigation.CurrentNavPath;

                for (var i = ctx.Navigation.CurrentWaypointIndex; i < path.Count - 1; i++)
                {
                    var a = path[i].Position;
                    var b = path[i + 1].Position;
                    var sa = camera.WorldToScreen(new Vector3(a.X, a.Y, playerZ));
                    var sb = camera.WorldToScreen(new Vector3(b.X, b.Y, playerZ));
                    var windowRect = ctx.Game.Window.GetWindowRectangle();

                    if (sa.X > 0 && sa.X < windowRect.Width && sa.Y > 0 && sa.Y < windowRect.Height)
                    {
                        var lineColor = path[i + 1].Action == WaypointAction.Blink
                            ? SharpDX.Color.Magenta : SharpDX.Color.Yellow;
                        gfx.DrawLine(new Vector2(sa.X, sa.Y), new Vector2(sb.X, sb.Y), 2, lineColor);
                    }
                }
            }
        }
    }

    internal enum FollowerState
    {
        SearchingForLeader,
        Following,
        NearLeader,
        NavigatingToTransition,
        ClickingTransition,
        WaitingForLoad
    }
}
