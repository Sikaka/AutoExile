using AutoExile.Recording;
using System.Numerics;

namespace AutoExile.Replay
{
    /// <summary>
    /// Analyzes human map run recordings to extract decision-making patterns.
    /// Focuses on the forward-momentum model: the player is always a wave moving
    /// through the map, making cost/benefit decisions about forward vs backtrack.
    /// </summary>
    public static class MapRunAnalyzer
    {
        public class MapRunReport
        {
            // ── Overview ──
            public string FileName { get; set; } = "";
            public string AreaName { get; set; } = "";
            public int TotalTicks { get; set; }
            public float DurationSeconds { get; set; }
            public float FinalCoverage { get; set; }
            public int DeathCount { get; set; }

            // ── Movement ──
            public float TotalDistanceTraveled { get; set; }
            public float StraightLineDistance { get; set; } // start to end
            public float AverageSpeed { get; set; } // grid units per second
            public float IdlePercent { get; set; } // % of ticks with <1 unit movement

            // ── Coverage efficiency ──
            public float CoveragePerSecond { get; set; } // average % gained per second
            public List<CoveragePhase> CoveragePhases { get; set; } = new();

            // ── Backtrack events ──
            public List<BacktrackEvent> Backtracks { get; set; } = new();
            public int BacktrackCount { get; set; }
            public float BacktrackPercent { get; set; } // % ticks spent backtracking
            public float TotalBacktrackDistance { get; set; }

            // ── Mechanic engagements ──
            public List<MechanicEngagement> Mechanics { get; set; } = new();

            // ── Loot behavior ──
            public List<LootDecision> LootDecisions { get; set; } = new();
            public int ItemsLooted { get; set; }
            public int ItemsSkipped { get; set; }
            public double TotalLootValue { get; set; }

            // ── Directional decisions ──
            public float AvgMonstersAhead { get; set; }
            public float AvgMonstersBehind { get; set; }

            // ── Key insights ──
            public List<string> Insights { get; set; } = new();
        }

        public class CoveragePhase
        {
            public int StartTick { get; set; }
            public int EndTick { get; set; }
            public float StartCoverage { get; set; }
            public float EndCoverage { get; set; }
            public string Type { get; set; } = ""; // "Progressing", "Plateau", "Mechanic", "Dead"
        }

        public class BacktrackEvent
        {
            public int StartTick { get; set; }
            public int EndTick { get; set; }
            public float Distance { get; set; }
            public float CoverageAtStart { get; set; }
            public string Trigger { get; set; } = ""; // what caused backtrack: "loot", "ritual", "unknown"
            public double ValueGained { get; set; } // chaos value of loot picked up during backtrack
        }

        public class MechanicEngagement
        {
            public int StartTick { get; set; }
            public int EndTick { get; set; }
            public string Type { get; set; } = ""; // "Ritual", "Wishes", "RitualShop"
            public float CoverageWhenEngaged { get; set; }
            public string Location { get; set; } = "";
            public int DurationTicks { get; set; }
            public bool WasDeferred { get; set; } // did player pass by earlier and come back?
        }

        public class LootDecision
        {
            public int Tick { get; set; }
            public string ItemText { get; set; } = "";
            public double ChaosValue { get; set; }
            public string Rarity { get; set; } = "";
            public bool WasPickedUp { get; set; }
            public float DistanceFromPlayer { get; set; }
            public bool RequiredBacktrack { get; set; } // was it behind the player?
        }

        /// <summary>Analyze a single recording.</summary>
        public static MapRunReport Analyze(GameplayRecording recording, string fileName = "")
        {
            var report = new MapRunReport
            {
                FileName = Path.GetFileName(fileName),
                AreaName = recording.AreaName,
                TotalTicks = recording.Ticks.Count,
                DurationSeconds = recording.DurationSeconds,
            };

            if (recording.Ticks.Count < 10) return report;

            // Forward-fill entities on non-scan ticks
            ForwardFillEntities(recording);

            AnalyzeMovement(recording, report);
            AnalyzeCoverage(recording, report);
            AnalyzeBacktracks(recording, report);
            AnalyzeMechanics(recording, report);
            AnalyzeLoot(recording, report);
            AnalyzeDirectionalDensity(recording, report);
            GenerateInsights(report);

            return report;
        }

