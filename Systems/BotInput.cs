using System.Numerics;
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
        /// Click a world entity with center-biased randomization derived from its actual bounds.
        /// Projects the entity's Render.BoundsNum to screen space for proper scaling with
        /// camera zoom and distance. Falls back to a small fixed offset if Render is unavailable.
        /// </summary>
        /// <returns>True if the click was sent, false if gate blocked or entity off-screen.</returns>
        public static bool ClickEntity(GameController gc, Entity entity)
        {
            var center = entity.BoundsCenterPosNum;
            var camera = gc.IngameState.Camera;
            var screenCenter = camera.WorldToScreen(center);
            var windowRect = gc.Window.GetWindowRectangle();

            // Off-screen check
            if (screenCenter.X < 5 || screenCenter.X > windowRect.Width - 5 ||
                screenCenter.Y < 5 || screenCenter.Y > windowRect.Height - 5)
                return false;

            // Derive screen-space extents from world-space bounds
            float halfW = 12f, halfH = 8f; // fallback pixels if no Render component
            var render = entity.GetComponent<Render>();
            if (render != null)
            {
                var bounds = render.BoundsNum;
                // Project center + X offset to screen, measure pixel delta for scale
                var offsetWorld = new Vector3(center.X + bounds.X * 0.4f, center.Y, center.Z);
                var screenOffset = camera.WorldToScreen(offsetWorld);
                var dx = Math.Abs(screenOffset.X - screenCenter.X);
                // Project center + Y offset (world Y maps to screen diagonal in isometric view)
                var offsetWorldY = new Vector3(center.X, center.Y + bounds.Y * 0.4f, center.Z);
                var screenOffsetY = camera.WorldToScreen(offsetWorldY);
                var dy = Math.Abs(screenOffsetY.Y - screenCenter.Y);

                if (dx > 3f) halfW = dx;
                if (dy > 3f) halfH = dy;
            }

            var clickPos = RandomizeWithinRect(screenCenter.X, screenCenter.Y, halfW, halfH);
            var absPos = new Vector2(windowRect.X + clickPos.X, windowRect.Y + clickPos.Y);
            return Click(absPos);
        }

        /// <summary>
        /// Click within a label/UI element rect with center-biased randomization.
        /// The rect should be in window-relative coordinates (from GetClientRect/ClientRect).
        /// </summary>
        /// <returns>True if the click was sent, false if gate blocked.</returns>
        public static bool ClickLabel(GameController gc, SharpDX.RectangleF rect)
        {
            var clickPos = RandomizeWithinRect(rect);
            var windowRect = gc.Window.GetWindowRectangle();
            var absPos = new Vector2(windowRect.X + clickPos.X, windowRect.Y + clickPos.Y);
            return Click(absPos);
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

        /// <summary>Global gate — no actions before this time.</summary>
        public static DateTime NextActionAt = DateTime.MinValue;

        private static readonly Random _rng = new();

        /// <summary>True if we can start a new action right now.</summary>
        public static bool CanAct => DateTime.Now >= NextActionAt;

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

        private static int RandSettle() => GaussianDelay(SettleMeanMs, SettleStdDevMs);
        private static int RandHold() => GaussianDelay(HoldMeanMs, HoldStdDevMs);

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

        /// <summary>Move cursor, settle, press+hold+release key.</summary>
        public static bool CursorPressKey(Vector2 absPos, Keys key)
        {
            if (!CanAct) { LogAction("CursorPressKey", absPos, key, false); return false; }
            if (!ClampToWindow(ref absPos)) { LogAction("CursorPressKey", absPos, key, false); return false; }
            var moveMs = EstimateMoveMs(absPos);
            var settle = RandSettle();
            var hold = RandHold();
            NextActionAt = DateTime.Now.AddMilliseconds(moveMs + settle + hold + ActionCooldownMs);
            _ = DoCursorPressKey(absPos, key, settle, hold);
            LogAction("CursorPressKey", absPos, key, true);
            return true;
        }

        private static async Task DoCursorPressKey(Vector2 absPos, Keys key, int settleMs, int holdMs)
        {
            await MoveCursorTo(absPos);
            await Task.Delay(settleMs);
            Input.KeyDown(key);
            await Task.Delay(holdMs);
            Input.KeyUp(key);
        }

        // ── Key press only (self-cast, flasks — no cursor move) ──

        /// <summary>Press and release a key. Returns false if gate is closed.</summary>
        public static bool PressKey(Keys key)
        {
            if (!CanAct) { LogAction("PressKey", null, key, false); return false; }
            var hold = RandHold();
            NextActionAt = DateTime.Now.AddMilliseconds(hold + ActionCooldownMs);
            _ = DoPressKey(key, hold);
            LogAction("PressKey", null, key, true);
            return true;
        }

        private static async Task DoPressKey(Keys key, int holdMs)
        {
            Input.KeyDown(key);
            await Task.Delay(holdMs);
            Input.KeyUp(key);
        }

        // ── Mouse clicks ──

        /// <summary>Move cursor, settle, left-click (hold+release).</summary>
        public static bool Click(Vector2 absPos)
        {
            if (!CanAct) { LogAction("Click", absPos, null, false); return false; }
            if (!ClampToWindow(ref absPos)) { LogAction("Click", absPos, null, false); return false; }
            var moveMs = EstimateMoveMs(absPos);
            var settle = RandSettle();
            var hold = RandHold();
            NextActionAt = DateTime.Now.AddMilliseconds(moveMs + settle + hold + ActionCooldownMs);
            _ = DoClick(absPos, rightClick: false, settle, hold);
            LogAction("Click", absPos, null, true);
            return true;
        }

        /// <summary>Move cursor, settle, right-click.</summary>
        public static bool RightClick(Vector2 absPos)
        {
            if (!CanAct) { LogAction("RightClick", absPos, null, false); return false; }
            if (!ClampToWindow(ref absPos)) { LogAction("RightClick", absPos, null, false); return false; }
            var moveMs = EstimateMoveMs(absPos);
            var settle = RandSettle();
            var hold = RandHold();
            NextActionAt = DateTime.Now.AddMilliseconds(moveMs + settle + hold + ActionCooldownMs);
            _ = DoClick(absPos, rightClick: true, settle, hold);
            LogAction("RightClick", absPos, null, true);
            return true;
        }

        private static async Task DoClick(Vector2 absPos, bool rightClick, int settleMs, int holdMs)
        {
            await MoveCursorTo(absPos);
            await Task.Delay(settleMs);
            if (rightClick)
            {
                Input.RightDown();
                await Task.Delay(holdMs);
                Input.RightUp();
            }
            else
            {
                Input.LeftDown();
                await Task.Delay(holdMs);
                Input.LeftUp();
            }
        }

        /// <summary>Ctrl+left-click (stash transfers).</summary>
        public static bool CtrlClick(Vector2 absPos)
        {
            if (!CanAct) { LogAction("CtrlClick", absPos, null, false); return false; }
            if (!ClampToWindow(ref absPos)) { LogAction("CtrlClick", absPos, null, false); return false; }
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
            Input.KeyDown(Keys.ControlKey);
            await Task.Delay(holdMs);
            await MoveCursorTo(absPos);
            await Task.Delay(settleMs);
            Input.LeftDown();
            await Task.Delay(holdMs);
            Input.LeftUp();
            await Task.Delay(holdMs);
            Input.KeyUp(Keys.ControlKey);
        }

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

        /// <summary>Cancel any pending action and reset gate.</summary>
        public static void Cancel()
        {
            NextActionAt = DateTime.MinValue;
        }
    }
}
