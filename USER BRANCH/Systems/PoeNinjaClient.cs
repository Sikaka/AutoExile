using System.Net.Http;
using System.Text.Json;

namespace AutoExile.Systems
{
    /// <summary>
    /// Lightweight poe.ninja price cache. Prices are fetched once per league per item type
    /// and held in memory for the session. All fetches are fire-and-forget async.
    ///
    /// Usage:
    ///   float chaosValue = PoeNinjaClient.GetChaosValue("Simulacrum", "Fragment", "Settlers");
    ///   // Returns -1 if not yet cached (fetch fires in background, call again next tick).
    /// </summary>
    public static class PoeNinjaClient
    {
        private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        // Key = "{league}|{category}|{itemName}" → chaos value
        private static readonly Dictionary<string, float> _cache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> _fetching = new(StringComparer.OrdinalIgnoreCase);

        // Maps poe.ninja category names → API type parameter
        // https://poe.ninja/api/data/itemoverview?league=X&type=Y
        private static readonly Dictionary<string, string> _categoryType = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Currency"] = "currency",     // uses currencyoverview endpoint
            ["Fragment"] = "Fragment",
            ["Scarab"]   = "Scarab",
            ["Oil"]      = "Oil",
        };

        /// <summary>
        /// Returns the chaos value of the named item, or -1 if not yet in cache.
        /// A background fetch is started on first miss — call again next tick.
        /// </summary>
        public static float GetChaosValue(string itemName, string category, string league)
        {
            var key = $"{league}|{category}|{itemName}";
            if (_cache.TryGetValue(key, out var v)) return v;

            // Fetch in background if not already in-flight
            var fetchKey = $"{league}|{category}";
            if (!_fetching.Contains(fetchKey))
            {
                _fetching.Add(fetchKey);
                _ = FetchAsync(category, league);
            }
            return -1f;
        }

        private static async Task FetchAsync(string category, string league)
        {
            try
            {
                bool isCurrency = string.Equals(category, "Currency", StringComparison.OrdinalIgnoreCase);
                string endpoint = isCurrency
                    ? $"https://poe.ninja/api/data/currencyoverview?league={Uri.EscapeDataString(league)}&type=Currency"
                    : $"https://poe.ninja/api/data/itemoverview?league={Uri.EscapeDataString(league)}&type={Uri.EscapeDataString(category)}";

                var json = await _http.GetStringAsync(endpoint);
                using var doc = JsonDocument.Parse(json);

                if (isCurrency)
                {
                    // currencyoverview: { lines: [ { currencyTypeName, chaosEquivalent } ] }
                    foreach (var line in doc.RootElement.GetProperty("lines").EnumerateArray())
                    {
                        var name = line.GetProperty("currencyTypeName").GetString() ?? "";
                        var chaos = line.GetProperty("chaosEquivalent").GetSingle();
                        _cache[$"{league}|{category}|{name}"] = chaos;
                    }
                }
                else
                {
                    // itemoverview: { lines: [ { name, chaosValue } ] }
                    foreach (var line in doc.RootElement.GetProperty("lines").EnumerateArray())
                    {
                        var name = line.GetProperty("name").GetString() ?? "";
                        if (!line.TryGetProperty("chaosValue", out var cv)) continue;
                        _cache[$"{league}|{category}|{name}"] = cv.GetSingle();
                    }
                }
            }
            catch { /* silently ignore — caller gets -1 until next fetch */ }
            finally
            {
                _fetching.Remove($"{league}|{category}");
            }
        }

        /// <summary>Pre-warm prices for a specific category (call on mode enter).</summary>
        public static void Prefetch(string category, string league)
        {
            var fetchKey = $"{league}|{category}";
            if (!_fetching.Contains(fetchKey))
            {
                _fetching.Add(fetchKey);
                _ = FetchAsync(category, league);
            }
        }

        /// <summary>Clear all cached prices (e.g. on league change).</summary>
        public static void Clear()
        {
            _cache.Clear();
            _fetching.Clear();
        }
    }
}
