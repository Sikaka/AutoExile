using System.Numerics;
using ExileCore;
using ExileCore.PoEMemory.MemoryObjects;

namespace AutoExile.Systems
{
    /// <summary>
    /// Evaluates skill gems for Divine Font transformation profitability.
    /// Uses NinjaPrice data to calculate expected value of "same colour" and "same type" transforms.
    /// Gem colour is read from game data (SkillGems.dat SocketType / attribute requirements).
    /// </summary>
    public class GemValuationService
    {
        // Gem colour grouping for "same colour" EV calculation
        public enum GemColour { Red, Green, Blue, Unknown }

        // Runtime colour map built from game data (SkillGems.dat)
        private Dictionary<string, GemColour> _colourMap = new(StringComparer.OrdinalIgnoreCase);
        private bool _colourMapBuilt;

        /// <summary>
        /// Build the gem colour map from ExileCore's SkillGems.dat file.
        /// Call once when game data is available. Uses attribute requirements
        /// (Str=Red, Dex=Green, Int=Blue) to classify gems.
        /// </summary>
        public void BuildColourMap(GameController gc)
        {
            if (_colourMapBuilt) return;
            try
            {
                var skillGems = gc.Files.SkillGems?.EntriesList;
                if (skillGems == null || skillGems.Count == 0) return;

                var map = new Dictionary<string, GemColour>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in skillGems)
                {
                    string? name = null;
                    try { name = entry.ItemType?.BaseName; } catch { continue; }
                    if (string.IsNullOrEmpty(name) || map.ContainsKey(name)) continue;

                    var str = entry.StrengthRequirementPercent;
                    var dex = entry.DexterityRequirementPercent;
                    var intel = entry.IntelligenceRequirementPercent;

                    // Dominant attribute determines colour
                    if (str > dex && str > intel) map[name] = GemColour.Red;
                    else if (dex > str && dex > intel) map[name] = GemColour.Green;
                    else if (intel > str && intel > dex) map[name] = GemColour.Blue;
                    else if (str > 0) map[name] = GemColour.Red; // tie-break
                    else if (dex > 0) map[name] = GemColour.Green;
                    else if (intel > 0) map[name] = GemColour.Blue;
                    // else: no requirements (white socket gems) — leave as Unknown
                }

                _colourMap = map;
                _colourMapBuilt = true;
            }
            catch { }
        }

        public class GemEvaluation
        {
            public string GemName { get; set; } = "";
            public double CurrentValue { get; set; }
            public double SameTypeEV { get; set; }
            public double SameColourEV { get; set; }
            public int SameTypeVariants { get; set; }
            public int SameColourVariants { get; set; }
            public double BestEV => Math.Max(SameTypeEV, SameColourEV);
            public double ExpectedProfit => BestEV - CurrentValue;
            public string BestStrategy => SameTypeEV >= SameColourEV ? "same type" : "same colour";
        }

        // Transfigured gem output value at two quality tiers
        public struct GemVariant
        {
            public string Name;
            public double Value0Q;  // level 1, quality 0
            public double Value20Q; // level 1, quality 20
        }

        // Cache rebuilt when ninja data refreshes
        private Dictionary<string, List<GemVariant>> _transfiguredByBase = new();
        private Dictionary<GemColour, List<GemVariant>> _transfiguredByColour = new();
        private int _lastPriceCount;