        /// <summary>Analyze all recordings in a directory and produce cross-run summary.</summary>
        public static (List<MapRunReport> Runs, string Summary) AnalyzeAll(
            IEnumerable<(GameplayRecording Recording, string Path)> recordings)
        {
            var runs = new List<MapRunReport>();
            foreach (var (rec, path) in recordings)
            {
                runs.Add(Analyze(rec, path));
            }

            var summary = BuildCrossRunSummary(runs);
            return (runs, summary);
        }

        // ══════════════════════════════════════════════════════════════
        // Forward fill
        // ══════════════════════════════════════════════════════════════

        private static void ForwardFillEntities(GameplayRecording recording)
        {
            List<RecordedEntity>? lastEntities = null;
            foreach (var tick in recording.Ticks)
            {
                if (tick.Entities.Count > 0)
                    lastEntities = tick.Entities;
                else if (lastEntities != null)
                    tick.Entities = lastEntities;
            }
        }

        // ══════════════════════════════════════════════════════════════
        // Movement analysis
        // ══════════════════════════════════════════════════════════════

        private static void AnalyzeMovement(GameplayRecording recording, MapRunReport report)
        {
            float totalDist = 0;
            int idleTicks = 0;
            int deathCount = 0;
            bool wasDead = false;

            var startPos = new Vector2(recording.Ticks[0].Player.GridX, recording.Ticks[0].Player.GridY);
            var prevPos = startPos;

            for (int i = 1; i < recording.Ticks.Count; i++)
            {
                var tick = recording.Ticks[i];
                var pos = new Vector2(tick.Player.GridX, tick.Player.GridY);
                var dist = Vector2.Distance(pos, prevPos);

                totalDist += dist;
                if (dist < 1f) idleTicks++;

                // Death tracking
                if (!tick.Player.IsAlive && !wasDead) deathCount++;
                wasDead = !tick.Player.IsAlive;

                prevPos = pos;
            }

            var endPos = prevPos;
            report.TotalDistanceTraveled = totalDist;
            report.StraightLineDistance = Vector2.Distance(startPos, endPos);
            report.AverageSpeed = report.DurationSeconds > 0 ? totalDist / report.DurationSeconds : 0;
            report.IdlePercent = recording.Ticks.Count > 0 ? (float)idleTicks / recording.Ticks.Count * 100f : 0;
            report.DeathCount = deathCount;
            report.FinalCoverage = recording.Ticks.Last().ExplorationCoverage;
        }

        // ══════════════════════════════════════════════════════════════
        // Coverage phases
        // ══════════════════════════════════════════════════════════════

