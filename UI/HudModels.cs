using System.Globalization;

namespace AutoExile.UI
{
    /// <summary>Header row every mode shows: run state, mode name and runtime.</summary>
    public sealed class HudHeader
    {
        public bool Running { get; init; }
        public string Mode { get; init; } = "";
        public string Elapsed { get; init; } = "";
        /// <summary>Runtime limit as "h:mm", or null when there is no limit.</summary>
        public string? Limit { get; init; }
        /// <summary>Less than 10% of the runtime limit is left.</summary>
        public bool LimitLow { get; init; }
        /// <summary>Less than 5 minutes of the runtime limit is left.</summary>
        public bool LimitCritical { get; init; }
        /// <summary>Human recorder tick count while recording, otherwise null.</summary>
        public long? RecordingTicks { get; init; }
    }

    /// <summary>Everything the Simulacrum body of the HUD shows, already formatted.</summary>
    public sealed class SimulacrumHud
    {
        public int Wave { get; init; }
        public int WaveMax { get; init; } = 15;
        public bool WaveActive { get; init; }

        public string RunTime { get; init; } = "--";
        public int Deaths { get; init; }
        public int MaxDeaths { get; init; }
        /// <summary>Null when no run is open.</summary>
        public int? RunItems { get; init; }
        public string RunValue { get; init; } = "";
        public string RunValueUnit { get; init; } = "";

        public string Phase { get; init; } = "";
        public string Status { get; init; } = "";
        /// <summary>Amber warning rows, in display order.</summary>
        public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

        public int Monsters { get; init; }
        public int Ignored { get; init; }
        public string Decision { get; init; } = "";

        /// <summary>"SEASON" once the league is known, otherwise "SESSION".</summary>
        public string TotalsLabel { get; init; } = "SESSION";
        public string? League { get; init; }
        public string TotalsTime { get; init; } = "";
        public string Runs { get; init; } = "0";
        public string FullClears { get; init; } = "0";
        public string AvgWaves { get; init; } = "--";
        public string AvgRun { get; init; } = "--";
        public string Value { get; init; } = "0";
        public string ValueUnit { get; init; } = "";
        public string Rate { get; init; } = "0";
        public string RateUnit { get; init; } = "";
    }

    public static class HudFormat
    {
        public static string Number(double value, string format) => value.ToString(format, CultureInfo.InvariantCulture);

        /// <summary>"4m 12s", or "1h 02m 03s" from an hour up.</summary>
        public static string Duration(TimeSpan value)
        {
            if (value < TimeSpan.Zero) value = TimeSpan.Zero;
            return value.TotalHours >= 1
                ? $"{(int)value.TotalHours}h {value.Minutes:D2}m {value.Seconds:D2}s"
                : $"{(int)value.TotalMinutes}m {value.Seconds:D2}s";
        }

        /// <summary>"3h 40m", or "4m 12s" below an hour.</summary>
        public static string ShortDuration(TimeSpan value)
        {
            if (value < TimeSpan.Zero) value = TimeSpan.Zero;
            return value.TotalHours >= 1
                ? $"{(int)value.TotalHours}h {value.Minutes:D2}m"
                : $"{(int)value.TotalMinutes}m {value.Seconds:D2}s";
        }

        /// <summary>Chaos value as divines when the divine price is known, else chaos.</summary>
        public static (string Value, string Unit) Currency(double chaos, double chaosPerDivine, string suffix = "")
            => chaosPerDivine > 0
                ? (Number(chaos / chaosPerDivine, "F2"), " div" + suffix)
                : (Number(chaos, "F1"), "c" + suffix);
    }
}
