using System.Numerics;
using System.Text.RegularExpressions;
using ExileCore;
using ExileCore.PoEMemory;
using ExileCore.PoEMemory.Elements;
using ExileCore.PoEMemory.MemoryObjects;
using AutoExile.Systems;

namespace AutoExile.Mechanics
{
    /// <summary>
    /// Result of an altar tick — tells MappingMode whether we're busy.
    /// </summary>
    public enum AltarTickResult
    {
        Nothing,    // No altar nearby or not worth taking
        Busy,       // Settling/clicking an altar — don't explore
        Done,       // Just finished clicking an altar, resume normal flow
    }

    /// <summary>
    /// Lightweight eldritch altar handler. NOT an IMapMechanic — altars are
    /// opportunistic clicks during exploration, not dedicated encounters.
    ///
    /// Called each tick by MappingMode alongside interactable checks.
    /// When a visible altar label is found:
    ///   1. Score both choices using configurable mod weights
    ///   2. If best choice net score >= threshold → settle movement → click → verify
    ///   3. If below threshold → skip, blacklist to avoid re-evaluating
    ///
    /// Entity paths:
    ///   - Searing Exarch: "CleansingFireAltar"
    ///   - Eater of Worlds: "TangleAltar"
    ///
    /// Scoring: net = sum(upside_weights) - sum(downside_weights).
    ///   Score >= threshold → take it. Deadly mods have very high negative weight.
    /// </summary>
    public class EldritchAltarHandler
    {
        // ── State ──
        private bool _settling;
        private DateTime _settleStart;
        private Element? _pendingButton;
        private uint _pendingAltarEntityId;
        private int _clickAttempts;
        private DateTime _lastClickTime = DateTime.MinValue;
        private const int MaxClickAttempts = 5;
        private const float ClickCooldownMs = 400;
        private const float SettleTimeMs = 300;

        // ── Blacklist — altars we've decided to skip or that failed ──
        private readonly HashSet<uint> _blacklist = new();

        // ── Status for overlay ──
        public string Status { get; private set; } = "";
        public bool IsBusy => _settling || _pendingButton != null;

