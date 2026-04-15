using System.Text.Json.Serialization;

namespace AutoExile.Systems
{
    // ═══════════════════════════════════════════════════
    // Price lookup result
    // ═══════════════════════════════════════════════════

    /// <summary>
    /// Result of a price lookup. MinChaosValue == MaxChaosValue when the item is unambiguous.
    /// For unidentified uniques with multiple candidates, Min/Max represent the price range.
    /// </summary>
    public class PriceResult
    {
        public double MinChaosValue { get; set; }
        public double MaxChaosValue { get; set; }
        public int MatchCount { get; set; }
        public string DetailsId { get; set; } = "";

        public static readonly PriceResult Zero = new();
    }

    // ═══════════════════════════════════════════════════
    // Category enum
    // ═══════════════════════════════════════════════════

    public enum NinjaPriceCategory
    {
        // Currency-style endpoints (exchange API)
        Currency, Fragment, DivinationCard, Essence, Scarab, Oil, Fossil, Resonator,
        DeliriumOrb, Artifact, Tattoo, Omen, KalguuranRune, AllflameEmber, DjinnCoin, Astrolabe,
        // Item-style endpoints (itemoverview API)
        UniqueJewel, UniqueArmour, UniqueWeapon, UniqueAccessory, UniqueFlask, UniqueMap,
        SkillGem, ClusterJewel, Map, BlightedMap, BlightRavagedMap, Invitation,
        Incubator, Vial, Beast, Wombgift, ValdoMap,
    }

    // ═══════════════════════════════════════════════════
    // API response DTOs — Item overview endpoints
    // ═══════════════════════════════════════════════════

    public class ItemOverviewResponse
    {
        [JsonPropertyName("lines")]
        public List<ItemLine> Lines { get; set; } = new();
    }

    public class ItemLine
    {
        [JsonPropertyName("id")]
        public long Id { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("baseType")]
        public string BaseType { get; set; } = "";

        [JsonPropertyName("chaosValue")]
        public double? ChaosValue { get; set; }

        [JsonPropertyName("divineValue")]
        public double? DivineValue { get; set; }

        [JsonPropertyName("detailsId")]
        public string DetailsId { get; set; } = "";

        [JsonPropertyName("variant")]
        public string? Variant { get; set; }

        [JsonPropertyName("links")]
        public int? Links { get; set; }

        [JsonPropertyName("corrupted")]
        public bool? Corrupted { get; set; }

        [JsonPropertyName("gemLevel")]
        public int? GemLevel { get; set; }

        [JsonPropertyName("gemQuality")]
        public int? GemQuality { get; set; }

        [JsonPropertyName("levelRequired")]
        public int? LevelRequired { get; set; }

        [JsonPropertyName("listingCount")]
        public int? ListingCount { get; set; }

        [JsonPropertyName("itemClass")]
        public int? ItemClass { get; set; }
    }

    // ═══════════════════════════════════════════════════
    // API response DTOs — Currency/exchange endpoints
    // ═══════════════════════════════════════════════════

    public class CurrencyOverviewResponse
    {
        [JsonPropertyName("core")]
        public CurrencyCore? Core { get; set; }

        [JsonPropertyName("lines")]
        public List<CurrencyLine> Lines { get; set; } = new();

        [JsonPropertyName("items")]
        public List<CurrencyItem> Items { get; set; } = new();
    }

    public class CurrencyCore
    {
        [JsonPropertyName("items")]
        public List<CurrencyItem> Items { get; set; } = new();

        [JsonPropertyName("rates")]
        public Dictionary<string, double> Rates { get; set; } = new();

        [JsonPropertyName("primary")]
        public string Primary { get; set; } = "chaos";
    }

    public class CurrencyLine
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("primaryValue")]
        public double? PrimaryValue { get; set; }
    }

    public class CurrencyItem
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("detailsId")]
        public string DetailsId { get; set; } = "";
    }
}
