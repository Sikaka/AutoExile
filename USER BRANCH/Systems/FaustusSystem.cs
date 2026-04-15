using ExileCore;
using ExileCore.PoEMemory.Elements;
using ExileCore.PoEMemory.MemoryObjects;
using System.Numerics;
using System.Windows.Forms;

namespace AutoExile.Systems
{
    public class FaustusSystem
    {
        private const string FaustusPath = "Metadata/NPC/League/Kalguur/VillageFaustusHideout";
        private const float StateTimeoutSeconds = 30f;
        private const int ClickCooldownMs = 500;

        private FaustusState _state = FaustusState.Idle;
        private DateTime _stateEnteredAt = DateTime.MinValue;
        private DateTime _lastClickAt = DateTime.MinValue;

        private string _wantedMetaSubstring = "";
        private string _wantedSearchName = "";
        private int _wantedQuantity;
        private string _payCurrencyBaseName = "";
        private string _paySearchName = "";
        private int _payQuantity;
        private int _dialogStep;
        private int _enterStep;
        private bool _typedSearch;
        private DateTime _typedAt = DateTime.MinValue;
        private string _wantedNinjaName = "";
        private string _ninjaCategory = "";

        public bool IsBusy => _state != FaustusState.Idle;
        public string Status { get; private set; } = "";

        public void Start(string wantedItemMetadataSubstring, string wantedSearchName, int wantedQuantity,
            string payCurrencyBaseName, string paySearchName, int payQuantity,
            string ninjaName = "", string ninjaCategory = "", string ninjaLeague = "")
        {
            _wantedMetaSubstring = wantedItemMetadataSubstring;
            _wantedSearchName = wantedSearchName;
            _wantedQuantity = wantedQuantity;
            _payCurrencyBaseName = payCurrencyBaseName;
            _paySearchName = paySearchName;
            _payQuantity = payQuantity;
            _wantedNinjaName = ninjaName;
            _ninjaCategory = ninjaCategory;
            SetState(FaustusState.WalkingToFaustus);
        }

        public void Cancel(GameController? gc = null, NavigationSystem? nav = null)
        {
            if (nav != null && gc != null) nav.Stop(gc);
            SetState(FaustusState.Idle);
        }

        public FaustusResult Tick(BotContext ctx)
        {
            if (_state == FaustusState.Idle) return FaustusResult.None;

            if ((DateTime.Now - _stateEnteredAt).TotalSeconds > StateTimeoutSeconds)
            {
                Status = $"Faustus: timeout in {_state}";
                Cancel(ctx.Game, ctx.Navigation);
                return FaustusResult.Failed;
            }

            return _state switch
            {
                FaustusState.WalkingToFaustus     => TickWalk(ctx),
                FaustusState.WaitingForDialog      => TickWaitDialog(ctx),
                FaustusState.ClickingCurrencyExchange => TickClickDialog(ctx),
                FaustusState.WaitingForPanel       => TickWaitPanel(ctx),
                FaustusState.PickingWanted         => TickPickWanted(ctx),
                FaustusState.PickingPay            => TickPickPay(ctx),
                FaustusState.EnteringQuantities    => TickEnterQuantities(ctx),
                FaustusState.PlacingOrder          => TickPlaceOrder(ctx),
                FaustusState.AwaitingFulfillment   => TickAwaitFulfillment(ctx),
                FaustusState.Done                  => FaustusResult.Succeeded,
                FaustusState.Failed                => FaustusResult.Failed,
                _                                  => FaustusResult.InProgress,
            };
        }

        // ── Walk to Faustus ──

        private FaustusResult TickWalk(BotContext ctx)
        {
            var gc = ctx.Game;
            if (gc.IngameState.IngameUi.NpcDialog?.IsVisible == true)
            {
                ctx.Interaction.Cancel(gc);
                ctx.Navigation.Stop(gc);
                SetState(FaustusState.ClickingCurrencyExchange);
                return FaustusResult.InProgress;
            }

            var faustus = FindFaustus(gc);
            if (faustus == null) { Status = "Faustus: NPC not found"; return FaustusResult.InProgress; }

            if (ctx.Interaction.IsBusy)
            {
                var r = ctx.Interaction.Tick(gc);
                Status = $"Faustus: walking ({ctx.Interaction.Status})";
                if (r == InteractionResult.Succeeded || r == InteractionResult.Failed)
                    SetState(FaustusState.WaitingForDialog);
                return FaustusResult.InProgress;
            }

            ctx.Interaction.InteractWithEntity(faustus, ctx.Navigation, requireProximity: true);
            Status = "Faustus: interacting";
            return FaustusResult.InProgress;
        }

