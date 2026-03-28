using ExileCore;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using System.Net.Http;
using System.Text.Json;

namespace AutoExile.Systems
{
    /// <summary>
    /// Fetches item prices from poe.ninja and provides min/max valuations.
    /// Replaces the external NinjaPrice PluginBridge dependency.
    /// For unidentified uniques, returns MaxChaosValue = highest possible match.
    /// </summary>
    public class NinjaPriceService
    {
        // ── Public state ──

        public bool IsLoaded => _priceCount > 0;
        public int PriceCount => _priceCount;
        public string Status { get; private set; } = "not initialized";
        public DateTime LastRefreshTime { get; private set; } = DateTime.MinValue;
        public int RefreshIntervalMinutes { get; set; } = 20;

        // ── Internal state ──

        private static readonly HttpClient _http = new()
        {
            Timeout = TimeSpan.FromSeconds(15),
        };

        static NinjaPriceService()
        {
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("AutoExile/1.0");
        }

        // Prices indexed by category → name → list of entries (multiple variants/links per name)
        private volatile Dictionary<NinjaPriceCategory, Dictionary<string, List<ItemLine>>> _prices = new();
        private volatile int _priceCount;

        // Currency prices need special handling (different API shape)
        private volatile Dictionary<string, double> _currencyPrices = new();

        // Unique art path → list of possible unique names
        private volatile Dictionary<string, List<string>> _uniqueArtMapping = new();
        private bool _artMappingBuilt;

        private string _cacheDir = "";
        private string _league = "";
        private DateTime _lastRefreshAttempt = DateTime.MinValue;
        private bool _refreshing;
        private Action<string> _log = _ => { };

        // ── Category → API URL mapping ──

        private static readonly Dictionary<NinjaPriceCategory, string> CurrencyEndpoints = new()
        {
            { NinjaPriceCategory.Currency, "Currency" },
            { NinjaPriceCategory.Fragment, "Fragment" },
            { NinjaPriceCategory.DivinationCard, "DivinationCard" },
            { NinjaPriceCategory.Essence, "Essence" },
            { NinjaPriceCategory.Scarab, "Scarab" },
            { NinjaPriceCategory.Oil, "Oil" },
            { NinjaPriceCategory.Fossil, "Fossil" },
            { NinjaPriceCategory.Resonator, "Resonator" },
            { NinjaPriceCategory.DeliriumOrb, "DeliriumOrb" },
            { NinjaPriceCategory.Artifact, "Artifact" },
            { NinjaPriceCategory.Tattoo, "Tattoo" },
            { NinjaPriceCategory.Omen, "Omen" },
            { NinjaPriceCategory.KalguuranRune, "Runegraft" },
            { NinjaPriceCategory.AllflameEmber, "AllflameEmber" },
            { NinjaPriceCategory.DjinnCoin, "DjinnCoin" },
        };

        private static readonly Dictionary<NinjaPriceCategory, string> ItemEndpoints = new()
        {
            { NinjaPriceCategory.UniqueJewel, "UniqueJewel" },
            { NinjaPriceCategory.UniqueArmour, "UniqueArmour" },
            { NinjaPriceCategory.UniqueWeapon, "UniqueWeapon" },
            { NinjaPriceCategory.UniqueAccessory, "UniqueAccessory" },
            { NinjaPriceCategory.UniqueFlask, "UniqueFlask" },
            { NinjaPriceCategory.UniqueMap, "UniqueMap" },
            { NinjaPriceCategory.SkillGem, "SkillGem" },
            { NinjaPriceCategory.ClusterJewel, "ClusterJewel" },
            { NinjaPriceCategory.Map, "Map" },
            { NinjaPriceCategory.BlightedMap, "BlightedMap" },
            { NinjaPriceCategory.BlightRavagedMap, "BlightRavagedMap" },
            { NinjaPriceCategory.Invitation, "Invitation" },
            { NinjaPriceCategory.Incubator, "Incubator" },
            { NinjaPriceCategory.Vial, "Vial" },
            { NinjaPriceCategory.Beast, "Beast" },
            { NinjaPriceCategory.Wombgift, "Wombgift" },
            { NinjaPriceCategory.ValdoMap, "ValdoMap" },
        };