        private static void AnalyzeCoverage(GameplayRecording recording, MapRunReport report)
        {
            if (recording.Ticks.Count == 0) return;

            report.CoveragePerSecond = report.DurationSeconds > 0
                ? report.FinalCoverage / report.DurationSeconds * 100f
                : 0;

            // Detect phases: progressing (coverage increasing), plateau (stalled), mechanic, dead
            float lastCoverage = recording.Ticks[0].ExplorationCoverage;
            int phaseStart = 0;
            string phaseType = "Progressing";

            for (int i = 1; i < recording.Ticks.Count; i++)
            {
                var tick = recording.Ticks[i];
                string currentType;

                if (!tick.Player.IsAlive)
                    currentType = "Dead";
                else if (tick.UI.RitualWindowOpen)
                    currentType = "RitualShop";
                else if (Math.Abs(tick.ExplorationCoverage - lastCoverage) > 0.001f)
                {
                    currentType = "Progressing";
                    lastCoverage = tick.ExplorationCoverage;
                }
                else
                    currentType = "Plateau";

                // Phase transition — but only after sustained change (30+ ticks = ~0.5s)
                if (currentType != phaseType && i - phaseStart > 30)
                {
                    report.CoveragePhases.Add(new CoveragePhase
                    {
                        StartTick = phaseStart,
                        EndTick = i - 1,
                        StartCoverage = recording.Ticks[phaseStart].ExplorationCoverage,
                        EndCoverage = recording.Ticks[i - 1].ExplorationCoverage,
                        Type = phaseType,
                    });
                    phaseStart = i;
                    phaseType = currentType;
                }
            }

            // Close final phase
            report.CoveragePhases.Add(new CoveragePhase
            {
                StartTick = phaseStart,
                EndTick = recording.Ticks.Count - 1,
                StartCoverage = recording.Ticks[phaseStart].ExplorationCoverage,
                EndCoverage = recording.Ticks.Last().ExplorationCoverage,
                Type = phaseType,
            });
        }

        // ══════════════════════════════════════════════════════════════
        // Backtrack detection
        // ══════════════════════════════════════════════════════════════

        private static void AnalyzeBacktracks(GameplayRecording recording, MapRunReport report)
        {
            // Track "frontier" — the furthest explored position along the movement path.
            // When player moves back toward already-explored areas while coverage is stalled,
            // that's a backtrack.

            // Use exploration regions: if player moves toward a region with high explored ratio
            // while regions with low explored ratio exist, they're backtracking.

            const int windowSize = 60; // ~1 second window for direction detection
            int backtrackTicks = 0;
            float backtrackDist = 0;
            int btStart = -1;
            float btStartCoverage = 0;

            for (int i = windowSize; i < recording.Ticks.Count; i++)
            {
                var tick = recording.Ticks[i];
                var prevTick = recording.Ticks[i - windowSize];

                if (!tick.Player.IsAlive) continue;

                var pos = new Vector2(tick.Player.GridX, tick.Player.GridY);
                var prevPos = new Vector2(prevTick.Player.GridX, prevTick.Player.GridY);
                var moveDir = pos - prevPos;

                if (moveDir.LengthSquared() < 4f) continue; // barely moved

                // Check if moving toward unexplored or explored regions
                bool movingTowardExplored = IsMovingTowardExplored(tick, moveDir);
                bool coverageStalled = Math.Abs(tick.ExplorationCoverage - prevTick.ExplorationCoverage) < 0.001f;

                if (movingTowardExplored && coverageStalled)
                {
                    backtrackTicks++;
                    var d = Vector2.Distance(pos,
                        new Vector2(recording.Ticks[i - 1].Player.GridX, recording.Ticks[i - 1].Player.GridY));
                    backtrackDist += d;

                    if (btStart < 0)
                    {
                        btStart = i;
                        btStartCoverage = tick.ExplorationCoverage;
                    }
                }
                else if (btStart >= 0)
                {
                    // End of backtrack
                    var btDist = 0f;
                    for (int j = btStart; j < i; j++)
                    {
                        btDist += Vector2.Distance(
                            new Vector2(recording.Ticks[j].Player.GridX, recording.Ticks[j].Player.GridY),
                            new Vector2(recording.Ticks[j - 1].Player.GridX, recording.Ticks[j - 1].Player.GridY));
                    }

                    if (i - btStart > 30) // only log backtracks > 0.5s
                    {
                        var trigger = DetectBacktrackTrigger(recording, btStart, i);
                        report.Backtracks.Add(new BacktrackEvent
                        {
                            StartTick = btStart,
                            EndTick = i,
                            Distance = btDist,
                            CoverageAtStart = btStartCoverage,
                            Trigger = trigger,
                        });
                    }
                    btStart = -1;
                }
            }

            report.BacktrackCount = report.Backtracks.Count;
            report.BacktrackPercent = recording.Ticks.Count > 0
                ? (float)backtrackTicks / recording.Ticks.Count * 100f : 0;
            report.TotalBacktrackDistance = backtrackDist;
        }

