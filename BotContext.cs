using ExileCore;
using AutoExile.Mechanics;
using AutoExile.Systems;

namespace AutoExile
{
    /// <summary>
    /// Passed to modes and systems each tick. Provides access to everything
    /// without coupling to the plugin class directly.
    /// </summary>
    public class BotContext
    {
        public required GameController Game { get; init; }
        public required NavigationSystem Navigation { get; init; }
        public required InteractionSystem Interaction { get; init; }
        public required TileMap TileMap { get; init; }
        public required CombatSystem Combat { get; init; }
        public required LootSystem Loot { get; init; }
        public required MapDeviceSystem MapDevice { get; init; }
        public required StashSystem Stash { get; init; }
        public required ExplorationMap Exploration { get; init; }
        public required LootTracker LootTracker { get; init; }
        public required MapMechanicManager Mechanics { get; init; }
        public required ThreatSystem Threat { get; init; }
        public required EldritchAltarHandler AltarHandler { get; init; }
        public required NinjaPriceService NinjaPrice { get; init; }
        public required EntityCache Entities { get; init; }
        public required ThreatMap ThreatMap { get; init; }
        public required MapDatabase MapDatabase { get; init; }
        public required BotSettings Settings { get; init; }

        /// <summary>
        /// Minimap icons discovered from TileEntities. Updated periodically by BotCore.
        /// Covers ~2x network bubble range — mechanics visible before entity list loads them.
        /// </summary>
        public IReadOnlyDictionary<long, BotCore.MinimapIconEntry> MinimapIcons { get; set; }
            = new Dictionary<long, BotCore.MinimapIconEntry>();

        /// <summary>
        /// Tile-based mechanic scan results. Computed at map load from terrain tile data.
        /// Covers entire map — instant mechanic detection before exploration.
        /// </summary>
        public TileScanResult? TileScan { get; set; }

        /// <summary>
        /// Graphics API for rendering overlays. Set during Render() calls.
        /// </summary>
        public ExileCore.Graphics? Graphics { get; set; }

        /// <summary>
        /// Elapsed seconds since last tick.
        /// </summary>
        public float DeltaTime { get; set; }

        /// <summary>
        /// Log a message to ExileCore's debug log.
        /// </summary>
        public Action<string> Log { get; set; } = _ => { };
    }
}
