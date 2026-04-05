namespace AutoExile.Systems
{
    /// <summary>
    /// Wish selection priority for the Faridun/Mirage encounter.
    /// Higher priority = pick first. Wishes are matched by name substring.
    /// The panel shows 3 options — we pick the highest-priority match.
    ///
    /// Final score = wish priority + coin type bonus.
    /// When two wishes are similar priority, the preferred coin type breaks the tie.
    /// This means "junk wish with Coin of Power" beats "junk wish with Coin of Knowledge"
    /// if Power is preferred.
    ///
    /// Priority tiers:
    ///   S (100+): Exceptional value — always take
    ///   A (80-99): High value — take over most alternatives
    ///   B (60-79): Good value — take if nothing better
    ///   C (40-59): Decent — filler
    ///   D (20-39): Low value — only if nothing else
    ///   F (0-19): Junk — avoid
    ///
    /// Coin bonus is small (0-10) so it only matters between similar-tier wishes.
    /// </summary>
    public static class WishPriority
    {
        /// <summary>Coin type bonuses. Added to wish priority to break ties.</summary>
        public static readonly Dictionary<string, Dictionary<string, int>> CoinBonuses = new()
        {
            ["Coin of Power"] = new() { ["Ruzhan"] = 10, ["Kelari"] = 3, ["Navira"] = 1 },
            ["Coin of Skill"] = new() { ["Kelari"] = 10, ["Ruzhan"] = 3, ["Navira"] = 1 },
            ["Coin of Knowledge"] = new() { ["Navira"] = 10, ["Kelari"] = 3, ["Ruzhan"] = 1 },
        };

        /// <summary>Map wish names to their Djinn (coin type source).</summary>
        private static readonly Dictionary<string, string> WishToDjinn = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Wish for Foes"] = "Kelari",
            ["Wish for Troves"] = "Kelari",
            ["Wish for Wealth"] = "Kelari",
            ["Wish for Treasures"] = "Kelari",
            ["Wish for Jewels"] = "Kelari",
            ["Wish for Knowledge"] = "Kelari",
            ["Wish for Elements"] = "Kelari",
            ["Wish for Betrayal"] = "Kelari",
            ["Wish for Skittering"] = "Kelari",
            ["Wish for Strange Horizons"] = "Kelari",
            ["Wish for Sands"] = "Kelari",
            ["Wish for Rust"] = "Kelari",
            ["Wish for Eminence"] = "Kelari",

            ["Wish for Scarabs"] = "Ruzhan",
            ["Wish for Gold"] = "Ruzhan",
            ["Wish for Pursuit"] = "Ruzhan",
            ["Wish for Glittering"] = "Ruzhan",
            ["Wish for Momentum"] = "Ruzhan",
            ["Wish for Souls"] = "Ruzhan",
            ["Wish for Power"] = "Ruzhan",
            ["Wish for Glyphs"] = "Ruzhan",
            ["Wish for Trinkets"] = "Ruzhan",
            ["Wish for Avarice"] = "Ruzhan",
            ["Wish for Fortune"] = "Ruzhan",
            ["Wish for Distant Horizons"] = "Ruzhan",
            ["Wish for Flames"] = "Ruzhan",

            ["Wish for Foreknowledge"] = "Navira",
            ["Wish for Horizons"] = "Navira",
            ["Wish for Hordes"] = "Navira",
            ["Wish for Meddling"] = "Navira",
            ["Wish for Risk"] = "Navira", // Actually Ruzhan per data
            ["Wish for Wisps"] = "Navira",
            ["Wish for Hindrance"] = "Navira",
            ["Wish for Oases"] = "Navira",
            ["Wish for Prosperity"] = "Navira",
            ["Wish for Providence"] = "Navira",
            ["Wish for Reflection"] = "Navira",
            ["Wish for Godhood"] = "Navira",
            ["Wish for Phantoms"] = "Navira",
            ["Wish for Augury"] = "Navira",
            ["Wish for Tides"] = "Navira",
            ["Wish for Craftsmanship"] = "Navira",
            ["Wish for Titans"] = "Navira",
        };
        /// <summary>
        /// Default wish priorities. Key = wish name substring (matched case-insensitive).
        /// Value = priority score (higher = better).
        /// </summary>
        public static readonly Dictionary<string, int> DefaultPriorities = new(StringComparer.OrdinalIgnoreCase)
        {
            // ═══ S-Tier: Exceptional value — always take ═══
            ["Wish for Reflection"] = 115,      // Reflecting Mist — mirror item, extremely rare/valuable
            ["Wish for Rust"] = 112,            // Ridan, of the Afarud boss — high value drops
            ["Wish for Providence"] = 110,      // Nameless Seer — valuable encounter
            ["Wish for Flames"] = 105,          // Coin of Power reward
            ["Wish for Tides"] = 104,           // Coin of Knowledge reward
            ["Wish for Sands"] = 103,           // Coin of Skill reward

            // ═══ A-Tier: High value ═══
            ["Wish for Terror"] = 95,           // Pinnacle boss from The Feared — big drops but dangerous
            ["Wish for Foreknowledge"] = 93,    // 100% more Divination Cards
            ["Wish for Wealth"] = 91,           // 100% more Currency
            ["Wish for Augury"] = 90,           // Cache of Stacked Decks (on chain break)
            ["Wish for Fortune"] = 88,          // Cache of Currency (on chain break)
            ["Wish for Titans"] = 86,           // Additional Atlas Boss packs
            ["Wish for Scarabs"] = 85,          // 80% more Scarabs
            ["Wish for Uncertainty"] = 83,      // 10 random Scarab modifiers
            ["Wish for Skittering"] = 82,       // Cache of Scarabs (on chain break)
            ["Wish for Risk"] = 80,             // 12 packs of difficult + rewarding monsters

            // ═══ B-Tier: Good value ═══
            ["Wish for Troves"] = 76,           // Additional Unique Strongbox
            ["Wish for Treasures"] = 75,        // 80% increased Rarity
            ["Wish for Jewels"] = 73,           // Jewel Cache (Golden > Silver > Bronze)
            ["Wish for Prosperity"] = 72,       // Fountain of wealth
            ["Wish for Horizons"] = 70,         // 100% more Maps
            ["Wish for Meddling"] = 68,         // 12 packs Astral monsters
            ["Wish for Wisps"] = 65,            // Wildwood Wisps empowerment
            ["Wish for Hordes"] = 63,           // 20% increased Pack Size

            // ═══ C-Tier: Decent ═══
            ["Wish for Glyphs"] = 55,           // Scrolls drop as other currencies
            ["Wish for Betrayal"] = 53,         // Syndicate members replace packs
            ["Wish for Gold"] = 52,             // 80% more Gold
            ["Wish for Eminence"] = 51,         // Unique Jewel on chain break
            ["Wish for Distant Horizons"] = 50, // Cache of Maps
            ["Wish for Strange Horizons"] = 49, // Unique Map
            ["Wish for Pursuit"] = 48,          // Golden Volatile on death
            ["Wish for Godhood"] = 47,          // Echoing + Divine Shrine
            ["Wish for Souls"] = 46,            // Soul Eater
            ["Wish for Power"] = 45,            // Enemies explode on death
            ["Wish for Momentum"] = 44,         // Onslaught + Adrenaline
            ["Wish for Glittering"] = 43,       // Quality on gems
            ["Wish for Knowledge"] = 42,        // 50% increased Experience
            ["Wish for Elements"] = 41,         // Blessing of the Storm
            ["Wish for Avarice"] = 40,          // Equipment → Gold conversion

            // ═══ D-Tier: Low value ═══
            ["Wish for Foes"] = 35,             // Extra mods on rares
            ["Wish for Rebirth"] = 33,          // Monsters revive (more kills but slower)
            ["Wish for Hindrance"] = 32,        // Chill + Hinder enemies
            ["Wish for Oases"] = 31,            // Oasis ground patches
            ["Wish for Trinkets"] = 30,         // Jewellery → Jewels or Rare Jewellery
            ["Wish for Phantoms"] = 28,         // No equipment drops
            ["Wish for Regency"] = 25,          // Regal Orbs
            ["Wish for Binding"] = 24,          // Orbs of Binding
            ["Wish for Connections"] = 23,      // Fusing/Jewellers
            ["Wish for Ancient Protection"] = 22, // Random unique armour
            ["Wish for Ancient Armaments"] = 21,  // Random unique weapon
            ["Wish for Ancient Curios"] = 20,     // Random unique jewellery

            // ═══ F-Tier: Junk ═══
            ["Wish for Craftsmanship"] = 15,    // 5-link body armour
            ["Wish for Mosaics"] = 12,          // Chromatic Orbs
            ["Wish for Swiftness"] = 10,        // Rare Boots
            ["Wish for Helms"] = 10,            // Rare Helmets
            ["Wish for Mitts"] = 10,            // Rare Gloves
            ["Wish for Protection"] = 10,       // Rare Body Armours
            ["Wish for Blades"] = 10,           // Rare Melee Weapons
            ["Wish for Missiles"] = 10,         // Rare Ranged Weapons
            ["Wish for Bastions"] = 10,         // Rare Shields
            ["Wish for Croaks"] = 5,            // Frogs (meme)
            ["Wish for Fishes"] = 5,            // Fishing Rod (meme)
        };

        /// <summary>
        /// Find the best wish option from a wishes panel container.
        /// Options are at container indices 3, 4, 5.
        /// Final score = wish priority + coin type bonus.
        /// Returns the index (3-5) of the highest-scoring wish.
        /// </summary>
        public static int FindBestOption(ExileCore.PoEMemory.Element container,
            string preferredCoin = "Coin of Power", Dictionary<string, int>? overrides = null)
        {
            int bestIndex = 3; // Default to first option
            int bestScore = -1;

            for (int i = 3; i <= 5; i++)
            {
                var option = container.GetChildAtIndex(i);
                if (option == null || !option.IsVisible) continue;

                var name = GetWishName(option);
                var basePriority = GetBasePriority(name, overrides);
                var coinBonus = GetCoinBonus(name, preferredCoin);
                var score = basePriority + coinBonus;

                if (score > bestScore)
                {
                    bestScore = score;
                    bestIndex = i;
                }
            }

            return bestIndex;
        }

        /// <summary>Get the coin type bonus for a wish based on preferred coin.</summary>
        public static int GetCoinBonus(string wishName, string preferredCoin)
        {
            if (string.IsNullOrEmpty(preferredCoin) || preferredCoin == "Any")
                return 0;

            // Find which Djinn this wish belongs to
            string? djinn = null;
            foreach (var (key, value) in WishToDjinn)
            {
                if (wishName.Contains(key, StringComparison.OrdinalIgnoreCase))
                {
                    djinn = value;
                    break;
                }
            }

            if (djinn == null) return 0;

            // Look up the bonus for this Djinn under the preferred coin
            if (CoinBonuses.TryGetValue(preferredCoin, out var bonuses) &&
                bonuses.TryGetValue(djinn, out var bonus))
                return bonus;

            return 0;
        }

        /// <summary>Get base priority score for a wish name. Checks overrides first, then defaults.</summary>
        public static int GetBasePriority(string wishName, Dictionary<string, int>? overrides = null)
        {
            if (overrides != null)
            {
                foreach (var (key, value) in overrides)
                {
                    if (wishName.Contains(key, StringComparison.OrdinalIgnoreCase))
                        return value;
                }
            }

            foreach (var (key, value) in DefaultPriorities)
            {
                if (wishName.Contains(key, StringComparison.OrdinalIgnoreCase))
                    return value;
            }

            return 1; // Unknown wish — lowest priority
        }

        /// <summary>Read wish name from option element. Title is at child index 2.</summary>
        private static string GetWishName(ExileCore.PoEMemory.Element option)
        {
            if (option.ChildCount > 2)
            {
                var titleEl = option.GetChildAtIndex(2);
                if (titleEl?.Text != null) return titleEl.Text;
            }
            return "Unknown";
        }
    }
}