        /// <summary>
        /// Rebuild the transfigured gem index from ninja price data.
        /// Tracks separate output values for 0q and 20q versions.
        /// Excludes corrupted gems (level 21, quality > 20).
        /// </summary>
        public void RebuildIndex(NinjaPriceService ninjaPrice)
        {
            var gemPrices = ninjaPrice.GetSkillGemPrices();
            if (gemPrices == null) return;
            if (ninjaPrice.PriceCount == _lastPriceCount) return;
            _lastPriceCount = ninjaPrice.PriceCount;

            var byBase = new Dictionary<string, List<GemVariant>>();
            var byColour = new Dictionary<GemColour, List<GemVariant>>
            {
                [GemColour.Red] = new(),
                [GemColour.Green] = new(),
                [GemColour.Blue] = new(),
            };

            foreach (var (name, entries) in gemPrices)
            {
                // Transfigured gems follow the pattern "BaseGem of Variant"
                var ofIndex = name.IndexOf(" of ", StringComparison.Ordinal);
                if (ofIndex <= 0) continue;

                // Find level 1 prices at 0q and 20q, excluding corrupted (lvl21, q>20)
                double val0Q = 0, val20Q = 0;
                foreach (var e in entries)
                {
                    if (e.ChaosValue == null || e.ChaosValue <= 0) continue;
                    if (e.GemLevel >= 21) continue;         // corrupted level
                    if ((e.GemQuality ?? 0) > 20) continue; // corrupted quality

                    if (e.GemLevel == 1 && (e.GemQuality ?? 0) == 0)
                    {
                        if (val0Q == 0 || e.ChaosValue.Value < val0Q)
                            val0Q = e.ChaosValue.Value;
                    }
                    if (e.GemLevel == 1 && (e.GemQuality ?? 0) == 20)
                    {
                        if (val20Q == 0 || e.ChaosValue.Value < val20Q)
                            val20Q = e.ChaosValue.Value;
                    }
                }

                // Need at least a 0q price
                if (val0Q <= 0) continue;

                var baseName = name.Substring(0, ofIndex);
                var variant = new GemVariant { Name = name, Value0Q = val0Q, Value20Q = val20Q };

                if (!byBase.TryGetValue(baseName, out var baseList))
                {
                    baseList = new List<GemVariant>();
                    byBase[baseName] = baseList;
                }
                baseList.Add(variant);

                var colour = InferGemColour(baseName);
                if (colour != GemColour.Unknown && byColour.TryGetValue(colour, out var colourList))
                    colourList.Add(variant);
            }

            _transfiguredByBase = byBase;
            _transfiguredByColour = byColour;
        }

        /// <summary>
        /// Evaluate a gem for transformation profitability.
        /// Quality-aware: uses matching quality tier (0q or 20q) output values.
        /// </summary>
        public GemEvaluation Evaluate(string gemName, double currentChaosValue, bool is20Quality = false)
        {
            var eval = new GemEvaluation
            {
                GemName = gemName,
                CurrentValue = currentChaosValue,
            };

            // Select the right value accessor based on input quality
            // Font output inherits input quality: 20q input → 20q transfigured output
            Func<GemVariant, double> getValue = is20Quality
                ? v => v.Value20Q > 0 ? v.Value20Q : v.Value0Q  // fallback to 0q if no 20q price
                : v => v.Value0Q;

            // "Same type" EV — average of all transfigured variants of this gem
            if (_transfiguredByBase.TryGetValue(gemName, out var typeVariants) && typeVariants.Count > 0)
            {
                eval.SameTypeEV = typeVariants.Average(getValue);
                eval.SameTypeVariants = typeVariants.Count;
            }

            // "Same colour" EV — average of all transfigured gems of this colour
            var colour = InferGemColour(gemName);
            if (colour != GemColour.Unknown && _transfiguredByColour.TryGetValue(colour, out var colourVariants) && colourVariants.Count > 0)
            {
                eval.SameColourEV = colourVariants.Average(getValue);
                eval.SameColourVariants = colourVariants.Count;
            }

            return eval;
        }

        /// <summary>
        /// Price a specific transfigured gem by exact name (for the result selection screen).
        /// Matches level and quality against ninja entries to avoid returning level 21 corrupted prices.
        /// Returns 0 if not found.
        /// </summary>
        public double PriceTransfiguredGem(string fullGemName, NinjaPriceService ninjaPrice, int gemLevel = 1, int gemQuality = 20)
        {
            var gemPrices = ninjaPrice.GetSkillGemPrices();
            if (gemPrices == null || !gemPrices.TryGetValue(fullGemName, out var entries) || entries.Count == 0)
                return 0;

            // Exact match: level + quality
            foreach (var e in entries)
            {
                if ((e.GemLevel ?? 1) == gemLevel && (e.GemQuality ?? 0) == gemQuality && (e.ChaosValue ?? 0) > 0)
                    return e.ChaosValue!.Value;
            }

            // Fallback: match level, quality 0 (common default entry on ninja)
            foreach (var e in entries)
            {
                if ((e.GemLevel ?? 1) == gemLevel && (e.GemQuality ?? 0) == 0 && (e.ChaosValue ?? 0) > 0)
                    return e.ChaosValue!.Value;
            }

            // Last resort: minimum value across non-corrupted entries (conservative)
            double minVal = 0;
            foreach (var e in entries)
            {
                if (e.GemLevel >= 21) continue;
                if ((e.GemQuality ?? 0) > 20) continue;
                var val = e.ChaosValue ?? 0;
                if (val > 0 && (minVal == 0 || val < minVal))
                    minVal = val;
            }
            return minVal;
        }

