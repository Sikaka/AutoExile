using ExileCore;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using AutoExile.Systems;
using System.Numerics;
using System.Windows.Forms;
using Pathfinding = AutoExile.Systems.Pathfinding;

namespace AutoExile.Modes
{
    /// <summary>
    /// 5-Way Legion Resetter (Experimental)
    ///
    /// Aura bot circle runner for Domain of Timeless Conflict.
    /// Dashes in and out of the obelisk circle to spawn monsters as fast as possible
    /// while the carry kills them. No combat — only movement skills.
    ///
    /// Flow:
    ///   1. Navigate to idle position near obelisk
    ///   2. Wait for leader to start sustained attacking (configurable threshold)
    ///   3. Circle dance: dash in → wait for spawn trigger → shift+dash out → dash in → repeat
    ///   4. When timer expires, exit via portal
    ///
    /// Key entity: LegionEndlessInitiator
    ///   - obelisk_state: 0=no monsters, 2=monsters active
    ///   - checking_control_zone: 0=player in circle, 1=player out
    ///   - Circle radius: ~35 grid units
    ///
    /// Requires a movement skill (frostblink/flame dash) configured in build settings.
    /// </summary>
    public class LegionResetterMode : IBotMode
    {
        public string Name => "5-Way Resetter";

        private const string InitiatorPath = "LegionEndlessInitiator";
        private const float CircleRadius = 35f;
        private const float IdleDistFromCircle = 50f;

        // State
        private ResetterPhase _phase = ResetterPhase.Idle;
        private DateTime _phaseStartTime;
        private string _status = "";

        // Entity tracking
        private Entity? _initiatorEntity;
        private Vector2 _initiatorPos;
        private bool _initiatorFound;

        // Leader attack detection
        private DateTime _leaderAttackStartTime;
        private bool _leaderAttacking;
        private float _leaderAttackDuration; // seconds of sustained attacking

        // Circle dance state
        private bool _insideCircle;
        private DateTime _enteredCircleTime;
        private DateTime _lastDashTime;
        private const float DashCooldownMs = 300f; // minimum between dashes

        // Timer tracking
        private string _lastTimerText = "";

        // Error state
        private string? _error;

        private enum ResetterPhase
        {
            Idle,
            NavigateToIdle,     // Walk to idle position
            WaitForLeader,      // At idle pos, watching leader for sustained attack
            DashIntoCircle,     // Dash toward obelisk
            WaitForSpawn,       // Inside circle, waiting ~3s for spawn trigger
            DashOut,            // Shift+dash away from obelisk
            DashBackIn,         // Immediately dash back in
            EventOver,          // Timer expired, exit
        }

        public void OnEnter(BotContext ctx)
        {
            _phase = ResetterPhase.Idle;
            _phaseStartTime = DateTime.Now;
            _status = "";
            _error = null;
            _initiatorEntity = null;
            _initiatorFound = false;
            _leaderAttacking = false;
            _leaderAttackDuration = 0;
            _insideCircle = false;
            _lastDashTime = DateTime.MinValue;

            // Validate: must have a movement skill configured
            if (ctx.Navigation.MovementSkills.Count == 0)
            {
                _error = "No movement skill configured! Set a blink/dash skill in Build Settings.";
                _status = _error;
                ctx.Log($"[5Way] ERROR: {_error}");
                return;
            }

            _phase = ResetterPhase.NavigateToIdle;
            _phaseStartTime = DateTime.Now;
            ctx.Log("[5Way] Mode entered — navigating to idle position");
        }

        public void OnExit()
        {
            _phase = ResetterPhase.Idle;
            _status = "";
            _error = null;
        }