        private static bool IsMovingTowardExplored(RecordingTick tick, Vector2 moveDir)
        {
            if (tick.Regions.Count == 0) return false;

            var playerPos = new Vector2(tick.Player.GridX, tick.Player.GridY);
            var moveDirNorm = Vector2.Normalize(moveDir);

            float unexploredAhead = 0, exploredAhead = 0;

            foreach (var region in tick.Regions)
            {
                var regionPos = new Vector2(region.CenterX, region.CenterY);
                var toRegion = regionPos - playerPos;
                if (toRegion.LengthSquared() < 100f) continue; // too close to be meaningful

                var dot = Vector2.Dot(Vector2.Normalize(toRegion), moveDirNorm);
                if (dot < 0.3f) continue; // not ahead

                var unexplored = region.CellCount - region.SeenCount;
                if (region.ExploredRatio > 0.9f)
                    exploredAhead += region.CellCount;
                else
                    unexploredAhead += unexplored;
            }

            return exploredAhead > unexploredAhead * 2; // mostly explored ahead
        }

        private static string DetectBacktrackTrigger(GameplayRecording recording, int start, int end)
        {
            // Check what's near the player during backtrack
            for (int i = start; i < end; i++)
            {
                var tick = recording.Ticks[i];

                // Looting?
                if (tick.GroundLabels.Any(l => l.IsVisible && l.ChaosValue > 0))
                    return "loot";

                // Ritual?
                if (tick.Entities.Any(e => e.Path.Contains("Ritual") && e.Distance < 40))
                    return "ritual";

                // Wishes?
                if (tick.Entities.Any(e => e.Path.Contains("Faridun") && e.Distance < 40))
                    return "wishes";
            }
            return "unknown";
        }

        // ══════════════════════════════════════════════════════════════
        // Mechanic engagement analysis
        // ══════════════════════════════════════════════════════════════

        private static void AnalyzeMechanics(GameplayRecording recording, MapRunReport report)
        {
            // Track mechanic entities we've seen and when the player first encountered them
            var firstSeen = new Dictionary<string, int>(); // entity key → tick first seen
            var engaged = new Dictionary<string, int>(); // entity key → tick engaged (state changed / close interaction)

            bool inMechanic = false;
            int mechanicStart = 0;
            string mechanicType = "";
            string mechanicLocation = "";

            for (int i = 0; i < recording.Ticks.Count; i++)
            {
                var tick = recording.Ticks[i];

                // Ritual shop
                if (tick.UI.RitualWindowOpen)
                {
                    if (!inMechanic || mechanicType != "RitualShop")
                    {
                        if (inMechanic) CloseMechanic(report, recording, mechanicStart, i - 1, mechanicType, mechanicLocation, firstSeen);
                        inMechanic = true;
                        mechanicStart = i;
                        mechanicType = "RitualShop";
                        mechanicLocation = $"tribute={tick.UI.RitualTribute}";
                    }
                    continue;
                }

                // Active ritual
                var activeRitual = tick.Entities.FirstOrDefault(e =>
                    e.Path.Contains("Ritual") && e.States?.GetValueOrDefault("current_state") == 2);
                if (activeRitual != null && activeRitual.Distance < 80)
                {
                    var key = $"ritual_{(int)activeRitual.GridX}_{(int)activeRitual.GridY}";
                    if (!inMechanic || mechanicType != "Ritual")
                    {
                        if (inMechanic) CloseMechanic(report, recording, mechanicStart, i - 1, mechanicType, mechanicLocation, firstSeen);
                        inMechanic = true;
                        mechanicStart = i;
                        mechanicType = "Ritual";
                        mechanicLocation = $"({activeRitual.GridX:F0},{activeRitual.GridY:F0})";
                    }
                    continue;
                }

                // Wishes
                var wishes = tick.Entities.FirstOrDefault(e =>
                    e.Path.Contains("Faridun") && e.Distance < 60);
                if (wishes != null)
                {
                    var key = $"wishes_{(int)wishes.GridX}_{(int)wishes.GridY}";
                    if (!inMechanic || mechanicType != "Wishes")
                    {
                        if (inMechanic) CloseMechanic(report, recording, mechanicStart, i - 1, mechanicType, mechanicLocation, firstSeen);
                        inMechanic = true;
                        mechanicStart = i;
                        mechanicType = "Wishes";
                        mechanicLocation = $"({wishes.GridX:F0},{wishes.GridY:F0})";
                    }
                    continue;
                }

                // Track first-seen for ritual/wishes entities
                foreach (var entity in tick.Entities)
                {
                    if (entity.Distance > 100) continue;
                    string? eKey = null;
                    if (entity.Path.Contains("Ritual") && entity.Path.Contains("Interactable"))
                        eKey = $"ritual_{(int)entity.GridX}_{(int)entity.GridY}";
                    else if (entity.Path.Contains("Faridun"))
                        eKey = $"wishes_{(int)entity.GridX}_{(int)entity.GridY}";

                    if (eKey != null && !firstSeen.ContainsKey(eKey))
                        firstSeen[eKey] = i;
                }

                // Close mechanic if we were in one
                if (inMechanic)
                {
                    CloseMechanic(report, recording, mechanicStart, i - 1, mechanicType, mechanicLocation, firstSeen);
                    inMechanic = false;
                }
            }

            if (inMechanic)
                CloseMechanic(report, recording, mechanicStart, recording.Ticks.Count - 1, mechanicType, mechanicLocation, firstSeen);
        }