        /// <summary>
        /// Select the best gem to transform from inventory.
        /// Skips gems above keepThreshold (too valuable to risk transforming).
        /// Uses quality-aware EV calculation.
        /// </summary>
        public GemEvaluation? SelectBestGem(
            IEnumerable<(string Name, double ChaosValue, int Quality)> inventoryGems,
            double minExpectedProfit,
            double keepThreshold = 0)
        {
            GemEvaluation? best = null;

            foreach (var (name, value, quality) in inventoryGems)
            {
                // Skip gems that are too valuable to transform (keep them as-is)
                if (keepThreshold > 0 && value >= keepThreshold)
                    continue;

                // Strip " of X" suffix if the inventory gem is itself transfigured
                var baseName = GetBaseGemName(name);
                bool is20Q = quality >= 20;
                var eval = Evaluate(baseName, value, is20Q);

                if (eval.ExpectedProfit >= minExpectedProfit &&
                    (best == null || eval.ExpectedProfit > best.ExpectedProfit))
                {
                    best = eval;
                }
            }

            return best;
        }

        // ═══════════════════════════════════════════════════
        // Valuation Report (for Web UI)
        // ═══════════════════════════════════════════════════

        public class ColourReport
        {
            public string Colour { get; set; } = "";
            public int TotalVariants { get; set; }
            public double AvgValue0Q { get; set; }
            public double AvgValue20Q { get; set; }
            public double MedianValue0Q { get; set; }
            public double MaxValue0Q { get; set; }
            public double MaxValue20Q { get; set; }
        }

        public class GemReport
        {
            public string BaseName { get; set; } = "";
            public string Colour { get; set; } = "";
            public int VariantCount { get; set; }
            public double InputCostLowQ { get; set; }
            public double InputCost20Q { get; set; }
            public double AvgOutput0Q { get; set; }
            public double AvgOutput20Q { get; set; }
            public double ExpectedProfitLowQ { get; set; }
            public double ExpectedProfit20Q { get; set; }
            public List<VariantInfo> Variants { get; set; } = new();
        }

        public class VariantInfo
        {
            public string Name { get; set; } = "";
            public double ChaosValue { get; set; }
            public double ChaosValue20Q { get; set; }
        }

        public class ValuationReport
        {
            public List<ColourReport> ColourSummary { get; set; } = new();
            public List<GemReport> TopGems { get; set; } = new();
            public DateTime GeneratedAt { get; set; }
        }

