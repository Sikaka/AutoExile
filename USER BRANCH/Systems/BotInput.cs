using System.Numerics;
using System.IO;
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
    ///
    /// Continuous movement layer:
    /// - Navigation holds a movement key down between ticks instead of discrete press-per-tick.
    /// - Discrete actions (clicks, targeted skills) call SuspendMovement() before firing.
    /// - TickMovementLayer() resumes movement automatically after the action gate clears.
    ///
    /// Global rate limiting:
    /// - ALL raw input (KeyDown/Up, MouseDown/Up) goes through Send* wrappers.
    /// - Wrappers enforce MinInputEventGapMs between every event.
    /// - CanTick hard floor prevents mode/nav logic from running too soon after any input.
    /// </summary>
    public static class BotInput
    {
        private static string _logPath;
        public static void InitializeLogging(string pluginDir)
        {
            _logPath = Path.Combine(pluginDir, "MovementDebug.log");
            FileLog("--- Movement Logging Started ---");
        }

        public static void FileLog(string msg)
        {
            if (string.IsNullOrEmpty(_logPath)) return;
            try { File.AppendAllText(_logPath, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}{Environment.NewLine}"); } catch { }
        }

        // ── Window bounds (set by BotCore each tick) ──
        /// <summary>Game window rect in absolute screen coordinates. Set by BotCore each tick.</summary>
        public static SharpDX.RectangleF WindowRect;

        private static bool ClampToWindow(ref Vector2 pos)
        {
            if (WindowRect.Width < 10 || WindowRect.Height < 10)
            {
                FileLog($"ClampToWindow: FAILED - Invalid WindowRect ({WindowRect.Width}x{WindowRect.Height})");
                return false;
            }

            const float pad = 5f;
            var original = pos;
            pos.X = Math.Clamp(pos.X, WindowRect.X + pad, WindowRect.X + WindowRect.Width - pad);
            pos.Y = Math.Clamp(pos.Y, WindowRect.Y + pad, WindowRect.Y + WindowRect.Height - pad);
            if (pos != original) FileLog($"Clamped cursor from {original} to {pos}");
            return true;
        }

        // ── Action Log ──
        private static readonly ActionRecord[] _actionLog = new ActionRecord[500];
        private static int _actionLogIndex;
        private static int _actionLogCount;

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

        // ── Timing gates ──

        /// <summary>Minimum ms between actions (end of one action to start of next). Configurable.</summary>
        public static int ActionCooldownMs = 100;

        /// <summary>
        /// Hard floor: minimum ms since last raw input before BotCore should run mode/nav/combat logic.
        /// Catches async race conditions and movement layer interleaving that per-action gate can't prevent.
        /// </summary>
        public const int HardFloorMs = 50;

        /// <summary>
        /// True if at least HardFloorMs have elapsed since the last raw input event.
        /// BotCore checks this before running mode/nav logic.
        /// </summary>
        public static bool CanTick =>
            (DateTime.Now - _lastInputEvent).TotalMilliseconds >= HardFloorMs;

        /// <summary>Global gate — no actions before this time.</summary>
        public static DateTime NextActionAt = DateTime.MinValue;

        /// <summary>Last time any input event was sent. Enforces minimum gap between ALL events.</summary>
        private static DateTime _lastInputEvent = DateTime.MinValue;

        /// <summary>Minimum ms between any two raw input events (derived from ActionCooldownMs).</summary>
        private static int MinInputEventGapMs => ActionCooldownMs;

        private static bool CanSendInputEvent =>
            (DateTime.Now - _lastInputEvent).TotalMilliseconds >= MinInputEventGapMs;

        private static void MarkInputEvent(string eventType = "?", string detail = "")
        {
            _lastInputEvent = DateTime.Now;
            LogRawInput(eventType, detail);
        }

        // ── Raw Input Wrappers ──
        // ALL game input MUST go through these. They enforce the global rate limit.
        // "Down" events enforce CanSendInputEvent and will be DROPPED if called too soon.
        // "Up" events always send immediately but still mark _lastInputEvent.

        private static void SendKeyDown(Keys key, string context = "")
        {
            if (!CanSendInputEvent)
            {
                var elapsed = (DateTime.Now - _lastInputEvent).TotalMilliseconds;
                FileLog($"KeyDown-DROPPED: {key} {context} (too soon: {elapsed:F0}ms)");
                LogRawInput("KeyDown-DROPPED", $"{key} {context} (too soon: {elapsed:F0}ms)".Trim());
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
                var elapsed = (DateTime.Now - _lastInputEvent).TotalMilliseconds;
                FileLog($"LeftDown-DROPPED: {context} (too soon: {elapsed:F0}ms)");
                LogRawInput("LeftDown-DROPPED", $"{context} (too soon: {elapsed:F0}ms)".Trim());
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
                var elapsed = (DateTime.Now - _lastInputEvent).TotalMilliseconds;
                FileLog($"RightDown-DROPPED: {context} (too soon: {elapsed:F0}ms)");
                LogRawInput("RightDown-DROPPED", $"{context} (too soon: {elapsed:F0}ms)".Trim());
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

        /// <summary>Async delay that enforces minimum gap between input events. Call before SendKeyDown/SendLeftDown.</summary>
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
        // position each tick. Discrete actions briefly suspend movement,
        // execute, then resume automatically via TickMovementLayer().
        // ══════════════════════════════════════════════════════════════

        /// <summary>True if the movement layer is actively holding a movement key.</summary>
        public static bool IsMovementActive { get; private set; }

        /// <summary>The key currently held for movement.</summary>
        private static Keys _movementKey;

        /// <summary>Last screen position the movement cursor was set to.</summary>
        private static Vector2 _movementCursorPos;

        /// <summary>True if movement is temporarily suspended for a discrete action.</summary>
        public static bool IsMovementSuspended { get; private set; }

        /// <summary>
        /// Start continuous movement: hold the movement key and aim cursor at target.
        /// Call UpdateMovementCursor() each tick to steer. Active until StopMovement() is called.
        /// Does NOT go through the action gate — movement is a background layer.
        /// </summary>
        public static bool StartMovement(Vector2 absScreenPos, Keys moveKey)
        {
            if (TryCaptureReplay("StartMovement", absScreenPos, moveKey)) return true;
            if (!ClampToWindow(ref absScreenPos)) return false;

            // Already moving with same key and not suspended — just steer cursor.
            if (IsMovementActive && _movementKey == moveKey && !IsMovementSuspended)
            {
                Input.SetCursorPos(absScreenPos);
                _movementCursorPos = absScreenPos;
                return true;
            }

            // Suspended with same key — update stored position, let TickMovementLayer resume.
            if (IsMovementActive && _movementKey == moveKey && IsMovementSuspended)
            {
                FileLog($"StartMovement: already active but suspended, updating target to {absScreenPos}");
                _movementCursorPos = absScreenPos;
                return true;
            }

            // Switching to a different key — release old.
            if (IsMovementActive && _movementKey != moveKey)
                SendKeyUp(_movementKey, "movement");

            Input.SetCursorPos(absScreenPos);
            _movementCursorPos = absScreenPos;
            _movementKey = moveKey;
            IsMovementActive = true;

            if (!IsMovementSuspended && CanSendInputEvent)
            {
                FileLog($"StartMovement: sending key {moveKey} to {absScreenPos}");
                SendKeyDown(moveKey, "movement");
                IsMovementSuspended = false;
            }
            else
            {
                FileLog($"StartMovement: logic starting but INITIALIZED AS SUSPENDED (suspended={IsMovementSuspended}, canSend={CanSendInputEvent})");
                IsMovementSuspended = true;
            }

            LogAction("StartMovement", absScreenPos, moveKey, true);
            return true;
        }

        private static DateTime _lastCursorUpdate = DateTime.MinValue;
        private const int CursorUpdateMinIntervalMs = 50;  // ~20 updates/sec max
        private const float CursorUpdateMinDistSq = 25f;   // 5px minimum change
        private static DateTime _lastMoveLog = DateTime.MinValue;

        /// <summary>
        /// Update cursor position while movement is active. Throttled to ~20/sec.
        /// Call every tick from NavigationSystem to steer the character.
        /// </summary>
        public static bool UpdateMovementCursor(Vector2 absScreenPos)
        {
            if (!IsMovementActive || IsMovementSuspended) return false;
            if (TryCaptureReplay("UpdateMovementCursor", absScreenPos)) return true;
            if (!ClampToWindow(ref absScreenPos)) return false;

            var distSq = Vector2.DistanceSquared(absScreenPos, _movementCursorPos);
            var elapsed = (DateTime.Now - _lastCursorUpdate).TotalMilliseconds;
            if (distSq < CursorUpdateMinDistSq && elapsed < CursorUpdateMinIntervalMs)
                return true;

            if ((DateTime.Now - _lastMoveLog).TotalSeconds > 1.0)
            {
                FileLog($"UpdateMovementCursor: active steering to {absScreenPos}");
                _lastMoveLog = DateTime.Now;
            }

            Input.SetCursorPos(absScreenPos);
            _movementCursorPos = absScreenPos;
            _lastCursorUpdate = DateTime.Now;
            return true;
        }

        /// <summary>Stop continuous movement and release the held key.</summary>
        public static void StopMovement()
        {
            if (!IsMovementActive) return;
            FileLog($"StopMovement: releasing {_movementKey}");
            SendKeyUp(_movementKey, "stop");
            IsMovementActive = false;
            IsMovementSuspended = false;
        }

        /// <summary>
        /// Suspend movement temporarily for a discrete action (click, targeted skill).
        /// Releases the held movement key without moving the cursor — avoids visible mouse jumps.
        /// ResumeMovement() will restore the cursor to the last movement target.
        /// </summary>
        public static void SuspendMovement()
        {
            if (!IsMovementActive || IsMovementSuspended) return;
            SendKeyUp(_movementKey, "suspend");
            IsMovementSuspended = true;
        }

        /// <summary>
        /// Resume movement after a discrete action completes.
        /// Re-presses the movement key and restores cursor to last movement position.
        /// </summary>
        public static void ResumeMovement()
        {
            if (!IsMovementActive || !IsMovementSuspended) return;
            if (!CanSendInputEvent) return;

            Input.SetCursorPos(_movementCursorPos);
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

            // Don't resume while modifier keys are held (e.g. Ctrl for stash transfers).
            if (HasHeldModifiers) return;

            if (CanAct)
                ResumeMovement();

            // Safety: force-resume if suspended too long (action got stuck).
            if (IsMovementSuspended && (DateTime.Now - _lastInputEvent).TotalMilliseconds > 2000)
            {
                FileLog("TickMovementLayer: Force-resuming movement after 2s timeout");
                ResumeMovement();
            }
        }

        // ══════════════════════════════════════════════════════════════
        // Replay mode — captures actions without sending real input
        // ══════════════════════════════════════════════════════════════

        public static bool ReplayMode { get; set; }

        public static readonly List<ReplayCapturedAction> CapturedActions = new();

        public static List<ReplayCapturedAction> HarvestCapturedActions()
        {
            var actions = new List<ReplayCapturedAction>(CapturedActions);
            CapturedActions.Clear();
            return actions;
        }

        public class ReplayCapturedAction
        {
            public string Type { get; set; } = "";
            public float? X { get; set; }
            public float? Y { get; set; }
            public int? Key { get; set; }
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
            return true;
        }

        // ══════════════════════════════════════════════════════════════
        // Held key tracking (channeling skills)
        // ══════════════════════════════════════════════════════════════

        private static readonly Dictionary<Keys, DateTime> _heldKeys = new();

        public static float HeldKeyTimeoutSeconds = 5f;

        public static bool HasHeldKeys => _heldKeys.Count > 0;

        /// <summary>True if a modifier key (Ctrl/Shift/Alt) is currently held.</summary>
        public static bool HasHeldModifiers => _heldKeys.Keys.Any(k =>
            k == Keys.LControlKey || k == Keys.RControlKey || k == Keys.ControlKey ||
            k == Keys.LShiftKey || k == Keys.RShiftKey || k == Keys.ShiftKey ||
            k == Keys.LMenu || k == Keys.RMenu || k == Keys.Menu);

        public static bool IsHeld(Keys key) => _heldKeys.ContainsKey(key);

        public static void TrackHeldKey(Keys key) => _heldKeys[key] = DateTime.Now;

        /// <summary>Hold a key down (KeyDown without KeyUp). Stays held until Released or watchdog fires.</summary>
        public static bool HoldKey(Keys key)
        {
            if (TryCaptureReplay("HoldKey", key: key)) return true;
            if (!CanAct) return false;
            SuspendMovement();
            if (_heldKeys.ContainsKey(key))
                SendKeyUp(key);
            SendKeyDown(key);
            _heldKeys[key] = DateTime.Now;
            NextActionAt = DateTime.Now.AddMilliseconds(ActionCooldownMs);
            return true;
        }

        /// <summary>Hold a key at a screen position (targeted channeling skills).</summary>
        public static bool HoldKeyAt(Vector2 absPos, Keys key)
        {
            if (TryCaptureReplay("HoldKeyAt", absPos, key)) return true;
            if (!CanAct) return false;
            if (!ClampToWindow(ref absPos)) return false;
            SuspendMovement();
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

        public static void ReleaseKey(Keys key)
        {
            if (_heldKeys.Remove(key))
                SendKeyUp(key);
        }

        /// <summary>Release ALL held keys. Call on mode exit, area change, phase transitions.</summary>
        public static void ReleaseAllKeys()
        {
            if (_heldKeys.Count > 0)
            {
                foreach (var key in _heldKeys.Keys)
                    SendKeyUp(key);
                _heldKeys.Clear();
            }
        }

        /// <summary>Tick held key watchdog. Call once per frame from BotCore.</summary>
        public static void TickHeldKeys()
        {
            if (_heldKeys.Count == 0) return;
            var now = DateTime.Now;
            var expired = new List<Keys>();
            foreach (var (key, pressedAt) in _heldKeys)
                if ((now - pressedAt).TotalSeconds >= HeldKeyTimeoutSeconds)
                    expired.Add(key);
            foreach (var key in expired)
            {
                SendKeyUp(key);
                _heldKeys.Remove(key);
            }
        }

        // ══════════════════════════════════════════════════════════════
        // Click position randomization
        // ══════════════════════════════════════════════════════════════

        public static Vector2 RandomizeWithinRect(float centerX, float centerY, float halfWidth, float halfHeight)
        {
            float rx = (float)(_rng.NextDouble() + _rng.NextDouble() - 1.0);
            float ry = (float)(_rng.NextDouble() + _rng.NextDouble() - 1.0);
            return new Vector2(centerX + rx * halfWidth, centerY + ry * halfHeight);
        }

        public static Vector2 RandomizeWithinRect(SharpDX.RectangleF rect)
        {
            return RandomizeWithinRect(
                rect.X + rect.Width / 2f,
                rect.Y + rect.Height / 2f,
                rect.Width * 0.4f,
                rect.Height * 0.4f);
        }

        // ── High-level click helpers ──

        private const int MaxHoverAttempts = 2;
        private const int HoverVerifyDelayMs = 35;

        /// <summary>
        /// Check if a window-relative rect is within the game window.
        /// </summary>
        public static bool IsRectOnScreen(SharpDX.RectangleF rect)
        {
            if (WindowRect.Width < 10 || WindowRect.Height < 10) return false;
            var centerX = rect.X + rect.Width / 2;
            var centerY = rect.Y + rect.Height / 2;
            const float pad = 5f;
            return centerX >= pad && centerX <= WindowRect.Width - pad &&
                   centerY >= pad && centerY <= WindowRect.Height - pad;
        }

        /// <summary>
        /// Click a world entity with hover verification via Targetable.isTargeted.
        /// Re-reads entity screen position each attempt to compensate for camera movement.
        /// </summary>
        public static bool ClickEntity(GameController gc, Entity entity)
        {
            if (!CanAct) return false;
            if (!GetEntityScreenBounds(gc, entity, out var screenCenter, out var halfW, out var halfH))
                return false;

            SuspendMovement();
            var windowRect = gc.Window.GetWindowRectangle();
            var settle = RandSettle();
            var hold = RandHold();
            var moveMs = EstimateMoveMs(new Vector2(windowRect.X + screenCenter.X, windowRect.Y + screenCenter.Y));
            NextActionAt = DateTime.Now.AddMilliseconds(
                MaxHoverAttempts * (moveMs + settle) + hold + ActionCooldownMs);
            _ = DoClickEntityWithVerify(gc, entity, screenCenter, halfW, halfH, windowRect, settle, hold);
            LogAction("ClickEntity", screenCenter, null, true);
            return true;
        }

        private static async Task DoClickEntityWithVerify(
            GameController gc, Entity entity, Vector2 screenCenter, float halfW, float halfH,
            SharpDX.RectangleF windowRect, int settleMs, int holdMs)
        {
            for (int attempt = 0; attempt < MaxHoverAttempts; attempt++)
            {
                // Re-read position each attempt — compensates for camera movement during settle
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

                var spread = attempt == 0 ? 1f : 1f + attempt * 0.3f;
                var clickPos = RandomizeWithinRect(screenCenter.X, screenCenter.Y, halfW * spread, halfH * spread);
                var absPos = new Vector2(windowRect.X + clickPos.X, windowRect.Y + clickPos.Y);

                await MoveCursorTo(absPos);
                await Task.Delay(settleMs);

                // Final snap right before verification
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

                try
                {
                    var targetable = entity.GetComponent<Targetable>();
                    if (targetable?.isTargeted == true)
                    {
                        await SendDelay();
                        SendLeftDown("entity-verified");
                        await Task.Delay(holdMs);
                        await SendDelay();
                        SendLeftUp("entity-verified");
                        return;
                    }
                }
                catch { }

                await Task.Delay(HoverVerifyDelayMs);
            }

            // Fallback — click at last position
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

        /// <summary>Click within a label rect with center-biased randomization.</summary>
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
        /// Invoked right before mousedown to compensate for character slide during settle.
        /// </summary>
        public static bool ClickLabelCorrected(GameController gc, SharpDX.RectangleF rect,
            Func<SharpDX.RectangleF?> rectProvider)
        {
            if (!IsRectOnScreen(rect)) return false;
            var clickPos = RandomizeWithinRect(rect);
            var windowRect = gc.Window.GetWindowRectangle();
            var absPos = new Vector2(windowRect.X + clickPos.X, windowRect.Y + clickPos.Y);
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
        /// Click within a label rect with hover verification. Aborts without clicking if verification
        /// fails on all attempts (prevents hitting overlapping entities like altars/dialogs).
        /// </summary>
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

        public static bool LastClickWasVerified { get; private set; }

        private static async Task DoClickLabelVerified(
            Entity entity, SharpDX.RectangleF rect, SharpDX.RectangleF windowRect,
            int settleMs, int holdMs, Func<SharpDX.RectangleF?>? rectProvider = null)
        {
            LastClickWasVerified = false;

            for (int attempt = 0; attempt < MaxHoverAttempts; attempt++)
            {
                var useRect = rectProvider?.Invoke() ?? rect;
                var clickPos = RandomizeWithinRect(useRect);
                var absPos = new Vector2(windowRect.X + clickPos.X, windowRect.Y + clickPos.Y);

                await MoveCursorTo(absPos);
                await Task.Delay(settleMs);

                if (rectProvider != null)
                {
                    var correctedRect = rectProvider.Invoke();
                    if (correctedRect.HasValue)
                    {
                        var correctedPos = RandomizeWithinRect(correctedRect.Value);
                        Input.SetCursorPos(new Vector2(windowRect.X + correctedPos.X, windowRect.Y + correctedPos.Y));
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

            // Verification failed — don't click. Prevents hitting overlapping entities.
            LastClickWasVerified = false;
        }

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

        // ══════════════════════════════════════════════════════════════
        // Gaussian delay generation (Box-Muller transform)
        // ══════════════════════════════════════════════════════════════

        public const float SettleMeanMs = 70f;
        public const float SettleStdDevMs = 10f;
        public const float HoldMeanMs = 40f;
        public const float HoldStdDevMs = 10f;
        private const int DelayFloorMs = 20;
        private const int DelayCeilingMs = 100;

        private static int GaussianDelay(float mean, float stdDev)
        {
            double u1 = 1.0 - _rng.NextDouble();
            double u2 = _rng.NextDouble();
            double normal = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
            return Math.Clamp((int)(mean + stdDev * normal), DelayFloorMs, DelayCeilingMs);
        }

        public static int RandSettle() => GaussianDelay(SettleMeanMs, SettleStdDevMs);
        public static int RandHold() => GaussianDelay(HoldMeanMs, HoldStdDevMs);

        // ══════════════════════════════════════════════════════════════
        // Mouse movement interpolation
        // ══════════════════════════════════════════════════════════════

        private const int MoveMinMs = 15;
        private const int MoveMaxMs = 80;
        private const float MoveMaxDistance = 2000f;
        private const int MoveSteps = 8;
        private const float MoveJitterPx = 3f;
        private const float LandingJitterPx = 3f;

        private static async Task MoveCursorTo(Vector2 target)
        {
            var start = Input.ForceMousePositionNum;
            var delta = target - start;
            var dist = delta.Length();

            if (dist < 5f)
            {
                Input.SetCursorPos(target);
                return;
            }

            float t = Math.Clamp(dist / MoveMaxDistance, 0f, 1f);
            int totalMs = MoveMinMs + (int)((MoveMaxMs - MoveMinMs) * t);
            int stepDelayMs = Math.Max(1, totalMs / MoveSteps);
            var perp = Vector2.Normalize(new Vector2(-delta.Y, delta.X));

            for (int i = 1; i <= MoveSteps; i++)
            {
                float progress = (float)i / MoveSteps;
                var pos = start + delta * progress;
                if (i < MoveSteps)
                {
                    float jitter = (float)(_rng.NextDouble() * 2 - 1) * MoveJitterPx;
                    float taper = 1f - Math.Abs(progress - 0.5f) * 2f;
                    pos += perp * jitter * taper;
                }
                Input.SetCursorPos(pos);
                await Task.Delay(stepDelayMs);
            }

            var jX = (float)(_rng.NextDouble() * 2 - 1) * LandingJitterPx;
            var jY = (float)(_rng.NextDouble() * 2 - 1) * LandingJitterPx;
            Input.SetCursorPos(target + new Vector2(jX, jY));
        }

        // ── Cursor + key press ──

        /// <summary>Move cursor, settle, press+hold+release key. Suspends continuous movement.</summary>
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

        /// <summary>Force cursor+key press, bypassing gate. For dodge.</summary>
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
            var corrected = positionCorrection?.Invoke();
            if (corrected.HasValue)
                Input.SetCursorPos(corrected.Value);
            await SendDelay();
            SendKeyDown(key);
            await Task.Delay(holdMs);
            await SendDelay();
            SendKeyUp(key);
        }

        // ── Key press only ──

        /// <summary>Press and release a key. Suspends continuous movement.</summary>
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
        /// </summary>
        public static bool PressKeyOverlay(Keys key)
        {
            if (TryCaptureReplay("PressKeyOverlay", key: key)) return true;
            if (!CanAct) { LogAction("PressKeyOverlay", null, key, false); return false; }
            // Block overlay keys while a modifier is held — e.g. Ctrl+flask sends wrong key combo.
            if (HasHeldModifiers) { LogAction("PressKeyOverlay", null, key, false); return false; }
            var hold = RandHold();
            NextActionAt = DateTime.Now.AddMilliseconds(hold + ActionCooldownMs);
            _ = DoPressKey(key, hold);
            LogAction("PressKeyOverlay", null, key, true);
            return true;
        }

        /// <summary>Ctrl+key press (no cursor move). For Ctrl+key combat skill bindings.</summary>
        public static bool CtrlPressKey(Keys key)
        {
            if (TryCaptureReplay("CtrlPressKey", key: key)) return true;
            if (!CanAct) { LogAction("CtrlPressKey", null, key, false); return false; }
            SuspendMovement();
            ReleaseAllKeys();
            var hold = RandHold();
            NextActionAt = DateTime.Now.AddMilliseconds(hold * 2 + ActionCooldownMs);
            _ = DoCtrlPressKey(key, hold);
            LogAction("CtrlPressKey", null, key, true);
            return true;
        }

        private static async Task DoCtrlPressKey(Keys key, int holdMs)
        {
            await SendDelay();
            SendKeyDown(Keys.ControlKey, "ctrl");
            await Task.Delay(holdMs);
            await SendDelay();
            SendKeyDown(key);
            await Task.Delay(holdMs);
            await SendDelay();
            SendKeyUp(key);
            await Task.Delay(holdMs);
            await SendDelay();
            SendKeyUp(Keys.ControlKey, "ctrl");
        }

        /// <summary>Move cursor, settle, Ctrl+key press. For targeted Ctrl+key combat skills.</summary>
        public static bool CtrlCursorPressKey(Vector2 absPos, Keys key)
        {
            if (TryCaptureReplay("CtrlCursorPressKey", absPos, key)) return true;
            if (!CanAct) { LogAction("CtrlCursorPressKey", absPos, key, false); return false; }
            if (!ClampToWindow(ref absPos)) { LogAction("CtrlCursorPressKey", absPos, key, false); return false; }
            SuspendMovement();
            ReleaseAllKeys();
            var moveMs = EstimateMoveMs(absPos);
            var settle = RandSettle();
            var hold = RandHold();
            NextActionAt = DateTime.Now.AddMilliseconds(moveMs + settle + hold * 2 + ActionCooldownMs);
            _ = DoCtrlCursorPressKey(absPos, key, settle, hold);
            LogAction("CtrlCursorPressKey", absPos, key, true);
            return true;
        }

        private static async Task DoCtrlCursorPressKey(Vector2 absPos, Keys key, int settleMs, int holdMs)
        {
            await SendDelay();
            SendKeyDown(Keys.ControlKey, "ctrl");
            await Task.Delay(holdMs);
            await MoveCursorTo(absPos);
            await Task.Delay(settleMs);
            await SendDelay();
            SendKeyDown(key);
            await Task.Delay(holdMs);
            await SendDelay();
            SendKeyUp(key);
            await Task.Delay(holdMs);
            await SendDelay();
            SendKeyUp(Keys.ControlKey, "ctrl");
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

        /// <summary>Move cursor, settle, left-click. Suspends continuous movement.</summary>
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

        /// <summary>Move cursor without clicking. Suspends continuous movement.</summary>
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

        /// <summary>Ctrl+left-click (stash transfers). Suspends movement.</summary>
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
            NextActionAt = DateTime.Now.AddMilliseconds(hold + moveMs + settle + hold + ActionCooldownMs);
            _ = DoCtrlClick(absPos, settle, hold);
            LogAction("CtrlClick", absPos, null, true);
            return true;
        }

        private static async Task DoCtrlClick(Vector2 absPos, int settleMs, int holdMs)
        {
            await SendDelay();
            // Use Input.KeyDown directly — Ctrl is a modifier and must never be dropped by the
            // rate limiter. SendKeyDown() would silently discard it if fired too soon.
            Input.KeyDown(Keys.ControlKey);
            MarkInputEvent("KeyDown", "ctrl");
            await Task.Delay(10); // explicit gap so game registers Ctrl before cursor moves
            await MoveCursorTo(absPos);
            await Task.Delay(settleMs);
            await SendDelay();
            SendLeftDown("ctrl-click");
            await Task.Delay(holdMs);
            await SendDelay();
            SendLeftUp("ctrl-click");
            await Task.Delay(10);
            Input.KeyUp(Keys.ControlKey);
            MarkInputEvent("KeyUp", "ctrl");
        }

        // ── Helpers ──

        private static int EstimateMoveMs(Vector2 target)
        {
            var dist = (target - Input.ForceMousePositionNum).Length();
            if (dist < 5f) return 0;
            float t = Math.Clamp(dist / MoveMaxDistance, 0f, 1f);
            return MoveMinMs + (int)((MoveMaxMs - MoveMinMs) * t);
        }

        /// <summary>Cancel any pending action and reset gate.</summary>
        public static void Cancel()
        {
            NextActionAt = DateTime.MinValue;
        }

        /// <summary>
        /// Drag the atlas panel: hold left mouse at <paramref name="from"/>, move to <paramref name="to"/>, release.
        /// Both positions must be absolute screen coordinates.
        /// Returns false if the action gate is closed.
        /// The drag takes ~dragMs milliseconds and the gate is reserved accordingly.
        /// </summary>
        public static bool DragAtlas(Vector2 from, Vector2 to, int dragMs = 300)
        {
            if (!CanAct) return false;
            if (!ClampToWindow(ref from)) return false;
            if (!ClampToWindow(ref to)) return false;
            SuspendMovement();
            ReleaseAllKeys();
            NextActionAt = DateTime.Now.AddMilliseconds(dragMs + ActionCooldownMs + 200);
            _ = DoDragAtlas(from, to, dragMs);
            FileLog($"DragAtlas: {from} → {to} over {dragMs}ms");
            return true;
        }

        private static async Task DoDragAtlas(Vector2 from, Vector2 to, int dragMs)
        {
            await MoveCursorTo(from);
            await Task.Delay(30);
            await SendDelay();
            SendLeftDown("atlas-drag");
            await Task.Delay(20);

            // Interpolate cursor from → to in small steps
            int steps = Math.Max(4, dragMs / 20);
            for (int i = 1; i <= steps; i++)
            {
                float t = (float)i / steps;
                var pos = Vector2.Lerp(from, to, t);
                Input.SetCursorPos(pos);
                await Task.Delay(dragMs / steps);
            }

            await Task.Delay(20);
            SendLeftUp("atlas-drag");
        }
    }
}