        // ── Wait for dialog ──

        private FaustusResult TickWaitDialog(BotContext ctx)
        {
            var gc = ctx.Game;
            if (ctx.Interaction.IsBusy)
            {
                var r = ctx.Interaction.Tick(gc);
                if (r == InteractionResult.Failed) return Fail("interaction failed");
            }

            if (gc.IngameState.IngameUi.NpcDialog?.IsVisible == true)
            {
                ctx.Interaction.Cancel(gc);
                ctx.Navigation.Stop(gc);
                SetState(FaustusState.ClickingCurrencyExchange);
                return FaustusResult.InProgress;
            }

            Status = "Faustus: waiting for dialog";
            return FaustusResult.InProgress;
        }

        // ── Click "Currency Exchange" in dialog ──

        private FaustusResult TickClickDialog(BotContext ctx)
        {
            var gc = ctx.Game;
            var dialog = gc.IngameState.IngameUi.NpcDialog;
            if (dialog == null || !dialog.IsVisible) return Fail("dialog closed");

            // Wait for dialog to fully settle
            if (!Settled(500)) return FaustusResult.InProgress;
            if (!CanClick()) return FaustusResult.InProgress;

            var lines = dialog.NpcLines;
            if (lines == null) { Status = "Faustus: waiting for dialog lines"; return FaustusResult.InProgress; }

            foreach (var line in lines)
            {
                if (line?.Element == null) continue;
                if (line.Text?.Contains("Currency Exchange", StringComparison.OrdinalIgnoreCase) == true)
                {
                    Status = "Faustus: clicking Currency Exchange";
                    Click(gc, line.Element);
                    SetState(FaustusState.WaitingForPanel);
                    return FaustusResult.InProgress;
                }
            }

            Status = "Faustus: Currency Exchange option not found in dialog";
            return FaustusResult.InProgress;
        }

        // ── Wait for exchange panel ──

        private FaustusResult TickWaitPanel(BotContext ctx)
        {
            var gc = ctx.Game;
            ctx.Navigation.Stop(gc);

            var panel = gc.IngameState.IngameUi.CurrencyExchangePanel;
            if (panel != null && panel.IsVisible)
            {
                SetState(FaustusState.PickingWanted);
                return FaustusResult.InProgress;
            }

            if ((DateTime.Now - _stateEnteredAt).TotalSeconds > 2.0 && gc.IngameState.IngameUi.NpcDialog?.IsVisible == true)
            {
                SetState(FaustusState.ClickingCurrencyExchange);
                return FaustusResult.InProgress;
            }

            Status = "Faustus: waiting for exchange panel";
            return FaustusResult.InProgress;
        }

        // ── Pick wanted item ──

        private FaustusResult TickPickWanted(BotContext ctx)
        {
            var gc = ctx.Game;
            var panel = gc.IngameState.IngameUi.CurrencyExchangePanel;
            if (panel == null || !panel.IsVisible) return Fail("panel closed");

            var picker = panel.CurrencyPicker;

            // Picker is open and in "wanted" mode
            if (picker != null && picker.IsVisible && picker.IsPickingWantedCurrency)
            {
                // Step 1: type search once after picker settles
                if (!_typedSearch && Settled(400))
                {
                    SendKeys.SendWait("^a");
                    System.Threading.Thread.Sleep(50);
                    TypeText(_wantedSearchName);
                    _typedSearch = true;
                    _typedAt = DateTime.Now;
                    Status = $"Faustus: typed {_wantedSearchName}";
                    return FaustusResult.InProgress;
                }

                // Step 2: wait 600ms after typing for results to filter, then click
                if (_typedSearch && (DateTime.Now - _typedAt).TotalMilliseconds >= 600)
                {
                    var option = FindPickerOption(picker, _wantedMetaSubstring, null);
                    if (option != null)
                    {
                        if (!CanClick()) return FaustusResult.InProgress;
                        Status = "Faustus: clicking wanted item";
                        Click(gc, option);
                        SetState(FaustusState.PickingPay);
                        return FaustusResult.InProgress;
                    }
                    Status = $"Faustus: no match for {_wantedSearchName}";
                }
                return FaustusResult.InProgress;
            }

            // Open the picker — only once we've settled into this state
            if (!Settled(400)) return FaustusResult.InProgress;
            if (!CanClick()) return FaustusResult.InProgress;

            var wantBtn = panel.GetChildAtIndex(7)?.GetChildAtIndex(0);
            if (wantBtn != null && wantBtn.IsVisible)
            {
                Status = "Faustus: opening I Want picker";
                Click(gc, wantBtn);
            }
            return FaustusResult.InProgress;
        }

