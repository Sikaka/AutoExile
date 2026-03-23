using System.Text.Json;

namespace AutoExile.WebServer
{
    /// <summary>
    /// Owns settings persistence via a JSON config file.
    /// Source of truth on startup — loads config and applies to ExileCore nodes.
    /// Saves after every web UI change.
    /// </summary>
    public class ConfigManager
    {
        private string _configPath = "";
        private readonly Action<string> _log;

        private static readonly JsonSerializerOptions WriteOpts = new()
        {
            WriteIndented = true,
        };

        public ConfigManager(Action<string> log)
        {
            _log = log;
        }

        public void Initialize(string pluginDir)
        {
            _configPath = Path.Combine(pluginDir, "config.json");
        }

        /// <summary>
        /// Load config file and apply all values to ExileCore settings nodes.
        /// Call during plugin init, after ExileCore has loaded its own defaults.
        /// If no config file exists, saves current defaults as the initial config.
        /// </summary>
        public void LoadAndApply(BotSettings settings)
        {
            if (!File.Exists(_configPath))
            {
                _log("No config.json found — saving current defaults");
                Save(settings);
                return;
            }

            try
            {
                var json = File.ReadAllText(_configPath);
                var config = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
                if (config == null) return;

                int applied = 0, skipped = 0;
                foreach (var (key, value) in config)
                {
                    // Handle non-node data stored with _ prefix
                    if (key == "_ultimatumModOverrides")
                    {
                        try
                        {
                            var overrides = JsonSerializer.Deserialize<Dictionary<string, int>>(value.GetRawText());
                            if (overrides != null)
                            {
                                settings.Mechanics.Ultimatum.ModRanking.DangerOverrides = overrides;
                                applied++;
                            }
                        }
                        catch { skipped++; }
                        continue;
                    }

                    var (success, _) = SettingsApi.Apply(settings, key, value);
                    if (success) applied++;
                    else skipped++;
                }

                _log($"Config loaded: {applied} settings applied, {skipped} skipped from {_configPath}");
            }
            catch (Exception ex)
            {
                _log($"Config load failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Save all current settings values to the config file.
        /// Reads current ExileCore node values via reflection.
        /// </summary>
        public void Save(BotSettings settings)
        {
            try
            {
                var flat = SettingsApi.SerializeFlat(settings);
                var config = new Dictionary<string, object?>();

                foreach (var (key, entry) in flat)
                {
                    if (entry.ReadOnly) continue; // skip hotkeys etc.
                    config[key] = entry.Value;
                }

                // Save non-node data that reflection can't reach
                // Ultimatum modifier danger overrides (Dictionary<string, int>)
                var overrides = settings.Mechanics?.Ultimatum?.ModRanking?.DangerOverrides;
                if (overrides != null && overrides.Count > 0)
                    config["_ultimatumModOverrides"] = overrides;

                var json = JsonSerializer.Serialize(config, WriteOpts);
                File.WriteAllText(_configPath, json);
            }
            catch (Exception ex)
            {
                _log($"Config save failed: {ex.Message}");
            }
        }
    }
}
