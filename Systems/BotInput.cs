using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using ExileCore;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;
using Input = ExileCore.Input;

namespace AutoExile.Systems
{
    /// <summary>
    /// Record of a single action sent (or rejected) by BotInput.
    /// </summary>
    public record ActionRecord(
        DateTime Timestamp,
        string Type,        // CursorPressKey, PressKey, Click, RightClick, CtrlClick
        Vector2? Position,  // screen position (null for PressKey)
        Keys? Key,          // key pressed (null for Click/RightClick)
        bool Accepted       // true if gate was open and action fired
    );

    /// <summary>
    /// Global action gate. ALL game inputs go through here.
    ///
    /// Design:
    /// - Single NextActionAt timestamp. Checked via CanAct — if not ready, actions are rejected.
    /// - Bot logic (modes, combat, exploration) ALWAYS ticks to keep state fresh.
    /// - Only actual input calls (Click, PressKey, etc.) are gated by CanAct.
    /// - When an action fires: set NextActionAt to now + full duration, then run async sequence.
    /// - Sequence: interpolate cursor → settle → button/key down → hold → up.
    /// - All delays use Gaussian distribution for human-like timing variance.
    /// - Mouse movement uses linear interpolation with random intermediate points.
    /// - All actions are logged to a ring buffer for post-hoc analysis.
    /// - Window bounds are enforced: positions outside the game window are clamped inward.
    /// </summary>
    public static class BotInput
    {
        // ── Window bounds (set by BotCore each tick) ──
        /// <summary>Game window rect in absolute screen coordinates. Set by BotCore each tick.</summary>
        public static SharpDX.RectangleF WindowRect;

        /// <summary>
        /// Clamp a position to stay within the game window (with padding to avoid edge pixels).
        /// Returns false if WindowRect is unset (zero-size).
        /// </summary>
        private static bool ClampToWindow(ref Vector2 pos)
        {
            if (WindowRect.Width < 10 || WindowRect.Height < 10)
                return false; // window rect not set or too small

            const float pad = 5f;
            var minX = WindowRect.X + pad;
            var maxX = WindowRect.X + WindowRect.Width - pad;
            var minY = WindowRect.Y + pad;
            var maxY = WindowRect.Y + WindowRect.Height - pad;

            pos.X = Math.Clamp(pos.X, minX, maxX);
            pos.Y = Math.Clamp(pos.Y, minY, maxY);
            return true;
        }

        // ── Action Log ──
        private static readonly ActionRecord[] _actionLog = new ActionRecord[500];
        private static int _actionLogIndex;
        private static int _actionLogCount;

        /// <summary>Get the last N action records (newest first).</summary>
        public static List<ActionRecord> GetRecentActions(int count = 50)
        {
            var result = new List<ActionRecord>(Math.Min(count, _actionLogCount));
            for (int i = 0; i < count && i < _actionLogCount; i++)
            {
                var idx = (_actionLogIndex - 1 - i + _actionLog.Length) % _actionLog.Length;
                result.Add(_actionLog[idx]);
            }
            return result;
        }

        /// <summary>Total actions logged this session.</summary>
        public static int TotalActionsLogged => _actionLogCount;

        // ── Input Rate Monitoring ──

        /// <summary>Rolling count of raw input events in the last 1 second.</summary>
        public static int RawInputEventsPerSecond { get; private set; }
        private static int _rawInputSecondCount;
        private static DateTime _rawInputSecondStart = DateTime.MinValue;

        private static void LogRawInput(string eventType, string detail)
        {
            var now = DateTime.Now;
            if ((now - _rawInputSecondStart).TotalSeconds >= 1.0)
            {
                RawInputEventsPerSecond = _rawInputSecondCount;
                _rawInputSecondCount = 0;
                _rawInputSecondStart = now;
            }
            _rawInputSecondCount++;
        }

        private static void LogAction(string type, Vector2? position, Keys? key, bool accepted)
        {
            _actionLog[_actionLogIndex] = new ActionRecord(DateTime.Now, type, position, key, accepted);
            _actionLogIndex = (_actionLogIndex + 1) % _actionLog.Length;
            if (_actionLogCount < _actionLog.Length) _actionLogCount++;
        }

        // ── Click position randomization ──

        /// <summary>
        /// Return a center-biased random point within a rectangle.
        /// Uses triangular distribution (average of two uniforms) so clicks cluster
        /// near the center but occasionally land toward edges — more human-like and
        /// helps bypass overlapping entities.
        /// </summary>
        /// <param name="centerX">Center X of the clickable area (window-relative).</param>
        /// <param name="centerY">Center Y of the clickable area (window-relative).</param>
        /// <param name="halfWidth">Half the width of the clickable area.</param>
        /// <param name="halfHeight">Half the height of the clickable area.</param>
        public static Vector2 RandomizeWithinRect(float centerX, float centerY, float halfWidth, float halfHeight)
        {
            // Triangular distribution: average of two uniforms → peaks at center, tapers to edges
            float rx = (float)(_rng.NextDouble() + _rng.NextDouble() - 1.0); // range [-1, 1], centered
            float ry = (float)(_rng.NextDouble() + _rng.NextDouble() - 1.0);
            return new Vector2(centerX + rx * halfWidth, centerY + ry * halfHeight);
        }

        /// <summary>
        /// Return a center-biased random point within a label/client rect.
        /// Convenience overload for SharpDX.RectangleF.
        /// </summary>
        public static Vector2 RandomizeWithinRect(SharpDX.RectangleF rect)
        {
            return RandomizeWithinRect(
                rect.X + rect.Width / 2f,
                rect.Y + rect.Height / 2f,
                rect.Width * 0.4f,  // stay within inner 80% of rect
                rect.Height * 0.4f);
        }

        // ── High-level click helpers (entity / label → randomized screen click) ──

        /// <summary>
        /// Click a world entity with hover verification via Targetable.isTargeted.
        /// Moves cursor to a random position within the entity's screen bounds, waits for settle,
        /// checks if the game reports the entity as targeted (highlighted). If not, retries at
        /// a different position. Once confirmed targeted, clicks.
        /// This handles overlapping entities (players on top of stash/monolith) — the bot
        /// keeps sampling positions until it finds one that targets the correct entity.
        /// </summary>
        /// <returns>True if the click sequence was initiated, false if gate blocked or entity off-screen.</returns>
        public static bool ClickEntity(GameController gc, Entity entity)
        {
            if (!CanAct) return false;
            if (!GetEntityScreenBounds(gc, entity, out var screenCenter, out var halfW, out var halfH))
                return false;

            SuspendMovement();
            var windowRect = gc.Window.GetWindowRectangle();
            var settle = RandSettle();
            var hold = RandHold();

            // Reserve gate for worst case: MaxHoverAttempts * (move + settle) + click hold + cooldown
            var moveMs = EstimateMoveMs(new Vector2(windowRect.X + screenCenter.X, windowRect.Y + screenCenter.Y));
            NextActionAt = DateTime.Now.AddMilliseconds(
                MaxHoverAttempts * (moveMs + settle) + hold + ActionCooldownMs);

            _ = DoClickEntityWithVerify(gc, entity, screenCenter, halfW, halfH, windowRect, settle, hold);
            LogAction("ClickEntity", screenCenter, null, true);
            return true;
        }

        private const int MaxHoverAttempts = 2;
        private const int HoverVerifyDelayMs = 35; // time after cursor settle to let game update isTargeted