        private static readonly NinjaPriceCategory[] UniqueCategories =
        {
            NinjaPriceCategory.UniqueJewel, NinjaPriceCategory.UniqueArmour,
            NinjaPriceCategory.UniqueWeapon, NinjaPriceCategory.UniqueAccessory,
            NinjaPriceCategory.UniqueFlask, NinjaPriceCategory.UniqueMap,
        };

        // ═══════════════════════════════════════════════════
        // Public API
        // ═══════════════════════════════════════════════════

        public void Initialize(string pluginDir, Action<string> log)
        {
            _cacheDir = Path.Combine(pluginDir, "NinjaCache");
            _log = log;
            Status = "initialized, waiting for league";
        }

        /// <summary>
        /// Call each tick from BotCore. Handles league detection and refresh timer.
        /// </summary>
        public void Tick(GameController gc)
        {
            // Detect league
            var rawLeague = gc.IngameState?.ServerData?.League;
            if (string.IsNullOrEmpty(rawLeague)) return;

            var league = rawLeague.StartsWith("SSF ") ? rawLeague[4..] : rawLeague;
            if (league != _league)
            {
                _league = league;
                _artMappingBuilt = false;
                _log($"League detected: {league}");
                TriggerRefresh();
            }

            // Build art mapping from game files (needs to be on main thread)
            if (!_artMappingBuilt)
                TryBuildArtMapping(gc);

            // Periodic refresh
            if (!_refreshing &&
                (DateTime.Now - _lastRefreshAttempt).TotalMinutes >= RefreshIntervalMinutes &&
                !string.IsNullOrEmpty(_league))
            {
                TriggerRefresh();
            }
        }

        /// <summary>
        /// Current chaos value of one Divine Orb (from poe.ninja currency data).
        /// Returns 0 if not yet loaded.
        /// </summary>
        public double ChaosPerDivine =>
            _currencyPrices.TryGetValue("Divine Orb", out var val) ? val : 0;

        /// <summary>
        /// Get the price of an item entity. Returns MaxChaosValue for unidentified uniques.
        /// </summary>
        public PriceResult GetPrice(GameController gc, Entity entity)
        {
            if (!IsLoaded || entity == null) return PriceResult.Zero;

            try
            {
                return ClassifyAndPrice(gc, entity);
            }
            catch
            {
                return PriceResult.Zero;
            }
        }

        /// <summary>
        /// Direct lookup by name and category.
        /// </summary>
        public PriceResult GetPrice(string name, NinjaPriceCategory category)
        {
            if (!IsLoaded || string.IsNullOrEmpty(name)) return PriceResult.Zero;

            if (CurrencyEndpoints.ContainsKey(category))
            {
                if (_currencyPrices.TryGetValue(name, out var chaosVal))
                    return new PriceResult { MinChaosValue = chaosVal, MaxChaosValue = chaosVal, MatchCount = 1 };
                return PriceResult.Zero;
            }

            return LookupItem(name, category);
        }

        /// <summary>
        /// Resolve an entity's art path to candidate unique item names via the art mapping.
        /// Returns empty list if art mapping not built or no match found.
        /// Used by LootSystem to check must-loot names for unidentified uniques.
        /// </summary>
        public List<string> GetCandidateNames(Entity entity)
        {
            entity.TryGetComponent<RenderItem>(out var renderItem);
            var artPath = renderItem?.ResourcePath;
            if (string.IsNullOrEmpty(artPath)) return new List<string>();

            var mapping = _uniqueArtMapping;
            if (mapping.TryGetValue(artPath, out var names))
                return names;
            return new List<string>();
        }

        /// <summary>
        /// Search cached unique item names across all unique categories.
        /// Returns up to maxResults matches with name and chaos value.
        /// Used by the web UI for must-loot unique selection.
        /// </summary>
        public List<(string Name, double ChaosValue, string Category)> SearchUniques(string query, int maxResults = 20)
        {
            var results = new List<(string Name, double ChaosValue, string Category)>();
            if (string.IsNullOrWhiteSpace(query) || !IsLoaded) return results;

            var prices = _prices;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var cat in UniqueCategories)
            {
                if (!prices.TryGetValue(cat, out var byName)) continue;
                var catName = cat.ToString().Replace("Unique", "");

                foreach (var (name, entries) in byName)
                {
                    if (!name.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!seen.Add(name)) continue;

                    var maxVal = 0.0;
                    foreach (var e in entries)
                        if ((e.ChaosValue ?? 0) > maxVal) maxVal = e.ChaosValue ?? 0;

                    results.Add((name, maxVal, catName));
                }
            }

