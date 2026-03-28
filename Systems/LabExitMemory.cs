using System.IO;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AutoExile.Systems
{
    /// <summary>
    /// Persists angle-based exit mappings for labyrinth zones.
    /// Remembers which angular direction from entry leads to which destination,
    /// so future runs on the same day can skip scouting.
    /// </summary>
    public class LabExitMemory
    {
        public class ExitAngleMapping
        {
            [JsonPropertyName("angle")]
            public float AngleDegrees { get; set; }

            [JsonPropertyName("dest")]
            public string DestinationName { get; set; } = "";
        }

        public class ExitMemoryEntry
        {
            [JsonPropertyName("zone")]
            public string ZoneName { get; set; } = "";

            [JsonPropertyName("exitCount")]
            public int ExitCount { get; set; }

            [JsonPropertyName("mappings")]
            public List<ExitAngleMapping> Mappings { get; set; } = new();
        }

        public class ExitMemoryFile
        {
            [JsonPropertyName("date")]
            public string Date { get; set; } = "";

            [JsonPropertyName("entries")]
            public List<ExitMemoryEntry> Entries { get; set; } = new();
        }

        private ExitMemoryFile _data = new();
        private bool _dirty;
        private const float DefaultTolerance = 20f; // degrees

        /// <summary>
        /// Load from JSON file. Discards entries not matching today's date.
        /// </summary>
        public void Load(string filePath, Action<string>? log = null)
        {
            var today = DateTime.Now.ToString("yyyy-MM-dd");
            try
            {
                if (!File.Exists(filePath))
                {
                    _data = new ExitMemoryFile { Date = today };
                    return;
                }

                var json = File.ReadAllText(filePath);
                var parsed = JsonSerializer.Deserialize<ExitMemoryFile>(json);
                if (parsed != null && parsed.Date == today)
                {
                    _data = parsed;
                    log?.Invoke($"Exit memory loaded: {_data.Entries.Count} zones for {today}");
                }
                else
                {
                    _data = new ExitMemoryFile { Date = today };
                    log?.Invoke($"Exit memory: stale date or empty, starting fresh for {today}");
                }
            }
            catch (Exception ex)
            {
                log?.Invoke($"Exit memory load error: {ex.Message}");
                _data = new ExitMemoryFile { Date = today };
            }
        }

        /// <summary>
        /// Save to JSON file (only today's entries).
        /// </summary>
        public void Save(string filePath, Action<string>? log = null)
        {
            if (!_dirty) return;
            try
            {
                var options = new JsonSerializerOptions { WriteIndented = true };
                var json = JsonSerializer.Serialize(_data, options);
                File.WriteAllText(filePath, json);
                _dirty = false;
                log?.Invoke($"Exit memory saved: {_data.Entries.Count} zones");
            }
            catch (Exception ex)
            {
                log?.Invoke($"Exit memory save error: {ex.Message}");
            }
        }

        /// <summary>
        /// Find the remembered angle for a preferred destination in a given zone.
        /// Returns the angle in degrees, or null if no match.
        /// </summary>
        public float? FindPreferredAngle(string zoneName, int exitCount, string preferredDestination)
        {
            var entry = FindEntry(zoneName, exitCount);
            if (entry == null) return null;

            foreach (var mapping in entry.Mappings)
            {
                if (mapping.DestinationName.Equals(preferredDestination, StringComparison.OrdinalIgnoreCase))
                    return mapping.AngleDegrees;
            }
            return null;
        }

        /// <summary>
        /// Record a discovered exit mapping. Upserts — overwrites if a similar angle already exists.
        /// Returns true if this was a new or changed record, false if duplicate.
        /// </summary>
        public bool Record(string zoneName, int exitCount, float angleDegrees, string destinationName)
        {
            var entry = FindEntry(zoneName, exitCount);
            if (entry == null)
            {
                entry = new ExitMemoryEntry
                {
                    ZoneName = zoneName,
                    ExitCount = exitCount,
                };
                _data.Entries.Add(entry);
            }

            // Check if we already have a mapping at a similar angle — update it
            foreach (var mapping in entry.Mappings)
            {
                if (AngleDifference(mapping.AngleDegrees, angleDegrees) < DefaultTolerance)
                {
                    if (mapping.DestinationName.Equals(destinationName, StringComparison.OrdinalIgnoreCase))
                        return false; // duplicate — same angle, same destination
                    mapping.AngleDegrees = angleDegrees;
                    mapping.DestinationName = destinationName;
                    _dirty = true;
                    return true;
                }
            }

            // New mapping
            entry.Mappings.Add(new ExitAngleMapping
            {
                AngleDegrees = angleDegrees,
                DestinationName = destinationName,
            });
            _dirty = true;
            return true;
        }

        public bool IsDirty => _dirty;

        private ExitMemoryEntry? FindEntry(string zoneName, int exitCount)
        {
            foreach (var entry in _data.Entries)
            {
                if (entry.ExitCount == exitCount &&
                    entry.ZoneName.Equals(zoneName, StringComparison.OrdinalIgnoreCase))
                    return entry;
            }
            return null;
        }

        /// <summary>
        /// Compute angle from entry position to exit position in degrees (-180 to 180).
        /// </summary>
        public static float ComputeAngle(Vector2 entryPos, Vector2 exitPos)
        {
            var dx = exitPos.X - entryPos.X;
            var dy = exitPos.Y - entryPos.Y;
            return (float)(Math.Atan2(dy, dx) * 180.0 / Math.PI);
        }

        /// <summary>
        /// Shortest angular distance between two angles in degrees (0 to 180).
        /// </summary>
        public static float AngleDifference(float a, float b)
        {
            var diff = Math.Abs(a - b) % 360f;
            return diff > 180f ? 360f - diff : diff;
        }
    }
}
