using ExileCore;
using ExileCore.PoEMemory.Elements;
using ExileCore.PoEMemory.MemoryObjects;
using System.Numerics;

namespace AutoExile.Systems
{
    /// <summary>
    /// Drives the Faustus NPC Currency Exchange to buy items (scarabs, simulacrum splinters, etc.).
    ///
    /// Usage:
    ///   faustus.Start("CurrencyAfflictionShard", 20, "Chaos Orb");
    ///   // Then each tick:
    ///   var result = faustus.Tick(ctx);
    /// </summary>
    public class FaustusSystem
    {
        // ── Faustus NPC path ──
        private const string FaustusPath = "Metadata/NPC/League/Kalguur/VillageFaustusHideout";

        // ── Timing ──
        private const int ClickCooldownMs = 400;
        private const float StateTimeoutSeconds = 10f;

        // ── State ──
        private FaustusState _state = FaustusState.Idle;
        private DateTime _stateEnteredAt = DateTime.MinValue;
        private DateTime _lastClickAt = DateTime.MinValue;

        // ── Purchase parameters ──
        private string _wantedMetaSubstring = "";
        private int _quantity;
        private string _payCurrencyBaseName = "";

        // ── Tracking ──
        private int _ordersPlaced;
        private bool _wantItemPicked;
        private bool _payItemPicked;

        public bool IsBusy => _state != FaustusState.Idle;
        public string Status { get; private set; } = "";

        /// <summary>
        /// Begin a purchase sequence.
        /// wantedItemMetadataSubstring: substring of Metadata path to match (e.g. "CurrencyAfflictionShard")
        /// quantity: number of orders to place (each order buys one stack from Faustus)
        /// payCurrencyBaseName: BaseName of the currency to pay with (e.g. "Chaos Orb")
        /// </summary>
        public void Start(string wantedItemMetadataSubstring, int quantity, string payCurrencyBaseName)
        {
            _wantedMetaSubstring = wantedItemMetadataSubstring;
            _quantity = quantity;
            _payCurrencyBaseName = payCurrencyBaseName;
            _ordersPlaced = 0;
            _wantItemPicked = false;
            _payItemPicked = false;
            SetState(FaustusState.WalkingToFaustus);
        }

        public void Cancel(GameController? gc = null, NavigationSystem? nav = null)
        {
            if (nav != null && gc != null)
                nav.Stop(gc);
            SetState(FaustusState.Idle);
        }

        public FaustusResult Tick(BotContext ctx)
        {
            if (_state == FaustusState.Idle)
                return FaustusResult.None;

            // Global state timeout guard
            if ((DateTime.Now - _stateEnteredAt).TotalSeconds > StateTimeoutSeconds)
            {
                Status = $"Faustus: timeout in state {_state}";
                Cancel(ctx.Game, ctx.Navigation);
                return FaustusResult.Failed;
            }

            return _state switch
            {
                FaustusState.WalkingToFaustus => TickWalkingToFaustus(ctx),
                FaustusState.WaitingForDialog => TickWaitingForDialog(ctx),
                FaustusState.ClickingCurrencyExchange => TickClickingCurrencyExchange(ctx),
                FaustusState.WaitingForPanel => TickWaitingForPanel(ctx),
                FaustusState.PickingWantedItem => TickPickingWantedItem(ctx),
                FaustusState.PickingPayCurrency => TickPickingPayCurrency(ctx),
                FaustusState.PlacingOrder => TickPlacingOrder(ctx),
                FaustusState.Done => FaustusResult.Succeeded,
                FaustusState.Failed => FaustusResult.Failed,
                _ => FaustusResult.InProgress,
            };
        }

        // ── State handlers ──

        private FaustusResult TickWalkingToFaustus(BotContext ctx)
        {
            var gc = ctx.Game;

            // If the dialog is already open, skip straight to clicking Currency Exchange
            var dialog = gc.IngameState.IngameUi.NpcDialog;
            if (dialog != null && dialog.IsVisible)
            {
                Status = "Faustus: dialog already open";
                SetState(FaustusState.ClickingCurrencyExchange);
                return FaustusResult.InProgress;
            }

            var faustus = FindFaustus(gc);
            if (faustus == null)
            {
                Status = "Faustus: NPC not found";
                return FaustusResult.InProgress;
            }

            // Use InteractionSystem to walk and click Faustus
            if (ctx.Interaction.IsBusy)
            {
                var interResult = ctx.Interaction.Tick(gc);
                Status = $"Faustus: walking to NPC ({ctx.Interaction.Status})";
                if (interResult == InteractionResult.Succeeded || interResult == InteractionResult.Failed)
                {
                    SetState(FaustusState.WaitingForDialog);
                }
                return FaustusResult.InProgress;
            }

            // Start the interaction
            ctx.Interaction.InteractWithEntity(faustus, ctx.Navigation, requireProximity: true);
            Status = "Faustus: interacting with NPC";
            return FaustusResult.InProgress;
        }

