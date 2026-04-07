using System.Numerics;

namespace AutoExile.Modes.WaveFarm
{
    /// <summary>
    /// A farm plan configures how the wave farming mode behaves.
    /// Plans are declarative — they configure behavior and define post-clear actions,
    /// they don't tick during clearing. The wave loop handles movement + combat + loot.
    /// </summary>
    public interface IFarmPlan
    {
        string Name { get; }

        /// <summary>Mechanic names to always defer (engage after clearing, not in-stride).</summary>
        IReadOnlySet<string> DeferredMechanics { get; }

        /// <summary>Mechanic mode overrides (e.g., "Ritual" → "Required").</summary>
        IReadOnlyDictionary<string, string> MechanicModeOverrides { get; }

        /// <summary>Atlas setup: scarabs, witness type, atlas tree preset.</summary>
        PlanAtlasConfig AtlasConfig { get; }

        /// <summary>Wave behavior tuning for this plan.</summary>
        WaveConfig Config { get; }

        /// <summary>
        /// Should we start engaging deferred mechanics now?
        /// Called each tick during clearing. Typical: "coverage > 60%".
        /// </summary>
        bool ShouldEngageDeferredNow(BotContext ctx);

        /// <summary>
        /// After map is fully cleared, what's next? Returns an action to perform
        /// (navigate to deferred mechanic, ritual shop, etc.).
        /// Returns null when plan is complete → exit map.
        /// </summary>
        WaveAction? GetPostClearAction(BotContext ctx, DeferredMechanicLog deferred);

        /// <summary>Reset for new run.</summary>
        void Reset();
    }

    /// <summary>Atlas configuration applied when entering a map.</summary>
    public class PlanAtlasConfig
    {
        public IReadOnlyList<string> Scarabs { get; init; } = Array.Empty<string>();
        public string WitnessType { get; init; } = "None";
        public int TreePreset { get; init; } = 0;
    }

    /// <summary>Wave behavior tuning. Plans set this to control movement + loot behavior.</summary>
    public class WaveConfig
    {
        /// <summary>Minimum monster density to slow down for combat. 0 = never pause.</summary>
        public int PauseDensity { get; set; } = 0;

        /// <summary>Suppress combat repositioning (true for most builds — explore target IS positioning).</summary>
        public bool SuppressCombatPositioning { get; set; } = true;

        /// <summary>Chaos value threshold to justify backtracking for loot behind the player.</summary>
        public double BacktrackLootThreshold { get; set; } = 50.0;

        /// <summary>Dot product threshold for "ahead" check. 0 = forward hemisphere, 0.5 = ~60° cone.</summary>
        public float ForwardAngle { get; set; } = 0f;

        /// <summary>Max click attempts for loot pickup before marking failed and moving on.</summary>
        public int MaxLootClickAttempts { get; set; } = 2;

        /// <summary>
        /// Minimum exploration coverage to consider map "cleared enough" for post-clear actions.
        /// 0 = use BotSettings.Farming.MinCoverage (user override). Values > 0 override the setting.
        /// </summary>
        public float MinCoverage { get; set; } = 0f;

        /// <summary>
        /// Minimum kill ratio (TotalDead / TotalTracked) before considering the map done.
        /// 0 = no kill threshold. Combined with MinCoverage: both must be met.
        /// </summary>
        public float MinKillRatio { get; set; } = 0f;
    }

    /// <summary>
    /// An action the wave tick loop should execute.
    /// Tagged union: check Type to determine which fields are populated.
    /// </summary>
    public struct WaveAction
    {
        public WaveActionType Type;
        public Vector2 TargetGridPos;          // for Explore, NavigateToMechanic
        public long TargetEntityId;            // for PickupLoot, Interact
        public DeferredMechanicLog.DeferredEntry? MechanicEntry; // for EngageMechanic

        public static WaveAction Explore(Vector2 target) =>
            new() { Type = WaveActionType.Explore, TargetGridPos = target };

        public static WaveAction PickupLoot(long entityId, Vector2 pos) =>
            new() { Type = WaveActionType.PickupLoot, TargetEntityId = entityId, TargetGridPos = pos };

        public static WaveAction Interact(long entityId, Vector2 pos) =>
            new() { Type = WaveActionType.Interact, TargetEntityId = entityId, TargetGridPos = pos };

        public static WaveAction EngageMechanic(DeferredMechanicLog.DeferredEntry entry) =>
            new() { Type = WaveActionType.EngageMechanic, MechanicEntry = entry, TargetGridPos = entry.GridPos };

        public static WaveAction NavigateToMechanic(Vector2 pos) =>
            new() { Type = WaveActionType.NavigateToMechanic, TargetGridPos = pos };

        public static readonly WaveAction ExitMap = new() { Type = WaveActionType.ExitMap };
        public static readonly WaveAction None = new() { Type = WaveActionType.None };
    }

    public enum WaveActionType
    {
        None,
        Explore,
        PickupLoot,
        Interact,
        EngageMechanic,
        NavigateToMechanic,
        ExitMap,
    }
}