            results.Sort((a, b) => b.ChaosValue.CompareTo(a.ChaosValue));
            if (results.Count > maxResults)
                results.RemoveRange(maxResults, results.Count - maxResults);
            return results;
        }

        // ═══════════════════════════════════════════════════
        // Item classification + pricing
        // ═══════════════════════════════════════════════════

        private PriceResult ClassifyAndPrice(GameController gc, Entity entity)
        {
            // Unwrap WorldItem container to get the actual item entity
            if (entity.Path != null && entity.Path.Contains("WorldItem") &&
                entity.TryGetComponent<WorldItem>(out var worldItem) &&
                worldItem.ItemEntity is { IsValid: true })
            {
                entity = worldItem.ItemEntity;
            }

            var baseItemType = gc.Files.BaseItemTypes.Translate(entity.Path);
            var className = baseItemType?.ClassName ?? "";
            var baseName = baseItemType?.BaseName ?? "";
            var path = entity.Path ?? "";

            entity.TryGetComponent<Mods>(out var mods);
            var rarity = mods?.ItemRarity ?? ItemRarity.Normal;
            var isIdentified = mods?.Identified ?? true;
            var uniqueName = mods?.UniqueName?.Replace('\x2019', '\'');

            // Stack size for currency
            var stackSize = 1;
            if (entity.TryGetComponent<Stack>(out var stack))
                stackSize = stack.Size;

            // Classify the item
            var category = ClassifyItem(path, className, baseName, rarity, entity);
            if (category == null) return PriceResult.Zero;

            // ── Unidentified unique: use art mapping for candidates ──
            if (rarity == ItemRarity.Unique && !isIdentified)
            {
                return PriceUnidentifiedUnique(entity);
            }

            // ── Identified unique: lookup by unique name ──
            if (rarity == ItemRarity.Unique && !string.IsNullOrEmpty(uniqueName))
            {
                return LookupItem(uniqueName, category.Value);
            }

            // ── Currency/stackable: lookup by base name, multiply by stack ──
            if (CurrencyEndpoints.ContainsKey(category.Value))
            {
                if (_currencyPrices.TryGetValue(baseName, out var chaosVal))
                {
                    var total = chaosVal * stackSize;
                    return new PriceResult { MinChaosValue = total, MaxChaosValue = total, MatchCount = 1 };
                }
                return PriceResult.Zero;
            }

            // ── Skill gem: match on level + quality for accurate pricing ──
            if (category.Value == NinjaPriceCategory.SkillGem)
            {
                return PriceSkillGem(entity, baseName);
            }

            // ── Cluster jewel (non-unique): lookup by enchant + passive count + ilvl ──
            if (category.Value == NinjaPriceCategory.ClusterJewel)
            {
                return PriceClusterJewel(entity, mods);
            }

            // ── Everything else: lookup by base name ──
            return LookupItem(baseName, category.Value);
        }

        private PriceResult PriceUnidentifiedUnique(Entity entity)
        {
            entity.TryGetComponent<RenderItem>(out var renderItem);
            var artPath = renderItem?.ResourcePath;
            if (string.IsNullOrEmpty(artPath)) return PriceResult.Zero;

            var mapping = _uniqueArtMapping;
            if (!mapping.TryGetValue(artPath, out var candidateNames) || candidateNames.Count == 0)
                return PriceResult.Zero;

            // Get the item's actual link count to filter price entries
            // poe.ninja has separate entries for 0-link, 5-link, 6-link variants
            int itemLinks = 0;
            if (entity.TryGetComponent<Sockets>(out var sockets))
                itemLinks = sockets.LargestLinkSize;

            // Map item links to the ninja link tier (0 = base, 5 = 5-link, 6 = 6-link)
            // Items with <5 links use the base (links=0/null) price entry
            int ninjaLinkTier = itemLinks >= 6 ? 6 : itemLinks >= 5 ? 5 : 0;

            // Look up all candidates across all unique categories
            double minVal = double.MaxValue;
            double maxVal = 0;
            int matchCount = 0;
            string detailsId = "";

            foreach (var name in candidateNames)
            {
                // Skip replicas for non-replica items
                if (name.StartsWith("Replica ") && !name.StartsWith("Replica Dragonfang's Flight"))
                    continue;

                foreach (var cat in UniqueCategories)
                {
                    var prices = _prices;
                    if (!prices.TryGetValue(cat, out var byName)) continue;
                    if (!byName.TryGetValue(name, out var entries)) continue;

                    foreach (var entry in entries)
                    {
                        // Filter by link count — only match entries for the item's link tier
                        var entryLinks = entry.Links ?? 0;
                        if (entryLinks != ninjaLinkTier) continue;

                        var val = entry.ChaosValue ?? 0;
                        if (val <= 0) continue;

                        matchCount++;
                        if (val < minVal) minVal = val;
                        if (val > maxVal) { maxVal = val; detailsId = entry.DetailsId; }
                    }
                }
            }

            if (matchCount == 0) return PriceResult.Zero;
            if (minVal == double.MaxValue) minVal = 0;

            return new PriceResult
            {
                MinChaosValue = minVal,
                MaxChaosValue = maxVal,
                MatchCount = matchCount,
                DetailsId = detailsId,
            };
        }

