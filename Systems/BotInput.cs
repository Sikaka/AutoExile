using System.Numerics;
using System.Windows.Forms;
using Input = ExileCore.Input;

namespace AutoExile.Systems
{
    /// <summary>
    /// Global action gate. ALL game inputs go through here.
    ///
    /// Design:
    /// - Single NextActionAt timestamp. Checked in BotCore.Tick() — if not ready, entire tick skips.
    /// - When an action fires: set NextActionAt to now + full duration, then run async sequence.
    /// - Sequence: move cursor → random delay (30-50ms) → button/key down → random delay (30-50ms) → up.
    /// - No bot logic runs during the async sequence because NextActionAt is already in the future.
    /// </summary>
    public static class BotInput
    {
        /// <summary>Minimum ms between actions (end of one action to start of next). Configurable.</summary>
        public static int ActionCooldownMs = 75;

        /// <summary>Min ms for cursor settle delay before key/button press.</summary>
        public const int SettleMinMs = 30;

        /// <summary>Max ms for cursor settle delay.</summary>
        public const int SettleMaxMs = 50;

        /// <summary>Min ms to hold key/button down before releasing.</summary>
        public const int HoldMinMs = 30;

        /// <summary>Max ms to hold key/button down.</summary>
        public const int HoldMaxMs = 50;

        /// <summary>Global gate — no actions before this time.</summary>
        public static DateTime NextActionAt = DateTime.MinValue;

        private static readonly Random _rng = new();

        /// <summary>True if we can start a new action right now.</summary>
        public static bool CanAct => DateTime.Now >= NextActionAt;

        private static int RandSettle() => _rng.Next(SettleMinMs, SettleMaxMs + 1);
        private static int RandHold() => _rng.Next(HoldMinMs, HoldMaxMs + 1);

        // ── Cursor + key press (walk commands, targeted skills) ──

        /// <summary>Move cursor, settle, press+hold+release key.</summary>
        public static bool CursorPressKey(Vector2 absPos, Keys key)
        {
            if (!CanAct) return false;
            var settle = RandSettle();
            var hold = RandHold();
            NextActionAt = DateTime.Now.AddMilliseconds(settle + hold + ActionCooldownMs);
            _ = DoCursorPressKey(absPos, key, settle, hold);
            return true;
        }

        private static async Task DoCursorPressKey(Vector2 absPos, Keys key, int settleMs, int holdMs)
        {
            Input.SetCursorPos(absPos);
            await Task.Delay(settleMs);
            Input.SetCursorPos(absPos); // re-assert in case of drift
            Input.KeyDown(key);
            await Task.Delay(holdMs);
            Input.KeyUp(key);
        }

        // ── Key press only (self-cast, flasks — no cursor move) ──

        /// <summary>Press and release a key. Returns false if gate is closed.</summary>
        public static bool PressKey(Keys key)
        {
            if (!CanAct) return false;
            var hold = RandHold();
            NextActionAt = DateTime.Now.AddMilliseconds(hold + ActionCooldownMs);
            _ = DoPressKey(key, hold);
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
            if (!CanAct) return false;
            var settle = RandSettle();
            var hold = RandHold();
            NextActionAt = DateTime.Now.AddMilliseconds(settle + hold + ActionCooldownMs);
            _ = DoClick(absPos, rightClick: false, settle, hold);
            return true;
        }

        /// <summary>Move cursor, settle, right-click.</summary>
        public static bool RightClick(Vector2 absPos)
        {
            if (!CanAct) return false;
            var settle = RandSettle();
            var hold = RandHold();
            NextActionAt = DateTime.Now.AddMilliseconds(settle + hold + ActionCooldownMs);
            _ = DoClick(absPos, rightClick: true, settle, hold);
            return true;
        }

        private static async Task DoClick(Vector2 absPos, bool rightClick, int settleMs, int holdMs)
        {
            Input.SetCursorPos(absPos);
            await Task.Delay(settleMs);
            Input.SetCursorPos(absPos);
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
            if (!CanAct) return false;
            var settle = RandSettle();
            var hold = RandHold();
            // Ctrl down + settle + cursor + settle + click hold + release + ctrl up
            NextActionAt = DateTime.Now.AddMilliseconds(hold + settle + hold + ActionCooldownMs);
            _ = DoCtrlClick(absPos, settle, hold);
            return true;
        }

        private static async Task DoCtrlClick(Vector2 absPos, int settleMs, int holdMs)
        {
            Input.KeyDown(Keys.ControlKey);
            await Task.Delay(holdMs);
            Input.SetCursorPos(absPos);
            await Task.Delay(settleMs);
            Input.SetCursorPos(absPos);
            Input.LeftDown();
            await Task.Delay(holdMs);
            Input.LeftUp();
            await Task.Delay(holdMs);
            Input.KeyUp(Keys.ControlKey);
        }

        /// <summary>Cancel any pending action and reset gate.</summary>
        public static void Cancel()
        {
            NextActionAt = DateTime.MinValue;
        }
    }
}