        private static async Task DoClickEntityWithVerify(
            GameController gc, Entity entity, Vector2 screenCenter, float halfW, float halfH,
            SharpDX.RectangleF windowRect, int settleMs, int holdMs)
        {
            for (int attempt = 0; attempt < MaxHoverAttempts; attempt++)
            {
                // Re-read entity screen position each attempt — compensates for camera
                // movement during interpolation + settle (player sliding from momentum)
                try
                {
                    if (GetEntityScreenBounds(gc, entity, out var freshCenter, out var freshHW, out var freshHH))
                    {
                        screenCenter = freshCenter;
                        halfW = freshHW;
                        halfH = freshHH;
                        windowRect = gc.Window.GetWindowRectangle();
                    }
                }
                catch { }

                // Pick a random position within entity bounds — first attempt is center-biased,
                // subsequent attempts spread wider to find an unblocked spot
                var spread = attempt == 0 ? 1f : 1f + attempt * 0.3f;
                var clickPos = RandomizeWithinRect(screenCenter.X, screenCenter.Y,
                    halfW * spread, halfH * spread);
                var absPos = new Vector2(windowRect.X + clickPos.X, windowRect.Y + clickPos.Y);

                // Move cursor and settle
                await MoveCursorTo(absPos);
                await Task.Delay(settleMs);

                // Final snap — re-read entity bounds right before verification/click
                try
                {
                    if (GetEntityScreenBounds(gc, entity, out var snapCenter, out var snapHW, out var snapHH))
                    {
                        var snapPos = RandomizeWithinRect(snapCenter.X, snapCenter.Y, snapHW, snapHH);
                        var wr = gc.Window.GetWindowRectangle();
                        Input.SetCursorPos(new Vector2(wr.X + snapPos.X, wr.Y + snapPos.Y));
                    }
                }
                catch { }

                // Verify the game reports this entity as targeted (hover highlight)
                try
                {
                    var targetable = entity.GetComponent<Targetable>();
                    if (targetable?.isTargeted == true)
                    {
                        // Confirmed — click now
                        await SendDelay();
                        SendLeftDown("entity-verified");
                        await Task.Delay(holdMs);
                        await SendDelay();
                        SendLeftUp("entity-verified");
                        return;
                    }
                }
                catch { }

                // Not targeted — wait briefly then try next position
                await Task.Delay(HoverVerifyDelayMs);
            }

            // Exhausted attempts — final snap then click anyway as fallback
            try
            {
                if (GetEntityScreenBounds(gc, entity, out var lastCenter, out var lastHW, out var lastHH))
                {
                    var lastPos = RandomizeWithinRect(lastCenter.X, lastCenter.Y, lastHW, lastHH);
                    var wr = gc.Window.GetWindowRectangle();
                    Input.SetCursorPos(new Vector2(wr.X + lastPos.X, wr.Y + lastPos.Y));
                }
            }
            catch { }
            await SendDelay();
            SendLeftDown("entity-fallback");
            await Task.Delay(holdMs);
            await SendDelay();
            SendLeftUp("entity-fallback");
        }

        /// <summary>
        /// Check if a window-relative rect is within the game window (not off-screen).
        /// Returns false if the rect center is outside the window bounds.
        /// </summary>
        public static bool IsRectOnScreen(SharpDX.RectangleF rect)
        {
            if (WindowRect.Width < 10 || WindowRect.Height < 10)
                return false;

            var centerX = rect.X + rect.Width / 2;
            var centerY = rect.Y + rect.Height / 2;
            const float pad = 5f;
            return centerX >= pad && centerX <= WindowRect.Width - pad &&
                   centerY >= pad && centerY <= WindowRect.Height - pad;
        }

        /// <summary>
        /// Click within a label/UI element rect with center-biased randomization.
        /// The rect should be in window-relative coordinates (from GetClientRect/ClientRect).
        /// </summary>
        /// <returns>True if the click was sent, false if gate blocked or label off-screen.</returns>
        public static bool ClickLabel(GameController gc, SharpDX.RectangleF rect)
        {
            if (!IsRectOnScreen(rect)) return false;
            var clickPos = RandomizeWithinRect(rect);
            var windowRect = gc.Window.GetWindowRectangle();
            var absPos = new Vector2(windowRect.X + clickPos.X, windowRect.Y + clickPos.Y);
            return Click(absPos);
        }

        /// <summary>
        /// Click a label with a fresh-position correction callback.
        /// The callback is invoked right before mousedown to get the label's
        /// current screen position, compensating for character slide during
        /// cursor interpolation + settle delay. The cursor is already very
        /// close from interpolation — this is a small final snap.
        /// </summary>
        /// <param name="rectProvider">Called at click time to get the label's current rect.
        /// Return null to skip correction (label disappeared).</param>
        public static bool ClickLabelCorrected(GameController gc, SharpDX.RectangleF rect,
            Func<SharpDX.RectangleF?> rectProvider)
        {
            if (!IsRectOnScreen(rect)) return false;
            var clickPos = RandomizeWithinRect(rect);
            var windowRect = gc.Window.GetWindowRectangle();
            var absPos = new Vector2(windowRect.X + clickPos.X, windowRect.Y + clickPos.Y);

            // Correction: re-read label rect at click time, snap to fresh center
            Func<Vector2?> correction = () =>
            {
                var freshRect = rectProvider();
                if (!freshRect.HasValue) return null;
                var wr = gc.Window.GetWindowRectangle();
                var fresh = RandomizeWithinRect(freshRect.Value);
                return new Vector2(wr.X + fresh.X, wr.Y + fresh.Y);
            };

            return Click(absPos, correction);
        }

        /// <summary>
        /// Click within a label rect with hover verification via Targetable.isTargeted.
        /// Moves cursor to a random position within the rect, waits for settle, then checks
        /// that the expected entity is targeted. If a different entity is under the cursor
        /// (overlapping labels), retries at different positions. Aborts without clicking if
        /// verification fails on all attempts.
        /// </summary>
        /// <returns>True if initiated, false if gate blocked. Check WasVerified after completion.</returns>
        public static bool ClickLabelVerified(GameController gc, SharpDX.RectangleF rect, Entity entity,
            Func<SharpDX.RectangleF?>? rectProvider = null)
        {
            if (!CanAct) return false;
            if (!IsRectOnScreen(rect)) return false;
            SuspendMovement();
            var windowRect = gc.Window.GetWindowRectangle();
            var settle = RandSettle();
            var hold = RandHold();
            var center = new Vector2(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);
            var moveMs = EstimateMoveMs(new Vector2(windowRect.X + center.X, windowRect.Y + center.Y));
            NextActionAt = DateTime.Now.AddMilliseconds(
                MaxHoverAttempts * (moveMs + settle) + hold + ActionCooldownMs);
            _ = DoClickLabelVerified(entity, rect, windowRect, settle, hold, rectProvider);
            LogAction("ClickLabelVerified", center, null, true);
            return true;
        }

        /// <summary>True if the last ClickLabelVerified confirmed the correct entity before clicking.</summary>
        public static bool LastClickWasVerified { get; private set; }