        private static void CloseMechanic(MapRunReport report, GameplayRecording recording,
            int start, int end, string type, string location, Dictionary<string, int> firstSeen)
        {
            if (end - start < 5) return; // ignore very short blips

            var coverage = recording.Ticks[start].ExplorationCoverage;

            // Check if this mechanic was deferred (seen earlier)
            var key = type.ToLower() + "_" + location.Trim('(', ')').Replace(",", "_").Replace(" ", "");
            bool deferred = firstSeen.TryGetValue(key, out var seenTick) && start - seenTick > 120; // >2s gap

            report.Mechanics.Add(new MechanicEngagement
            {
                StartTick = start,
                EndTick = end,
                Type = type,
                CoverageWhenEngaged = coverage,
                Location = location,
                DurationTicks = end - start + 1,
                WasDeferred = deferred,
            });
        }

        // ══════════════════════════════════════════════════════════════
        // Loot analysis
        // ══════════════════════════════════════════════════════════════

        private static void AnalyzeLoot(GameplayRecording recording, MapRunReport report)
        {
            // Track items that appeared on ground — were they picked up or left?
            var seenItems = new Dictionary<long, (string Text, double Value, string Rarity, int FirstTick, float GridX, float GridY)>();
            var pickedUp = new HashSet<long>(); // entity IDs that disappeared after being visible

            for (int i = 0; i < recording.Ticks.Count; i++)
            {
                var tick = recording.Ticks[i];
                var currentIds = new HashSet<long>();

                foreach (var label in tick.GroundLabels)
                {
                    if (!label.IsVisible || label.EntityId == 0) continue;
                    currentIds.Add(label.EntityId);

                    if (!seenItems.ContainsKey(label.EntityId))
                    {
                        seenItems[label.EntityId] = (label.Text, label.ChaosValue, label.Rarity, i,
                            label.GridX, label.GridY);
                    }
                }

                // Items that were visible last tick but gone now = picked up
                foreach (var (id, info) in seenItems)
                {
                    if (!currentIds.Contains(id) && !pickedUp.Contains(id) && i - info.FirstTick < 300)
                    {
                        // Check if player was close when it disappeared
                        var playerPos = new Vector2(tick.Player.GridX, tick.Player.GridY);
                        var itemPos = new Vector2(info.GridX, info.GridY);
                        if (Vector2.Distance(playerPos, itemPos) < 30)
                            pickedUp.Add(id);
                    }
                }
            }

            // Build loot decisions for valuable items
            foreach (var (id, info) in seenItems)
            {
                if (info.Value < 1 && info.Rarity != "Unique") continue; // skip common junk

                var wasLooted = pickedUp.Contains(id);
                report.LootDecisions.Add(new LootDecision
                {
                    Tick = info.FirstTick,
                    ItemText = info.Text,
                    ChaosValue = info.Value,
                    Rarity = info.Rarity,
                    WasPickedUp = wasLooted,
                });

                if (wasLooted)
                {
                    report.ItemsLooted++;
                    report.TotalLootValue += info.Value;
                }
                else
                {
                    report.ItemsSkipped++;
                }
            }
        }