        /// <summary>
        /// Call each tick during MappingMode exploration. Returns whether
        /// the handler is busy (MappingMode should not navigate).
        /// </summary>
        public AltarTickResult Tick(BotContext ctx)
        {
            var gc = ctx.Game;

            // ── Pending click verification ──
            if (_pendingAltarEntityId != 0)
            {
                // Check if altar was consumed
                var altar = FindEntityById(gc, _pendingAltarEntityId);
                if (altar == null || !altar.IsTargetable)
                {
                    ctx.Log($"[Altar] Choice applied successfully");
                    ClearPending();
                    return AltarTickResult.Done;
                }

                // Label gone = choice accepted
                var label = FindAltarLabelForEntity(gc, _pendingAltarEntityId);
                if (label == null && (DateTime.Now - _lastClickTime).TotalMilliseconds > 500)
                {
                    ctx.Log($"[Altar] Choice accepted (label gone)");
                    _blacklist.Add(_pendingAltarEntityId);
                    ClearPending();
                    return AltarTickResult.Done;
                }

                // Click didn't take — retry
                if ((DateTime.Now - _lastClickTime).TotalMilliseconds > 1000)
                {
                    if (_clickAttempts >= MaxClickAttempts)
                    {
                        ctx.Log($"[Altar] Max click attempts, blacklisting altar");
                        _blacklist.Add(_pendingAltarEntityId);
                        ClearPending();
                        return AltarTickResult.Done;
                    }

                    // Re-score to get fresh element reference
                    if (label != null)
                    {
                        var rescore = TryScoreAltar(label, ctx);
                        if (rescore != null && !rescore.Value.Skip)
                        {
                            _pendingButton = rescore.Value.Button;
                        }
                    }

                    if (_pendingButton != null && BotInput.CanAct)
                    {
                        ClickElement(gc, _pendingButton);
                        _clickAttempts++;
                        _lastClickTime = DateTime.Now;
                        ctx.Log($"[Altar] Retry click {_clickAttempts}/{MaxClickAttempts}");
                    }
                }

                Status = $"Verifying altar click ({_clickAttempts}/{MaxClickAttempts})";
                return AltarTickResult.Busy;
            }

            // ── Settling before click ──
            if (_settling)
            {
                if ((DateTime.Now - _settleStart).TotalMilliseconds < SettleTimeMs)
                {
                    Status = "Settling before altar click...";
                    return AltarTickResult.Busy;
                }
                _settling = false;
                // Fall through to scan — will re-find and click
            }

            // ── Scan for altar labels ──
            if (!ctx.Settings.Mechanics.EldritchAltar.Enabled.Value) return AltarTickResult.Nothing;

            var settings = ctx.Settings.Mechanics.EldritchAltar;
            var labels = gc.IngameState?.IngameUi?.ItemsOnGroundLabelsVisible;
            if (labels == null) return AltarTickResult.Nothing;

            for (int i = 0; i < labels.Count; i++)
            {
                var lbl = labels[i];
                if (lbl?.ItemOnGround?.Path == null || lbl.Label?.IsVisible != true) continue;
                if (!IsAltarPath(lbl.ItemOnGround.Path)) continue;

                var entityId = lbl.ItemOnGround.Id;
                if (_blacklist.Contains(entityId)) continue;

                // Score the choices
                var result = TryScoreAltar(lbl.Label, ctx);
                if (result == null) continue; // mod text not loaded

                var (button, bestScore, chosenText, skip) = result.Value;

                if (skip || bestScore < settings.MinScoreThreshold.Value)
                {
                    _blacklist.Add(entityId);
                    ctx.Log($"[Altar] Skipping (score {bestScore} < threshold {settings.MinScoreThreshold.Value}): {chosenText}");
                    continue;
                }

                // Worth taking — check distance
                var playerGrid = new Vector2(gc.Player.GridPosNum.X, gc.Player.GridPosNum.Y);
                var altarGrid = new Vector2(lbl.ItemOnGround.GridPosNum.X, lbl.ItemOnGround.GridPosNum.Y);
                var dist = Vector2.Distance(playerGrid, altarGrid);

                if (dist > 25)
                {
                    // Too far for reliable click — let exploration bring us closer
                    // Don't blacklist, we'll try again when closer
                    continue;
                }

                // Close enough — settle then click
                if (!_settling && (DateTime.Now - _lastClickTime).TotalMilliseconds < 500)
                {
                    // Just finished a click recently, wait
                    continue;
                }

                ctx.Log($"[Altar] Taking altar (score {bestScore}): {chosenText}");

                if (!BotInput.CanAct) return AltarTickResult.Busy;

                // Click the choice
                ClickElement(gc, button);
                _pendingButton = button;
                _pendingAltarEntityId = entityId;
                _clickAttempts = 1;
                _lastClickTime = DateTime.Now;
                Status = $"Clicked altar: {chosenText} (score {bestScore})";
                return AltarTickResult.Busy;
            }

            return AltarTickResult.Nothing;
        }

        /// <summary>Clear all state on area change.</summary>
        public void Reset()
        {
            ClearPending();
            _blacklist.Clear();
            Status = "";
        }

        private void ClearPending()
        {
            _settling = false;
            _pendingButton = null;
            _pendingAltarEntityId = 0;
            _clickAttempts = 0;
            Status = "";
        }

        // ══════════════════════════════════════════════════════════════
        // Scoring
        // ══════════════════════════════════════════════════════════════