        private const string ClusterEnchantPrefix = "Added Small Passive Skills grant: ";

        /// <summary>
        /// Price a skill gem by matching its actual level and quality against poe.ninja entries.
        /// Without this, LookupItem returns the max across all variants (e.g., 21/20 price for a 20/0 gem).
        /// </summary>
        private PriceResult PriceSkillGem(Entity entity, string baseName)
        {
            var prices = _prices;
            if (!prices.TryGetValue(NinjaPriceCategory.SkillGem, out var byName)) return PriceResult.Zero;
            if (!byName.TryGetValue(baseName, out var entries) || entries.Count == 0) return PriceResult.Zero;

            // Read the gem's actual level and quality
            int gemLevel = 1;
            int gemQuality = 0;
            if (entity.TryGetComponent<SkillGem>(out var sg))
                gemLevel = sg.Level;
            if (entity.TryGetComponent<Quality>(out var q))
                gemQuality = q.ItemQuality;

            // Try exact match first (level + quality)
            foreach (var e in entries)
            {
                if ((e.GemLevel ?? 1) == gemLevel && (e.GemQuality ?? 0) == gemQuality)
                {
                    var val = e.ChaosValue ?? 0;
                    return new PriceResult { MinChaosValue = val, MaxChaosValue = val, MatchCount = 1, DetailsId = e.DetailsId };
                }
            }

            // Fallback: match level only (quality 0 is the most common default entry)
            foreach (var e in entries)
            {
                if ((e.GemLevel ?? 1) == gemLevel && (e.GemQuality ?? 0) == 0)
                {
                    var val = e.ChaosValue ?? 0;
                    return new PriceResult { MinChaosValue = val, MaxChaosValue = val, MatchCount = 1, DetailsId = e.DetailsId };
                }
            }

            // Last resort: return the minimum value across all entries (conservative)
            double minVal = double.MaxValue;
            string detailsId = entries[0].DetailsId;
            foreach (var e in entries)
            {
                var val = e.ChaosValue ?? 0;
                if (val > 0 && val < minVal) { minVal = val; detailsId = e.DetailsId; }
            }
            if (minVal == double.MaxValue) return PriceResult.Zero;
            return new PriceResult { MinChaosValue = minVal, MaxChaosValue = minVal, MatchCount = entries.Count, DetailsId = detailsId };
        }

