using System.Numerics;

namespace AutoExile.Modes.WaveFarm.FarmPlans
{
    /// <summary>
    /// Stacked Deck farming strategy — Cloister scarab maps with ritual + mirage zones.
    ///
    /// Completion criteria:
    ///   - 80% exploration coverage
    ///   - 75% of seen monsters killed
    ///   - All required mechanics completed (Ritual, Wishes/mirage)
    ///
    /// Mechanic behavior:
    ///   - Ritual: deferred until 75% coverage, then engaged. Required.
    ///   - Wishes (mirage zone): engaged immediately when detected. Required.
    ///   - Eldritch altars: handled opportunistically (EldritchAltarHandler).
    ///   - Essence: skipped (frozen mobs waste time for a single rare).
    ///   - Shrines/chests: clicked during normal exploration.
    ///
    /// Map device: user-preferred map + 5x Scarab of the Cloister.
    /// </summary>
    public class StackedDeckPlan : IFarmPlan
    {
        public string Name => "Stacked Deck";

        // Defer Ritual until after most clearing — it interrupts exploration flow.
        // Wishes engages immediately (triggers sub-zone, best handled in-stride).
        public IReadOnlySet<string> DeferredMechanics { get; } = new HashSet<string>
        {
            "Ritual",
        };

        // Ritual and Wishes are required — must complete before exiting.
        // Essence is skipped — frozen mobs aren't worth the detour.
        public IReadOnlyDictionary<string, string> MechanicModeOverrides { get; } =
            new Dictionary<string, string>
            {
                { "Ritual", "Required" },
                { "Wishes", "Required" },
                { "Essence", "Skip" },
            };

        public PlanAtlasConfig AtlasConfig { get; } = new()
        {
            Scarabs = new[]
            {
                "Scarab of the Cloister",
                "Scarab of the Cloister",
                "Scarab of the Cloister",
                "Scarab of the Cloister",
                "Scarab of the Cloister",
            },
        };

        public WaveConfig Config { get; } = new()
        {
            MinCoverage = 0.80f,
            MinKillRatio = 0.75f,
            PauseDensity = 5,
            BacktrackLootThreshold = 5.0,
            MaxLootClickAttempts = 2,
        };

        public bool ShouldEngageDeferredNow(BotContext ctx)
        {
            // Start engaging deferred mechanics (Ritual) once 75% explored
            return ctx.Exploration.ActiveBlobCoverage >= 0.75f;
        }

        public WaveAction? GetPostClearAction(BotContext ctx, DeferredMechanicLog deferred)
        {
            // Check for unengaged deferred mechanics — navigate to them
            var playerPos = new Vector2(ctx.Game.Player.GridPosNum.X, ctx.Game.Player.GridPosNum.Y);
            var nextDeferred = deferred.GetNearestUnengaged(playerPos);
            if (nextDeferred.HasValue)
            {
                return WaveAction.EngageMechanic(nextDeferred.Value);
            }

            // All deferred mechanics handled (or none found) — check required mechanics
            if (!ctx.Mechanics.AllRequiredComplete(ctx.Settings.Mechanics))
            {
                // Required mechanic not yet complete — keep exploring to find it
                var target = ctx.Exploration.GetNextExplorationTarget(playerPos);
                if (target.HasValue)
                    return WaveAction.Explore(target.Value);
            }

            // Everything done — exit map
            return null;
        }

        public void Reset() { }
    }
}
