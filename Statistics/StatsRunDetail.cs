using Microsoft.Data.Sqlite;

namespace AutoExile.Statistics
{
    public sealed partial class StatsService
    {
        private const int RunDetailLootCap = 2000;

        /// <summary>Per-run drill-down: waves, loot and events. Returns null when the run does not exist.</summary>
        public StatsRunDetail? ReadRunDetail(string runId)
        {
            using var db = OpenReadConnection();
            var run = ReadRuns(db, "WHERE r.run_id=$runId", 1, runId).FirstOrDefault();
            if (run == null) return null;

            var events = new List<(long At, string Type, int? Wave, string? Details)>();
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = @"SELECT occurred_at_ms,event_type,wave,details FROM stats_events
WHERE run_id=$run ORDER BY occurred_at_ms,rowid";
                cmd.Parameters.AddWithValue("$run", runId);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    events.Add((r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetInt32(2),
                        r.IsDBNull(3) ? null : r.GetString(3)));
            }

            var loot = new List<(string Id, long At, string Name, int Quantity, long Milli)>();
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = @"SELECT loot_id,picked_at_ms,item_name,quantity,chaos_value_milli FROM loot_events
WHERE run_id=$run ORDER BY picked_at_ms,loot_id";
                cmd.Parameters.AddWithValue("$run", runId);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    loot.Add((r.GetString(0), r.GetInt64(1), r.GetString(2), r.GetInt32(3), r.GetInt64(4)));
            }

            long itemsTotal, milliTotal;
            var top = new List<StatsRunTopItem>();
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = @"SELECT COALESCE(SUM(quantity),0),COALESCE(SUM(chaos_value_milli),0)
FROM loot_events WHERE run_id=$run";
                cmd.Parameters.AddWithValue("$run", runId);
                using var r = cmd.ExecuteReader();
                r.Read();
                itemsTotal = r.GetInt64(0);
                milliTotal = r.GetInt64(1);
            }
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = @"SELECT item_name,SUM(quantity),SUM(chaos_value_milli) FROM loot_events
WHERE run_id=$run GROUP BY item_name ORDER BY SUM(chaos_value_milli) DESC,item_name LIMIT 12";
                cmd.Parameters.AddWithValue("$run", runId);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    top.Add(new StatsRunTopItem { ItemName = r.GetString(0), Count = r.GetInt64(1), ChaosValue = r.GetInt64(2) / 1000d });
            }

            // Waves (Simulacrum only).
            var starts = new Dictionary<int, long>();
            var completes = new Dictionary<int, long>();
            var maxWave = run.HighestWaveStarted;
            foreach (var e in events)
            {
                // Only wave events carry a wave index; run_ended stores waves_completed there.
                if (!IsWaveEvent(e.Type) || e.Wave is not (>= 1 and <= 15)) continue;
                maxWave = Math.Max(maxWave, e.Wave.Value);
                if (e.Type == "wave_started") starts.TryAdd(e.Wave.Value, e.At);
                else if (e.Type == "wave_completed") completes.TryAdd(e.Wave.Value, e.At);
            }
            var isSimulacrum = string.Equals(run.Mode, "Simulacrum", StringComparison.OrdinalIgnoreCase);
            var waveCount = isSimulacrum ? Math.Clamp(maxWave, 0, 15) : 0;

            int? WaveFor(long at)
            {
                int? best = null;
                foreach (var (wave, start) in starts)
                    if (wave <= waveCount && start <= at && (best == null || wave > best)) best = wave;
                return best;
            }

            var lootCountByWave = new Dictionary<int, int>();
            var lootMilliByWave = new Dictionary<int, long>();
            var lootRows = new List<StatsRunLoot>();
            foreach (var l in loot)
            {
                var wave = isSimulacrum ? WaveFor(l.At) : null;
                if (wave.HasValue)
                {
                    lootCountByWave[wave.Value] = lootCountByWave.GetValueOrDefault(wave.Value) + l.Quantity;
                    lootMilliByWave[wave.Value] = lootMilliByWave.GetValueOrDefault(wave.Value) + l.Milli;
                }
                if (lootRows.Count < RunDetailLootCap)
                    lootRows.Add(new StatsRunLoot
                    {
                        LootId = l.Id, PickedAtUtc = FromMs(l.At), ItemName = l.Name, Quantity = l.Quantity,
                        ChaosValue = l.Milli / 1000d, Wave = wave,
                    });
            }

            var waves = new List<StatsRunWave>();
            var durations = new List<(int Wave, long Ms)>();
            long totalGap = 0;
            for (var wave = 1; wave <= waveCount; wave++)
            {
                long? start = starts.TryGetValue(wave, out var s) ? s : null;
                long? done = completes.TryGetValue(wave, out var c) ? c : null;
                long? duration = start.HasValue && done.HasValue && done.Value >= start.Value ? done.Value - start.Value : null;
                long? gap = null;
                if (start.HasValue && completes.TryGetValue(wave - 1, out var prevDone) && start.Value >= prevDone)
                    gap = start.Value - prevDone;
                if (duration.HasValue) durations.Add((wave, duration.Value));
                if (gap.HasValue) totalGap += gap.Value;
                waves.Add(new StatsRunWave
                {
                    Wave = wave, StartedAtUtc = start.HasValue ? FromMs(start.Value) : null,
                    CompletedAtUtc = done.HasValue ? FromMs(done.Value) : null, DurationMs = duration, GapBeforeMs = gap,
                    LootCount = lootCountByWave.GetValueOrDefault(wave),
                    ChaosValue = lootMilliByWave.GetValueOrDefault(wave) / 1000d,
                });
            }

            StatsRunWaveSummary summary;
            if (!isSimulacrum || waves.Count == 0)
                summary = new StatsRunWaveSummary { Completed = isSimulacrum ? run.WavesCompleted : 0 };
            else
            {
                // Imported runs have no wave events; keep the count consistent with the list row.
                var completed = Math.Max(waves.Count(w => w.CompletedAtUtc.HasValue), run.WavesCompleted);
                if (durations.Count == 0)
                    summary = new StatsRunWaveSummary { Completed = completed, TotalGapMs = totalGap };
                else
                {
                    var sorted = durations.Select(d => d.Ms).OrderBy(x => x).ToList();
                    var mid = sorted.Count / 2;
                    var median = sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2d;
                    var fastest = durations.OrderBy(d => d.Ms).ThenBy(d => d.Wave).First();
                    var slowest = durations.OrderByDescending(d => d.Ms).ThenBy(d => d.Wave).First();
                    summary = new StatsRunWaveSummary
                    {
                        Completed = completed, AverageMs = sorted.Average(), MedianMs = median,
                        FastestWave = fastest.Wave, FastestMs = fastest.Ms, SlowestWave = slowest.Wave, SlowestMs = slowest.Ms,
                        TotalWaveMs = sorted.Sum(), TotalGapMs = totalGap,
                    };
                }
            }

            return new StatsRunDetail
            {
                Run = run,
                Waves = waves,
                WaveSummary = summary,
                Loot = lootRows,
                LootTruncated = loot.Count > RunDetailLootCap,
                LootSummary = new StatsRunLootSummary { Items = itemsTotal, ChaosValue = milliTotal / 1000d, TopItems = top },
                Events = events.Select(e => new StatsRunEvent
                {
                    OccurredAtUtc = FromMs(e.At), EventType = e.Type, Wave = IsWaveEvent(e.Type) ? e.Wave : null, Details = e.Details,
                }).ToList(),
            };
        }

        private static bool IsWaveEvent(string type) => type is "wave_started" or "wave_completed";
    }
}
