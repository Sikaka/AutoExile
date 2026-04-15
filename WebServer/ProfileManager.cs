using System.Text.Json;

namespace AutoExile.WebServer
{
    /// <summary>
    /// Profile-based settings persistence.
    ///
    /// Layout:
    ///   &lt;plugin&gt;/meta.json          — { "activeProfile": "Default", "schemaVersion": N }
    ///   &lt;plugin&gt;/Profiles/*.json    — individual named profiles
    ///
    /// The web UI is the only valid editor of bot behavior. On every edit the active
    /// profile JSON is rewritten; switching profiles is explicit (SwitchProfile).
    ///
    /// Profiles are the single source of truth for bot behavior. ExileCore's own
    /// settings file is not used for persisting bot config — only infrastructure
    /// settings (WebUiEnabled, port, network access) live there.
    ///
    /// Schema versioning: each profile carries "_schemaVersion". On load, if the
    /// profile's version is older than <see cref="CurrentSchemaVersion"/>, the
    /// migrations in <see cref="_migrations"/> run in order to bring it up to date.
    /// Register new migrations by appending to that list whenever a rename/move
    /// ships.
    /// </summary>
    public class ProfileManager
    {
        /// <summary>
        /// Bump this whenever a backwards-incompatible rename/move ships, and add
        /// a matching migration to <see cref="_migrations"/>.
        ///
        ///   v1 — initial profile format
        ///   v2 — centralization: stash/run/mapRolling/mapDevice extracted from per-mode classes
        /// </summary>
        public const int CurrentSchemaVersion = 2;

        private const string DefaultProfileName = "Default";

        private string _pluginDir = "";
        private string _metaPath = "";
        private string _profilesDir = "";
        private readonly Action<string> _log;

        /// <summary>Name of the profile currently loaded in memory.</summary>
        public string ActiveProfileName { get; private set; } = DefaultProfileName;

        /// <summary>
        /// Fires after a successful profile switch (passes the new active name).
        /// Subscribers reset session-scoped state — e.g. the runtime tracker zeroes
        /// since a profile change is logically a new session.
        /// </summary>
        public event Action<string>? OnProfileSwitched;

        private static readonly JsonSerializerOptions WriteOpts = new() { WriteIndented = true };

        /// <summary>
        /// Registered schema migrations. Each runs when the profile being loaded
        /// has <c>_schemaVersion == From</c>; after running, the profile's version
        /// is bumped to <c>To</c>. Migrations execute in list order so chaining
        /// from very old profiles still works (v1 → v2 → v3 → ...).
        /// </summary>
        private static readonly List<(int From, int To, Action<Dictionary<string, JsonElement>> Migrate)> _migrations =
            new()
            {
                // v1 → v2: stash/run/mapRolling/mapDevice extracted from per-mode classes.
                // Old per-mode keys map to their new shared homes.
                (1, 2, cfg =>
                {
                    // Stash — moved from BossSettings to central StashSettings (already shipped at v1
                    // but kept here so any v0/v1 profile still gets the rename on load).
                    Rename(cfg, "boss.dumpTabName",       "stash.dumpTabName");
                    Rename(cfg, "boss.resourceTabName",   "stash.fragmentTabName");

                    // Run Control — MaxDeaths / StashItemThreshold / LootSweepTimeoutSeconds / PortalKey
                    // were duplicated across Boss/Simulacrum/Labyrinth/Farming.
                    Rename(cfg, "boss.maxDeaths",                 "run.maxDeaths");
                    Rename(cfg, "boss.lootSweepTimeoutSeconds",   "run.lootSweepTimeoutSeconds");
                    Rename(cfg, "boss.stashItemThreshold",        "run.stashItemThreshold");
                    Rename(cfg, "boss.portalKey",                 "run.portalKey");
                    Rename(cfg, "simulacrum.maxDeaths",           "run.maxDeaths");
                    Rename(cfg, "simulacrum.stashItemThreshold",  "run.stashItemThreshold");
                    Rename(cfg, "labyrinth.maxDeaths",            "run.maxDeaths");
                    Rename(cfg, "farming.portalKey",              "run.portalKey");

                    // Map Rolling — extracted from FarmingSettings.
                    Rename(cfg, "farming.minMapTier",        "mapRolling.minMapTier");
                    Rename(cfg, "farming.dangerousMapMods",  "mapRolling.dangerousMapMods");
                    Rename(cfg, "farming.minMapQuantity",    "mapRolling.minMapQuantity");

                    // Map Device — ScarabSlot1-5 → MapDevice.Slot1-5.
                    Rename(cfg, "farming.scarabSlot1", "mapDevice.slot1");
                    Rename(cfg, "farming.scarabSlot2", "mapDevice.slot2");
                    Rename(cfg, "farming.scarabSlot3", "mapDevice.slot3");
                    Rename(cfg, "farming.scarabSlot4", "mapDevice.slot4");
                    Rename(cfg, "farming.scarabSlot5", "mapDevice.slot5");
                }),
            };