        public void Tick(BotContext ctx)
        {
            if (_error != null)
            {
                _status = _error;
                return;
            }

            var gc = ctx.Game;
            if (gc?.Player == null) return;

            var playerGrid = new Vector2(gc.Player.GridPosNum.X, gc.Player.GridPosNum.Y);
            var settings = ctx.Settings.LegionResetter;

            // Suppress combat always — we're an aura bot, not a fighter
            ctx.Combat.SuppressPositioning = true;

            // Scan for initiator
            ScanInitiator(gc);

            // Read circle state
            bool inCircle = false;
            int obeliskState = -1;
            if (_initiatorEntity != null && _initiatorEntity.TryGetComponent<StateMachine>(out var sm) && sm?.States != null)
            {
                foreach (var s in sm.States)
                {
                    if (s.Name == "checking_control_zone") inCircle = s.Value == 0;
                    if (s.Name == "obelisk_state") obeliskState = (int)s.Value;
                }
            }
            _insideCircle = inCircle;

            // Check timer
            var timerText = ReadTimer(gc);
            if (timerText == "00:00" && _phase != ResetterPhase.EventOver && _phase != ResetterPhase.Idle
                && _phase != ResetterPhase.NavigateToIdle && _phase != ResetterPhase.WaitForLeader)
            {
                _phase = ResetterPhase.EventOver;
                _phaseStartTime = DateTime.Now;
                ctx.Log("[5Way] Timer expired — exiting");
            }

            switch (_phase)
            {
                case ResetterPhase.NavigateToIdle:
                    TickNavigateToIdle(ctx, gc, playerGrid, settings);
                    break;
                case ResetterPhase.WaitForLeader:
                    TickWaitForLeader(ctx, gc, playerGrid, settings);
                    break;
                case ResetterPhase.DashIntoCircle:
                    TickDashIntoCircle(ctx, gc, playerGrid);
                    break;
                case ResetterPhase.WaitForSpawn:
                    TickWaitForSpawn(ctx, gc, playerGrid, settings);
                    break;
                case ResetterPhase.DashOut:
                    TickDashOut(ctx, gc, playerGrid);
                    break;
                case ResetterPhase.DashBackIn:
                    TickDashBackIn(ctx, gc, playerGrid);
                    break;
                case ResetterPhase.EventOver:
                    TickEventOver(ctx, gc, playerGrid);
                    break;
            }
        }

        // ── Phase Ticks ──

        private void TickNavigateToIdle(BotContext ctx, GameController gc, Vector2 playerGrid,
            BotSettings.LegionResetterSettings settings)
        {
            var idlePos = new Vector2(settings.IdlePositionX.Value, settings.IdlePositionY.Value);
            var dist = Vector2.Distance(playerGrid, idlePos);

            if (dist > 10)
            {
                if (!ctx.Navigation.IsNavigating)
                    ctx.Navigation.NavigateTo(gc, idlePos);
                _status = $"Walking to idle position ({dist:F0}g)";
                return;
            }

            // At idle position — look for initiator
            if (!_initiatorFound)
            {
                _status = "At idle — searching for obelisk...";
                return;
            }

            _phase = ResetterPhase.WaitForLeader;
            _phaseStartTime = DateTime.Now;
            _leaderAttacking = false;
            _leaderAttackDuration = 0;
            ctx.Log($"[5Way] At idle position, obelisk at ({_initiatorPos.X:F0},{_initiatorPos.Y:F0}) — waiting for leader");
        }