        private static async Task DoClickLabelVerified(
            Entity entity, SharpDX.RectangleF rect, SharpDX.RectangleF windowRect,
            int settleMs, int holdMs, Func<SharpDX.RectangleF?>? rectProvider = null)
        {
            LastClickWasVerified = false;

            for (int attempt = 0; attempt < MaxHoverAttempts; attempt++)
            {
                // Use fresh rect if available (compensates for character slide)
                var useRect = rectProvider?.Invoke() ?? rect;
                var useWindowRect = rectProvider != null
                    ? new SharpDX.RectangleF(windowRect.X, windowRect.Y, windowRect.Width, windowRect.Height)
                    : windowRect;

                var clickPos = RandomizeWithinRect(useRect);
                var absPos = new Vector2(useWindowRect.X + clickPos.X, useWindowRect.Y + clickPos.Y);

                await MoveCursorTo(absPos);
                await Task.Delay(settleMs);

                // Final snap correction — re-read label position right before verification
                if (rectProvider != null)
                {
                    var correctedRect = rectProvider.Invoke();
                    if (correctedRect.HasValue)
                    {
                        var correctedPos = RandomizeWithinRect(correctedRect.Value);
                        var correctedAbs = new Vector2(useWindowRect.X + correctedPos.X, useWindowRect.Y + correctedPos.Y);
                        Input.SetCursorPos(correctedAbs);
                    }
                }

                try
                {
                    var targetable = entity.GetComponent<Targetable>();
                    if (targetable?.isTargeted == true)
                    {
                        LastClickWasVerified = true;
                        await SendDelay();
                        SendLeftDown("label-verified");
                        await Task.Delay(holdMs);
                        await SendDelay();
                        SendLeftUp("label-verified");
                        return;
                    }
                }
                catch { }

                await Task.Delay(HoverVerifyDelayMs);
            }

            // Verification failed on all attempts — DON'T click.
            // Clicking an unverified target risks hitting overlapping entities
            // (e.g. ritual altar opens shop, NPC opens dialog).
            // The interaction system will detect the failed attempt and either
            // reposition the player or mark the item as blocked.
            LastClickWasVerified = false;
        }

        /// <summary>
        /// Get screen-space center and half-extents for an entity, derived from Render.BoundsNum.
        /// Useful when callers need the position for overlap checks before clicking.
        /// </summary>
        /// <returns>True if entity is on-screen; outputs screen center and half-extents.</returns>
        public static bool GetEntityScreenBounds(GameController gc, Entity entity,
            out Vector2 screenCenter, out float halfW, out float halfH)
        {
            var center = entity.BoundsCenterPosNum;
            var camera = gc.IngameState.Camera;
            screenCenter = camera.WorldToScreen(center);
            halfW = 12f;
            halfH = 8f;

            var windowRect = gc.Window.GetWindowRectangle();
            if (screenCenter.X < 5 || screenCenter.X > windowRect.Width - 5 ||
                screenCenter.Y < 5 || screenCenter.Y > windowRect.Height - 5)
                return false;

            var render = entity.GetComponent<Render>();
            if (render != null)
            {
                var bounds = render.BoundsNum;
                var offsetWorld = new Vector3(center.X + bounds.X * 0.4f, center.Y, center.Z);
                var screenOffset = camera.WorldToScreen(offsetWorld);
                var dx = Math.Abs(screenOffset.X - screenCenter.X);
                var offsetWorldY = new Vector3(center.X, center.Y + bounds.Y * 0.4f, center.Z);
                var screenOffsetY = camera.WorldToScreen(offsetWorldY);
                var dy = Math.Abs(screenOffsetY.Y - screenCenter.Y);
                if (dx > 3f) halfW = dx;
                if (dy > 3f) halfH = dy;
            }

            return true;
        }

        /// <summary>Minimum ms between actions (end of one action to start of next). Configurable.</summary>
        public static int ActionCooldownMs = 100;




        /// <summary>
        /// Hard floor: minimum ms since last raw input before the tick loop should
        /// run any mode/nav/combat logic. Catches async race conditions and
        /// movement layer interleaving that the per-action gate can't prevent.
        /// If ANY raw input was sent within this window, BotCore skips the tick.
        /// </summary>
        public const int HardFloorMs = 50;

        /// <summary>
        /// True if at least HardFloorMs have elapsed since the last raw input event.
        /// BotCore checks this before running mode/nav logic to prevent input floods
        /// from async race conditions and movement layer interleaving.
        /// </summary>
        public static bool CanTick =>
            (DateTime.Now - _lastInputEvent).TotalMilliseconds >= HardFloorMs;

        /// <summary>Global gate — no actions before this time.</summary>
        public static DateTime NextActionAt = DateTime.MinValue;

        /// <summary>
        /// Last time any input event (KeyDown/KeyUp/MouseDown/MouseUp) was sent.
        /// Enforces a minimum gap between ALL input events across ALL paths
        /// (movement, overlay, discrete, clicks). Prevents total input rate
        /// from exceeding human-plausible levels.
        /// </summary>
        private static DateTime _lastInputEvent = DateTime.MinValue;

        /// <summary>
        /// Minimum ms between any two raw input events (key/mouse down/up).
        /// Derived from ActionCooldownMs — uses the same user-configurable setting.
        /// This is the single rate limiter for ALL input to the game.
        /// </summary>
        private static int MinInputEventGapMs => ActionCooldownMs;

        /// <summary>Check if enough time has passed since the last input event.</summary>
        private static bool CanSendInputEvent =>
            (DateTime.Now - _lastInputEvent).TotalMilliseconds >= MinInputEventGapMs;

        /// <summary>Record that an input event was just sent.</summary>
        private static void MarkInputEvent(string eventType = "?", string detail = "")
        {
            _lastInputEvent = DateTime.Now;
            LogRawInput(eventType, detail);
        }

        // ── Raw Input Wrappers ──
        // ALL game input MUST go through these. They log every event, enforce
        // the global rate limit, and are the ONLY methods that call ExileCore's
        // Input class directly. Everything else in BotInput uses these.
        //
        // These are intentionally private — external code uses the public
        // action methods (Click, PressKey, PressKeyOverlay, StartMovement, etc.)
        // which compose these primitives with proper gating and timing.
        //
        // "Down" events (key press, mouse press) are the ones the server counts
        // as actions. They enforce the minimum input gap and will be DROPPED if
        // called too soon — async callers must use SendDelay() before them.
        //
        // "Up" events (key release, mouse release) are cleanup — the server
        // doesn't penalise releases. They always send immediately but still
        // mark _lastInputEvent so the NEXT down event respects the gap.

        private static void SendKeyDown(Keys key, string context = "")
        {
            if (!CanSendInputEvent)
            {
                LogRawInput("KeyDown-DROPPED", $"{key} {context} (too soon: {(DateTime.Now - _lastInputEvent).TotalMilliseconds:F0}ms)".Trim());
                return;
            }
            MarkInputEvent("KeyDown", $"{key} {context}".Trim());
            Input.KeyDown(key);
        }

        private static void SendKeyUp(Keys key, string context = "")
        {
            MarkInputEvent("KeyUp", $"{key} {context}".Trim());
            Input.KeyUp(key);
        }

        private static void SendLeftDown(string context = "")
        {
            if (!CanSendInputEvent)
            {
                LogRawInput("LeftDown-DROPPED", $"{context} (too soon: {(DateTime.Now - _lastInputEvent).TotalMilliseconds:F0}ms)".Trim());
                return;
            }
            MarkInputEvent("LeftDown", context);
            Input.LeftDown();
        }

        private static void SendLeftUp(string context = "")
        {
            MarkInputEvent("LeftUp", context);
            Input.LeftUp();
        }

        private static void SendRightDown(string context = "")
        {
            if (!CanSendInputEvent)
            {
                LogRawInput("RightDown-DROPPED", $"{context} (too soon: {(DateTime.Now - _lastInputEvent).TotalMilliseconds:F0}ms)".Trim());
                return;
            }
            MarkInputEvent("RightDown", context);
            Input.RightDown();
        }

        private static void SendRightUp(string context = "")
        {
            MarkInputEvent("RightUp", context);
            Input.RightUp();
        }

        /// <summary>
        /// Async delay that enforces the minimum gap between input events.
        /// Call before sending a new input event in async methods.
        /// </summary>
        private static async Task SendDelay()
        {
            var elapsed = (DateTime.Now - _lastInputEvent).TotalMilliseconds;
            if (elapsed < MinInputEventGapMs)
                await Task.Delay((int)(MinInputEventGapMs - elapsed));
        }

        private static readonly Random _rng = new();

