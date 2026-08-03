namespace AutoExile.Statistics
{
    public sealed class StatsSnapshot
    {
        public int SchemaVersion { get; init; } = 3;
        public long Revision { get; init; }
        public DateTime UpdatedAtUtc { get; init; } = DateTime.UtcNow;
        public StatsHealth Health { get; init; } = new();
        public StatsRunSnapshot? CurrentRun { get; init; }
        public StatsAggregate Session { get; init; } = new();
        public StatsAggregate Lifetime { get; init; } = new();
        public IReadOnlyDictionary<string, StatsAggregate> SessionByMode { get; init; }
            = new Dictionary<string, StatsAggregate>(StringComparer.OrdinalIgnoreCase);
        public IReadOnlyDictionary<string, StatsAggregate> LifetimeByMode { get; init; }
            = new Dictionary<string, StatsAggregate>(StringComparer.OrdinalIgnoreCase);
        public IReadOnlyList<StatsRunSnapshot> RecentRuns { get; init; } = Array.Empty<StatsRunSnapshot>();
        public IReadOnlyList<StatsLootSnapshot> RecentLoot { get; init; } = Array.Empty<StatsLootSnapshot>();
        public IReadOnlyList<StatsLootSnapshot> BestFinds { get; init; } = Array.Empty<StatsLootSnapshot>();
        public int BestFindsMinimumChaos { get; init; } = 100;
        public IReadOnlyList<StatsEventSnapshot> RecentEvents { get; init; } = Array.Empty<StatsEventSnapshot>();

        public StatsAggregate SessionFor(string mode) =>
            SessionByMode.TryGetValue(mode, out var value) ? value : new StatsAggregate { SessionId = Session.SessionId };

        public StatsAggregate LifetimeFor(string mode) =>
            LifetimeByMode.TryGetValue(mode, out var value) ? value : new StatsAggregate();
    }

    public sealed class StatsHealth
    {
        public string Status { get; init; } = "initializing";
        public string? Error { get; init; }
        public string DatabasePath { get; init; } = "";
        public int PendingWrites { get; init; }
    }

    public sealed class StatsAggregate
    {
        public string? Mode { get; init; }
        public string? SessionId { get; init; }
        public DateTime? StartedAtUtc { get; init; }
        public long ActiveDurationMs { get; init; }
        public int Attempts { get; init; }
        public int Consumed { get; init; }
        public int Entered { get; init; }
        public int FullClears { get; init; }
        public int PartialRuns { get; init; }
        public int FailedRuns { get; init; }
        public int NoProgressRuns { get; init; }
        public int NotEnteredRuns { get; init; }
        public int InterruptedRuns { get; init; }
        public int Entries { get; init; }
        public int Reentries { get; init; }
        public int Deaths { get; init; }
        public int WavesCompleted { get; init; }
        public int ItemsLooted { get; init; }
        public long ChaosValueMilli { get; init; }
        public long RunDurationMs { get; init; }
        public double AverageWavesPerEnteredRun { get; init; }
        public double AverageRunDurationMs { get; init; }
        public double ChaosValue => ChaosValueMilli / 1000d;
        public double ChaosPerHour => ActiveDurationMs > 0
            ? ChaosValue * 3_600_000d / ActiveDurationMs
            : 0d;
    }

    public sealed class StatsRunSnapshot
    {
        public string RunId { get; init; } = "";
        public string SessionId { get; init; } = "";
        public string Mode { get; init; } = "";
        public bool Consumed { get; init; }
        public string State { get; init; } = "";
        public string? Result { get; init; }
        public string? TerminalReason { get; init; }
        public string AreaName { get; init; } = "";
        public long? InstanceHash { get; init; }
        public DateTime ActivatedAtUtc { get; init; }
        public DateTime? FirstEntryAtUtc { get; init; }
        public DateTime? EndedAtUtc { get; init; }
        public int EntryCount { get; init; }
        public int DeathCount { get; init; }
        public int HighestWaveStarted { get; init; }
        public int WavesCompleted { get; init; }
        public int ItemsLooted { get; init; }
        public long ChaosValueMilli { get; init; }
        public long WallDurationMs { get; init; }
        public string DataQuality { get; init; } = "native";
        public double ChaosValue => ChaosValueMilli / 1000d;
    }

    public sealed class StatsLootSnapshot
    {
        public string LootId { get; init; } = "";
        public string? RunId { get; init; }
        public DateTime PickedAtUtc { get; init; }
        public string ItemName { get; init; } = "";
        public int Quantity { get; init; }
        public int Slots { get; init; }
        public long ChaosValueMilli { get; init; }
        public string AreaName { get; init; } = "";
        public string Mode { get; init; } = "Session";
        public double ChaosValue => ChaosValueMilli / 1000d;
    }

    public sealed class StatsEventSnapshot
    {
        public string EventId { get; init; } = "";
        public string? RunId { get; init; }
        public DateTime OccurredAtUtc { get; init; }
        public string EventType { get; init; } = "";
        public int? Wave { get; init; }
        public string? Details { get; init; }
    }
}