        private void TickWaitForLeader(BotContext ctx, GameController gc, Vector2 playerGrid,
            BotSettings.LegionResetterSettings settings)
        {
            var leaderName = ctx.Settings.Follower.LeaderName.Value;
            var threshold = settings.LeaderAttackThresholdSeconds.Value;

            // Find leader entity
            Entity? leader = null;
            if (!string.IsNullOrEmpty(leaderName))
            {
                try
                {
                    foreach (var e in gc.EntityListWrapper.OnlyValidEntities)
                    {
                        if (e.Type != EntityType.Player) continue;
                        var playerComp = e.GetComponent<Player>();
                        if (playerComp?.PlayerName == leaderName)
                        {
                            leader = e;
                            break;
                        }
                    }
                }
                catch { }
            }

            if (leader == null && !string.IsNullOrEmpty(leaderName))
            {
                _status = $"Waiting for leader '{leaderName}' to appear...";
                _leaderAttacking = false;
                _leaderAttackDuration = 0;
                return;
            }

            // No leader configured — start immediately after settle
            if (string.IsNullOrEmpty(leaderName))
            {
                var settleElapsed = (DateTime.Now - _phaseStartTime).TotalSeconds;
                if (settleElapsed > 3)
                {
                    StartCircleDance(ctx);
                    return;
                }
                _status = $"No leader — starting in {3 - settleElapsed:F0}s";
                return;
            }

            // Check leader animation
            var leaderActor = leader!.GetComponent<Actor>();
            var anim = leaderActor?.Animation ?? AnimationE.Idle;
            bool isAttacking = leaderActor?.Action == ActionFlags.UsingAbility
                || anim.ToString().Contains("Attack")
                || (anim != AnimationE.Idle && anim != AnimationE.Run
                    && leaderActor?.Action == ActionFlags.UsingAbility);

            // Use a simple check: action == UsingAbility means attacking
            isAttacking = leaderActor?.Action.HasFlag(ActionFlags.UsingAbility) == true;

            if (isAttacking)
            {
                if (!_leaderAttacking)
                {
                    _leaderAttacking = true;
                    _leaderAttackStartTime = DateTime.Now;
                }
                _leaderAttackDuration = (float)(DateTime.Now - _leaderAttackStartTime).TotalSeconds;
            }
            else
            {
                // Allow brief gaps (natural attack cooldowns) — only reset after 1s of idle
                if (_leaderAttacking && (DateTime.Now - _leaderAttackStartTime).TotalSeconds > _leaderAttackDuration + 1.0)
                {
                    _leaderAttacking = false;
                    _leaderAttackDuration = 0;
                }
            }

            if (_leaderAttackDuration >= threshold)
            {
                ctx.Log($"[5Way] Leader sustained attack for {_leaderAttackDuration:F1}s — starting circle dance!");
                StartCircleDance(ctx);
                return;
            }

            _status = _leaderAttacking
                ? $"Leader attacking ({_leaderAttackDuration:F1}s / {threshold:F1}s)"
                : "Waiting for leader to start attacking...";
        }

        private void StartCircleDance(BotContext ctx)
        {
            _phase = ResetterPhase.DashIntoCircle;
            _phaseStartTime = DateTime.Now;
            ctx.Log("[5Way] Circle dance started!");
        }

        private void TickDashIntoCircle(BotContext ctx, GameController gc, Vector2 playerGrid)
        {
            if (_insideCircle)
            {
                _phase = ResetterPhase.WaitForSpawn;
                _phaseStartTime = DateTime.Now;
                _enteredCircleTime = DateTime.Now;
                _status = "Inside circle — waiting for spawn";
                return;
            }

            // Dash toward the initiator
            if (DashToward(ctx, gc, _initiatorPos))
            {
                _status = "Dashing into circle...";
            }
            else
            {
                // Walk if dash not ready
                if (!ctx.Navigation.IsNavigating)
                    ctx.Navigation.NavigateTo(gc, _initiatorPos);
                _status = "Walking into circle (dash on cooldown)";
            }
        }

        private void TickWaitForSpawn(BotContext ctx, GameController gc, Vector2 playerGrid,
            BotSettings.LegionResetterSettings settings)
        {
            var elapsed = (DateTime.Now - _enteredCircleTime).TotalSeconds;
            var spawnDelay = settings.SpawnDelaySeconds.Value;

            if (!_insideCircle)
            {
                // Fell out of circle — dash back in
                _phase = ResetterPhase.DashIntoCircle;
                _status = "Fell out of circle — re-entering";
                return;
            }

            if (elapsed >= spawnDelay)
            {
                // Spawn should have triggered — dash out immediately
                _phase = ResetterPhase.DashOut;
                _phaseStartTime = DateTime.Now;
                ctx.Log($"[5Way] Spawn triggered ({elapsed:F1}s) — dashing out");
                return;
            }

            _status = $"In circle — spawn in {spawnDelay - elapsed:F1}s";
        }

        private void TickDashOut(BotContext ctx, GameController gc, Vector2 playerGrid)
        {
            if (!_insideCircle)
            {
                // Successfully outside — dash back in
                _phase = ResetterPhase.DashBackIn;
                _phaseStartTime = DateTime.Now;
                _status = "Outside circle — dashing back in";
                return;
            }

            // Shift + dash = dash backward (away from cursor which is aimed at obelisk)
            if (DashAwayFrom(ctx, gc, _initiatorPos))
            {
                _status = "Dashing OUT of circle...";
            }
            else
            {
                // Walk away if dash not ready
                var awayDir = Vector2.Normalize(playerGrid - _initiatorPos);
                var walkTarget = _initiatorPos + awayDir * (CircleRadius + 20);
                if (!ctx.Navigation.IsNavigating)
                    ctx.Navigation.NavigateTo(gc, walkTarget);
                _status = "Walking out (dash on cooldown)";
            }
        }

