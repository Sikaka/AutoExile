using Microsoft.Data.Sqlite;

namespace AutoExile.Statistics
{
    public sealed partial class StatsService
    {
        private const int SeasonRunLimit = 500;

        /// <summary>Every league with recorded sessions, newest first; the pre-tracking bucket has a null league.</summary>
        public IReadOnlyList<StatsSeasonSummary> ReadSeasons()
        {
            using var db = OpenReadConnection();
            var current = ScalarString(db, null, "SELECT value FROM stats_meta WHERE key='current_league'");
            using var cmd = db.CreateCommand();
            cmd.CommandText = @"SELECT s.league,MIN(s.started_at_ms),
MAX(COALESCE((SELECT MAX(r.last_observed_at_ms) FROM runs r WHERE r.session_id=s.session_id),s.ended_at_ms,s.started_at_ms)),
COALESCE(SUM((SELECT COUNT(*) FROM runs r WHERE r.session_id=s.session_id)),0),
COALESCE(SUM((SELECT COUNT(*) FROM runs r WHERE r.session_id=s.session_id AND r.result='completed')),0),
COALESCE(SUM((SELECT COALESCE(SUM(l.quantity),0) FROM loot_events l WHERE l.session_id=s.session_id)),0),
COALESCE(SUM((SELECT COALESCE(SUM(l.chaos_value_milli),0) FROM loot_events l WHERE l.session_id=s.session_id)),0)
FROM sessions s GROUP BY s.league ORDER BY MAX(s.started_at_ms) DESC";
            var result = new List<StatsSeasonSummary>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var league = r.IsDBNull(0) ? null : r.GetString(0);
                var summary = new StatsSeasonSummary
                {
                    League = league,
                    Current = league != null && league == current,
                    StartedAtUtc = r.IsDBNull(1) ? null : FromMs(r.GetInt64(1)),
                    LastActivityAtUtc = r.IsDBNull(2) ? null : FromMs(r.GetInt64(2)),
                    Runs = r.GetInt32(3), FullClears = r.GetInt32(4), ItemsLooted = r.GetInt64(5),
                    ChaosValue = r.GetInt64(6) / 1000d,
                };
                // An empty pre-tracking bucket (a fresh install) is not a season worth listing.
                if (league == null && summary.Runs == 0 && summary.ItemsLooted == 0) continue;
                result.Add(summary);
            }
            return result;
        }

        /// <summary>One season's totals, per-mode totals, closed runs and best finds. Null when no session has that league.</summary>
        public StatsSeasonDetail? ReadSeason(string? league)
        {
            using var db = OpenReadConnection();
            var scope = new LeagueScope(string.IsNullOrWhiteSpace(league) ? null : league.Trim());
            var season = ReadSeasons().FirstOrDefault(s => s.League == scope.League);
            if (season == null) return null;
            return new StatsSeasonDetail
            {
                Season = season,
                Summary = ReadAggregate(db, null, null, scope),
                ByMode = ReadModeAggregates(db, null, scope),
                Runs = ReadRuns(db, $"WHERE r.state='closed' AND r.session_id IN ({LeagueSessionsSql})", SeasonRunLimit, null, scope),
                BestFinds = ReadBestFinds(db, Volatile.Read(ref _bestFindsMinimumChaos) * 1000L, 20, scope),
            };
        }
    }
}