        private FaustusResult TickWaitingForDialog(BotContext ctx)
        {
            var gc = ctx.Game;

            // Tick interaction if still navigating/clicking
            if (ctx.Interaction.IsBusy)
            {
                var interResult = ctx.Interaction.Tick(gc);
                Status = $"Faustus: waiting for dialog ({ctx.Interaction.Status})";
                if (interResult == InteractionResult.Failed)
                {
                    Status = "Faustus: interaction failed";
                    SetState(FaustusState.Failed);
                    return FaustusResult.Failed;
                }
            }

            var dialog = gc.IngameState.IngameUi.NpcDialog;
            if (dialog == null || !dialog.IsVisible)
            {
                Status = "Faustus: waiting for dialog to open";
                return FaustusResult.InProgress;
            }

            // Dialog is open — handle it
            SetState(FaustusState.ClickingCurrencyExchange);
            return FaustusResult.InProgress;
        }

        private FaustusResult TickClickingCurrencyExchange(BotContext ctx)
        {
            var gc = ctx.Game;

            var dialog = gc.IngameState.IngameUi.NpcDialog;
            if (dialog == null || !dialog.IsVisible)
            {
                Status = "Faustus: dialog closed unexpectedly";
                SetState(FaustusState.Failed);
                return FaustusResult.Failed;
            }

            if (!CanClick()) return FaustusResult.InProgress;

            // Look for NPC dialog lines
            var lines = dialog.NpcLines;
            if (lines == null || lines.Count == 0)
            {
                Status = "Faustus: no dialog lines";
                return FaustusResult.InProgress;
            }

            // Click "Continue" to skip lore dialogs
            foreach (var line in lines)
            {
                if (line?.Text?.Contains("Continue", StringComparison.OrdinalIgnoreCase) == true)
                {
                    Status = "Faustus: clicking Continue";
                    ClickElement(gc, line.Element);
                    return FaustusResult.InProgress;
                }
            }

            // Look for "Currency Exchange" option
            foreach (var line in lines)
            {
                if (line?.Text?.Contains("Currency Exchange", StringComparison.OrdinalIgnoreCase) == true ||
                    line?.Text?.Contains("Exchange", StringComparison.OrdinalIgnoreCase) == true)
                {
                    Status = "Faustus: clicking Currency Exchange";
                    ClickElement(gc, line.Element);
                    SetState(FaustusState.WaitingForPanel);
                    return FaustusResult.InProgress;
                }
            }

            // No matching option found yet — wait
            Status = "Faustus: looking for Currency Exchange option";
            return FaustusResult.InProgress;
        }

        private FaustusResult TickWaitingForPanel(BotContext ctx)
        {
            var gc = ctx.Game;

            var panel = gc.IngameState.IngameUi.CurrencyExchangePanel;
            if (panel != null && panel.IsVisible)
            {
                Status = "Faustus: panel opened";
                SetState(FaustusState.PickingWantedItem);
                return FaustusResult.InProgress;
            }

            // Give the panel 2 seconds to appear before assuming the click didn't land
            if ((DateTime.Now - _stateEnteredAt).TotalSeconds < 2.0)
            {
                Status = "Faustus: waiting for exchange panel";
                return FaustusResult.InProgress;
            }

            // Timed out waiting — if dialog is still visible, try clicking Exchange again
            var dialog = gc.IngameState.IngameUi.NpcDialog;
            if (dialog != null && dialog.IsVisible)
            {
                SetState(FaustusState.ClickingCurrencyExchange);
                return FaustusResult.InProgress;
            }

            Status = "Faustus: waiting for exchange panel";
            return FaustusResult.InProgress;
        }