        public ProfileManager(Action<string> log)
        {
            _log = log;
        }

        public void Initialize(string pluginDir)
        {
            _pluginDir = pluginDir;
            _metaPath = Path.Combine(pluginDir, "meta.json");
            _profilesDir = Path.Combine(pluginDir, "Profiles");
            Directory.CreateDirectory(_profilesDir);
        }

        // ================================================================
        // Lifecycle — startup + every-change save
        // ================================================================

        /// <summary>
        /// On plugin startup: read meta.json, load the active profile into the
        /// BotSettings tree. If no meta or no active profile exists, capture the
        /// current settings as a new "Default" profile and make it active.
        /// </summary>
        public void LoadActive(BotSettings settings)
        {
            var meta = ReadMeta();
            var name = meta.ActiveProfile;

            if (!string.IsNullOrWhiteSpace(name))
            {
                var path = PathFor(name);
                if (File.Exists(path))
                {
                    if (LoadProfileFile(settings, path, name))
                    {
                        ActiveProfileName = name;
                        return;
                    }
                }
                else
                {
                    _log($"Meta points to missing profile '{name}' — falling back to defaults");
                }
            }

            // First run (no meta) OR meta pointed at a deleted profile — seed a
            // Default profile from current BotSettings defaults.
            ActiveProfileName = DefaultProfileName;
            SaveActive(settings);
            _log($"Created initial profile: {DefaultProfileName}");
        }

        /// <summary>
        /// Write the current BotSettings to the active profile's file and refresh
        /// meta.json. Called after every successful web-UI edit.
        /// </summary>
        public void SaveActive(BotSettings settings)
        {
            try
            {
                WriteProfileFile(settings, ActiveProfileName);
                WriteMeta(new Meta
                {
                    ActiveProfile = ActiveProfileName,
                    SchemaVersion = CurrentSchemaVersion,
                });
            }
            catch (Exception ex)
            {
                _log($"Save active profile failed: {ex.Message}");
            }
        }

        // ================================================================
        // Profile management — list / switch / create / rename / delete
        // ================================================================