        /// <summary>True if we can start a new action right now.</summary>
        public static bool CanAct => ReplayMode || DateTime.Now >= NextActionAt;

        // ══════════════════════════════════════════════════════════════
        // Continuous movement layer
        // Holds a movement key down continuously while updating cursor
        // position each tick. Discrete actions (clicks, targeted skills)
        // briefly suspend movement, execute, then resume automatically.
        //
        // Human play pattern: hold move key → tap skills → click loot
        // Bot pattern before: [move-click] [100ms wait] [move-click] ...
        // Bot pattern now:    [hold-move ──── tap-skill ── click-loot ──]
        // ══════════════════════════════════════════════════════════════

        // ── Move-only key safety ──
        // PoE's move-only skill walks to the cursor position. Two failure modes:
        //   1. Cursor sitting on the player character → nothing to walk toward.
        //   2. A modifier key (Ctrl/Shift/Alt) is held at the moment the move key
        //      goes down → the game sees Ctrl+MoveKey, not MoveKey. Ctrl+move-only
        //      is "attack in place" in PoE, which looks exactly like our stall.
        // Guards live in StartMovement / ResumeMovement, not in the high-level
        // walk helpers — so every caller of StartMovement gets them.

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        /// <summary>True if the OS reports a modifier key as physically held
        /// (outside our own tracking — e.g. a user-pressed key or a leaked
        /// KeyDown from an earlier action we failed to release).</summary>
        private static bool OsHasModifierHeld()
        {
            const int mask = unchecked((short)0x8000);
            return (GetAsyncKeyState((int)Keys.LControlKey) & mask) != 0
                || (GetAsyncKeyState((int)Keys.RControlKey) & mask) != 0
                || (GetAsyncKeyState((int)Keys.LShiftKey)   & mask) != 0
                || (GetAsyncKeyState((int)Keys.RShiftKey)   & mask) != 0
                || (GetAsyncKeyState((int)Keys.LMenu)       & mask) != 0
                || (GetAsyncKeyState((int)Keys.RMenu)       & mask) != 0;
        }

        /// <summary>Force-release any tracked modifier keys AND flush any OS-level
        /// modifier state before the move key fires. Called from StartMovement /
        /// ResumeMovement so Ctrl+move-only can never reach the game.</summary>
        private static void ReleaseAllModifiersBeforeMove()
        {
            // Never touch modifiers while CtrlClickBatch is executing — it holds
            // Ctrl intentionally across many clicks and manages its own lifecycle.
            if (IsBatchRunning) return;

            // 1. Release anything we're tracking as held.
            ReleaseTrackedModifiers();

            // 2. Belt-and-braces: if the OS still reports a modifier held (leaked
            //    from a prior CtrlClickBatch / crashed flow / user physically
            //    pressing Ctrl), send explicit KeyUps. This can't forcibly undo
            //    a physically-held key, but it clears any ghost state from our
            //    own KeyDown events that TrackHeldKey never recorded.
            if (!OsHasModifierHeld()) return;
            SendKeyUp(Keys.LControlKey, "movement-guard");
            SendKeyUp(Keys.RControlKey, "movement-guard");
            SendKeyUp(Keys.LShiftKey,   "movement-guard");
            SendKeyUp(Keys.RShiftKey,   "movement-guard");
            SendKeyUp(Keys.LMenu,       "movement-guard");
            SendKeyUp(Keys.RMenu,       "movement-guard");
        }

        private static void ReleaseTrackedModifiers()
        {
            if (_heldKeys.Count == 0) return;
            var toRelease = new List<Keys>();
            foreach (var k in _heldKeys.Keys)
            {
                if (k == Keys.LControlKey || k == Keys.RControlKey || k == Keys.ControlKey ||
                    k == Keys.LShiftKey   || k == Keys.RShiftKey   || k == Keys.ShiftKey   ||
                    k == Keys.LMenu       || k == Keys.RMenu       || k == Keys.Menu)
                    toRelease.Add(k);
            }
            foreach (var k in toRelease)
            {
                SendKeyUp(k, "movement-guard");
                _heldKeys.Remove(k);
            }
        }

        /// <summary>Minimum pixel distance from window center for a move-only target.
        /// PoE's move-only walks TO the cursor — if the cursor is on the player
        /// (≈ window center) the character stands still. Push it out.</summary>
        private const float MoveCursorMinOffset = 60f;

        /// <summary>If the target is too close to the player on screen, shift it
        /// outward along the same direction. Returns the (possibly-nudged) target.
        /// If the target is exactly on center, nudges straight down.</summary>
        private static Vector2 NudgeOffPlayer(Vector2 absScreenPos)
        {
            if (WindowRect.Width < 10 || WindowRect.Height < 10) return absScreenPos;
            var center = new Vector2(
                WindowRect.X + WindowRect.Width  / 2f,
                WindowRect.Y + WindowRect.Height / 2f);
            var dir = absScreenPos - center;
            var len = dir.Length();
            if (len >= MoveCursorMinOffset) return absScreenPos;
            var unit = len < 0.5f ? new Vector2(0, 1) : dir / len;
            return center + unit * MoveCursorMinOffset;
        }

        /// <summary>True if the movement layer is actively holding a movement key.</summary>
        public static bool IsMovementActive { get; private set; }

        /// <summary>The key currently held for movement (e.g. move-only key T).</summary>
        private static Keys _movementKey;

        /// <summary>Last screen position the movement cursor was set to.</summary>
        private static Vector2 _movementCursorPos;

        /// <summary>True if movement is temporarily suspended for a discrete action.</summary>
        public static bool IsMovementSuspended { get; private set; }




        /// <summary>
        /// Start continuous movement: hold the movement key and set cursor toward target.
        /// Call UpdateMovementCursor() each tick to steer. Movement stays active until
        /// StopMovement() is called or it's suspended by a discrete action.
        /// Does NOT go through the action gate — movement is a background layer.
        /// </summary>
        public static bool StartMovement(Vector2 absScreenPos, Keys moveKey)
        {
            if (TryCaptureReplay("StartMovement", absScreenPos, moveKey)) return true;
            if (!ClampToWindow(ref absScreenPos)) return false;

            // Move-only walks TO the cursor. If the target is on the player the
            // character stands still. Safety-nudge every call, including the
            // steer-only fast path below.
            absScreenPos = NudgeOffPlayer(absScreenPos);

            // If already moving with the same key and not suspended, just steer cursor.
            // No key release/press needed — the key is already held.
            if (IsMovementActive && _movementKey == moveKey && !IsMovementSuspended)
            {
                Input.SetCursorPos(absScreenPos);
                _movementCursorPos = absScreenPos;
                return true;
            }

            // If suspended with the same key, just update the stored cursor position.
            // TickMovementLayer will resume when the minimum delay has elapsed.
            // Don't press KeyDown here — let ResumeMovement handle it with proper timing.
            if (IsMovementActive && _movementKey == moveKey && IsMovementSuspended)
            {
                _movementCursorPos = absScreenPos;
                return true;
            }

            // Switching to a different key — release old, press new
            if (IsMovementActive && _movementKey != moveKey)
            {
                SendKeyUp(_movementKey, "movement");
            }

            // Flush any held modifiers so the move key doesn't register as
            // Ctrl+move (attack-in-place) / Shift+move / Alt+move.
            ReleaseAllModifiersBeforeMove();

            Input.SetCursorPos(absScreenPos);
            _movementCursorPos = absScreenPos;
            _movementKey = moveKey;

            IsMovementActive = true;

            // Press the movement key if the input rate allows.
            // If too soon after the last input event, start in suspended state —
            // TickMovementLayer will retry on the next frame via ResumeMovement.
            if (!IsMovementSuspended && CanSendInputEvent)
            {
                SendKeyDown(moveKey, "movement");
                IsMovementSuspended = false;
            }
            else
            {
                IsMovementSuspended = true;
            }

            LogAction("StartMovement", absScreenPos, moveKey, true);
            return true;
        }