        // ── Pick pay currency ──

        private FaustusResult TickPickPay(BotContext ctx)
        {
            var gc = ctx.Game;
            var panel = gc.IngameState.IngameUi.CurrencyExchangePanel;
            if (panel == null || !panel.IsVisible) return Fail("panel closed");

            var picker = panel.CurrencyPicker;

            // Wanted picker still open — wait for it to close
            if (picker != null && picker.IsVisible && picker.IsPickingWantedCurrency)
            {
                Status = "Faustus: waiting for wanted picker to close";
                return FaustusResult.InProgress;
            }

            // Pay picker open
            if (picker != null && picker.IsVisible && !picker.IsPickingWantedCurrency)
            {
                // Step 1: type search once after picker settles
                if (!_typedSearch && Settled(400))
                {
                    SendKeys.SendWait("^a");
                    System.Threading.Thread.Sleep(50);
                    TypeText(_paySearchName);
                    _typedSearch = true;
                    _typedAt = DateTime.Now;
                    Status = $"Faustus: typed {_paySearchName}";
                    return FaustusResult.InProgress;
                }

                // Step 2: wait 600ms after typing, then click
                if (_typedSearch && (DateTime.Now - _typedAt).TotalMilliseconds >= 600)
                {
                    var option = FindPickerOption(picker, null, _payCurrencyBaseName);
                    if (option != null)
                    {
                        if (!CanClick()) return FaustusResult.InProgress;
                        Status = "Faustus: clicking pay currency";
                        Click(gc, option);
                        SetState(FaustusState.EnteringQuantities);
                        return FaustusResult.InProgress;
                    }
                    Status = $"Faustus: no match for {_paySearchName}";
                }
                return FaustusResult.InProgress;
            }

            // Open pay picker
            if (!Settled(400)) return FaustusResult.InProgress;
            if (!CanClick()) return FaustusResult.InProgress;

            var haveBtn = panel.GetChildAtIndex(10)?.GetChildAtIndex(0);
            if (haveBtn != null && haveBtn.IsVisible)
            {
                Status = "Faustus: opening I Have picker";
                Click(gc, haveBtn);
            }
            return FaustusResult.InProgress;
        }

        // ── Enter quantities ──

