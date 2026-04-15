namespace AutoExile.Modes.WaveFarm.FarmPlans
{
    /// <summary>
    /// Simplest farm plan: clear map, loot everything, engage mechanics in-stride, exit.
    /// No deferred mechanics, no post-clear actions. Pure alch-and-go mapping.
    /// </summary>
    public class AlchAndGoPlan : IFarmPlan
    {
        public string Name => "Alch & Go";

        public IReadOnlySet<string> DeferredMechanics { get; } = new HashSet<string>();

        public IReadOnlyDictionary<string, string> MechanicModeOverrides { get; } =
            new Dictionary<string, string>();

        public PlanAtlasConfig AtlasConfig { get; } = new();

        // No altar preference — fall back to the built-in default weights.
        public IReadOnlyDictionary<string, int> AltarWeightDefaults { get; } =
            new Dictionary<string, int>();

        public WaveConfig Config { get; } = new()
        {
            BacktrackLootThreshold = 2.0,
            MaxLootClickAttempts = 2,
        };

        public bool ShouldEngageDeferredNow(BotContext ctx) => true; // no deferral

        public WaveAction? GetPostClearAction(BotContext ctx, DeferredMechanicLog deferred)
        {
            // Nothing to do post-clear — exit map
            return null;
        }

        public void Reset() { }
    }
}