        private void TickDashBackIn(BotContext ctx, GameController gc, Vector2 playerGrid)
        {
            if (_insideCircle)
            {
                // Back inside — wait for next spawn
                _phase = ResetterPhase.WaitForSpawn;
                _phaseStartTime = DateTime.Now;
                _enteredCircleTime = DateTime.Now;
                _status = "Back in circle — waiting for spawn";
                return;
            }

            // Dash toward obelisk
            if (DashToward(ctx, gc, _initiatorPos))
            {
                _status = "Dashing back INTO circle...";
            }
            else
            {
                if (!ctx.Navigation.IsNavigating)
                    ctx.Navigation.NavigateTo(gc, _initiatorPos);
                _status = "Walking back in (dash on cooldown)";
            }
        }

        private void TickEventOver(BotContext ctx, GameController gc, Vector2 playerGrid)
        {
            // Find and click exit portal
            Entity? portal = null;
            try
            {
                foreach (var e in gc.EntityListWrapper.OnlyValidEntities)
                {
                    if (e.Type != EntityType.TownPortal && e.Type != EntityType.AreaTransition) continue;
                    if (!e.IsTargetable) continue;
                    portal = e;
                    break;
                }
            }
            catch { }

            if (portal != null)
            {
                if (!ctx.Interaction.IsBusy)
                    ctx.Interaction.InteractWithEntity(portal, ctx.Navigation, requireProximity: true);
                _status = $"Timer done — exiting via portal ({portal.DistancePlayer:F0}g)";
            }
            else
            {
                _status = "Timer done — looking for exit portal...";
            }
        }

        // ── Dash Helpers ──

        /// <summary>Dash toward a target position. Returns true if dash fired.</summary>
        private bool DashToward(BotContext ctx, GameController gc, Vector2 target)
        {
            if ((DateTime.Now - _lastDashTime).TotalMilliseconds < DashCooldownMs)
                return false;

            var skill = FindReadyMovementSkill(ctx);
            if (skill == null) return false;

            var screenPos = Pathfinding.GridToScreen(gc, target);
            var windowRect = gc.Window.GetWindowRectangle();
            if (screenPos.X <= 0 || screenPos.X >= windowRect.Width ||
                screenPos.Y <= 0 || screenPos.Y >= windowRect.Height)
                return false;

            var absPos = new Vector2(windowRect.X + screenPos.X, windowRect.Y + screenPos.Y);
            BotInput.ForceCursorPressKey(absPos, skill.Key);
            skill.LastUsedAt = DateTime.Now;
            _lastDashTime = DateTime.Now;
            return true;
        }

        /// <summary>Shift+dash away from target (dash backward). Returns true if fired.</summary>
        private bool DashAwayFrom(BotContext ctx, GameController gc, Vector2 target)
        {
            if ((DateTime.Now - _lastDashTime).TotalMilliseconds < DashCooldownMs)
                return false;

            var skill = FindReadyMovementSkill(ctx);
            if (skill == null) return false;

            // Aim cursor at the target (obelisk), then shift+skill = dash backward
            var screenPos = Pathfinding.GridToScreen(gc, target);
            var windowRect = gc.Window.GetWindowRectangle();
            if (screenPos.X <= 0 || screenPos.X >= windowRect.Width ||
                screenPos.Y <= 0 || screenPos.Y >= windowRect.Height)
                return false;

            var absPos = new Vector2(windowRect.X + screenPos.X, windowRect.Y + screenPos.Y);

            // Hold shift, then press the skill key
            // ForceCursorPressKey moves cursor + presses key. We need shift held during the press.
            // Use the async sequence: move cursor → hold shift → press key → release shift
            _ = DashBackwardAsync(absPos, skill.Key);
            skill.LastUsedAt = DateTime.Now;
            _lastDashTime = DateTime.Now;
            return true;
        }

