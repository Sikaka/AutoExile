using AutoExile.UI;

namespace AutoExile.Modes
{
    public partial class SimulacrumMode
    {
        /// <summary>Snapshot of what the in-game panel shows for Simulacrum, formatted for display.</summary>
        public SimulacrumHud BuildHud(BotContext ctx)
        {
            var stats = ctx.Stats.Snapshot;
            var currentRun = stats.CurrentRun;
            var chaosPerDivine = ctx.NinjaPrice.ChaosPerDivine;

            var warnings = new List<string>();
            if (stats.Health.Status != "healthy") warnings.Add($"Stats {stats.Health.Status}");
            if (ctx.Interaction.IsBusy) warnings.Add($"Interact: {ctx.Interaction.Status}");

            // Totals follow the current league season once the game has reported it.
            var season = stats.SeasonFor(Name);
            var totals = season ?? stats.SessionFor(Name);
            var runValue = currentRun != null ? HudFormat.Currency(currentRun.ChaosValue, chaosPerDivine) : ("", "");
            var value = HudFormat.Currency(totals.ChaosValue, chaosPerDivine);
            var rate = HudFormat.Currency(totals.ChaosPerHour, chaosPerDivine, "/h");

            return new SimulacrumHud
            {
                Wave = _state.CurrentWave,
                WaveActive = _state.IsWaveActive,
                RunTime = currentRun != null ? HudFormat.Duration(DateTime.UtcNow - currentRun.ActivatedAtUtc) : "--",
                Deaths = _state.DeathCount,
                MaxDeaths = ctx.Settings.Run.MaxDeaths.Value,
                RunItems = currentRun?.ItemsLooted,
                RunValue = runValue.Item1,
                RunValueUnit = runValue.Item2,
                // Decision is never cleared, so it goes stale; StatusText tracks the live step.
                Phase = _phase.ToString(),
                Status = StatusText,
                Warnings = warnings,
                Monsters = ctx.Combat.NearbyMonsterCount,
                Ignored = ctx.Loot.FailedCount,
                Decision = Decision,
                TotalsLabel = season != null ? "SEASON" : "SESSION",
                League = season != null ? stats.CurrentLeague : null,
                TotalsTime = HudFormat.ShortDuration(TimeSpan.FromMilliseconds(totals.ActiveDurationMs)),
                Runs = totals.Attempts.ToString(),
                FullClears = totals.FullClears.ToString(),
                AvgWaves = totals.Entered > 0 ? HudFormat.Number(totals.AverageWavesPerEnteredRun, "F1") : "--",
                AvgRun = totals.Entered > 0 ? HudFormat.Duration(TimeSpan.FromMilliseconds(totals.AverageRunDurationMs)) : "--",
                Value = value.Value,
                ValueUnit = value.Unit,
                Rate = rate.Value,
                RateUnit = rate.Unit,
            };
        }
    }
}