        private FaustusResult TickPickingWantedItem(BotContext ctx)
        {
            var gc = ctx.Game;

            var panel = gc.IngameState.IngameUi.CurrencyExchangePanel;
            if (panel == null || !panel.IsVisible)
            {
                Status = "Faustus: exchange panel closed";
                SetState(FaustusState.Failed);
                return FaustusResult.Failed;
            }

            var picker = panel.CurrencyPicker;

            // Item was clicked — wait for picker to close, then move on
            if (_wantItemPicked)
            {
                if (picker != null && picker.IsVisible)
                {
                    Status = "Faustus: waiting for I Want picker to close";
                    return FaustusResult.InProgress;
                }
                Status = "Faustus: wanted item confirmed, opening I Have picker";
                SetState(FaustusState.PickingPayCurrency);
                return FaustusResult.InProgress;
            }

            // Picker is open for "I Want" — find and click our item
            if (picker != null && picker.IsVisible && picker.IsPickingWantedCurrency)
            {
                var option = FindPickerOption(picker, _wantedMetaSubstring, null);
                if (option == null)
                {
                    Status = $"Faustus: wanted item not in picker ({_wantedMetaSubstring})";
                    return FaustusResult.InProgress;
                }

                if (!CanClick()) return FaustusResult.InProgress;

                var rect = option.GetClientRect();
                var center = new Vector2(rect.X + rect.Width / 2f, rect.Y + rect.Height / 2f);
                BotInput.Click(ToAbsolutePos(gc, center));
                _lastClickAt = DateTime.Now;
                _wantItemPicked = true;
                Status = "Faustus: selected wanted item";
                return FaustusResult.InProgress;
            }

            // Picker not open — click the "I Want" button to open it
            if (!CanClick()) return FaustusResult.InProgress;

            try
            {
                var wantButton = panel.GetChildAtIndex(7)?.GetChildAtIndex(0);
                if (wantButton != null && wantButton.IsVisible)
                {
                    var rect = wantButton.GetClientRect();
                    var center = new Vector2(rect.X + rect.Width / 2f, rect.Y + rect.Height / 2f);
                    BotInput.Click(ToAbsolutePos(gc, center));
                    _lastClickAt = DateTime.Now;
                    Status = "Faustus: opening I Want picker";
                    return FaustusResult.InProgress;
                }
            }
            catch { }

            Status = "Faustus: waiting for I Want button";
            return FaustusResult.InProgress;
        }

        private FaustusResult TickPickingPayCurrency(BotContext ctx)
        {
            var gc = ctx.Game;

            var panel = gc.IngameState.IngameUi.CurrencyExchangePanel;
            if (panel == null || !panel.IsVisible)
            {
                Status = "Faustus: exchange panel closed";
                SetState(FaustusState.Failed);
                return FaustusResult.Failed;
            }

            var picker = panel.CurrencyPicker;

            // Pay currency was clicked — wait for picker to close, then move on
            if (_payItemPicked)
            {
                if (picker != null && picker.IsVisible)
                {
                    Status = "Faustus: waiting for I Have picker to close";
                    return FaustusResult.InProgress;
                }
                Status = "Faustus: pay currency confirmed, placing order";
                SetState(FaustusState.PlacingOrder);
                return FaustusResult.InProgress;
            }

            // If I Want picker is open (shouldn't happen here) — just wait
            if (picker != null && picker.IsVisible && picker.IsPickingWantedCurrency)
            {
                Status = "Faustus: waiting for I Want picker to close";
                return FaustusResult.InProgress;
            }

            // Picker is open for "I Have" — find and click pay currency
            if (picker != null && picker.IsVisible && !picker.IsPickingWantedCurrency)
            {
                var option = FindPickerOption(picker, null, _payCurrencyBaseName);
                if (option == null)
                {
                    Status = $"Faustus: pay currency not in picker ({_payCurrencyBaseName})";
                    return FaustusResult.InProgress;
                }

                if (!CanClick()) return FaustusResult.InProgress;

                var rect = option.GetClientRect();
                var center = new Vector2(rect.X + rect.Width / 2f, rect.Y + rect.Height / 2f);
                BotInput.Click(ToAbsolutePos(gc, center));
                _lastClickAt = DateTime.Now;
                _payItemPicked = true;
                Status = "Faustus: selected pay currency";
                return FaustusResult.InProgress;
            }

            // Picker not open — click "I Have" button to open it
            if (!CanClick()) return FaustusResult.InProgress;

            try
            {
                var haveButton = panel.GetChildAtIndex(10)?.GetChildAtIndex(0);
                if (haveButton != null && haveButton.IsVisible)
                {
                    var rect = haveButton.GetClientRect();
                    var center = new Vector2(rect.X + rect.Width / 2f, rect.Y + rect.Height / 2f);
                    BotInput.Click(ToAbsolutePos(gc, center));
                    _lastClickAt = DateTime.Now;
                    Status = "Faustus: opening I Have picker";
                    return FaustusResult.InProgress;
                }
            }
            catch { }

            Status = "Faustus: waiting for I Have button";
            return FaustusResult.InProgress;
        }

