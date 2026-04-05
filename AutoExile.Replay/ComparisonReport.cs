using AutoExile.Recording;

namespace AutoExile.Replay
{
    /// <summary>
    /// Compares human vs bot action timelines at the decision level.
    /// Produces a report showing agreement %, key disagreements, and metrics.
    /// </summary>
    public class ComparisonReport
    {
        public float AgreementPercent { get; set; }
        public int TotalTicks { get; set; }
        public int AgreeTicks { get; set; }
        public int DisagreeTicks { get; set; }
        public List<Disagreement> Disagreements { get; set; } = new();
        public Dictionary<ActionType, int> HumanActionCounts { get; set; } = new();
        public Dictionary<ActionType, int> BotActionCounts { get; set; } = new();

        public class Disagreement
        {
            public int Tick { get; set; }
            public ActionType HumanAction { get; set; }
            public ActionType BotAction { get; set; }
            public string HumanDetail { get; set; } = "";
            public string BotDetail { get; set; } = "";
        }

        /// <summary>
        /// Build a comparison from two classified timelines.
        /// Both timelines must cover the same tick range.
        /// </summary>
        public static ComparisonReport Compare(
            List<ClassifiedAction> humanTimeline,
            List<ClassifiedAction> botTimeline,
            int totalTicks)
        {
            var report = new ComparisonReport { TotalTicks = totalTicks };

            // Build per-tick lookups
            var humanPerTick = BuildTickLookup(humanTimeline);
            var botPerTick = BuildTickLookup(botTimeline);

            int agree = 0;
            int disagree = 0;
            ActionType? lastDisagreeHuman = null;

            for (int t = 0; t < totalTicks; t++)
            {
                var (hType, hDetail) = humanPerTick.TryGetValue(t, out var h)
                    ? (h.Type, h.Detail) : (ActionType.Idle, "");
                var (bType, bDetail) = botPerTick.TryGetValue(t, out var b)
                    ? (b.Type, b.Detail) : (ActionType.Idle, "");

                report.HumanActionCounts[hType] = report.HumanActionCounts.GetValueOrDefault(hType) + 1;
                report.BotActionCounts[bType] = report.BotActionCounts.GetValueOrDefault(bType) + 1;

                if (hType == bType)
                {
                    agree++;
                    lastDisagreeHuman = null;
                }
                else
                {
                    disagree++;
                    // Log first tick of each disagreement streak (avoid spamming)
                    if (hType != lastDisagreeHuman)
                    {
                        report.Disagreements.Add(new Disagreement
                        {
                            Tick = t,
                            HumanAction = hType,
                            BotAction = bType,
                            HumanDetail = hDetail,
                            BotDetail = bDetail,
                        });
                        lastDisagreeHuman = hType;
                    }
                }
            }

            report.AgreeTicks = agree;
            report.DisagreeTicks = disagree;
            report.AgreementPercent = totalTicks > 0 ? (float)agree / totalTicks * 100f : 0;

            return report;
        }

        /// <summary>Print a human-readable report to console.</summary>
        public string ToText()
        {
            var lines = new List<string>
            {
                "═══════════════════════════════════════════════",
                "  HUMAN vs BOT COMPARISON REPORT",
                "═══════════════════════════════════════════════",
                "",
                $"Total ticks: {TotalTicks}",
                $"Agreement:   {AgreementPercent:F1}% ({AgreeTicks} agree, {DisagreeTicks} disagree)",
                "",
                "── Action Distribution ──",
                $"{"Action",-20} {"Human",8} {"Bot",8} {"Delta",8}",
            };

            var allTypes = HumanActionCounts.Keys.Union(BotActionCounts.Keys).OrderBy(t => t);
            foreach (var type in allTypes)
            {
                var h = HumanActionCounts.GetValueOrDefault(type);
                var b = BotActionCounts.GetValueOrDefault(type);
                var delta = b - h;
                var deltaStr = delta > 0 ? $"+{delta}" : delta.ToString();
                lines.Add($"  {type,-20} {h,6} {b,6} {deltaStr,8}");
            }

            lines.Add("");
            lines.Add($"── Key Disagreements ({Disagreements.Count}) ──");
            foreach (var d in Disagreements.Take(30))
            {
                lines.Add($"  tick {d.Tick,5}: Human={d.HumanAction} ({d.HumanDetail}) | Bot={d.BotAction} ({d.BotDetail})");
            }
            if (Disagreements.Count > 30)
                lines.Add($"  ... and {Disagreements.Count - 30} more");

            return string.Join(Environment.NewLine, lines);
        }

        private static Dictionary<int, ClassifiedAction> BuildTickLookup(List<ClassifiedAction> timeline)
        {
            var lookup = new Dictionary<int, ClassifiedAction>();
            foreach (var action in timeline)
            {
                for (int t = action.StartTick; t <= action.EndTick; t++)
                    lookup[t] = action;
            }
            return lookup;
        }
    }
}