        /// <summary>
        /// Generate a full valuation report for the web UI.
        /// Shows per-colour EV and top gems by expected profit, split by quality.
        /// Excludes corrupted gems (level 21, quality > 20).
        /// </summary>
        public ValuationReport GenerateReport(NinjaPriceService ninjaPrice, int topN = 50)
        {
            RebuildIndex(ninjaPrice);
            var gemPrices = ninjaPrice.GetSkillGemPrices();
            var report = new ValuationReport { GeneratedAt = DateTime.Now };
            if (gemPrices == null) return report;

            // Build colour summary with quality split
            foreach (var colour in new[] { GemColour.Red, GemColour.Green, GemColour.Blue })
            {
                if (!_transfiguredByColour.TryGetValue(colour, out var variants) || variants.Count == 0)
                    continue;

                var sorted0Q = variants.Select(v => v.Value0Q).OrderBy(v => v).ToList();
                var vals20Q = variants.Where(v => v.Value20Q > 0).Select(v => v.Value20Q).OrderBy(v => v).ToList();
                report.ColourSummary.Add(new ColourReport
                {
                    Colour = colour.ToString(),
                    TotalVariants = variants.Count,
                    AvgValue0Q = sorted0Q.Average(),
                    AvgValue20Q = vals20Q.Count > 0 ? vals20Q.Average() : 0,
                    MedianValue0Q = sorted0Q[sorted0Q.Count / 2],
                    MaxValue0Q = sorted0Q.Last(),
                    MaxValue20Q = vals20Q.Count > 0 ? vals20Q.Last() : 0,
                });
            }

            // Build per-gem reports with quality split
            var gemReports = new List<GemReport>();
            foreach (var (baseName, variants) in _transfiguredByBase)
            {
                var colour = InferGemColour(baseName);
                var avgOut0Q = variants.Average(v => v.Value0Q);
                var with20Q = variants.Where(v => v.Value20Q > 0).ToList();
                var avgOut20Q = with20Q.Count > 0 ? with20Q.Average(v => v.Value20Q) : 0;

                // Get input costs for the BASE gem (what you buy to transform)
                double inputLowQ = 0;
                double input20Q = 0;
                if (gemPrices.TryGetValue(baseName, out var baseEntries))
                {
                    foreach (var e in baseEntries)
                    {
                        if (e.GemLevel >= 21) continue;
                        if ((e.GemQuality ?? 0) > 20) continue;
                        if (e.ChaosValue == null || e.ChaosValue <= 0) continue;

                        if (e.GemLevel == 1 && (e.GemQuality ?? 0) == 0)
                        {
                            if (inputLowQ == 0 || e.ChaosValue.Value < inputLowQ)
                                inputLowQ = e.ChaosValue.Value;
                        }
                        if (e.GemLevel == 1 && (e.GemQuality ?? 0) == 20)
                        {
                            if (input20Q == 0 || e.ChaosValue.Value < input20Q)
                                input20Q = e.ChaosValue.Value;
                        }
                    }
                }

                gemReports.Add(new GemReport
                {
                    BaseName = baseName,
                    Colour = colour.ToString(),
                    VariantCount = variants.Count,
                    InputCostLowQ = inputLowQ,
                    InputCost20Q = input20Q,
                    AvgOutput0Q = avgOut0Q,
                    AvgOutput20Q = avgOut20Q,
                    ExpectedProfitLowQ = inputLowQ > 0 ? avgOut0Q - inputLowQ : 0,
                    ExpectedProfit20Q = input20Q > 0 && avgOut20Q > 0 ? avgOut20Q - input20Q : 0,
                    Variants = variants
                        .OrderByDescending(v => v.Value0Q)
                        .Select(v => new VariantInfo { Name = v.Name, ChaosValue = v.Value0Q, ChaosValue20Q = v.Value20Q })
                        .ToList(),
                });
            }

            // Sort by best expected profit (low quality input)
            report.TopGems = gemReports
                .OrderByDescending(g => g.ExpectedProfitLowQ)
                .Take(topN)
                .ToList();

            return report;
        }

        /// <summary>
        /// Get the base gem name (strip " of X" suffix for transfigured gems).
        /// </summary>
        public static string GetBaseGemName(string gemName)
        {
            var ofIndex = gemName.IndexOf(" of ", StringComparison.Ordinal);
            return ofIndex > 0 ? gemName.Substring(0, ofIndex) : gemName;
        }

        /// <summary>
        /// Get the cheapest level-1 entry for a gem (what you'd buy as transformation input).
        /// </summary>
        private static ItemLine? GetCheapestEntry(List<ItemLine> entries)
        {
            ItemLine? best = null;
            foreach (var e in entries)
            {
                if (e.ChaosValue == null || e.ChaosValue <= 0) continue;
                // Prefer level 1 quality 0 (cheapest input)
                if (e.GemLevel == 1 && (e.GemQuality ?? 0) == 0)
                    return e;
                if (best == null || e.ChaosValue < best.ChaosValue)
                    best = e;
            }
            return best;
        }

        /// <summary>
        /// Get gem colour from runtime map (built from SkillGems.dat) or static fallback.
        /// </summary>
        public GemColour InferGemColour(string baseGemName)
        {
            // Runtime map from game data (most complete)
            if (_colourMap.TryGetValue(baseGemName, out var colour))
                return colour;

            // Static fallback
            if (_redGems.Contains(baseGemName)) return GemColour.Red;
            if (_greenGems.Contains(baseGemName)) return GemColour.Green;
            if (_blueGems.Contains(baseGemName)) return GemColour.Blue;

            return GemColour.Unknown;
        }