        /// <summary>
        /// Price a non-unique cluster jewel by its enchant text, passive count, and item level.
        /// Ninja indexes these by enchant name with variant = "{N} passives" and levelRequired tiers.
        /// </summary>
        private PriceResult PriceClusterJewel(Entity entity, Mods? mods)
        {
            if (mods?.EnchantedStats == null || mods.EnchantedStats.Count == 0)
                return PriceResult.Zero;

            // Extract enchant name: "Added Small Passive Skills grant: 4% increased maximum Life"
            string? enchantName = null;
            int passiveCount = 0;

            foreach (var stat in mods.EnchantedStats)
            {
                if (stat.StartsWith(ClusterEnchantPrefix, StringComparison.Ordinal))
                {
                    enchantName = stat[ClusterEnchantPrefix.Length..].Replace("\n", ", ");
                }
                else if (stat.StartsWith("Adds ", StringComparison.Ordinal) && stat.Contains("Passive Skill"))
                {
                    // "Adds 3 Passive Skills" → extract the number
                    var parts = stat.Split(' ');
                    if (parts.Length >= 2 && int.TryParse(parts[1], out var count))
                        passiveCount = count;
                }
            }

            if (string.IsNullOrEmpty(enchantName))
            {
                // Try LocalStats for passive count if enchant parsing didn't get it
                if (entity.TryGetComponent<LocalStats>(out var localStats) && localStats.StatDictionary != null)
                {
                    var statKey = Enum.Parse<GameStat>(nameof(GameStat.LocalJewelExpansionPassiveNodeCount));
                    localStats.StatDictionary.TryGetValue(statKey, out passiveCount);
                }
                return PriceResult.Zero;
            }

            // Also try LocalStats for passive count as backup
            if (passiveCount == 0 && entity.TryGetComponent<LocalStats>(out var ls) && ls.StatDictionary != null)
            {
                var statKey = Enum.Parse<GameStat>(nameof(GameStat.LocalJewelExpansionPassiveNodeCount));
                ls.StatDictionary.TryGetValue(statKey, out passiveCount);
            }

            var itemLevel = mods.ItemLevel;
            var variantStr = passiveCount > 0 ? $"{passiveCount} passives" : null;

            // Look up by enchant name in cluster jewel data
            var prices = _prices;
            if (!prices.TryGetValue(NinjaPriceCategory.ClusterJewel, out var byName))
                return PriceResult.Zero;
            if (!byName.TryGetValue(enchantName, out var entries) || entries.Count == 0)
                return PriceResult.Zero;

            // Filter by variant (passive count) and find best level match (highest levelRequired <= itemLevel)
            ItemLine? bestMatch = null;
            foreach (var entry in entries)
            {
                // Filter by passive count if we know it
                if (variantStr != null && entry.Variant != null && entry.Variant != variantStr)
                    continue;

                // Filter by level requirement
                if (entry.LevelRequired.HasValue && entry.LevelRequired.Value > itemLevel)
                    continue;

                // Pick the entry with the highest levelRequired that fits (most specific price)
                if (bestMatch == null ||
                    (entry.LevelRequired ?? 0) > (bestMatch.LevelRequired ?? 0))
                {
                    bestMatch = entry;
                }
            }

            if (bestMatch == null) return PriceResult.Zero;

            var val = bestMatch.ChaosValue ?? 0;
            return new PriceResult
            {
                MinChaosValue = val,
                MaxChaosValue = val,
                MatchCount = 1,
                DetailsId = bestMatch.DetailsId,
            };
        }

        private PriceResult LookupItem(string name, NinjaPriceCategory category)
        {
            var prices = _prices;
            if (!prices.TryGetValue(category, out var byName)) return PriceResult.Zero;
            if (!byName.TryGetValue(name, out var entries) || entries.Count == 0) return PriceResult.Zero;

            if (entries.Count == 1)
            {
                var val = entries[0].ChaosValue ?? 0;
                return new PriceResult
                {
                    MinChaosValue = val,
                    MaxChaosValue = val,
                    MatchCount = 1,
                    DetailsId = entries[0].DetailsId,
                };
            }

            // Multiple matches (different links, variants, etc.)
            double minVal = double.MaxValue, maxVal = 0;
            string detailsId = entries[0].DetailsId;
            foreach (var e in entries)
            {
                var val = e.ChaosValue ?? 0;
                if (val <= 0) continue;
                if (val < minVal) minVal = val;
                if (val > maxVal) { maxVal = val; detailsId = e.DetailsId; }
            }
            if (minVal == double.MaxValue) minVal = 0;

            return new PriceResult
            {
                MinChaosValue = minVal,
                MaxChaosValue = maxVal,
                MatchCount = entries.Count,
                DetailsId = detailsId,
            };
        }

        // ═══════════════════════════════════════════════════
        // Item classification (simplified from Get-Chaos-Value)
        // ═══════════════════════════════════════════════════

        private static readonly HashSet<string> WeaponClasses = new()
        {
            "One Hand Mace", "Two Hand Mace", "One Hand Axe", "Two Hand Axe",
            "One Hand Sword", "Two Hand Sword", "Thrusting One Hand Sword",
            "Bow", "Claw", "Dagger", "Sceptre", "Staff", "Wand",
        };