        private async Task DashBackwardAsync(Vector2 absPos, Keys skillKey)
        {
            // Move cursor to target (obelisk center)
            ExileCore.Input.SetCursorPos(absPos);
            await Task.Delay(30);
            // Hold shift
            ExileCore.Input.KeyDown(Keys.ShiftKey);
            await Task.Delay(20);
            // Press skill
            ExileCore.Input.KeyDown(skillKey);
            await Task.Delay(40);
            ExileCore.Input.KeyUp(skillKey);
            await Task.Delay(20);
            // Release shift
            ExileCore.Input.KeyUp(Keys.ShiftKey);
        }

        private MovementSkillInfo? FindReadyMovementSkill(BotContext ctx)
        {
            foreach (var ms in ctx.Navigation.MovementSkills)
            {
                if (!ms.IsReady) continue;
                if (ms.MinCastIntervalMs > 0 &&
                    (DateTime.Now - ms.LastUsedAt).TotalMilliseconds < ms.MinCastIntervalMs)
                    continue;
                return ms;
            }
            return null;
        }

        // ── Entity Scanning ──

        private void ScanInitiator(GameController gc)
        {
            _initiatorEntity = null;
            try
            {
                foreach (var e in gc.EntityListWrapper.OnlyValidEntities)
                {
                    if (e.Path?.Contains(InitiatorPath) != true) continue;
                    _initiatorEntity = e;
                    _initiatorPos = e.GridPosNum;
                    _initiatorFound = true;
                    return;
                }
            }
            catch { }
        }

        private string ReadTimer(GameController gc)
        {
            try
            {
                var text = gc.IngameState.IngameUi
                    .GetChildAtIndex(25)?
                    .GetChildAtIndex(4)?
                    .GetChildAtIndex(0)?
                    .GetChildAtIndex(3)?
                    .GetChildAtIndex(0)?
                    .GetChildAtIndex(0)?
                    .Text;
                if (!string.IsNullOrEmpty(text))
                {
                    _lastTimerText = text;
                    return text;
                }
            }
            catch { }
            return _lastTimerText;
        }

        // ── Render ──

        public void Render(BotContext ctx)
        {
            var gc = ctx.Game;
            var g = ctx.Graphics;
            if (gc?.Player == null || g == null) return;

            var cam = gc.IngameState.Camera;

            // Draw obelisk marker
            if (_initiatorFound)
            {
                var world = new Vector3(_initiatorPos.X * 10.88f, _initiatorPos.Y * 10.88f, 0);
                var screen = cam.WorldToScreen(world);
                if (screen.X > 0 && screen.X < 2400)
                {
                    g.DrawText("OBELISK", screen + new Vector2(-30, -30), SharpDX.Color.Cyan);

                    // Draw circle radius
                    for (int i = 0; i < 16; i++)
                    {
                        float a = i * MathF.PI / 8f;
                        var pt = _initiatorPos + new Vector2(MathF.Cos(a) * CircleRadius, MathF.Sin(a) * CircleRadius);
                        var ptWorld = new Vector3(pt.X * 10.88f, pt.Y * 10.88f, 0);
                        var ptScreen = cam.WorldToScreen(ptWorld);
                        if (ptScreen.X > 0 && ptScreen.X < 2400)
                        {
                            var color = _insideCircle ? SharpDX.Color.LimeGreen : SharpDX.Color.Yellow;
                            g.DrawText(".", ptScreen, color);
                        }
                    }
                }
            }

            // HUD
            float hudX = 20, hudY = 250, lineH = 18;
            var phaseColor = _phase switch
            {
                ResetterPhase.WaitForLeader => SharpDX.Color.Yellow,
                ResetterPhase.WaitForSpawn => SharpDX.Color.Cyan,
                ResetterPhase.DashIntoCircle or ResetterPhase.DashBackIn => SharpDX.Color.LimeGreen,
                ResetterPhase.DashOut => SharpDX.Color.Orange,
                ResetterPhase.EventOver => SharpDX.Color.Red,
                _ => SharpDX.Color.White,
            };

            g.DrawText($"5-Way: {_phase}", new Vector2(hudX, hudY), phaseColor);
            hudY += lineH;
            g.DrawText(_status, new Vector2(hudX, hudY), SharpDX.Color.Gray);
            hudY += lineH;
            if (!string.IsNullOrEmpty(_lastTimerText))
                g.DrawText($"Timer: {_lastTimerText}", new Vector2(hudX, hudY), SharpDX.Color.White);
        }
    }
}