        private (Element Button, int BestScore, string Chosen, bool Skip)? TryScoreAltar(
            Element label, BotContext ctx)
        {
            try
            {
                var modElements = new List<Element>();
                FindElementsByText(label, "valuedefault", modElements);
                if (modElements.Count < 2) return null;

                var topMods = modElements[0];
                var bottomMods = modElements[1];

                string topClean = CleanText(topMods.Text ?? "");
                string botClean = CleanText(bottomMods.Text ?? "");
                if (string.IsNullOrWhiteSpace(topClean) || string.IsNullOrWhiteSpace(botClean))
                    return null;

                var weights = ctx.Settings.Mechanics.EldritchAltar.ModWeights;
                int topScore = ScoreChoice(topMods, weights);
                int bottomScore = ScoreChoice(bottomMods, weights);

                Element topButton = topMods.Parent;
                Element bottomButton = bottomMods.Parent;
                if (topButton == null || bottomButton == null) return null;

                // Pick the better option
                bool pickTop = topScore >= bottomScore;
                var button = pickTop ? topButton : bottomButton;
                var bestScore = pickTop ? topScore : bottomScore;
                var chosen = pickTop
                    ? $"TOP ({topScore}) over BOT ({bottomScore})"
                    : $"BOT ({bottomScore}) over TOP ({topScore})";

                return (button, bestScore, chosen, false);
            }
            catch (Exception ex)
            {
                ctx.Log($"[Altar] Score error: {ex.Message}");
                return null;
            }
        }

        private static int ScoreChoice(Element modsElement,
            Dictionary<string, int> userWeights)
        {
            string raw = modsElement.Text ?? string.Empty;
            string cleaned = CleanText(raw);

            var lines = cleaned.Split('\n');
            if (lines.Length == 0) return 0;

            // Line 0 = target type (used as context but all mods scored the same)
            int net = 0;

            for (int i = 1; i < lines.Length; i++)
            {
                string mod = lines[i].Trim();
                if (string.IsNullOrEmpty(mod)) continue;

                string key = NormalizeLetters(mod);
                if (string.IsNullOrEmpty(key)) continue;

                // User override first, then built-in defaults
                if (userWeights.TryGetValue(key, out var userWeight))
                    net += userWeight;
                else if (DefaultModWeights.TryGetValue(key, out var defaultWeight))
                    net += defaultWeight;
                // Unknown mod: 0 weight (neutral)
            }

            return net;
        }

        // ══════════════════════════════════════════════════════════════
        // UI element helpers
        // ══════════════════════════════════════════════════════════════

        private static void FindElementsByText(Element element, string searchText, List<Element> results)
        {
            if (element == null) return;
            try
            {
                var text = element.Text ?? "";
                if (text.Contains(searchText, StringComparison.OrdinalIgnoreCase))
                {
                    bool anyChildHasText = false;
                    foreach (var child in element.Children)
                    {
                        if ((child?.Text ?? "").Contains(searchText, StringComparison.OrdinalIgnoreCase))
                        { anyChildHasText = true; break; }
                    }
                    if (!anyChildHasText) { results.Add(element); return; }
                }
                foreach (var child in element.Children)
                    FindElementsByText(child, searchText, results);
            }
            catch { }
        }

        private static bool IsAltarPath(string path)
        {
            return path.Contains("CleansingFireAltar", StringComparison.OrdinalIgnoreCase)
                || path.Contains("TangleAltar", StringComparison.OrdinalIgnoreCase);
        }

        private static Element? FindAltarLabelForEntity(GameController gc, uint entityId)
        {
            try
            {
                var labels = gc.IngameState.IngameUi.ItemsOnGroundLabelsVisible;
                if (labels == null) return null;
                for (int i = 0; i < labels.Count; i++)
                {
                    var lbl = labels[i];
                    if (lbl?.ItemOnGround == null || lbl.Label?.IsVisible != true) continue;
                    if (lbl.ItemOnGround.Id == entityId && IsAltarPath(lbl.ItemOnGround.Path ?? ""))
                        return lbl.Label;
                }
            }
            catch { }
            return null;
        }

        private static Entity? FindEntityById(GameController gc, uint id)
        {
            foreach (var e in gc.EntityListWrapper.OnlyValidEntities)
                if (e.Id == id) return e;
            return null;
        }

        private void ClickElement(GameController gc, Element element)
        {
            var rect = element.GetClientRectCache;
            var clickPos = new Vector2(rect.Center.X, rect.Center.Y);
            var windowRect = gc.Window.GetWindowRectangleTimeCache;
            var absPos = clickPos + new Vector2(windowRect.X, windowRect.Y);
            BotInput.Click(absPos);
        }

