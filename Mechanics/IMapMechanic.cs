using System.Numerics;

namespace AutoExile.Mechanics
{
    /// <summary>
    /// How the user wants to handle a mechanic during mapping.
    /// </summary>
    public enum MechanicMode
    {
        Skip,       // Ignore entirely
        Optional,   // Complete if encountered during exploration
        Required,   // Must find and complete for map to be "done"
    }

    /// <summary>
    /// Result of a mechanic tick.
    /// </summary>
    public enum MechanicResult
    {
        Idle,        // Not started yet
        InProgress,  // Actively working on it
        Complete,    // Done, rewards collected
        Abandoned,   // Chose to stop (bad mods, wrong type, etc.)
        Failed,      // Died, timed out, encounter ended
    }

    /// <summary>
    /// Interface for in-map mechanics that MappingMode can detect and delegate to.
    /// Each mechanic handles its own detection, lifecycle, and UI interaction.
    /// MappingMode calls Detect() periodically, then Tick() when the mechanic is active.
    /// </summary>
    public interface IMapMechanic
    {
        /// <summary>Display name for overlay/logging.</summary>
        string Name { get; }

        /// <summary>Current status message for overlay display.</summary>
        string Status { get; }

        /// <summary>
        /// Scan nearby entities for this mechanic. Returns true if found and actionable
        /// (i.e., not already complete, not set to Skip, entity is within detection range).
        /// Called periodically by MapMechanicManager during exploration.
        /// </summary>
        bool Detect(BotContext ctx);

        /// <summary>
        /// Grid position of the mechanic's anchor point (altar, ritual circle, etc.).
        /// Used for navigation. Null if not yet detected.
        /// </summary>
        Vector2? AnchorGridPos { get; }

        /// <summary>
        /// True when the mechanic is in an active encounter phase (combat waves, etc.).
        /// During active encounter, MappingMode should not loot or explore.
        /// </summary>
        bool IsEncounterActive { get; }

        /// <summary>
        /// True when the mechanic has reached a terminal state (Complete/Abandoned/Failed).
        /// </summary>
        bool IsComplete { get; }

        /// <summary>
        /// Run one tick of the mechanic's logic. MappingMode delegates entirely when active.
        /// Handles navigation to anchor, starting encounter, wave management, reward collection.
        /// </summary>
        MechanicResult Tick(BotContext ctx);

        /// <summary>
        /// Render debug overlay for this mechanic.
        /// </summary>
        void Render(BotContext ctx) { }

        /// <summary>
        /// Reset all state. Called on area change or mode exit.
        /// </summary>
        void Reset();
    }
}