        private NinjaPriceCategory? ClassifyItem(string path, string className, string baseName, ItemRarity rarity, Entity entity)
        {
            // Path-based checks first (most specific)
            if (path.StartsWith("Metadata/Items/Currency/Runegraft", StringComparison.Ordinal))
                return NinjaPriceCategory.KalguuranRune;
            if (path.StartsWith("Metadata/Items/MapFragments/", StringComparison.Ordinal) &&
                path.EndsWith("AllflamePack", StringComparison.Ordinal))
                return NinjaPriceCategory.AllflameEmber;
            if (path.Contains("Metadata/Items/DivinationCards"))
                return NinjaPriceCategory.DivinationCard;
            if (className == "MapFragment" && path.StartsWith("Metadata/Items/Scarabs/"))
                return NinjaPriceCategory.Scarab;

            // Class/name-based checks
            if (baseName == "Imprinted Bestiary Orb")
                return NinjaPriceCategory.Beast;
            if (className == "MiscMapItem" &&
                path.StartsWith("Metadata/Items/MapFragments/Primordial/", StringComparison.Ordinal) &&
                path.EndsWith("Key", StringComparison.Ordinal))
                return NinjaPriceCategory.Invitation;
            if (baseName.StartsWith("Coin of"))
                return NinjaPriceCategory.DjinnCoin;
            if (className == "BrequelFruit")
                return NinjaPriceCategory.Wombgift;
            if (baseName.EndsWith(" Oil"))
                return NinjaPriceCategory.Oil;
            if (baseName.Contains("Tattoo "))
                return NinjaPriceCategory.Tattoo;
            if (baseName.StartsWith("Omen "))
                return NinjaPriceCategory.Omen;
            if (baseName.Contains("Essence") || baseName.Contains("Remnant of"))
                return NinjaPriceCategory.Essence;
            if (baseName.EndsWith(" Fossil"))
                return NinjaPriceCategory.Fossil;
            if (className == "DelveStackableSocketableCurrency")
                return NinjaPriceCategory.Resonator;
            if (baseName.EndsWith("Delirium Orb"))
                return NinjaPriceCategory.DeliriumOrb;
            if (baseName.StartsWith("Vial "))
                return NinjaPriceCategory.Vial;
            if (baseName.Contains("Astragali") || baseName.Contains("Burial Medallion") ||
                baseName.Contains("Scrap Metal") || baseName.Contains("Exotic Coinage"))
                return NinjaPriceCategory.Artifact;
            if (className is "Incubator" or "IncubatorStackable")
                return NinjaPriceCategory.Incubator;

            // Fragments (must come after scarab/essence/oil/etc.)
            if (className == "MapFragment" || baseName.Contains("Timeless ") ||
                baseName.StartsWith("Simulacrum") ||
                (className == "StackableCurrency" && baseName.StartsWith("Splinter of ")) ||
                baseName.StartsWith("Crescent Splinter") ||
                className == "VaultKey" ||
                baseName == "Valdo's Puzzle Box")
                return NinjaPriceCategory.Fragment;

            // Gems
            if (className is "Support Skill Gem" or "Active Skill Gem")
                return NinjaPriceCategory.SkillGem;

            // Maps — detect by path since MapKey component may not be accessible
            if (rarity != ItemRarity.Unique && path.Contains("Metadata/Items/Maps/"))
            {
                if (baseName == "Valdo Map") return NinjaPriceCategory.ValdoMap;
                if (entity.TryGetComponent<Mods>(out var mapMods))
                {
                    foreach (var mod in mapMods.ItemMods)
                    {
                        if (mod.RawName == "UberInfectedMap__") return NinjaPriceCategory.BlightRavagedMap;
                        if (mod.RawName == "InfectedMap") return NinjaPriceCategory.BlightedMap;
                    }
                }
                return NinjaPriceCategory.Map;
            }

            // Cluster jewels (non-unique)
            if (rarity != ItemRarity.Unique && baseName is "Large Cluster Jewel" or "Medium Cluster Jewel" or "Small Cluster Jewel")
                return NinjaPriceCategory.ClusterJewel;

            // Uniques by equipment type
            if (rarity == ItemRarity.Unique)
            {
                if (path.Contains("Metadata/Items/Maps/")) return NinjaPriceCategory.UniqueMap;
                if (className is "Amulet" or "Ring" or "Belt") return NinjaPriceCategory.UniqueAccessory;
                if (entity.HasComponent<Flask>()) return NinjaPriceCategory.UniqueFlask;
                if (className is "Jewel" or "AbyssJewel") return NinjaPriceCategory.UniqueJewel;
                if (entity.HasComponent<Armour>() || className == "Quiver") return NinjaPriceCategory.UniqueArmour;
                if (entity.HasComponent<Weapon>()) return NinjaPriceCategory.UniqueWeapon;
            }

            // Generic currency (catch-all, after all specific types)
            if (className == "StackableCurrency")
                return NinjaPriceCategory.Currency;
            if (baseName.EndsWith(" Catalyst"))
                return NinjaPriceCategory.Currency;

            return null;
        }