        /// <summary>
        /// Update the cursor position while movement is active.
        /// Throttled — only sends SetCursorPos when the target moved significantly
        /// or enough time has passed. Prevents flooding the game with mouse events.
        /// Called every tick by NavigationSystem to steer the character.
        /// </summary>
        private static DateTime _lastCursorUpdate = DateTime.MinValue;
        private const int CursorUpdateMinIntervalMs = 50; // ~20 updates/sec max
        private const float CursorUpdateMinDistSq = 25f;  // 5px minimum change

        public static bool UpdateMovementCursor(Vector2 absScreenPos)
        {
            if (!IsMovementActive || IsMovementSuspended) return false;
            if (TryCaptureReplay("UpdateMovementCursor", absScreenPos)) return true;
            if (!ClampToWindow(ref absScreenPos)) return false;
            absScreenPos = NudgeOffPlayer(absScreenPos);

            // Throttle: only update if position changed significantly or enough time passed
            var distSq = Vector2.DistanceSquared(absScreenPos, _movementCursorPos);
            var elapsed = (DateTime.Now - _lastCursorUpdate).TotalMilliseconds;
            if (distSq < CursorUpdateMinDistSq && elapsed < CursorUpdateMinIntervalMs)
            {
                return true; // Skip this update — position hasn't changed meaningfully
            }

            Input.SetCursorPos(absScreenPos);
            _movementCursorPos = absScreenPos;
            _lastCursorUpdate = DateTime.Now;
            return true;
        }

        /// <summary>
        /// Stop continuous movement and release the held key.
        /// Called when navigation stops, on area change, mode exit, etc.
        /// </summary>
        public static void StopMovement()
        {
            if (!IsMovementActive) return;

            SendKeyUp(_movementKey, "stop");
            IsMovementActive = false;
            IsMovementSuspended = false;
        }

        /// <summary>
        /// Suspend movement temporarily for a discrete action (click, targeted skill).
        /// Before releasing the move key, snaps the cursor to the player's screen position
        /// (≈ window center). Since the move-only skill walks toward the cursor position,
        /// this tells the game "walk to where you already are" — effectively a stop command.
        /// Without this, releasing the key leaves the character walking toward the last
        /// cursor target (potentially far away), causing sustained drift during the click.
        /// </summary>
        public static void SuspendMovement()
        {
            if (!IsMovementActive || IsMovementSuspended) return;

            // Snap cursor to player position (screen center) while key is still held.
            // The game processes this on the next tick: "walk to here" = stop in place.
            // Then we release the key, and the character has minimal residual movement.
            if (WindowRect.Width > 10 && WindowRect.Height > 10)
            {
                var playerScreenPos = new Vector2(
                    WindowRect.X + WindowRect.Width / 2f,
                    WindowRect.Y + WindowRect.Height / 2f);
                Input.SetCursorPos(playerScreenPos);
            }

            SendKeyUp(_movementKey, "suspend");
            IsMovementSuspended = true;
        }

        /// <summary>
        /// Resume movement after a discrete action completes.
        /// Re-presses the movement key and restores cursor to last movement position.
        /// Enforces a minimum delay since the key was released (SuspendMovement)
        /// to avoid rapid KeyUp→KeyDown patterns that trigger anti-cheat.
        /// Called automatically by TickMovementLayer().
        /// </summary>
        public static void ResumeMovement()
        {
            if (!IsMovementActive || !IsMovementSuspended) return;

            // Enforce global input rate limit before re-pressing the movement key
            if (!CanSendInputEvent) return;

            // Same guards as StartMovement — cursor off player, no stuck modifiers.
            var target = NudgeOffPlayer(_movementCursorPos);
            _movementCursorPos = target;
            ReleaseAllModifiersBeforeMove();
            Input.SetCursorPos(target);
            SendKeyDown(_movementKey, "resume");
            IsMovementSuspended = false;
        }

        /// <summary>
        /// Tick the movement layer. Call once per frame from BotCore.
        /// Auto-resumes movement after discrete actions complete.
        /// </summary>
        public static void TickMovementLayer()
        {
            if (!IsMovementActive || !IsMovementSuspended) return;

            // Don't resume movement while modifier keys are held (e.g. Ctrl for batch stash transfers).
            // Resuming would press the movement key with Ctrl still down, sending Ctrl+moveKey.
            // Held skill keys (channel attacks) are fine — only actual modifiers cause combo issues.
            if (HasHeldModifiers) return;

            // Auto-resume when the discrete action's gate has cleared.
            // ResumeMovement internally checks CanSendInputEvent for rate limiting.
            if (CanAct)
                ResumeMovement();

            // Safety: force-resume if suspended too long (action got stuck)
            if (IsMovementSuspended && (DateTime.Now - _lastInputEvent).TotalMilliseconds > 2000)
                ResumeMovement();
        }

        // ══════════════════════════════════════════════════════════════
        // Replay mode — captures actions without sending real input
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// When true, all action methods log to CapturedActions but don't call Input.*.
        /// CanAct always returns true. Used by offline replay harness.
        /// </summary>
        public static bool ReplayMode { get; set; }

        /// <summary>Actions captured during replay mode. Harvest and clear after each tick.</summary>
        public static readonly List<ReplayCapturedAction> CapturedActions = new();

        /// <summary>Harvest all captured actions and clear the list.</summary>
        public static List<ReplayCapturedAction> HarvestCapturedActions()
        {
            var actions = new List<ReplayCapturedAction>(CapturedActions);
            CapturedActions.Clear();
            return actions;
        }

        public class ReplayCapturedAction
        {
            public string Type { get; set; } = ""; // Click, PressKey, CursorPressKey, HoldKey, etc.
            public float? X { get; set; }
            public float? Y { get; set; }
            public int? Key { get; set; } // Keys enum cast to int
            public DateTime Timestamp { get; set; } = DateTime.Now;
        }

        private static bool TryCaptureReplay(string type, Vector2? pos = null, Keys? key = null)
        {
            if (!ReplayMode) return false;
            CapturedActions.Add(new ReplayCapturedAction
            {
                Type = type,
                X = pos?.X,
                Y = pos?.Y,
                Key = key.HasValue ? (int)key.Value : null,
            });
            return true; // Action "succeeded" in replay mode
        }

        // ══════════════════════════════════════════════════════════════
        // Held key tracking (channeling skills)
        // Keys held down without immediate release. Tracked so they can be
        // safely released on interruption (mode exit, phase change, new action).
        // ══════════════════════════════════════════════════════════════

        /// <summary>Currently held keys and when they were pressed.</summary>
        private static readonly Dictionary<Keys, DateTime> _heldKeys = new();

        /// <summary>Auto-release held keys after this many seconds (safety watchdog).</summary>
        public static float HeldKeyTimeoutSeconds = 5f;

        /// <summary>True if any keys are currently held down.</summary>
        public static bool HasHeldKeys => _heldKeys.Count > 0;

        /// <summary>True if a modifier key (Ctrl, Shift, Alt) is currently held.
        /// Used to block overlay key presses that would combine with the modifier.</summary>
        public static bool HasHeldModifiers => _heldKeys.Keys.Any(k =>
            k == Keys.LControlKey || k == Keys.RControlKey || k == Keys.ControlKey ||
            k == Keys.LShiftKey || k == Keys.RShiftKey || k == Keys.ShiftKey ||
            k == Keys.LMenu || k == Keys.RMenu || k == Keys.Menu);

        /// <summary>Check if a specific key is currently held.</summary>
        public static bool IsHeld(Keys key) => _heldKeys.ContainsKey(key);

