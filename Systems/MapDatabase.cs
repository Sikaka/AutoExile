using System.Text.Json;
using System.Text.Json.Serialization;

namespace AutoExile.Systems
{
    /// <summary>
    /// Stores per-map metadata — boss tile signatures, support status.
    /// Persisted to Data/map_bosses.json. Populated by F8 tile scanner,
    /// consumed by MappingMode for boss-finding navigation.
    /// </summary>
    public class MapDatabase
    {
        private string _filePath = "";
        private Dictionary<string, MapBossEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
        private readonly Action<string> _log;

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = true,
        };

        public MapDatabase(Action<string> log)
        {
            _log = log;
        }

        public void Initialize(string pluginDir)
        {
            var dataDir = Path.Combine(pluginDir, "Data");
            Directory.CreateDirectory(dataDir);
            _filePath = Path.Combine(dataDir, "map_bosses.json");
            Load();
        }

        /// <summary>
        /// Check if a map has boss tile data (is "supported").
        /// </summary>
        public bool IsSupported(string mapName)
        {
            return _entries.TryGetValue(mapName, out var entry)
                && entry.BossTiles != null
                && entry.BossTiles.Count > 0;
        }

        /// <summary>
        /// Get boss tile keys for a map, or null if not supported.
        /// </summary>
        public List<string>? GetBossTiles(string mapName)
        {
            return _entries.TryGetValue(mapName, out var entry) ? entry.BossTiles : null;
        }

        /// <summary>
        /// Get the full entry for a map, or null.
        /// </summary>
        public MapBossEntry? GetEntry(string mapName)
        {
            return _entries.TryGetValue(mapName, out var entry) ? entry : null;
        }

        /// <summary>
        /// All supported map names (have boss tile data).
        /// </summary>
        public IEnumerable<string> SupportedMaps =>
            _entries.Where(kv => kv.Value.BossTiles?.Count > 0).Select(kv => kv.Key);

        /// <summary>
        /// Save boss tile signatures for a map. Overwrites any existing entry.
        /// Called by F8 tile scanner when results are captured.
        /// </summary>
        public void SaveBossTiles(string mapName, List<string> tileKeys)
        {
            _entries[mapName] = new MapBossEntry
            {
                BossTiles = tileKeys,
                LastScanned = DateTime.UtcNow,
            };
            Save();
            _log($"MapDatabase: saved {tileKeys.Count} boss tiles for '{mapName}'");
        }

        private void Load()
        {
            if (!File.Exists(_filePath))
            {
                _log("MapDatabase: no existing data file, starting fresh");
                return;
            }

            try
            {
                var json = File.ReadAllText(_filePath);
                var data = JsonSerializer.Deserialize<Dictionary<string, MapBossEntry>>(json, JsonOpts);
                if (data != null)
                {
                    _entries = new Dictionary<string, MapBossEntry>(data, StringComparer.OrdinalIgnoreCase);
                    _log($"MapDatabase: loaded {_entries.Count} map entries ({SupportedMaps.Count()} supported)");
                }
            }
            catch (Exception ex)
            {
                _log($"MapDatabase: load error: {ex.Message}");
            }
        }

        private void Save()
        {
            try
            {
                var json = JsonSerializer.Serialize(_entries, JsonOpts);
                var path = _filePath;
                Task.Run(() =>
                {
                    try { File.WriteAllText(path, json); }
                    catch (Exception ex) { _log($"MapDatabase: save error: {ex.Message}"); }
                });
            }
            catch (Exception ex)
            {
                _log($"MapDatabase: serialize error: {ex.Message}");
            }
        }
    }

    public class MapBossEntry
    {
        public List<string>? BossTiles { get; set; }
        public DateTime? LastScanned { get; set; }
        /// <summary>
        /// Entity path substring to identify the boss monster (e.g., "BossMonsterName").
        /// When set, kill verification checks for dead unique monsters matching this path.
        /// When null, any unique monster death in the boss radius counts.
        /// </summary>
        public string? BossEntityPath { get; set; }
    }
}
