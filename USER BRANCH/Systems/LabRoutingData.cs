using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AutoExile.Systems
{
    /// <summary>
    /// Reads poelab.com daily lab layout JSON to determine optimal pathing.
    /// Finds the shortest path from current room to the next Aspirant's Trial.
    /// </summary>
    public class LabRoutingData
    {
        public class LabRoom
        {
            [JsonPropertyName("name")]
            public string Name { get; set; } = "";

            [JsonPropertyName("id")]
            public string Id { get; set; } = "";

            [JsonPropertyName("contents")]
            public List<string> Contents { get; set; } = new();

            [JsonPropertyName("exits")]
            public Dictionary<string, string> Exits { get; set; } = new();
        }

        public class LabLayout
        {
            [JsonPropertyName("difficulty")]
            public string Difficulty { get; set; } = "";

            [JsonPropertyName("date")]
            public string Date { get; set; } = "";

            [JsonPropertyName("rooms")]
            public List<LabRoom> Rooms { get; set; } = new();
        }

        private LabLayout? _layout;
        private Dictionary<string, LabRoom> _roomById = new();
        private Dictionary<string, List<LabRoom>> _roomsByName = new(StringComparer.OrdinalIgnoreCase);
        public bool IsLoaded => _layout != null;
        public string? Date => _layout?.Date;
        public string? Difficulty => _layout?.Difficulty;

        /// <summary>
        /// Load routing data from a JSON file. Returns true if loaded and date matches today.
        /// </summary>
        public bool Load(string filePath, Action<string>? log = null)
        {
            try
            {
                if (!File.Exists(filePath))
                {
                    log?.Invoke($"Lab routing file not found: {filePath}");
                    return false;
                }

                var json = File.ReadAllText(filePath);
                _layout = JsonSerializer.Deserialize<LabLayout>(json);
                if (_layout == null || _layout.Rooms.Count == 0)
                {
                    log?.Invoke("Lab routing: empty or invalid JSON");
                    return false;
                }

                // Check if date matches today
                var today = DateTime.Now.ToString("yyyy-MM-dd");
                if (_layout.Date != today)
                {
                    log?.Invoke($"Lab routing: date mismatch (file={_layout.Date}, today={today}) — ignoring");
                    _layout = null;
                    return false;
                }

                // Build lookup tables
                _roomById.Clear();
                _roomsByName.Clear();
                foreach (var room in _layout.Rooms)
                {
                    _roomById[room.Id] = room;
                    if (!_roomsByName.TryGetValue(room.Name, out var list))
                    {
                        list = new List<LabRoom>();
                        _roomsByName[room.Name] = list;
                    }
                    list.Add(room);
                }

                log?.Invoke($"Lab routing loaded: {_layout.Difficulty} {_layout.Date}, {_layout.Rooms.Count} rooms");
                return true;
            }
            catch (Exception ex)
            {
                log?.Invoke($"Lab routing error: {ex.Message}");
                _layout = null;
                return false;
            }
        }

        /// <summary>
        /// Given the current zone name and the number of Izaro encounters completed,
        /// find which exit zone name leads most directly to the next Aspirant's Trial.
        /// Returns the preferred destination zone name(s) in priority order, or empty if unknown.
        /// </summary>
        public List<string> GetPreferredExits(string currentZoneName, int izaroEncountersCompleted)
        {
            if (_layout == null) return new();

            // Find current room(s) matching this zone name
            if (!_roomsByName.TryGetValue(currentZoneName, out var currentRooms))
                return new();

            // We want to find the shortest path to the next Aspirant's Trial
            // Try each matching room and find which exits lead to trial fastest
            var preferredExits = new List<(string ZoneName, int Distance)>();

            foreach (var currentRoom in currentRooms)
            {
                foreach (var (direction, targetId) in currentRoom.Exits)
                {
                    if (!_roomById.TryGetValue(targetId, out var targetRoom)) continue;

                    // BFS from target room to find distance to next Aspirant's Trial
                    int dist = DistanceToTrial(targetId);
                    if (dist >= 0)
                    {
                        preferredExits.Add((targetRoom.Name, dist));
                    }
                }
            }

            // Sort by distance (shortest first) and return zone names
            return preferredExits
                .OrderBy(e => e.Distance)
                .Select(e => e.ZoneName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// BFS to find distance from a room to the nearest Aspirant's Trial.
        /// Returns 0 if the room IS a trial, -1 if no path found.
        /// </summary>
        private int DistanceToTrial(string startRoomId)
        {
            if (!_roomById.TryGetValue(startRoomId, out var startRoom)) return -1;

            // If this room is already a trial, distance is 0
            if (startRoom.Name.Equals("aspirant's trial", StringComparison.OrdinalIgnoreCase))
                return 0;

            // BFS
            var visited = new HashSet<string> { startRoomId };
            var queue = new Queue<(string RoomId, int Depth)>();
            queue.Enqueue((startRoomId, 0));

            while (queue.Count > 0)
            {
                var (roomId, depth) = queue.Dequeue();
                if (!_roomById.TryGetValue(roomId, out var room)) continue;

                foreach (var (_, nextId) in room.Exits)
                {
                    if (visited.Contains(nextId)) continue;
                    visited.Add(nextId);

                    if (_roomById.TryGetValue(nextId, out var nextRoom) &&
                        nextRoom.Name.Equals("aspirant's trial", StringComparison.OrdinalIgnoreCase))
                    {
                        return depth + 1;
                    }

                    queue.Enqueue((nextId, depth + 1));
                }
            }

            return -1; // no path to trial
        }

        /// <summary>
        /// Check if a destination zone name is on the preferred path.
        /// Used when exit transitions become visible and we can read their RenderName.
        /// </summary>
        public bool IsPreferredDestination(string currentZoneName, string destinationZoneName, int izaroEncounters)
        {
            var preferred = GetPreferredExits(currentZoneName, izaroEncounters);
            return preferred.Count > 0 &&
                   preferred[0].Equals(destinationZoneName, StringComparison.OrdinalIgnoreCase);
        }
    }
}