        // ══════════════════════════════════════════════════════════════
        // Directional density
        // ══════════════════════════════════════════════════════════════

        private static void AnalyzeDirectionalDensity(GameplayRecording recording, MapRunReport report)
        {
            int count = 0;
            float totalAhead = 0, totalBehind = 0;

            foreach (var tick in recording.Ticks)
            {
                if (tick.MonstersAhead > 0 || tick.MonstersBehind > 0)
                {
                    totalAhead += tick.MonstersAhead;
                    totalBehind += tick.MonstersBehind;
                    count++;
                }
            }

            if (count > 0)
            {
                report.AvgMonstersAhead = totalAhead / count;
                report.AvgMonstersBehind = totalBehind / count;
            }
        }

        // ══════════════════════════════════════════════════════════════
        // Insights
        // ══════════════════════════════════════════════════════════════

        private static void GenerateInsights(MapRunReport report)
        {
            // Speed
            if (report.AverageSpeed > 0)
                report.Insights.Add($"Average speed: {report.AverageSpeed:F1} grid/s ({report.TotalDistanceTraveled:F0} total distance)");

            // Idle time
            if (report.IdlePercent > 15)
                report.Insights.Add($"High idle time: {report.IdlePercent:F0}% of ticks stationary — potential optimization target");

            // Coverage rate
            if (report.CoveragePerSecond > 0)
                report.Insights.Add($"Coverage rate: {report.CoveragePerSecond:F2}%/s to {report.FinalCoverage:P0}");

            // Backtracking
            if (report.BacktrackCount > 0)
            {
                var backtrackReasons = report.Backtracks
                    .GroupBy(b => b.Trigger)
                    .Select(g => $"{g.Count()} for {g.Key}")
                    .ToList();
                report.Insights.Add($"Backtracks: {report.BacktrackCount} ({report.BacktrackPercent:F0}% of time) — {string.Join(", ", backtrackReasons)}");
            }

            // Mechanic timing
            var rituals = report.Mechanics.Where(m => m.Type == "Ritual").ToList();
            if (rituals.Count > 0)
            {
                var avgCoverage = rituals.Average(r => r.CoverageWhenEngaged);
                var deferred = rituals.Count(r => r.WasDeferred);
                report.Insights.Add($"Rituals: {rituals.Count} engaged, avg coverage at engagement: {avgCoverage:P0}, {deferred} deferred");
            }

            var shops = report.Mechanics.Where(m => m.Type == "RitualShop").ToList();
            if (shops.Count > 0)
            {
                var shopTicks = shops.Sum(s => s.DurationTicks);
                report.Insights.Add($"Ritual shop: {shops.Count} visits, {shopTicks * 16 / 1000f:F1}s total");
            }

            // Deaths
            if (report.DeathCount > 0)
                report.Insights.Add($"Deaths: {report.DeathCount}");

            // Loot efficiency
            if (report.ItemsLooted + report.ItemsSkipped > 0)
                report.Insights.Add($"Loot: {report.ItemsLooted} picked up ({report.TotalLootValue:F0}c), {report.ItemsSkipped} skipped");

            // Directional bias
            if (report.AvgMonstersAhead > 0 || report.AvgMonstersBehind > 0)
                report.Insights.Add($"Avg monsters ahead: {report.AvgMonstersAhead:F1}, behind: {report.AvgMonstersBehind:F1} — " +
                    (report.AvgMonstersAhead > report.AvgMonstersBehind * 1.5f
                        ? "good forward momentum"
                        : "frequently has mobs behind"));
        }