        private FaustusResult TickEnterQuantities(BotContext ctx)
        {
            var gc = ctx.Game;
            var panel = gc.IngameState.IngameUi.CurrencyExchangePanel;
            if (panel == null || !panel.IsVisible) return Fail("panel closed");

            // Wait for picker to fully close
            if (panel.CurrencyPicker?.IsVisible == true) return FaustusResult.InProgress;

            // Resolve poe.ninja pay price if needed
            if (_payQuantity <= 0 && !string.IsNullOrEmpty(_wantedNinjaName))
            {
                float price = PoeNinjaClient.GetChaosValue(_wantedNinjaName, _ninjaCategory, "");
                if (price < 0) { Status = "Faustus: fetching poe.ninja price..."; return FaustusResult.InProgress; }
                _payQuantity = Math.Max(1, (int)Math.Ceiling(price));
            }

            if (!Settled(400)) return FaustusResult.InProgress;
            if (!CanClick()) return FaustusResult.InProgress;

            // Step 0: click I Want quantity field
            // Step 1: type wanted quantity
            // Step 2: click I Have quantity field
            // Step 3: type pay quantity
            // Step 4: advance to PlacingOrder
            switch (_enterStep)
            {
                case 0:
                {
                    var wantInput = panel.GetChildAtIndex(5);
                    if (wantInput != null && wantInput.IsVisible)
                    {
                        Status = $"Faustus: clicking want qty field";
                        Click(gc, wantInput);
                        _enterStep++;
                    }
                    return FaustusResult.InProgress;
                }
                case 1:
                {
                    SendKeys.SendWait("^a");
                    System.Threading.Thread.Sleep(40);
                    TypeText(_wantedQuantity.ToString());
                    Status = $"Faustus: entered wanted={_wantedQuantity}";
                    _enterStep++;
                    return FaustusResult.InProgress;
                }
                case 2:
                {
                    var payInput = panel.GetChildAtIndex(8);
                    if (payInput != null && payInput.IsVisible)
                    {
                        Status = "Faustus: clicking pay qty field";
                        Click(gc, payInput);
                        _enterStep++;
                    }
                    return FaustusResult.InProgress;
                }
                case 3:
                {
                    SendKeys.SendWait("^a");
                    System.Threading.Thread.Sleep(40);
                    TypeText(_payQuantity.ToString());
                    Status = $"Faustus: entered pay={_payQuantity}";
                    _enterStep++;
                    return FaustusResult.InProgress;
                }
                default:
                    SetState(FaustusState.PlacingOrder);
                    return FaustusResult.InProgress;
            }
        }

        // ── Place order ──

        private FaustusResult TickPlaceOrder(BotContext ctx)
        {
            var gc = ctx.Game;
            var panel = gc.IngameState.IngameUi.CurrencyExchangePanel;
            if (panel == null || !panel.IsVisible) return Fail("panel closed");

            if (!Settled(300)) return FaustusResult.InProgress;
            if (!CanClick()) return FaustusResult.InProgress;

            var btn = panel.GetChildAtIndex(16)?.GetChildAtIndex(0);
            if (btn != null && btn.IsVisible)
            {
                Status = "Faustus: placing order";
                Click(gc, btn);
                SetState(FaustusState.AwaitingFulfillment);
            }
            else
            {
                Status = "Faustus: waiting for place order button";
            }
            return FaustusResult.InProgress;
        }

        // ── Await fulfillment ──

        private int _collectStep;

        private FaustusResult TickAwaitFulfillment(BotContext ctx)
        {
            var gc = ctx.Game;
            var panel = gc.IngameState.IngameUi.CurrencyExchangePanel;
            if (panel == null || !panel.IsVisible) return Fail("panel closed");

            // Find the active order row at panel[20][2][0]
            ExileCore.PoEMemory.Element? orderRow = null;
            try { orderRow = panel.GetChildAtIndex(20)?.GetChildAtIndex(2)?.GetChildAtIndex(0); } catch { }

            bool orderExists  = orderRow?.IsVisible == true;
            bool orderDone    = orderExists &&
                                orderRow!.GetChildAtIndex(3)?.Text
                                    ?.Contains("Order Completed", StringComparison.OrdinalIgnoreCase) == true;

            // Only time out if there's no active order at all (panel emptied / order cancelled)
            if (!orderExists)
            {
                if ((DateTime.Now - _stateEnteredAt).TotalSeconds > 10.0)
                {
                    Status = "Faustus: no active order found, moving on";
                    SetState(FaustusState.Done);
                    return FaustusResult.Succeeded;
                }
                Status = "Faustus: waiting for order to appear...";
                return FaustusResult.InProgress;
            }

            // Order exists but not yet completed — wait indefinitely
            if (!orderDone)
            {
                Status = "Faustus: waiting for order to complete...";
                return FaustusResult.InProgress;
            }

            if (!CanClick()) return FaustusResult.InProgress;

            // Ctrl+RightClick slot[4][0] (wanted item), then slot[5][0] (pay refund if present)
            switch (_collectStep)
            {
                case 0:
                {
                    var item = orderRow!.GetChildAtIndex(4)?.GetChildAtIndex(0);
                    if (item != null && item.IsVisible)
                    {
                        Status = "Faustus: collecting wanted item";
                        CtrlRightClick(gc, item);
                    }
                    _collectStep++;
                    return FaustusResult.InProgress;
                }
                case 1:
                {
                    var item = orderRow!.GetChildAtIndex(5)?.GetChildAtIndex(0);
                    if (item != null && item.IsVisible)
                    {
                        Status = "Faustus: collecting pay refund";
                        CtrlRightClick(gc, item);
                    }
                    // Whether or not there was a refund, we're done
                    _collectStep++;
                    return FaustusResult.InProgress;
                }
                default:
                    Status = "Faustus: done";
                    SetState(FaustusState.Done);
                    return FaustusResult.Succeeded;
            }
        }