        // Known gem colour mappings — populated from game data
        // These are the most common/valuable gems for lab farming
        // Can be expanded as needed
        private static readonly HashSet<string> _redGems = new(StringComparer.OrdinalIgnoreCase)
        {
            // Attack skills
            "Cleave", "Double Strike", "Ground Slam", "Heavy Strike", "Infernal Blow",
            "Leap Slam", "Molten Strike", "Shield Charge", "Sunder", "Tectonic Slam",
            "Earthquake", "Ice Crash", "Ancestral Warchief", "Ancestral Protector",
            "Consecrated Path", "Vaal Ground Slam", "Vaal Earthquake",
            "Perforate", "Earthshatter", "General's Cry", "Boneshatter",
            "Corrupting Fever", "Reap", "Exsanguinate",
            // Warcries
            "Enduring Cry", "Intimidating Cry", "Rallying Cry", "Seismic Cry",
            "Ancestral Cry", "Infernal Cry", "Battlemage's Cry",
            // Support
            "Brutality Support", "Melee Physical Damage Support", "Multistrike Support",
            "Ruthless Support", "Fortify Support", "Rage Support",
            // Auras / guards
            "Determination", "Vitality", "Purity of Fire", "Anger",
            "Herald of Purity", "Herald of Ash", "Pride",
            "Molten Shell", "Immortal Call", "Steelskin",
            "Punishment", "Vulnerability", "War Banner",
            "Blood and Sand", "Flesh and Stone",
            "Shield Bash", "Lancing Steel", "Shattering Steel", "Splitting Steel",
            "Bladestorm", "Lacerate", "Blade Flurry", "Cyclone",
        };

        private static readonly HashSet<string> _greenGems = new(StringComparer.OrdinalIgnoreCase)
        {
            // Attack skills
            "Barrage", "Burning Arrow", "Caustic Arrow", "Frenzy", "Ice Shot",
            "Lightning Arrow", "Rain of Arrows", "Scourge Arrow", "Shrapnel Shot",
            "Split Arrow", "Tornado Shot", "Galvanic Arrow", "Artillery Ballista",
            "Lightning Strike", "Spectral Throw", "Whirling Blades", "Flicker Strike",
            "Viper Strike", "Pestilent Strike", "Cobra Lash", "Poisonous Concoction",
            "Explosive Arrow",
            // Traps / mines
            "Bear Trap", "Explosive Trap", "Fire Trap", "Lightning Trap", "Seismic Trap",
            "Icicle Mine", "Pyroclast Mine", "Stormblast Mine",
            // Movement
            "Blink Arrow", "Mirror Arrow", "Dash", "Phase Run", "Withering Step",
            // Auras / utility
            "Grace", "Haste", "Herald of Agony", "Herald of Ice",
            "Arctic Armour", "Blood Rage", "Temporal Chains",
            "Sniper's Mark", "Poacher's Mark",
            // Other
            "Puncture", "Ethereal Knives", "Blade Trap", "Ensnaring Arrow",
            "Toxic Rain", "Mirage Archer Support",
        };

        private static readonly HashSet<string> _blueGems = new(StringComparer.OrdinalIgnoreCase)
        {
            // Spells
            "Arc", "Ball Lightning", "Fireball", "Freezing Pulse", "Glacial Cascade",
            "Ice Nova", "Ice Spear", "Spark", "Storm Brand", "Winter Orb",
            "Flame Dash", "Frostbolt", "Orb of Storms", "Lightning Warp",
            "Creeping Frost", "Cold Snap", "Vortex", "Wintertide Brand",
            "Firestorm", "Detonate Dead", "Volatile Dead", "Cremation",
            "Storm Call", "Shock Nova", "Lightning Tendrils",
            "Blazing Salvo", "Rolling Magma", "Eye of Winter",
            "Forbidden Rite", "Dark Pact", "Bane", "Soulrend", "Essence Drain",
            "Contagion", "Blight",
            // Minions
            "Raise Zombie", "Raise Spectre", "Summon Raging Spirit", "Summon Skeletons",
            "Summon Reaper", "Absolution", "Animate Weapon",
            "Summon Carrion Golem", "Summon Chaos Golem", "Summon Flame Golem",
            "Summon Ice Golem", "Summon Lightning Golem", "Summon Stone Golem",
            // Auras / curses
            "Discipline", "Clarity", "Zealotry", "Wrath", "Hatred", "Malevolence",
            "Herald of Thunder", "Purity of Elements", "Purity of Ice", "Purity of Lightning",
            "Assassin's Mark", "Warlord's Mark", "Elemental Weakness", "Conductivity",
            "Flammability", "Frostbite", "Despair", "Enfeeble",
            // Guards / utility
            "Bone Armour", "Frost Shield", "Tempest Shield", "Arcane Cloak",
            "Sigil of Power", "Hydrosphere", "Arcanist Brand",
            "Power Siphon", "Kinetic Blast", "Blade Vortex", "Bladefall",
            "Bodyswap", "Flame Wall", "Hexblast",
        };
    }
}