        // ══════════════════════════════════════════════════════════════
        // Cross-run summary
        // ══════════════════════════════════════════════════════════════

        private static string BuildCrossRunSummary(List<MapRunReport> runs)
        {
            if (runs.Count == 0) return "No runs to summarize.";

            var lines = new List<string>
            {
                "═══════════════════════════════════════════════════════════════",
                $"  CROSS-RUN SUMMARY — {runs.Count} recordings",
                "═══════════════════════════════════════════════════════════════",
                "",
            };

            // Per-run table
            lines.Add($"{"Run",-35} {"Ticks",6} {"Time",6} {"Cover",6} {"Speed",6} {"Idle%",6} {"BT%",5} {"Deaths",6} {"Loot$",6}");
            lines.Add(new string('-', 90));
            foreach (var r in runs)
            {
                lines.Add($"{r.FileName,-35} {r.TotalTicks,6} {r.DurationSeconds,5:F0}s {r.FinalCoverage,5:P0} " +
                    $"{r.AverageSpeed,5:F1} {r.IdlePercent,5:F0}% {r.BacktrackPercent,4:F0}% {r.DeathCount,6} {r.TotalLootValue,5:F0}c");
            }
            lines.Add("");

            // Averages
            lines.Add("── Averages ──");
            lines.Add($"  Speed:        {runs.Average(r => r.AverageSpeed):F1} grid/s");
            lines.Add($"  Coverage:     {runs.Average(r => r.FinalCoverage):P0} final");
            lines.Add($"  Coverage/s:   {runs.Average(r => r.CoveragePerSecond):F2}%/s");
            lines.Add($"  Idle:         {runs.Average(r => r.IdlePercent):F0}%");
            lines.Add($"  Backtrack:    {runs.Average(r => r.BacktrackPercent):F0}% of time, {runs.Average(r => r.BacktrackCount):F1} per run");
            lines.Add($"  Deaths:       {runs.Average(r => r.DeathCount):F1} per run");
            lines.Add($"  Run time:     {runs.Average(r => r.DurationSeconds):F0}s avg");
            lines.Add("");

            // Mechanic patterns
            var allMechanics = runs.SelectMany(r => r.Mechanics).ToList();
            var rituals = allMechanics.Where(m => m.Type == "Ritual").ToList();
            if (rituals.Count > 0)
            {
                lines.Add("── Ritual Patterns ──");
                lines.Add($"  Avg per map:         {(float)rituals.Count / runs.Count:F1}");
                lines.Add($"  Avg coverage @start: {rituals.Average(r => r.CoverageWhenEngaged):P0}");
                lines.Add($"  Deferred:            {rituals.Count(r => r.WasDeferred)}/{rituals.Count}");
                lines.Add($"  Avg duration:        {rituals.Average(r => r.DurationTicks) * 16 / 1000f:F1}s");
                lines.Add("");
            }

            // Bot design insights
            lines.Add("── Bot Design Implications ──");

            var avgSpeed = runs.Average(r => r.AverageSpeed);
            lines.Add($"  Target move speed: {avgSpeed:F0} grid/s (match human pace)");

            var avgBtPct = runs.Average(r => r.BacktrackPercent);
            if (avgBtPct < 5)
                lines.Add("  Backtrack policy: human rarely backtracks — bot should almost never backtrack");
            else if (avgBtPct < 15)
                lines.Add($"  Backtrack policy: moderate backtracking ({avgBtPct:F0}%) — bot should backtrack for high-value only");
            else
                lines.Add($"  Backtrack policy: significant backtracking ({avgBtPct:F0}%) — bot needs backtrack logic");

            if (rituals.Count > 0)
            {
                var deferRate = (float)rituals.Count(r => r.WasDeferred) / rituals.Count;
                if (deferRate > 0.3f)
                    lines.Add($"  Ritual timing: {deferRate:P0} deferred — bot should defer rituals and batch");
                else
                    lines.Add("  Ritual timing: mostly engaged immediately — bot can engage on contact");
            }

            var avgIdle = runs.Average(r => r.IdlePercent);
            if (avgIdle > 10)
                lines.Add($"  Idle concern: {avgIdle:F0}% idle — bot must avoid getting stuck/waiting unnecessarily");

            return string.Join(Environment.NewLine, lines);
        }

