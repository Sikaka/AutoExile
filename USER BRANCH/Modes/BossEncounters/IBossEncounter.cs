using ExileCore;
using ExileCore.PoEMemory;

namespace AutoExile.Modes.BossEncounters
{
    public enum BossEncounterResult
    {
        Idle,
        InProgress,
        Complete,    // Boss killed
        Failed,      // Died, timed out, encounter broken
    }

    /// <summary>
    /// Interface for a boss encounter script. Each boss type implements this to handle
    /// zone navigation, phase management, and kill detection. BossMode handles the
    /// hideout loop, loot sweep, and exit — encounters focus only on the fight.
    /// </summary>
    public interface IBossEncounter
    {
        /// <summary>Display name shown in settings dropdown and overlay.</summary>
        string Name { get; }

        /// <summary>Current status for overlay.</summary>
        string Status { get; }

        /// <summary>Filter for MapDeviceSystem — identifies which fragment/map to insert.</summary>
        Func<Element, bool> MapFilter { get; }

        /// <summary>Fragment entity path substring for inventory fallback (right-click from inventory).
        /// Null = stash only.</summary>
        string? InventoryFragmentPath => null;

        /// <summary>Number of fragments consumed per map open. Used to stop when inventory is insufficient.</summary>
        int FragmentCost => 1;

        /// <summary>Item names to always pick up during this encounter, regardless of value.
        /// Substring match, case-insensitive. Empty = no overrides.</summary>
        IReadOnlyList<string> MustLootItems => Array.Empty<string>();

        /// <summary>Called when player enters the boss zone (area change, not hideout/town).</summary>
        void OnEnterZone(BotContext ctx);

        /// <summary>Per-tick encounter logic. Returns Complete when boss is dead.</summary>
        BossEncounterResult Tick(BotContext ctx);

        /// <summary>True to suppress ALL combat (no skills, no positioning). Use during pre-fight setup.</summary>
        bool SuppressCombat => false;

        /// <summary>True to suppress combat positioning (don't chase packs). Skills still fire.</summary>
        bool SuppressCombatPositioning => false;

        /// <summary>True to use flat-cost A* and relaxed smoothing (tight corridors like mazes).</summary>
        bool RelaxedPathing => false;

        /// <summary>Optional: render debug overlay.</summary>
        void Render(BotContext ctx) { }

        /// <summary>Reset all state for a new run.</summary>
        void Reset();
    }
}
