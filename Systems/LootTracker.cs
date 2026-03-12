using ExileCore;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;
using System.Numerics;

namespace AutoExile.Systems
{
    /// <summary>
    /// Tracks looted items and their estimated chaos value over time.
    /// Records each pickup for total/per-hour statistics.
    /// Uses NinjaPrice bridge for valuations.
    /// </summary>
    public class LootTracker
    {
        private DateTime _sessionStart;
        private bool _sessionActive;
        private DateTime _pausedAt;
        private TimeSpan _pausedDuration;
        private double _totalChaosValue;
        private int _totalItemsLooted;
        private int _mapsCompleted;
        private Func<Entity, double>? _getNinjaValue;
        private bool _ninjaBridgeResolved;

        // Recent loot log (capped to prevent unbounded growth)
        private readonly List<LootRecord> _recentLoot = new();
        private const int MaxRecentLoot = 100;

        public double TotalChaosValue => _totalChaosValue;
        public int TotalItemsLooted => _totalItemsLooted;
        public int MapsCompleted => _mapsCompleted;
        public IReadOnlyList<LootRecord> RecentLoot => _recentLoot;

        public TimeSpan SessionDuration
        {
            get
            {
                if (_sessionStart == default) return TimeSpan.Zero;
                var total = (_sessionActive ? DateTime.Now : _pausedAt) - _sessionStart;
                return total - _pausedDuration;
            }
        }

        public double ChaosPerHour
        {
            get
            {
                var hours = SessionDuration.TotalHours;
                return hours > 0 ? _totalChaosValue / hours : 0;
            }
        }

        /// <summary>
        /// Start or resume the session. If already has data, resumes without clearing.
        /// Only resets on first start (no prior data).
        /// </summary>
        public void StartSession()
        {
            if (!_sessionActive)
            {
                if (_sessionStart == default)
                    _sessionStart = DateTime.Now;
                else
                    _pausedDuration += DateTime.Now - _pausedAt;
                _sessionActive = true;
            }
        }

        public void StopSession()
        {
            if (_sessionActive)
            {
                _pausedAt = DateTime.Now;
                _sessionActive = false;
            }
        }

        /// <summary>
        /// Fully reset all tracking data. Call explicitly when a fresh session is needed.
        /// </summary>
        public void ResetSession()
        {
            _sessionStart = DateTime.Now;
            _sessionActive = false;
            _pausedDuration = TimeSpan.Zero;
            _pausedAt = DateTime.Now;
            _totalChaosValue = 0;
            _totalItemsLooted = 0;
            _mapsCompleted = 0;
            _recentLoot.Clear();
        }

        public bool IsActive => _sessionActive;

        /// <summary>
        /// Record that a map was completed.
        /// </summary>
        public void RecordMapComplete()
        {
            _mapsCompleted++;
        }

        /// <summary>
        /// Record a looted item. Call when an item is confirmed picked up.
        /// </summary>
        public void RecordItem(GameController gc, Entity itemEntity, string itemName)
        {
            ResolveNinjaBridge(gc);

            var chaosValue = 0.0;
            if (_getNinjaValue != null && itemEntity != null)
            {
                try { chaosValue = _getNinjaValue(itemEntity); }
                catch { }
            }

            _totalChaosValue += chaosValue;
            _totalItemsLooted++;

            _recentLoot.Add(new LootRecord
            {
                ItemName = itemName,
                ChaosValue = chaosValue,
                Time = DateTime.Now,
            });

            // Cap recent log
            if (_recentLoot.Count > MaxRecentLoot)
                _recentLoot.RemoveAt(0);
        }

        /// <summary>
        /// Record a looted item by chaos value directly (when entity isn't available).
        /// </summary>
        public void RecordItem(string itemName, double chaosValue)
        {
            _totalChaosValue += chaosValue;
            _totalItemsLooted++;

            _recentLoot.Add(new LootRecord
            {
                ItemName = itemName,
                ChaosValue = chaosValue,
                Time = DateTime.Now,
            });

            if (_recentLoot.Count > MaxRecentLoot)
                _recentLoot.RemoveAt(0);
        }

        private void ResolveNinjaBridge(GameController gc)
        {
            if (_ninjaBridgeResolved) return;
            try
            {
                _getNinjaValue = gc.PluginBridge.GetMethod<Func<Entity, double>>("NinjaPrice.GetValue");
            }
            catch { }
            _ninjaBridgeResolved = true;
        }

        /// <summary>
        /// Render the loot tracker overlay. Call from BotCore.Render().
        /// </summary>
        public void Render(ExileCore.Graphics graphics, Vector2 position)
        {
            if (_sessionStart == default) return; // never started

            var duration = SessionDuration;
            var timeStr = duration.TotalHours >= 1
                ? $"{(int)duration.TotalHours}h {duration.Minutes:D2}m"
                : $"{duration.Minutes}m {duration.Seconds:D2}s";

            var lineH = 18f;
            var y = position.Y;
            var x = position.X;

            var headerColor = SharpDX.Color.Gold;
            var textColor = SharpDX.Color.White;
            var valueColor = SharpDX.Color.LimeGreen;

            graphics.DrawText("Loot Tracker", new Vector2(x, y), headerColor);
            y += lineH;

            graphics.DrawText($"Session: {timeStr}", new Vector2(x, y), textColor);
            y += lineH;

            graphics.DrawText($"Maps: {_mapsCompleted}", new Vector2(x, y), textColor);
            y += lineH;

            graphics.DrawText($"Items: {_totalItemsLooted}", new Vector2(x, y), textColor);
            y += lineH;

            graphics.DrawText($"Total: {_totalChaosValue:F1}c", new Vector2(x, y), valueColor);
            y += lineH;

            graphics.DrawText($"Per Hour: {ChaosPerHour:F1}c/h", new Vector2(x, y), valueColor);
            y += lineH;

            // Show last few pickups
            if (_recentLoot.Count > 0)
            {
                y += 4f;
                graphics.DrawText("Recent:", new Vector2(x, y), headerColor);
                y += lineH;

                var startIdx = Math.Max(0, _recentLoot.Count - 5);
                for (int i = _recentLoot.Count - 1; i >= startIdx; i--)
                {
                    var record = _recentLoot[i];
                    var valStr = record.ChaosValue > 0 ? $" ({record.ChaosValue:F1}c)" : "";
                    var name = record.ItemName.Length > 30
                        ? record.ItemName[..30] + "..."
                        : record.ItemName;
                    graphics.DrawText($"  {name}{valStr}", new Vector2(x, y), textColor);
                    y += lineH;
                }
            }
        }
    }

    public class LootRecord
    {
        public string ItemName { get; init; } = "";
        public double ChaosValue { get; init; }
        public DateTime Time { get; init; }
    }
}