        // ══════════════════════════════════════════════════════════════
        // Formatting
        // ══════════════════════════════════════════════════════════════

        public static string FormatReport(MapRunReport report)
        {
            var lines = new List<string>
            {
                $"═══ {report.FileName} ═══",
                $"Area: {report.AreaName} | {report.TotalTicks} ticks | {report.DurationSeconds:F0}s | Coverage: {report.FinalCoverage:P0}",
                "",
            };

            // Coverage phases
            if (report.CoveragePhases.Count > 0)
            {
                lines.Add("── Coverage Phases ──");
                foreach (var phase in report.CoveragePhases)
                {
                    var durMs = (phase.EndTick - phase.StartTick + 1) * 16;
                    lines.Add($"  tick {phase.StartTick,5}-{phase.EndTick,5} ({durMs / 1000f,5:F1}s): {phase.Type,-14} {phase.StartCoverage:P0} → {phase.EndCoverage:P0}");
                }
                lines.Add("");
            }

            // Mechanics
            if (report.Mechanics.Count > 0)
            {
                lines.Add("── Mechanic Engagements ──");
                foreach (var m in report.Mechanics)
                {
                    var durS = m.DurationTicks * 16 / 1000f;
                    var deferred = m.WasDeferred ? " [DEFERRED]" : "";
                    lines.Add($"  tick {m.StartTick,5}: {m.Type,-12} {m.Location,-30} {durS:F1}s  coverage={m.CoverageWhenEngaged:P0}{deferred}");
                }
                lines.Add("");
            }

            // Backtracks
            if (report.Backtracks.Count > 0)
            {
                lines.Add("── Backtrack Events ──");
                foreach (var bt in report.Backtracks)
                {
                    var durS = (bt.EndTick - bt.StartTick) * 16 / 1000f;
                    lines.Add($"  tick {bt.StartTick,5}-{bt.EndTick,5}: {durS:F1}s, {bt.Distance:F0} units, trigger={bt.Trigger}, coverage={bt.CoverageAtStart:P0}");
                }
                lines.Add("");
            }

            // Loot
            if (report.LootDecisions.Count > 0)
            {
                lines.Add("── Loot Decisions (valued items) ──");
                foreach (var l in report.LootDecisions.OrderByDescending(l => l.ChaosValue).Take(20))
                {
                    var action = l.WasPickedUp ? "PICKED" : "SKIP  ";
                    lines.Add($"  {action} {l.ChaosValue,6:F0}c  {l.Rarity,-8} {l.ItemText}");
                }
                if (report.LootDecisions.Count > 20)
                    lines.Add($"  ... and {report.LootDecisions.Count - 20} more");
                lines.Add("");
            }

            // Insights
            if (report.Insights.Count > 0)
            {
                lines.Add("── Insights ──");
                foreach (var insight in report.Insights)
                    lines.Add($"  • {insight}");
            }

            return string.Join(Environment.NewLine, lines);
        }
    }
}
