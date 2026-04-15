using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace AutoExile.Systems
{
    /// <summary>
    /// Sends item drop notifications to a Discord webhook.
    /// </summary>
    public static class DiscordNotifier
    {
        private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };

        /// <summary>
        /// Send a loot notification. Fire-and-forget — exceptions are swallowed.
        /// </summary>
        public static void Notify(string webhookUrl, string itemName, string area, double chaosValue)
        {
            if (string.IsNullOrWhiteSpace(webhookUrl)) return;
            _ = SendAsync(webhookUrl, itemName, area, chaosValue);
        }

        public static async Task SendAsync(string webhookUrl, string itemName, string area, double chaosValue)
        {
            try
            {
                var content = BuildMessage(itemName, area, chaosValue, isTest: false);
                await PostAsync(webhookUrl, content);
            }
            catch
            {
                // Notifications must never crash the bot
            }
        }

        public static async Task SendTestAsync(string webhookUrl)
        {
            var content = BuildMessage("Mirror of Kalandra", "Valdo's Rest", 999999, isTest: true);
            await PostAsync(webhookUrl, content);
        }

        private static async Task PostAsync(string webhookUrl, object payload)
        {
            var json = JsonSerializer.Serialize(payload);
            using var req = new StringContent(json, Encoding.UTF8, "application/json");
            var resp = await _http.PostAsync(webhookUrl, req);
            resp.EnsureSuccessStatusCode();
        }

        private static object BuildMessage(string itemName, string area, double chaosValue, bool isTest)
        {
            var prefix = isTest ? "🧪 **[TEST]** " : "💰 ";
            var valueStr = chaosValue >= 1 ? $"{chaosValue:F0}c" : $"{chaosValue:F1}c";
            var areaStr = string.IsNullOrWhiteSpace(area) ? "Unknown area" : area;
            var description = isTest
                ? "This is a test notification from AutoExile."
                : $"**{itemName}** dropped in **{areaStr}** — worth ~{valueStr}";

            return new
            {
                username = "AutoExile",
                embeds = new[]
                {
                    new
                    {
                        title = $"{prefix}{itemName}",
                        description,
                        color = isTest ? 0x7289DA : 0xFFD700,
                        footer = new { text = $"AutoExile • {DateTime.Now:HH:mm:ss}" }
                    }
                }
            };
        }
    }
}
