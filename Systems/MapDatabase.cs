using System.Text.Json;
using System.Text.Json.Serialization;

namespace AutoExile.Systems
{
    /// <summary>
    /// Stores per-map metadata — boss tile signatures, support status.
    /// Persisted to Data/map_bosses.json. Populated by F8 tile scanner,
    /// consumed by WaveFarmMode for boss-finding navigation.
    /// </summary>
    public class MapDatabase
    {
        private string _filePath = "";
        private Dictionary<string, MapEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
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
            _filePath = Path.Combine(dataDir, "map_data.json");

            // Migration: load from old filename if new doesn't exist
            if (!File.Exists(_filePath))
            {
                var oldPath = Path.Combine(dataDir, "map_bosses.json");
                if (File.Exists(oldPath))
                {
                    File.Copy(oldPath, _filePath);
                    _log("MapDatabase: migrated map_bosses.json → map_data.json");
                }
            }

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
        public MapEntry? GetEntry(string mapName)
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
            if (!_entries.TryGetValue(mapName, out var entry))
                entry = new MapEntry();
            entry.BossTiles = tileKeys;
            entry.LastScanned = DateTime.UtcNow;
            _entries[mapName] = entry;
            Save();
            _log($"MapDatabase: saved {tileKeys.Count} boss tiles for '{mapName}'");
        }

        /// <summary>
        /// Save transition detail name for a map. Called by F8 scanner when a
        /// concentrated cluster of medium-rarity tiles is detected near the player.
        /// </summary>
        public void SaveTransitionDetailName(string mapName, string detailName)
        {
            if (!_entries.TryGetValue(mapName, out var entry))
                entry = new MapEntry();
            entry.TransitionDetailName = detailName;
            entry.LastScanned = DateTime.UtcNow;
            _entries[mapName] = entry;
            Save();
            _log($"MapDatabase: saved transition detail '{detailName}' for '{mapName}'");
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
                var data = JsonSerializer.Deserialize<Dictionary<string, MapEntry>>(json, JsonOpts);
                if (data != null)
                {
                    _entries = new Dictionary<string, MapEntry>(data, StringComparer.OrdinalIgnoreCase);
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

    public class MapEntry
    {
        public List<string>? BossTiles { get; set; }
        public DateTime? LastScanned { get; set; }
        /// <summary>
        /// Entity path substring to identify the boss monster (e.g., "BossMonsterName").
        /// When set, kill verification checks for dead unique monsters matching this path.
        /// When null, any unique monster death in the boss radius counts.
        /// </summary>
        public string? BossEntityPath { get; set; }

        /// <summary>
        /// Tile detail name used for area transition detection (e.g., "beachtownnorth" for Strand).
        /// Set by F8 scanner. TileScanner uses this to find transition clusters at map load.
        /// </summary>
        public string? TransitionDetailName { get; set; }
    }
}