        public List<string> ListProfiles()
        {
            if (!Directory.Exists(_profilesDir)) return new List<string>();
            return Directory.GetFiles(_profilesDir, "*.json")
                .Select(f => Path.GetFileNameWithoutExtension(f))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public bool ProfileExists(string name)
            => !string.IsNullOrWhiteSpace(name) && File.Exists(PathFor(name));

        /// <summary>
        /// Save the in-memory settings to the currently-active profile, then load
        /// the named profile into settings and mark it active. No-op if the name
        /// matches the current active profile.
        /// </summary>
        public bool SwitchProfile(BotSettings settings, string name)
        {
            var clean = Sanitize(name);
            if (string.IsNullOrWhiteSpace(clean)) return false;
            if (clean.Equals(ActiveProfileName, StringComparison.OrdinalIgnoreCase)) return true;
            if (!ProfileExists(clean))
            {
                _log($"Cannot switch — profile not found: {clean}");
                return false;
            }

            // Flush in-flight edits to the outgoing profile first.
            SaveActive(settings);

            if (!LoadProfileFile(settings, PathFor(clean), clean)) return false;
            ActiveProfileName = clean;
            WriteMeta(new Meta { ActiveProfile = ActiveProfileName, SchemaVersion = CurrentSchemaVersion });
            _log($"Switched to profile: {clean}");
            OnProfileSwitched?.Invoke(clean);
            return true;
        }

        /// <summary>
        /// Create a new profile from the current BotSettings. If <paramref name="switchTo"/>
        /// is true, the new profile becomes active after creation.
        /// </summary>
        public bool CreateProfile(BotSettings settings, string name, bool switchTo)
        {
            var clean = Sanitize(name);
            if (string.IsNullOrWhiteSpace(clean)) return false;
            if (ProfileExists(clean))
            {
                _log($"Cannot create — profile already exists: {clean}");
                return false;
            }

            try
            {
                WriteProfileFile(settings, clean);
                if (switchTo)
                {
                    ActiveProfileName = clean;
                    WriteMeta(new Meta { ActiveProfile = ActiveProfileName, SchemaVersion = CurrentSchemaVersion });
                }
                _log($"Profile created: {clean}{(switchTo ? " (active)" : "")}");
                return true;
            }
            catch (Exception ex)
            {
                _log($"Create profile failed: {ex.Message}");
                return false;
            }
        }

        public bool DeleteProfile(string name)
        {
            var clean = Sanitize(name);
            if (string.IsNullOrWhiteSpace(clean)) return false;
            if (clean.Equals(ActiveProfileName, StringComparison.OrdinalIgnoreCase))
            {
                _log($"Refusing to delete active profile: {clean}");
                return false;
            }
            var path = PathFor(clean);
            if (!File.Exists(path)) return false;
            try
            {
                File.Delete(path);
                _log($"Profile deleted: {clean}");
                return true;
            }
            catch (Exception ex)
            {
                _log($"Delete profile failed: {ex.Message}");
                return false;
            }
        }

        public bool RenameProfile(string oldName, string newName)
        {
            var oldClean = Sanitize(oldName);
            var newClean = Sanitize(newName);
            if (string.IsNullOrWhiteSpace(oldClean) || string.IsNullOrWhiteSpace(newClean)) return false;
            if (!ProfileExists(oldClean)) return false;
            if (ProfileExists(newClean))
            {
                _log($"Cannot rename — target name already exists: {newClean}");
                return false;
            }
            try
            {
                File.Move(PathFor(oldClean), PathFor(newClean));
                if (oldClean.Equals(ActiveProfileName, StringComparison.OrdinalIgnoreCase))
                {
                    ActiveProfileName = newClean;
                    WriteMeta(new Meta { ActiveProfile = ActiveProfileName, SchemaVersion = CurrentSchemaVersion });
                }
                _log($"Profile renamed: {oldClean} → {newClean}");
                return true;
            }
            catch (Exception ex)
            {
                _log($"Rename profile failed: {ex.Message}");
                return false;
            }
        }

        // ================================================================
        // Import / export — raw JSON round-trip for sharing
        // ================================================================

        public string? ExportProfile(string name)
        {
            var path = PathFor(Sanitize(name));
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }

        public bool ImportProfile(string name, string json)
        {
            var clean = Sanitize(name);
            if (string.IsNullOrWhiteSpace(clean) || string.IsNullOrWhiteSpace(json)) return false;
            try
            {
                // Validate — must be a flat string→value dictionary.
                JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
                File.WriteAllText(PathFor(clean), json);
                _log($"Profile imported: {clean}");
                return true;
            }
            catch (Exception ex)
            {
                _log($"Import profile failed: {ex.Message}");
                return false;
            }
        }

        // ================================================================
        // Internals
        // ================================================================

        private string PathFor(string name) => Path.Combine(_profilesDir, Sanitize(name) + ".json");

        private static string Sanitize(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "";
            var invalid = Path.GetInvalidFileNameChars();
            return new string(name.Where(c => !invalid.Contains(c)).ToArray()).Trim();
        }

        private Meta ReadMeta()
        {
            if (!File.Exists(_metaPath)) return new Meta();
            try
            {
                var json = File.ReadAllText(_metaPath);
                return JsonSerializer.Deserialize<Meta>(json) ?? new Meta();
            }
            catch (Exception ex)
            {
                _log($"meta.json read failed: {ex.Message} — ignoring");
                return new Meta();
            }
        }

        private void WriteMeta(Meta meta)
        {
            var json = JsonSerializer.Serialize(meta, WriteOpts);
            File.WriteAllText(_metaPath, json);
        }

        private bool LoadProfileFile(BotSettings settings, string path, string name)
        {
            try
            {
                var json = File.ReadAllText(path);
                var cfg = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
                if (cfg == null) return false;

                var fromVersion = ReadSchemaVersion(cfg);
                if (fromVersion < CurrentSchemaVersion)
                {
                    foreach (var m in _migrations)
                    {
                        if (m.From >= fromVersion && m.To <= CurrentSchemaVersion && m.From == fromVersion)
                        {
                            m.Migrate(cfg);
                            fromVersion = m.To;
                        }
                    }
                    cfg["_schemaVersion"] = JsonSerializer.SerializeToElement(CurrentSchemaVersion);
                    // Persist the migrated form so we don't re-run migrations next load.
                    File.WriteAllText(path, JsonSerializer.Serialize(cfg, WriteOpts));
                    _log($"Profile '{name}' migrated to schema v{CurrentSchemaVersion}");
                }

                int applied = 0, skipped = 0;
                foreach (var (key, value) in cfg)
                {
                    if (key.StartsWith("_")) continue; // meta keys (schemaVersion, future extensions)

                    // Special-case: non-node data the flat serializer can't round-trip
                    if (key == "ultimatumModOverrides")
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
                    if (success) applied++; else skipped++;
                }

                _log($"Profile loaded: {name} ({applied} applied, {skipped} skipped)");
                return true;
            }
            catch (Exception ex)
            {
                _log($"Profile load failed ({name}): {ex.Message}");
                return false;
            }
        }

        private void WriteProfileFile(BotSettings settings, string name)
        {
            var flat = SettingsApi.SerializeFlat(settings);
            var cfg = new Dictionary<string, object?>
            {
                ["_schemaVersion"] = CurrentSchemaVersion,
            };

            foreach (var (key, entry) in flat)
            {
                if (entry.ReadOnly) continue;
                cfg[key] = entry.Value;
            }

            // Non-node data kept out of the reflection-based serializer
            var overrides = settings.Mechanics?.Ultimatum?.ModRanking?.DangerOverrides;
            if (overrides != null && overrides.Count > 0)
                cfg["ultimatumModOverrides"] = overrides;

            File.WriteAllText(PathFor(name), JsonSerializer.Serialize(cfg, WriteOpts));
        }

        private static int ReadSchemaVersion(Dictionary<string, JsonElement> cfg)
        {
            if (cfg.TryGetValue("_schemaVersion", out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n))
                return n;
            return 0; // Any profile without a version stamp is treated as pre-v1.
        }

        /// <summary>Helper for migrations: rename a key without touching its value.</summary>
        private static void Rename(Dictionary<string, JsonElement> cfg, string oldKey, string newKey)
        {
            if (cfg.TryGetValue(oldKey, out var val) && !cfg.ContainsKey(newKey))
            {
                cfg[newKey] = val;
                cfg.Remove(oldKey);
            }
        }

        // ================================================================
        // DTO
        // ================================================================

        private class Meta
        {
            public string ActiveProfile { get; set; } = "";
            public int SchemaVersion { get; set; } = CurrentSchemaVersion;
        }
    }
}
