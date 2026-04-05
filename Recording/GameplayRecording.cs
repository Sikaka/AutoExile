using System.Numerics;

namespace AutoExile.Recording
{
    /// <summary>
    /// Complete recording of a human gameplay session.
    /// Contains per-tick snapshots of game state + player inputs.
    /// Used for both recording (in-game) and replay (offline console app).
    /// </summary>
    public class GameplayRecording
    {
        public string Version { get; set; } = "1.0";
        public DateTime RecordedAt { get; set; }
        public string AreaName { get; set; } = "";
        public long AreaHash { get; set; }
        public float DurationSeconds { get; set; }
        public int TickCount { get; set; }

        /// <summary>Per-tick snapshots, ordered by tick number.</summary>
        public List<RecordingTick> Ticks { get; set; } = new();

        /// <summary>
        /// Terrain snapshot taken once on area enter.
        /// Grid data for pathfinding/exploration replay.
        /// Null if not captured (e.g., recording started mid-map).
        /// </summary>
        public TerrainSnapshot? Terrain { get; set; }
    }

    /// <summary>
    /// One tick of recorded gameplay (~16ms at 60fps).
    /// Contains the game state BEFORE any decisions are made,
    /// plus the inputs the human actually sent this tick.
    /// </summary>
    public class RecordingTick
    {
        public int TickNumber { get; set; }
        public float DeltaTime { get; set; }

        // ── Player state ──
        public PlayerSnapshot Player { get; set; } = new();

        // ── Entities (monsters, interactables, mechanics) ──
        public List<RecordedEntity> Entities { get; set; } = new();

        // ── UI state ──
        public UISnapshot UI { get; set; } = new();

        // ── Ground item labels ──
        public List<GroundLabelSnapshot> GroundLabels { get; set; } = new();

        // ── Minimap icons ──
        public List<MinimapIconSnapshot> MinimapIcons { get; set; } = new();

        // ── Human inputs this tick ──
        public List<InputEvent> Inputs { get; set; } = new();
        public float CursorX { get; set; }
        public float CursorY { get; set; }

        // ── Area ──
        public string AreaName { get; set; } = "";
        public long AreaHash { get; set; }

        // ── Exploration ──
        public float ExplorationCoverage { get; set; }

        /// <summary>Per-region exploration state. Lightweight: center + counts only.</summary>
        public List<ExplorationRegionSnapshot> Regions { get; set; } = new();

        // ── Combat summary (cheap to capture) ──
        public int NearbyMonsterCount { get; set; }
        public bool InCombat { get; set; }
        public long BestTargetId { get; set; }

        // ── Directional density (monsters in 90° sectors relative to movement) ──
        /// <summary>Hostile alive monsters in the forward 90° arc of player movement.</summary>
        public int MonstersAhead { get; set; }
        /// <summary>Hostile alive monsters in the rear 90° arc.</summary>
        public int MonstersBehind { get; set; }
        /// <summary>Hostile alive monsters in the left/right 90° arcs combined.</summary>
        public int MonstersFlanking { get; set; }
    }

    public class PlayerSnapshot
    {
        public float GridX { get; set; }
        public float GridY { get; set; }
        public float HpPercent { get; set; }
        public float EsPercent { get; set; }
        public float ManaPercent { get; set; }
        public bool IsAlive { get; set; }

        // Animation state (for timing analysis)
        public string Animation { get; set; } = "";
        public float AnimationProgress { get; set; }

        // Active buffs (name only, for condition checking)
        public List<string> Buffs { get; set; } = new();
    }

    public class RecordedEntity
    {
        public long Id { get; set; }
        public string Path { get; set; } = "";
        public string RenderName { get; set; } = "";
        public string EntityType { get; set; } = ""; // EntityType enum name
        public float GridX { get; set; }
        public float GridY { get; set; }
        public float Distance { get; set; }
        public bool IsAlive { get; set; }
        public bool IsTargetable { get; set; }
        public bool IsHostile { get; set; }
        public string Rarity { get; set; } = ""; // MonsterRarity enum name
        public float HpPercent { get; set; }

        // StateMachine (only for interactables/mechanics)
        public Dictionary<string, long>? States { get; set; }

        // Buffs (only for special entities like proximity shield packs)
        public List<string>? Buffs { get; set; }
    }