        /// <summary>
        /// Register a key as held in the tracking map (for watchdog/release).
        /// Use when KeyDown is sent directly (bypassing HoldKey) to ensure
        /// ReleaseAllKeys and the watchdog know about it.
        /// </summary>
        public static void TrackHeldKey(Keys key) => _heldKeys[key] = DateTime.Now;

        /// <summary>
        /// Hold a key down (KeyDown without KeyUp). The key stays held until
        /// ReleaseKey, ReleaseAllKeys, or the timeout watchdog releases it.
        /// Automatically releases any previously held instance of this key.
        /// </summary>
        public static bool HoldKey(Keys key)
        {
            if (TryCaptureReplay("HoldKey", key: key)) return true;
            if (!CanAct) return false;

            SuspendMovement();

            // Release if already held (prevents double-down)
            if (_heldKeys.ContainsKey(key))
            {
                SendKeyUp(key);
            }

            SendKeyDown(key);
            _heldKeys[key] = DateTime.Now;
            NextActionAt = DateTime.Now.AddMilliseconds(ActionCooldownMs);
            return true;
        }

        /// <summary>
        /// Hold a key at a screen position (for targeted channeling skills).
        /// Moves cursor to position, then holds the key down.
        /// </summary>
        public static bool HoldKeyAt(Vector2 absPos, Keys key)
        {
            if (TryCaptureReplay("HoldKeyAt", absPos, key)) return true;
            if (!CanAct) return false;
            if (!ClampToWindow(ref absPos)) return false;

            SuspendMovement();

            // Release if already held
            if (_heldKeys.ContainsKey(key))
                SendKeyUp(key);

            var moveMs = EstimateMoveMs(absPos);
            NextActionAt = DateTime.Now.AddMilliseconds(moveMs + ActionCooldownMs);
            _ = DoHoldKeyAt(absPos, key);
            return true;
        }

        private static async Task DoHoldKeyAt(Vector2 absPos, Keys key)
        {
            await MoveCursorTo(absPos);
            await Task.Delay(RandSettle());
            await SendDelay();
            SendKeyDown(key);
            _heldKeys[key] = DateTime.Now;
        }

        /// <summary>Release a specific held key.</summary>
        public static void ReleaseKey(Keys key)
        {
            if (_heldKeys.Remove(key))
                SendKeyUp(key);
        }

        /// <summary>
        /// Release ALL held keys. Called automatically before new actions,
        /// on mode exit, area change, and phase transitions.
        /// </summary>
        public static void ReleaseAllKeys()
        {
            if (_heldKeys.Count > 0)
            {
                foreach (var key in _heldKeys.Keys)
                    SendKeyUp(key);
                _heldKeys.Clear();
            }
        }

        /// <summary>
        /// Tick the held key watchdog. Call once per frame from BotCore.
        /// Auto-releases keys held longer than HeldKeyTimeoutSeconds.
        /// </summary>
        public static void TickHeldKeys()
        {
            if (_heldKeys.Count == 0) return;

            var now = DateTime.Now;
            var expired = new List<Keys>();
            foreach (var (key, pressedAt) in _heldKeys)
            {
                if ((now - pressedAt).TotalSeconds >= HeldKeyTimeoutSeconds)
                    expired.Add(key);
            }

            foreach (var key in expired)
            {
                SendKeyUp(key);
                _heldKeys.Remove(key);
            }
        }

        // ══════════════════════════════════════════════════════════════
        // Gaussian delay generation (Box-Muller transform)
        // Human reaction times follow normal distribution, not uniform.
        // ══════════════════════════════════════════════════════════════

        /// <summary>Mean settle delay in ms (cursor → click).</summary>
        public const float SettleMeanMs = 70f;
        /// <summary>Settle delay standard deviation.</summary>
        public const float SettleStdDevMs = 10f;

        /// <summary>Mean key/button hold duration in ms.</summary>
        public const float HoldMeanMs = 40f;
        /// <summary>Hold duration standard deviation.</summary>
        public const float HoldStdDevMs = 10f;

        /// <summary>Minimum delay for any Gaussian sample (floor).</summary>
        private const int DelayFloorMs = 20;
        /// <summary>Maximum delay for any Gaussian sample (ceiling).</summary>
        private const int DelayCeilingMs = 100;

        /// <summary>
        /// Generate a Gaussian-distributed random delay using Box-Muller transform.
        /// More human-like than uniform Random.Next — clusters around the mean
        /// with occasional faster/slower outliers.
        /// </summary>
        private static int GaussianDelay(float mean, float stdDev)
        {
            // Box-Muller: two uniform randoms → one normal random
            double u1 = 1.0 - _rng.NextDouble(); // (0, 1]
            double u2 = _rng.NextDouble();        // [0, 1)
            double normal = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
            int result = (int)(mean + stdDev * normal);
            return Math.Clamp(result, DelayFloorMs, DelayCeilingMs);
        }

        public static int RandSettle() => GaussianDelay(SettleMeanMs, SettleStdDevMs);
        public static int RandHold() => GaussianDelay(HoldMeanMs, HoldStdDevMs);

        // ══════════════════════════════════════════════════════════════
        // Mouse movement interpolation
        // Instead of teleporting the cursor, move it in steps along
        // a slightly randomized path. Duration scales with distance.
        // ══════════════════════════════════════════════════════════════

        /// <summary>Min ms for cursor travel (very short moves).</summary>
        private const int MoveMinMs = 15;
        /// <summary>Max ms for cursor travel (full screen moves).</summary>
        private const int MoveMaxMs = 80;
        /// <summary>Distance in pixels that maps to MoveMaxMs.</summary>
        private const float MoveMaxDistance = 2000f;
        /// <summary>Number of intermediate points for interpolation.</summary>
        private const int MoveSteps = 8;
        /// <summary>Max perpendicular pixel offset for path randomization.</summary>
        private const float MoveJitterPx = 3f;
        /// <summary>Max random pixel offset applied to final cursor landing position.</summary>
        private const float LandingJitterPx = 3f;

        /// <summary>
        /// Interpolate cursor from current position to target over a distance-proportional duration.
        /// Path has slight random perpendicular jitter for organic movement.
        /// </summary>
        private static async Task MoveCursorTo(Vector2 target)
        {
            var mp = Input.MousePosition;
            var start = new Vector2(mp.X, mp.Y);
            var delta = target - start;
            var dist = delta.Length();

            if (dist < 5f)
            {
                // Too close to bother interpolating
                Input.SetCursorPos(target);
                return;
            }

            // Duration scales linearly with distance, clamped
            float t = Math.Clamp(dist / MoveMaxDistance, 0f, 1f);
            int totalMs = MoveMinMs + (int)((MoveMaxMs - MoveMinMs) * t);
            int stepDelayMs = Math.Max(1, totalMs / MoveSteps);

            // Perpendicular direction for jitter
            var perp = Vector2.Normalize(new Vector2(-delta.Y, delta.X));

            for (int i = 1; i <= MoveSteps; i++)
            {
                float progress = (float)i / MoveSteps;
                var pos = start + delta * progress;

                // Add slight perpendicular jitter (not on final step)
                if (i < MoveSteps)
                {
                    float jitter = (float)(_rng.NextDouble() * 2 - 1) * MoveJitterPx;
                    // Taper jitter: strongest in the middle, zero at endpoints
                    float taper = 1f - Math.Abs(progress - 0.5f) * 2f;
                    pos += perp * jitter * taper;
                }

                Input.SetCursorPos(pos);
                await Task.Delay(stepDelayMs);
            }

            // Land with slight random offset — avoids pixel-perfect cursor patterns
            var jitterX = (float)(_rng.NextDouble() * 2 - 1) * LandingJitterPx;
            var jitterY = (float)(_rng.NextDouble() * 2 - 1) * LandingJitterPx;
            Input.SetCursorPos(target + new Vector2(jitterX, jitterY));
        }