        // ══════════════════════════════════════════════════════════════
        // Text normalization
        // ══════════════════════════════════════════════════════════════

        private static readonly Regex RgbRegex = new(@"<[^>]*>", RegexOptions.Compiled);

        private static string CleanText(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            string s = text
                .Replace("<valuedefault>", "").Replace("{", "").Replace("}", "")
                .Replace("<enchanted>", "").Replace("\u00a0", "")
                .Replace("gain:", "").Replace("gains:", "");
            return RgbRegex.Replace(s, "");
        }

        internal static string NormalizeLetters(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            var buf = new char[value.Length];
            int len = 0;
            foreach (char c in value)
                if (char.IsLetter(c)) buf[len++] = c;
            return new string(buf, 0, len);
        }

        // ══════════════════════════════════════════════════════════════
        // Default mod weights — positive = good, negative = bad
        // Key = NormalizeLetters(mod text), no target prefix needed
        // Users can override any of these via settings
        // ══════════════════════════════════════════════════════════════

        internal static readonly Dictionary<string, int> DefaultModWeights = BuildDefaults();

        /// <summary>
        /// All known altar mod texts for UI enumeration, grouped by default weight.
        /// Key = normalized letters, Value = (display text, default weight).
        /// </summary>
        internal static readonly Dictionary<string, (string Display, int Weight)> AllKnownMods = BuildKnownMods();

        private static Dictionary<string, int> BuildDefaults()
        {
            var d = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in BuildKnownMods())
                d[kvp.Key] = kvp.Value.Weight;
            return d;
        }