        // ═══════════════════════════════════════════════════
        // Art mapping for unidentified uniques
        // ═══════════════════════════════════════════════════

        private void TryBuildArtMapping(GameController gc)
        {
            try
            {
                // Try game files first
                var ivi = gc.Files.ItemVisualIdentities?.EntriesList;
                var uid = gc.Files.UniqueItemDescriptions?.EntriesList;
                if (ivi != null && uid != null && ivi.Count > 0 && uid.Count > 0)
                {
                    var mapping = new Dictionary<string, List<string>>();
                    var uidByIdentity = uid
                        .Where(x => x.ItemVisualIdentity != null && x.UniqueName?.Text != null)
                        .GroupBy(x => x.ItemVisualIdentity)
                        .ToDictionary(g => g.Key, g => g.Select(x => x.UniqueName.Text).Distinct().ToList());

                    foreach (var entry in ivi)
                    {
                        if (string.IsNullOrEmpty(entry.ArtPath)) continue;
                        if (uidByIdentity.TryGetValue(entry, out var names) && names.Count > 0)
                        {
                            if (mapping.TryGetValue(entry.ArtPath, out var existing))
                                existing.AddRange(names);
                            else
                                mapping[entry.ArtPath] = new List<string>(names);
                        }
                    }

                    _uniqueArtMapping = mapping;
                    _artMappingBuilt = true;
                    _log($"Art mapping built from game files: {mapping.Count} entries");
                    return;
                }

                // Fall back to embedded JSON baked into the DLL
                LoadEmbeddedArtMapping();
            }
            catch (Exception ex)
            {
                _log($"Art mapping from game files failed: {ex.Message}");
                LoadEmbeddedArtMapping();
            }
        }

        private void LoadEmbeddedArtMapping()
        {
            try
            {
                var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                using var stream = assembly.GetManifestResourceStream("uniqueArtMapping.json");
                if (stream == null)
                {
                    _log("Embedded art mapping not found in assembly");
                    _artMappingBuilt = true; // Don't retry
                    return;
                }

                using var reader = new StreamReader(stream);
                var json = reader.ReadToEnd();
                var mapping = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (mapping != null && mapping.Count > 0)
                {
                    // Normalize fancy quotes to straight quotes (same as Get-Chaos-Value)
                    foreach (var kvp in mapping)
                    {
                        for (int i = 0; i < kvp.Value.Count; i++)
                            kvp.Value[i] = kvp.Value[i].Replace('\x2019', '\'');
                    }

                    _uniqueArtMapping = mapping;
                    _artMappingBuilt = true;
                    _log($"Art mapping loaded from embedded resource: {mapping.Count} entries");
                }
            }
            catch (Exception ex)
            {
                _log($"Embedded art mapping failed: {ex.Message}");
                _artMappingBuilt = true; // Don't retry
            }
        }

        // ═══════════════════════════════════════════════════
        // Data fetching and caching
        // ═══════════════════════════════════════════════════