        // ── Cursor + key press (walk commands, targeted skills) ──

        /// <summary>Move cursor, settle, press+hold+release key. Suspends continuous movement if active.</summary>
        public static bool CursorPressKey(Vector2 absPos, Keys key)
        {
            if (TryCaptureReplay("CursorPressKey", absPos, key)) return true;
            if (!CanAct) { LogAction("CursorPressKey", absPos, key, false); return false; }
            if (!ClampToWindow(ref absPos)) { LogAction("CursorPressKey", absPos, key, false); return false; }
            SuspendMovement();
            ReleaseAllKeys();
            var moveMs = EstimateMoveMs(absPos);
            var settle = RandSettle();
            var hold = RandHold();
            NextActionAt = DateTime.Now.AddMilliseconds(moveMs + settle + hold + ActionCooldownMs);
            _ = DoCursorPressKey(absPos, key, settle, hold);
            LogAction("CursorPressKey", absPos, key, true);
            return true;
        }

        /// <summary>Force cursor+key press, bypassing the input gate. For dodge — survival trumps input cadence.</summary>
        public static bool ForceCursorPressKey(Vector2 absPos, Keys key)
        {
            if (!ClampToWindow(ref absPos)) { LogAction("ForceCursorPressKey", absPos, key, false); return false; }
            SuspendMovement();
            ReleaseAllKeys();
            var moveMs = EstimateMoveMs(absPos);
            var settle = RandSettle();
            var hold = RandHold();
            NextActionAt = DateTime.Now.AddMilliseconds(moveMs + settle + hold + ActionCooldownMs);
            _ = DoCursorPressKey(absPos, key, settle, hold);
            LogAction("ForceCursorPressKey", absPos, key, true);
            return true;
        }

        private static async Task DoCursorPressKey(Vector2 absPos, Keys key, int settleMs, int holdMs,
            Func<Vector2?>? positionCorrection = null)
        {
            await MoveCursorTo(absPos);
            await Task.Delay(settleMs);

            // Final position correction — snap to where the target is NOW
            var corrected = positionCorrection?.Invoke();
            if (corrected.HasValue)
                Input.SetCursorPos(corrected.Value);

            await SendDelay();
            SendKeyDown(key);
            await Task.Delay(holdMs);
            await SendDelay();
            SendKeyUp(key);
        }

        // ── Key press only (self-cast, flasks — no cursor move) ──

        /// <summary>Press and release a key. Returns false if gate is closed. Suspends movement.</summary>
        public static bool PressKey(Keys key)
        {
            if (TryCaptureReplay("PressKey", key: key)) return true;
            if (!CanAct) { LogAction("PressKey", null, key, false); return false; }
            SuspendMovement();
            ReleaseAllKeys();
            var hold = RandHold();
            NextActionAt = DateTime.Now.AddMilliseconds(hold + ActionCooldownMs);
            _ = DoPressKey(key, hold);
            LogAction("PressKey", null, key, true);
            return true;
        }

        /// <summary>
        /// Press and release a key WITHOUT interrupting continuous movement.
        /// For self-cast skills (buffs, guards, flasks) that don't need cursor positioning.
        /// Uses a shorter gate since no cursor movement is involved.
        /// </summary>
        public static bool PressKeyOverlay(Keys key)
        {
            if (TryCaptureReplay("PressKeyOverlay", key: key)) return true;
            if (!CanAct) { LogAction("PressKeyOverlay", null, key, false); return false; }
            // Block overlay keys while a modifier is held (e.g. Ctrl held for batch stash transfers).
            // Firing a flask key while Ctrl is down would send Ctrl+flask_key instead of just flask_key.
            // Held skill keys (channel attacks) are fine — only actual modifiers cause combo issues.
            if (HasHeldModifiers) { LogAction("PressKeyOverlay", null, key, false); return false; }
            // Do NOT suspend movement or release held keys — this fires alongside movement
            var hold = RandHold();
            NextActionAt = DateTime.Now.AddMilliseconds(hold + ActionCooldownMs);
            _ = DoPressKey(key, hold);
            LogAction("PressKeyOverlay", null, key, true);
            return true;
        }

        private static async Task DoPressKey(Keys key, int holdMs)
        {
            await SendDelay();
            SendKeyDown(key);
            await Task.Delay(holdMs);
            await SendDelay();
            SendKeyUp(key);
        }

        // ── Mouse clicks ──

        /// <summary>Move cursor, settle, left-click (hold+release). Suspends continuous movement.</summary>
        public static bool Click(Vector2 absPos, Func<Vector2?>? positionCorrection = null)
        {
            if (TryCaptureReplay("Click", absPos)) return true;
            if (!CanAct) { LogAction("Click", absPos, null, false); return false; }
            if (!ClampToWindow(ref absPos)) { LogAction("Click", absPos, null, false); return false; }
            SuspendMovement();
            ReleaseAllKeys();
            var moveMs = EstimateMoveMs(absPos);
            var settle = RandSettle();
            var hold = RandHold();
            NextActionAt = DateTime.Now.AddMilliseconds(moveMs + settle + hold + ActionCooldownMs);
            _ = DoClick(absPos, rightClick: false, settle, hold, positionCorrection);
            LogAction("Click", absPos, null, true);
            return true;
        }

        /// <summary>Move cursor to position without clicking. Suspends continuous movement.</summary>
        public static bool MoveMouse(Vector2 absPos)
        {
            if (TryCaptureReplay("MoveMouse", absPos)) return true;
            if (!CanAct) return false;
            if (!ClampToWindow(ref absPos)) return false;
            SuspendMovement();
            ReleaseAllKeys();
            var moveMs = EstimateMoveMs(absPos);
            NextActionAt = DateTime.Now.AddMilliseconds(moveMs + ActionCooldownMs);
            _ = MoveCursorTo(absPos);
            return true;
        }

        /// <summary>Move cursor, settle, right-click. Suspends continuous movement.</summary>
        public static bool RightClick(Vector2 absPos)
        {
            if (TryCaptureReplay("RightClick", absPos)) return true;
            if (!CanAct) { LogAction("RightClick", absPos, null, false); return false; }
            if (!ClampToWindow(ref absPos)) { LogAction("RightClick", absPos, null, false); return false; }
            SuspendMovement();
            ReleaseAllKeys();
            var moveMs = EstimateMoveMs(absPos);
            var settle = RandSettle();
            var hold = RandHold();
            NextActionAt = DateTime.Now.AddMilliseconds(moveMs + settle + hold + ActionCooldownMs);
            _ = DoClick(absPos, rightClick: true, settle, hold);
            LogAction("RightClick", absPos, null, true);
            return true;
        }

        private static async Task DoClick(Vector2 absPos, bool rightClick, int settleMs, int holdMs,
            Func<Vector2?>? positionCorrection = null)
        {
            await MoveCursorTo(absPos);
            await Task.Delay(settleMs);

            // Final position correction — snap cursor to where the target is NOW,
            // not where it was when the click was initiated. During the interpolation +
            // settle (~100-150ms), the player character continues sliding from momentum,
            // shifting all screen-space label/entity positions. The correction callback
            // provides fresh coordinates (e.g., re-reading a ground label's ClientRect).
            // The cursor is already very close — this is just a small snap correction.
            var corrected = positionCorrection?.Invoke();
            if (corrected.HasValue)
                Input.SetCursorPos(corrected.Value);

            await SendDelay();
            if (rightClick)
            {
                SendRightDown();
                await Task.Delay(holdMs);
                await SendDelay();
                SendRightUp();
            }
            else
            {
                SendLeftDown();
                await Task.Delay(holdMs);
                await SendDelay();
                SendLeftUp();
            }
        }