        private static Dictionary<string, (string Display, int Weight)> BuildKnownMods()
        {
            var d = new Dictionary<string, (string Display, int Weight)>(StringComparer.OrdinalIgnoreCase);

            void Add(string display, int weight)
            {
                var key = NormalizeLetters(display);
                if (!d.ContainsKey(key))
                    d[key] = (display, weight);
            }

            // ────── POSITIVE (rewards) ──────
            Add("#% chance to drop an additional Divine Orb", 100);
            Add("Final Boss drops # additional Divine Orbs", 100);
            Add("#% increased Quantity of Items found in this Area", 70);
            Add("Scarabs dropped by slain Enemies have #% chance to be Duplicated", 60);
            Add("#% chance to drop an additional Divination Card which rewards League Currency", 55);
            Add("Final Boss drops # additional Divination Cards which reward League Currency", 55);
            Add("#% chance to drop an additional Exalted Orb", 50);
            Add("Final Boss drops # additional Exalted Orbs", 50);
            Add("Divination Cards dropped by slain Enemies have #% chance to be Duplicated", 50);
            Add("Basic Currency Items dropped by slain Enemies have #% chance to be Duplicated", 45);
            Add("#% chance to drop an additional Divination Card which rewards Currency", 45);
            Add("Final Boss drops # additional Divination Cards which reward Currency", 45);
            Add("#% chance to drop an additional Eldritch Exalted Orb", 45);
            Add("#% increased Rarity of Items found in this Area", 40);
            Add("#% chance to drop an additional Grand Eldritch Ichor", 40);
            Add("#% chance to drop an additional Grand Eldritch Ember", 40);
            Add("Final Boss drops # additional Grand Eldritch Ichors", 40);
            Add("Final Boss drops # additional Grand Eldritch Embers", 40);
            Add("#% chance to drop an additional Eldritch Orb of Annulment", 38);
            Add("Final Boss drops # additional Eldritch Exalted Orbs", 36);
            Add("#% chance to drop an additional Orb of Annulment", 35);
            Add("Final Boss drops # additional Orbs of Annulment", 35);
            Add("Maps dropped by slain Enemies have #% chance to be Duplicated", 35);
            Add("#% chance to drop an additional Ambush Scarab", 32);
            Add("Final Boss drops # additional Ambush Scarabs", 32);
            Add("#% chance to drop an additional Divination Scarab", 30);
            Add("#% chance to drop an additional Domination Scarab", 30);
            Add("Final Boss drops # additional Eldritch Orbs of Annulment", 30);
            Add("Final Boss drops # additional Divination Scarabs", 30);
            Add("Final Boss drops # additional Domination Scarabs", 30);
            Add("Unique Items dropped by slain Enemies have #% chance to be Duplicated", 28);
            Add("#% chance to drop an additional Legion Scarab", 26);
            Add("Final Boss drops # additional Legion Scarabs", 26);
            Add("#% chance to drop an additional Breach Scarab", 25);
            Add("#% chance to drop an additional Vaal Orb", 25);
            Add("Final Boss drops # additional Breach Scarabs", 25);
            Add("#% increased Experience gain", 25);
            Add("#% chance to drop an additional Chaos Orb", 20);
            Add("Final Boss drops # additional Chaos Orbs", 20);
            Add("#% chance to drop an additional Greater Eldritch Ichor", 20);
            Add("#% chance to drop an additional Greater Eldritch Ember", 20);
            Add("Final Boss drops # additional Greater Eldritch Ichors", 20);
            Add("Final Boss drops # additional Greater Eldritch Embers", 20);
            Add("#% chance to drop an additional Regal Orb", 15);

            // ────── NEGATIVE (dangers) ──────
            // Deadly — very high penalty
            Add("#% reduced Recovery Rate of Life, Mana and Energy Shield per Endurance Charge", -500);
            Add("Take # Chaos Damage per second during any Flask Effect", -500);

            // Dangerous
            Add("Projectiles are fired in random directions", -100);
            Add("#% reduced Defences per Frenzy Charge", -100);
            Add("Non-Damaging Ailments you inflict are reflected back to you", -100);
            Add("Curses you inflict are reflected back to you", -100);
            Add("-#% to Chaos Resistance", -75);
            Add("Nearby Enemies Gain #% of their Physical Damage as Extra Chaos Damage", -75);
            Add("Gain # Grasping Vines per second while Stationary", -70);
            Add("Gain #% of Physical Damage as Extra Chaos Damage", -60);
            Add("-#% to Fire Resistance", -60);
            Add("-#% to Cold Resistance", -60);
            Add("-#% to Lightning Resistance", -60);
            Add("-#% additional Physical Damage Reduction", -55);
            Add("Gain #% of Physical Damage as Extra Cold Damage", -50);
            Add("Gain #% of Physical Damage as Extra Fire Damage", -50);
            Add("Gain #% of Physical Damage as Extra Lightning Damage", -50);
            Add("Damage Penetrates #% of Enemy Elemental Resistances", -45);
            Add("#% chance to be targeted by a Meteor when you use a Flask", -40);
            Add("Hits have #% chance to ignore Enemy Physical Damage Reduction", -40);
            Add("#% additional Physical Damage Reduction", -35);
            Add("+#% to maximum Fire Resistance", -30);
            Add("+#% to maximum Cold Resistance", -30);
            Add("+#% to maximum Lightning Resistance", -30);
            Add("+#% to maximum Chaos Resistance", -30);
            Add("All Damage taken from Hits can Sap you", -30);
            Add("All Damage taken from Hits can Scorch you", -30);
            Add("+#% to Fire Resistance", -25);
            Add("+#% to Cold Resistance", -25);
            Add("+#% to Lightning Resistance", -25);
            Add("+#% to Chaos Resistance", -25);
            Add("Skills fire # additional Projectiles", -25);
            Add("#% increased Area of Effect", -20);
            Add("#% increased Attack Speed", -20);
            Add("#% increased Cast Speed", -20);
            Add("#% increased Flask Charges used", -20);
            Add("#% reduced Flask Effect Duration", -20);
            Add("All Damage can Ignite", -20);
            Add("All Damage can Shock", -20);
            Add("#% increased Movement Speed", -15);
            Add("Hits always Ignite", -15);
            Add("Hits always Shock", -15);

            return d;
        }
    }
}