        private void TriggerRefresh()
        {
            if (_refreshing || string.IsNullOrEmpty(_league)) return;
            _refreshing = true;
            _lastRefreshAttempt = DateTime.Now;
            Status = "refreshing...";

            var league = _league;
            var cacheDir = Path.Combine(_cacheDir, league);

            Task.Run(async () =>
            {
                try
                {
                    Directory.CreateDirectory(cacheDir);

                    var newPrices = new Dictionary<NinjaPriceCategory, Dictionary<string, List<ItemLine>>>();
                    var newCurrencyPrices = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                    int totalEntries = 0;
                    int successCount = 0;
                    int totalEndpoints = CurrencyEndpoints.Count + ItemEndpoints.Count;

                    // Fetch currency endpoints
                    foreach (var (category, type) in CurrencyEndpoints)
                    {
                        try
                        {
                            var url = $"https://poe.ninja/poe1/api/economy/exchange/current/overview?league={Uri.EscapeDataString(league)}&type={type}";
                            var cacheFile = Path.Combine(cacheDir, $"{type}.json");
                            var json = await FetchWithCache(url, cacheFile);
                            if (json == null) continue;

                            var response = JsonSerializer.Deserialize<CurrencyOverviewResponse>(json,
                                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                            if (response == null) continue;

                            // Join lines + items by id to build name → chaos value map
                            var itemsById = new Dictionary<string, string>();
                            foreach (var item in response.Items)
                                itemsById[item.Id] = item.Name;
                            if (response.Core?.Items != null)
                                foreach (var item in response.Core.Items)
                                    itemsById[item.Id] = item.Name;

                            foreach (var line in response.Lines)
                            {
                                if (line.PrimaryValue.HasValue && itemsById.TryGetValue(line.Id, out var name))
                                {
                                    newCurrencyPrices[name] = line.PrimaryValue.Value;
                                    totalEntries++;
                                }
                            }
                            successCount++;
                        }
                        catch (Exception ex)
                        {
                            _log($"Failed to fetch {type}: {ex.Message}");
                        }

                        await Task.Delay(200); // Rate limit
                    }

                    // Fetch item endpoints
                    foreach (var (category, type) in ItemEndpoints)
                    {
                        try
                        {
                            var url = $"https://poe.ninja/api/data/itemoverview?league={Uri.EscapeDataString(league)}&type={type}&language=en";
                            var cacheFile = Path.Combine(cacheDir, $"{type}.json");
                            var json = await FetchWithCache(url, cacheFile);
                            if (json == null) continue;

                            var response = JsonSerializer.Deserialize<ItemOverviewResponse>(json,
                                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                            if (response?.Lines == null) continue;

                            // Filter out relics
                            var lines = response.Lines
                                .Where(l => !l.DetailsId.Contains("-relic"))
                                .ToList();

                            // Index by name
                            var byName = new Dictionary<string, List<ItemLine>>(StringComparer.OrdinalIgnoreCase);
                            foreach (var line in lines)
                            {
                                if (string.IsNullOrEmpty(line.Name)) continue;
                                if (!byName.TryGetValue(line.Name, out var list))
                                {
                                    list = new List<ItemLine>();
                                    byName[line.Name] = list;
                                }
                                list.Add(line);
                                totalEntries++;
                            }

                            newPrices[category] = byName;
                            successCount++;
                        }
                        catch (Exception ex)
                        {
                            _log($"Failed to fetch {type}: {ex.Message}");
                        }

                        await Task.Delay(200); // Rate limit
                    }

                    // Write metadata
                    try
                    {
                        var metaPath = Path.Combine(cacheDir, "meta.json");
                        var meta = JsonSerializer.Serialize(new { lastLoadTime = DateTime.UtcNow });
                        File.WriteAllText(metaPath, meta);
                    }
                    catch { }

                    // Atomic swap
                    _prices = newPrices;
                    _currencyPrices = newCurrencyPrices;
                    _priceCount = totalEntries;
                    LastRefreshTime = DateTime.Now;
                    Status = $"loaded {totalEntries} prices ({successCount}/{totalEndpoints} endpoints)";
                    _log(Status);
                }
                catch (Exception ex)
                {
                    Status = $"refresh failed: {ex.Message}";
                    _log(Status);
                }
                finally
                {
                    _refreshing = false;
                }
            });
        }

        private async Task<string?> FetchWithCache(string url, string cacheFile)
        {
            // Try web first
            try
            {
                var json = await _http.GetStringAsync(url);
                if (!string.IsNullOrEmpty(json))
                {
                    try { File.WriteAllText(cacheFile, json); } catch { }
                    return json;
                }
            }
            catch { }

            // Fall back to cache
            try
            {
                if (File.Exists(cacheFile))
                    return File.ReadAllText(cacheFile);
            }
            catch { }

            return null;
        }
    }
}
