namespace AutoExile.Statistics
{
    public sealed class StatsSnapshot
    {
        public int SchemaVersion { get; init; } = 4;
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
        /// <summary>League of the current season; null until the game reports one.</summary>
        public string? CurrentLeague { get; init; }
        public StatsAggregate? Season { get; init; }
        public IReadOnlyDictionary<string, StatsAggregate> SeasonByMode { get; init; }
            = new Dictionary<string, StatsAggregate>(StringComparer.OrdinalIgnoreCase);

        public StatsAggregate SessionFor(string mode) =>
            SessionByMode.TryGetValue(mode, out var value) ? value : new StatsAggregate { SessionId = Session.SessionId };

        public StatsAggregate? SeasonFor(string mode) =>
            Season == null ? null : SeasonByMode.TryGetValue(mode, out var value) ? value : new StatsAggregate();

        public StatsAggregate LifetimeFor(string mode) =>
            LifetimeByMode.TryGetValue(mode, out var value) ? value : new StatsAggregate();
    }

    public sealed class StatsSeasonSummary
    {
        /// <summary>Null for records made before league tracking.</summary>
        public string? League { get; init; }
        public bool Current { get; init; }
        public DateTime? StartedAtUtc { get; init; }
        public DateTime? LastActivityAtUtc { get; init; }
        public int Runs { get; init; }
        public int FullClears { get; init; }
        public long ItemsLooted { get; init; }
        public double ChaosValue { get; init; }
    }

    public sealed class StatsSeasonDetail
    {
        public StatsSeasonSummary Season { get; init; } = new();
        public StatsAggregate Summary { get; init; } = new();
        public IReadOnlyDictionary<string, StatsAggregate> ByMode { get; init; }
            = new Dictionary<string, StatsAggregate>(StringComparer.OrdinalIgnoreCase);
        public IReadOnlyList<StatsRunSnapshot> Runs { get; init; } = Array.Empty<StatsRunSnapshot>();
        public IReadOnlyList<StatsLootSnapshot> BestFinds { get; init; } = Array.Empty<StatsLootSnapshot>();
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

    public sealed class StatsRunDetail
    {
        public StatsRunSnapshot Run { get; init; } = new();
        public IReadOnlyList<StatsRunWave> Waves { get; init; } = Array.Empty<StatsRunWave>();
        public StatsRunWaveSummary WaveSummary { get; init; } = new();
        public IReadOnlyList<StatsRunLoot> Loot { get; init; } = Array.Empty<StatsRunLoot>();
        public bool LootTruncated { get; init; }
        public StatsRunLootSummary LootSummary { get; init; } = new();
        public IReadOnlyList<StatsRunEvent> Events { get; init; } = Array.Empty<StatsRunEvent>();
    }

    public sealed class StatsRunWave
    {
        public int Wave { get; init; }
        public DateTime? StartedAtUtc { get; init; }
        public DateTime? CompletedAtUtc { get; init; }
        public long? DurationMs { get; init; }
        public long? GapBeforeMs { get; init; }
        public int LootCount { get; init; }
        public double ChaosValue { get; init; }
    }

    public sealed class StatsRunWaveSummary
    {
        public int Completed { get; init; }
        public double? AverageMs { get; init; }
        public double? MedianMs { get; init; }
        public int? FastestWave { get; init; }
        public long? FastestMs { get; init; }
        public int? SlowestWave { get; init; }
        public long? SlowestMs { get; init; }
        public long TotalWaveMs { get; init; }
        public long TotalGapMs { get; init; }
    }

    public sealed class StatsRunLoot
    {
        public string LootId { get; init; } = "";
        public DateTime PickedAtUtc { get; init; }
        public string ItemName { get; init; } = "";
        public int Quantity { get; init; }
        public double ChaosValue { get; init; }
        public int? Wave { get; init; }
    }

    public sealed class StatsRunLootSummary
    {
        public long Items { get; init; }
        public double ChaosValue { get; init; }
        public IReadOnlyList<StatsRunTopItem> TopItems { get; init; } = Array.Empty<StatsRunTopItem>();
    }

    public sealed class StatsRunTopItem
    {
        public string ItemName { get; init; } = "";
        public long Count { get; init; }
        public double ChaosValue { get; init; }
    }

    public sealed class StatsRunEvent
    {
        public DateTime OccurredAtUtc { get; init; }
        public string EventType { get; init; } = "";
        public int? Wave { get; init; }
        public string? Details { get; init; }
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

    public sealed class RuntimeEventReport
    {
        public DateTime GeneratedAtUtc { get; init; } = DateTime.UtcNow;
        public int WindowHours { get; init; }
        public long TotalEvents { get; init; }
        public DateTime? OldestEventUtc { get; init; }
        public DateTime? NewestEventUtc { get; init; }
        public RuntimeStorageSnapshot Storage { get; init; } = new();
        public IReadOnlyList<RuntimeEventBreakdown> Categories { get; init; } = Array.Empty<RuntimeEventBreakdown>();
        public IReadOnlyList<RuntimeEventBreakdown> Outcomes { get; init; } = Array.Empty<RuntimeEventBreakdown>();
        public IReadOnlyList<RuntimeEventSnapshot> Events { get; init; } = Array.Empty<RuntimeEventSnapshot>();
        public bool HasMore { get; init; }
        public long? NextBeforeSequence { get; init; }
        public string? Error { get; init; }
    }

    public sealed class RuntimeEventQuery
    {
        public int Limit { get; init; } = 100;
        public int WindowHours { get; init; } = 6;
        public string? Category { get; init; }
        public string? EventName { get; init; }
        public string? Outcome { get; init; }
        public long? BeforeSequence { get; init; }
        public long? SinceSequence { get; init; }
        public DateTimeOffset? FromUtc { get; init; }
        public DateTimeOffset? ToUtc { get; init; }
    }

    public sealed class RuntimeStorageSnapshot
    {
        public long DatabaseBytes { get; init; }
        public long WalBytes { get; init; }
        public long ShmBytes { get; init; }
        public int RetentionDays { get; init; }
        public int CriticalRetentionDays { get; init; }
        public int RetentionRows { get; init; }
        public int PendingWrites { get; init; }
        public long DroppedEvents { get; init; }
    }

    public sealed class RuntimeEventBreakdown
    {
        public string Name { get; init; } = "";
        public long Count { get; init; }
    }

    public sealed class RuntimeEventSnapshot
    {
        public long Sequence { get; init; }
        public DateTime OccurredAtUtc { get; init; }
        public long CommitLatencyMs { get; init; }
        public int ProducerThreadId { get; init; }
        public string? SessionId { get; init; }
        public string? RunId { get; init; }
        public string Category { get; init; } = "";
        public string EventName { get; init; } = "";
        public string Outcome { get; init; } = "";
        public string? Reason { get; init; }
        public string? ContextJson { get; init; }
    }
}