        /// <summary>Ctrl+left-click (stash transfers). Suspends continuous movement.</summary>
        public static bool CtrlClick(Vector2 absPos)
        {
            if (TryCaptureReplay("CtrlClick", absPos)) return true;
            if (!CanAct) { LogAction("CtrlClick", absPos, null, false); return false; }
            if (!ClampToWindow(ref absPos)) { LogAction("CtrlClick", absPos, null, false); return false; }
            SuspendMovement();
            ReleaseAllKeys();
            var moveMs = EstimateMoveMs(absPos);
            var settle = RandSettle();
            var hold = RandHold();
            // Ctrl down + settle + cursor move + settle + click hold + release + ctrl up
            NextActionAt = DateTime.Now.AddMilliseconds(hold + moveMs + settle + hold + ActionCooldownMs);
            _ = DoCtrlClick(absPos, settle, hold);
            LogAction("CtrlClick", absPos, null, true);
            return true;
        }

        private static async Task DoCtrlClick(Vector2 absPos, int settleMs, int holdMs)
        {
            await SendDelay();
            SendKeyDown(Keys.ControlKey, "ctrl");
            await Task.Delay(holdMs);
            await MoveCursorTo(absPos);
            await Task.Delay(settleMs);
            await SendDelay();
            SendLeftDown("ctrl-click");
            await Task.Delay(holdMs);
            await SendDelay();
            SendLeftUp("ctrl-click");
            await Task.Delay(holdMs);
            await SendDelay();
            SendKeyUp(Keys.ControlKey, "ctrl");
        }

        /// <summary>
        /// Left-click while a modifier key is already held (e.g. Ctrl held for batch stash transfers).
        /// Does NOT call ReleaseAllKeys — the caller is responsible for holding/releasing the modifier.
        /// Lighter than CtrlClick: skips Ctrl down/up per item, just moves cursor and clicks.
        /// </summary>
        public static bool ClickWithModifierHeld(Vector2 absPos)
        {
            if (TryCaptureReplay("ClickWithModifierHeld", absPos)) return true;
            if (!CanAct) { LogAction("ClickWithModifierHeld", absPos, null, false); return false; }
            if (!ClampToWindow(ref absPos)) { LogAction("ClickWithModifierHeld", absPos, null, false); return false; }
            SuspendMovement();
            // Do NOT call ReleaseAllKeys — modifier must stay held
            var moveMs = EstimateMoveMs(absPos);
            var settle = RandSettle();
            var hold = RandHold();
            NextActionAt = DateTime.Now.AddMilliseconds(moveMs + settle + hold + ActionCooldownMs);
            _ = DoClickWithModifierHeld(absPos, settle, hold);
            LogAction("ClickWithModifierHeld", absPos, null, true);
            return true;
        }

        private static async Task DoClickWithModifierHeld(Vector2 absPos, int settleMs, int holdMs)
        {
            await MoveCursorTo(absPos);
            await Task.Delay(settleMs);
            await SendDelay();
            SendLeftDown("modifier-click");
            await Task.Delay(holdMs);
            await SendDelay();
            SendLeftUp("modifier-click");
        }

        // ── Batch operations ──

        /// <summary>
        /// Whether a batch operation (CtrlClickBatch) is currently executing.
        /// While true, the tick loop should not try to start new actions.
        /// </summary>
        public static bool IsBatchRunning { get; private set; }

        /// <summary>
        /// Execute a batch of Ctrl+click operations as a single async sequence.
        /// Holds Ctrl down, clicks each position with randomized timing, then releases Ctrl.
        /// Bypasses the tick-level input gating — the sequence manages its own timing.
        /// Does not break out early; executes the full list.
        /// </summary>
        /// <param name="positions">Absolute screen positions to Ctrl+click.</param>
        /// <param name="onComplete">Called when the batch finishes (success or empty).</param>
        public static bool CtrlClickBatch(IReadOnlyList<Vector2> positions, Action<int>? onComplete = null)
        {
            if (IsBatchRunning) return false;
            if (positions.Count == 0)
            {
                onComplete?.Invoke(0);
                return true;
            }

            SuspendMovement();
            ReleaseAllKeys();
            IsBatchRunning = true;

            // Estimate total duration for gate: (move + settle + hold + cooldown) per item + ctrl hold/release
            var totalMs = positions.Count * (MoveMaxMs + (int)SettleMeanMs + (int)HoldMeanMs + ActionCooldownMs) + 200;
            NextActionAt = DateTime.Now.AddMilliseconds(totalMs);

            _ = DoCtrlClickBatch(positions, onComplete);
            LogAction("CtrlClickBatch", null, null, true);
            return true;
        }

        private static async Task DoCtrlClickBatch(IReadOnlyList<Vector2> positions, Action<int>? onComplete)
        {
            int clicked = 0;
            try
            {
                // Press Ctrl
                await SendDelay();
                Input.KeyDown(Keys.ControlKey);
                MarkInputEvent("KeyDown", "Ctrl batch-start");
                await Task.Delay(RandHold());

                for (int i = 0; i < positions.Count; i++)
                {
                    var pos = positions[i];

                    // Move cursor
                    await MoveCursorTo(pos);
                    await Task.Delay(RandSettle());

                    // Click
                    await SendDelay();
                    Input.LeftDown();
                    MarkInputEvent("LeftDown", $"batch-click {i + 1}/{positions.Count}");
                    await Task.Delay(RandHold());
                    Input.LeftUp();
                    MarkInputEvent("LeftUp", $"batch-click {i + 1}/{positions.Count}");

                    clicked++;

                    // Inter-item delay: random + action cooldown
                    if (i < positions.Count - 1)
                        await Task.Delay(ActionCooldownMs + _rng.Next(20, 80));
                }

                // Release Ctrl
                await Task.Delay(RandHold());
                Input.KeyUp(Keys.ControlKey);
                MarkInputEvent("KeyUp", "Ctrl batch-end");
            }
            catch (Exception ex)
            {
                // Safety: always release Ctrl
                try { Input.KeyUp(Keys.ControlKey); } catch { }
                LogRawInput("BatchError", ex.Message);
            }
            finally
            {
                IsBatchRunning = false;
                NextActionAt = DateTime.Now.AddMilliseconds(ActionCooldownMs);
                onComplete?.Invoke(clicked);
            }
        }

        // ── Public helpers for scripted batch operations ──
        // Used by StashSystem and similar systems that run their own async sequences
        // and need direct access to cursor movement and input event marking.

        /// <summary>Move cursor with interpolation (public wrapper for batch operations).</summary>
        public static Task MoveCursorToPublic(Vector2 target) => MoveCursorTo(target);

        /// <summary>Mark an input event (public wrapper for batch operations).</summary>
        public static void MarkInputEventPublic(string eventType, string detail = "") => MarkInputEvent(eventType, detail);

        // ── Helpers ──

        /// <summary>
        /// Estimate how long cursor interpolation will take for gate timing.
        /// Must match the logic in MoveCursorTo.
        /// </summary>
        private static int EstimateMoveMs(Vector2 target)
        {
            var mp = Input.MousePosition;
            var dist = (target - new Vector2(mp.X, mp.Y)).Length();
            if (dist < 5f) return 0;
            float t = Math.Clamp(dist / MoveMaxDistance, 0f, 1f);
            return MoveMinMs + (int)((MoveMaxMs - MoveMinMs) * t);
        }

        /// <summary>Cancel any pending action, release held keys, stop movement, and reset gate.</summary>
        public static void Cancel()
        {
            StopMovement();
            ReleaseAllKeys();
            NextActionAt = DateTime.MinValue;
        }
    }
}