        // ── Helpers ──

        private FaustusResult Fail(string reason)
        {
            Status = $"Faustus fail: {reason}";
            SetState(FaustusState.Failed);
            return FaustusResult.Failed;
        }

        private void SetState(FaustusState s)
        {
            if (s == FaustusState.ClickingCurrencyExchange) _dialogStep = 0;
            if (s == FaustusState.PickingWanted || s == FaustusState.PickingPay)
            {
                _typedSearch = false;
                _typedAt = DateTime.MinValue;
            }
            if (s == FaustusState.EnteringQuantities) _enterStep = 0;
            if (s == FaustusState.AwaitingFulfillment) _collectStep = 0;
            _state = s;
            _stateEnteredAt = DateTime.Now;
        }

        private bool CanClick() => (DateTime.Now - _lastClickAt).TotalMilliseconds >= ClickCooldownMs && BotInput.CanAct;

        private bool Settled(int ms) => (DateTime.Now - _stateEnteredAt).TotalMilliseconds >= ms;

        private void CtrlRightClick(GameController gc, ExileCore.PoEMemory.Element el)
        {
            var rect = el.GetClientRect();
            var win  = gc.Window.GetWindowRectangle();
            var pos  = new Vector2(win.X + rect.Center.X, win.Y + rect.Center.Y);
            // Ctrl is a modifier — hold it around the right-click (same pattern as BotInput.CtrlClick)
            ExileCore.Input.KeyDown(Keys.ControlKey);
            BotInput.RightClick(pos);
            ExileCore.Input.KeyUp(Keys.ControlKey);
            _lastClickAt = DateTime.Now;
        }

        private void Click(GameController gc, ExileCore.PoEMemory.Element el)
        {
            var rect = el.GetClientRect();
            var win  = gc.Window.GetWindowRectangle();
            var pos  = new Vector2(win.X + rect.Center.X, win.Y + rect.Center.Y);
            BotInput.Click(pos);
            _lastClickAt = DateTime.Now;
        }

        private static void TypeText(string text)
        {
            // Escape special SendKeys chars, then type
            var escaped = text
                .Replace("{", "{{")
                .Replace("}", "}}")
                .Replace("(", "(")
                .Replace(")", ")")
                .Replace("+", "{+}")
                .Replace("^", "{^}")
                .Replace("%", "{%}")
                .Replace("~", "{~}")
                .Replace("[", "{[}")
                .Replace("]", "{]}");
            SendKeys.SendWait(escaped);
        }

        private static Entity? FindFaustus(GameController gc)
        {
            foreach (var e in gc.EntityListWrapper.OnlyValidEntities)
                if (e.Path?.Contains(FaustusPath, StringComparison.OrdinalIgnoreCase) == true) return e;
            return null;
        }

        private static ExileCore.PoEMemory.Element? FindPickerOption(dynamic picker, string? metaSubstring, string? baseName)
        {
            try
            {
                foreach (var option in picker.Options)
                {
                    if (option == null) continue;
                    var itemType = option.ItemType;
                    if (itemType == null) continue;
                    if (metaSubstring != null && (itemType.Metadata as string)?.Contains(metaSubstring, StringComparison.OrdinalIgnoreCase) == true)
                        return (ExileCore.PoEMemory.Element)option;
                    if (baseName != null && (itemType.BaseName as string)?.Equals(baseName, StringComparison.OrdinalIgnoreCase) == true)
                        return (ExileCore.PoEMemory.Element)option;
                }
            }
            catch { }
            return null;
        }
    }

    public enum FaustusResult { None, InProgress, Succeeded, Failed }

    internal enum FaustusState
    {
        Idle, WalkingToFaustus, WaitingForDialog, ClickingCurrencyExchange,
        WaitingForPanel, PickingWanted, PickingPay, EnteringQuantities,
        PlacingOrder, AwaitingFulfillment, Done, Failed,
    }
}
