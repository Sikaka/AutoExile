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
    ///   - Ritual: engaged immediately on detection — ritual circle teleports nearby
    ///     un-killed monsters; deferring loses the bulk of the tribute pool.
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

        // Nothing deferred — ritual MUST engage immediately to maximize tribute.
        // The ritual circle teleports nearby monsters into it; if we wait until
        // most of the area is cleared the circle is half-empty and we lose out
        // on the bulk of the tribute pool. Wishes also engages in-stride (triggers
        // a sub-zone; best handled when the player is right next to the altar).
        public IReadOnlySet<string> DeferredMechanics { get; } = new HashSet<string>();

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
                "Divination Scarab of The Cloister",
                "Divination Scarab of The Cloister",
                "Divination Scarab of The Cloister",
                "Divination Scarab of The Cloister",
                "Divination Scarab of The Cloister",
            },
        };

        /// <summary>
        /// Altar weights tuned for Stacked Deck farming. The map is already saturated
        /// with divination cards from Cloister scarabs — so we boost mods that
        /// MULTIPLY card / currency / item drops and downweight mods that focus on
        /// scarab/unique drops (less relevant for this strategy).
        ///
        /// Keys are the normalized-letters form of the altar's display text (matches
        /// <see cref="Mechanics.EldritchAltarHandler.NormalizeLetters"/>). Only mods
        /// listed here override the engine defaults — everything else uses the built-in
        /// weight from <see cref="Mechanics.EldritchAltarHandler.DefaultModWeights"/>.
        /// </summary>
        public IReadOnlyDictionary<string, int> AltarWeightDefaults { get; } = BuildAltarWeights();

        private static IReadOnlyDictionary<string, int> BuildAltarWeights()
        {
            var d = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            void Add(string display, int w) => d[Mechanics.EldritchAltarHandler.NormalizeLetters(display)] = w;

            // Top priority — directly multiplies our intended drops
            Add("Divination Cards dropped by slain Enemies have #% chance to be Duplicated", 100);
            Add("Basic Currency Items dropped by slain Enemies have #% chance to be Duplicated", 95);
            Add("#% increased Quantity of Items found in this Area", 90);
            Add("#% chance to drop an additional Divination Card which rewards League Currency", 85);
            Add("Final Boss drops # additional Divination Cards which reward League Currency", 85);
            Add("#% chance to drop an additional Divination Card which rewards Currency", 80);
            Add("Final Boss drops # additional Divination Cards which reward Currency", 80);
            Add("#% chance to drop an additional Divine Orb", 80);
            Add("Final Boss drops # additional Divine Orbs", 80);
            Add("#% increased Rarity of Items found in this Area", 60);

            // Map sustain — useful for chained farming runs
            Add("Maps dropped by slain Enemies have #% chance to be Duplicated", 50);

            // De-emphasize scarab-focused mods (Cloister already gives us plenty of cards)
            Add("Scarabs dropped by slain Enemies have #% chance to be Duplicated", 20);
            Add("#% chance to drop an additional Divination Scarab", 15);

            return d;
        }

        /// <summary>
        /// Plan-critical drops that bypass the chaos-value filter. Substring match,
        /// case-insensitive. Stacked Decks are the headline target so they're never
        /// skipped. Heist Blueprints / Contracts drop from atlas-tree heist chests
        /// in maps and can be high-value even when individual chaos value reads low.
        /// </summary>
        public IReadOnlyList<string> MustLootItems { get; } = new[]
        {
            "Stacked Deck",
            "Blueprint",
            "Contract",
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
            // Nothing is deferred for this plan — ritual + wishes engage immediately.
            // Returning true means any future deferred-list entry would also engage
            // right away rather than waiting for a coverage gate.
            return true;
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

            // No deferred work pending — exit map.
            // Required mechanics (Wishes/Ritual) are enforced via engagement priority during
            // exploration, not as a blocking condition here. Trying to enforce them at exit
            // causes infinite loops when in sub-zones (mechanic state was reset on entry,
            // so AllRequiredComplete always returns false even though we ARE doing the wishes).
            return null;
        }

        public void Reset() { }
    }
}