        private FaustusResult TickPlacingOrder(BotContext ctx)
        {
            var gc = ctx.Game;

            var panel = gc.IngameState.IngameUi.CurrencyExchangePanel;
            if (panel == null || !panel.IsVisible)
            {
                Status = "Faustus: exchange panel closed";
                SetState(FaustusState.Failed);
                return FaustusResult.Failed;
            }

            if (!CanClick()) return FaustusResult.InProgress;

            try
            {
                var placeOrderBtn = panel.GetChildAtIndex(16)?.GetChildAtIndex(0);
                if (placeOrderBtn != null && placeOrderBtn.IsVisible)
                {
                    var rect = placeOrderBtn.GetClientRect();
                    var center = new Vector2(rect.X + rect.Width / 2f, rect.Y + rect.Height / 2f);
                    var absCenter = ToAbsolutePos(gc, center);
                    Status = "Faustus: placing order";
                    BotInput.Click(absCenter);
                    _lastClickAt = DateTime.Now;
                    _ordersPlaced++;

                    if (_ordersPlaced >= _quantity)
                    {
                        Status = $"Faustus: all {_ordersPlaced} orders placed";
                        SetState(FaustusState.Done);
                        return FaustusResult.Succeeded;
                    }

                    // More orders needed — reset flags and go back to picking wanted item
                    _wantItemPicked = false;
                    _payItemPicked = false;
                    SetState(FaustusState.PickingWantedItem);
                    return FaustusResult.InProgress;
                }
            }
            catch { }

            Status = "Faustus: waiting for place order button";
            return FaustusResult.InProgress;
        }

        // ── Helpers ──

        private void SetState(FaustusState newState)
        {
            _state = newState;
            _stateEnteredAt = DateTime.Now;
        }

        private bool CanClick()
        {
            return (DateTime.Now - _lastClickAt).TotalMilliseconds >= ClickCooldownMs
                   && BotInput.CanAct;
        }

        private void ClickElement(GameController gc, ExileCore.PoEMemory.Element element)
        {
            try
            {
                var rect = element.GetClientRect();
                var center = new Vector2(rect.X + rect.Width / 2f, rect.Y + rect.Height / 2f);
                var absCenter = ToAbsolutePos(gc, center);
                BotInput.Click(absCenter);
                _lastClickAt = DateTime.Now;
            }
            catch { }
        }

        private static Vector2 ToAbsolutePos(GameController gc, Vector2 clientPos)
        {
            // GetClientRect() already returns window-relative coords for UI elements.
            // BotInput.Click expects absolute screen coords.
            var wr = gc.Window.GetWindowRectangle();
            return new Vector2(wr.X + clientPos.X, wr.Y + clientPos.Y);
        }

        private static Entity? FindFaustus(GameController gc)
        {
            foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
            {
                if (entity.Path?.Contains(FaustusPath, StringComparison.OrdinalIgnoreCase) == true)
                    return entity;
            }
            return null;
        }

        private static ExileCore.PoEMemory.Element? FindPickerOption(
            dynamic picker,
            string? metaSubstring,
            string? baseName)
        {
            try
            {
                foreach (var option in picker.Options)
                {
                    if (option == null) continue;
                    var itemType = option.ItemType;
                    if (itemType == null) continue;

                    string? meta = itemType.Metadata;
                    string? bname = itemType.BaseName;

                    if (metaSubstring != null)
                    {
                        if (meta?.Contains(metaSubstring, StringComparison.OrdinalIgnoreCase) == true)
                            return (ExileCore.PoEMemory.Element)option;
                    }
                    else if (baseName != null)
                    {
                        if (bname?.Equals(baseName, StringComparison.OrdinalIgnoreCase) == true)
                            return (ExileCore.PoEMemory.Element)option;
                    }
                }
            }
            catch { }
            return null;
        }
    }

    public enum FaustusResult
    {
        None,
        InProgress,
        Succeeded,
        Failed,
    }

    internal enum FaustusState
    {
        Idle,
        WalkingToFaustus,
        WaitingForDialog,
        ClickingCurrencyExchange,
        WaitingForPanel,
        PickingWantedItem,
        PickingPayCurrency,
        PlacingOrder,
        Done,
        Failed,
    }
}
