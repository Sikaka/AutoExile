using AutoExile.Statistics;

namespace AutoExile.Systems
{
    /// <summary>
    /// Stores per-map metadata — boss tile signatures, support status.
    /// Persisted in the canonical SQLite store. Populated by F8 tile scanner,
    /// consumed by WaveFarmMode for boss-finding navigation.
    /// </summary>
    public class MapDatabase
    {
        private StatsService? _stats;
        private Dictionary<string, MapEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
        private readonly Action<string> _log;

        public MapDatabase(Action<string> log)
        {
            _log = log;
        }

        public void Initialize(StatsService stats)
        {
            _stats = stats;
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
            Save(mapName, entry);
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
            Save(mapName, entry);
            _log($"MapDatabase: saved transition detail '{detailName}' for '{mapName}'");
        }

        private void Load()
        {
            if (_stats == null)
            {
                _log("MapDatabase: SQLite store unavailable");
                return;
            }

            try
            {
                _entries.Clear();
                foreach (var item in _stats.ReadMapMetadata())
                {
                    _entries[item.MapName] = new MapEntry
                    {
                        BossTiles = item.BossTiles.ToList(),
                        LastScanned = item.LastScannedUtc,
                        BossEntityPath = item.BossEntityPath,
                        TransitionDetailName = item.TransitionDetailName,
                    };
                }
                _log($"MapDatabase: loaded {_entries.Count} map entries from SQLite ({SupportedMaps.Count()} supported)");
            }
            catch (Exception ex)
            {
                _log($"MapDatabase: load error: {ex.Message}");
            }
        }

        private void Save(string mapName, MapEntry entry)
        {
            if (_stats == null) return;
            _stats.UpsertMapMetadata(new MapMetadataSnapshot(mapName, entry.BossTiles ?? new List<string>(),
                entry.LastScanned, entry.BossEntityPath, entry.TransitionDetailName));
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