    public class UISnapshot
    {
        public bool StashOpen { get; set; }
        public bool InventoryOpen { get; set; }
        public bool RitualWindowOpen { get; set; }
        public bool AtlasPanelOpen { get; set; }
        public bool WishesPanelOpen { get; set; }
        public bool MapDeviceOpen { get; set; }

        // Ritual-specific (when window is open)
        public int RitualTribute { get; set; }
        public int RitualRerolls { get; set; }
    }

    public class GroundLabelSnapshot
    {
        public long EntityId { get; set; }
        public string Text { get; set; } = "";
        public float RectX { get; set; }
        public float RectY { get; set; }
        public float RectW { get; set; }
        public float RectH { get; set; }
        public bool IsVisible { get; set; }

        /// <summary>Grid position of the item (for distance/direction analysis).</summary>
        public float GridX { get; set; }
        public float GridY { get; set; }

        /// <summary>Estimated chaos value from ninja prices. 0 if unknown/unpriced.</summary>
        public double ChaosValue { get; set; }

        /// <summary>Item rarity (Normal/Magic/Rare/Unique) for quick filtering.</summary>
        public string Rarity { get; set; } = "";
    }

    public class MinimapIconSnapshot
    {
        public long Id { get; set; }
        public string IconName { get; set; } = "";
        public string Path { get; set; } = "";
        public float GridX { get; set; }
        public float GridY { get; set; }
    }

    public class InputEvent
    {
        public float TimestampMs { get; set; } // ms since tick start
        public InputEventType Type { get; set; }
        public float? X { get; set; } // screen position (for clicks/cursor)
        public float? Y { get; set; }
        public int? Key { get; set; } // Keys enum cast to int
    }

    public enum InputEventType
    {
        KeyDown,
        KeyUp,
        LeftClick,
        RightClick,
        MouseMove,
    }

    /// <summary>
    /// Lightweight snapshot of one exploration region.
    /// Captures center + coverage, not individual cells.
    /// </summary>
    public class ExplorationRegionSnapshot
    {
        public int Index { get; set; }
        public float CenterX { get; set; }
        public float CenterY { get; set; }
        public int CellCount { get; set; }
        public int SeenCount { get; set; }
        /// <summary>SeenCount / CellCount — 0.0 to 1.0.</summary>
        public float ExploredRatio { get; set; }
    }

    /// <summary>
    /// One-time terrain snapshot, captured on area enter.
    /// Allows offline pathfinding and exploration simulation.
    /// </summary>
    public class TerrainSnapshot
    {
        public int Rows { get; set; }
        public int Cols { get; set; }

        /// <summary>Flattened pathfinding grid (row-major). 0=impassable, 1-5=weighted.</summary>
        public int[] PathfindingGrid { get; set; } = Array.Empty<int>();

        /// <summary>Flattened targeting grid (row-major). More permissive than pathfinding.</summary>
        public int[] TargetingGrid { get; set; } = Array.Empty<int>();
    }

    // ══════════════════════════════════════════════════════════════
    // Action classification types (used by both classifier and replay)
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// High-level action the player/bot was performing during a tick range.
    /// Abstracts away exact inputs into decision-level categories.
    /// </summary>
    public enum ActionType
    {
        Idle,
        Exploring,      // Moving through map, no combat
        Fighting,       // Engaged with monsters (skills firing)
        Looting,        // Clicking ground item labels
        Interacting,    // Clicking shrine/chest/interactable
        MechanicRitual, // Activating/fighting ritual altar
        MechanicWishes, // Wish encounter (guards, NPC, portal)
        MechanicOther,  // Other mechanics (harvest, ultimatum, etc.)
        RitualShop,     // Browsing/buying ritual rewards
        ExitingSubZone, // Clicking SekhemaPortal to return
        ExitingMap,     // Opening portal, clicking to leave
        InHideout,      // Stash, map device, etc.
        Dead,           // Player dead
    }

    /// <summary>
    /// A classified action spanning a range of ticks.
    /// </summary>
    public class ClassifiedAction
    {
        public int StartTick { get; set; }
        public int EndTick { get; set; }
        public ActionType Type { get; set; }
        public string Detail { get; set; } = ""; // e.g., "Chaos Orb" for LOOTING, "altar at (300,500)" for RITUAL

        public int Duration => EndTick - StartTick + 1;
    }
}
