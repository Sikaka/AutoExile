using ExileCore;
using AutoExile.Systems;
using System.Numerics;

namespace AutoExile.Modes
{
    /// <summary>
    /// Pathfinding benchmark mode. Record waypoints with a hotkey, then run through
    /// them sequentially to measure timing, path quality, and stuck recovery.
    /// Uses its own navigation logic cloned from NavigationSystem so changes don't
    /// affect the production code.
    ///
    /// Hotkeys (while mode is active + Running):
    ///   F9  = Add waypoint at current player position
    ///   F10 = Clear all waypoints
    ///   F11 = Start/restart benchmark run
    /// </summary>
    public class PathBenchmarkMode : IBotMode
    {
        public string Name => "Path Benchmark";

        // ── Waypoints ──
        private readonly List<Vector2> _waypoints = new();     // grid positions
        private int _currentWaypointIndex;
        private bool _running;

        // ── Timing ──
        private DateTime _runStartTime;
        private DateTime _legStartTime;
        private readonly List<LegResult> _legResults = new();

        // ── Rendering cache ──
        private List<NavWaypoint> _renderNavPath = new();
        private Vector2 _playerGrid;

        // ── Status ──
        public string Status { get; private set; } = "Ready — F9=add waypoint, F10=clear, F11=start";
        public string Decision { get; private set; } = "";
        public bool IsRunning => _running;
        public int WaypointCount => _waypoints.Count;
        public int CurrentWaypoint => _currentWaypointIndex;
        public IReadOnlyList<Vector2> Waypoints => _waypoints;
        public IReadOnlyList<LegResult> LegResults => _legResults;

        public struct LegResult
        {
            public int LegIndex;
            public Vector2 From;
            public Vector2 To;
            public float Distance;       // grid units
            public double ElapsedMs;
            public int PathfindMs;
            public int WaypointCount;     // nav waypoints in path
            public int StuckRecoveries;
            public bool Completed;
        }

        public void OnEnter(BotContext ctx)
        {
            Status = $"Path Benchmark — {_waypoints.Count} waypoints. F9=add, F10=clear, F11=start";
            ctx.Log("[PathBenchmark] Mode entered");
        }

        public void OnExit()
        {
            _running = false;
            _renderNavPath.Clear();
        }

        public void Tick(BotContext ctx)
        {
            var gc = ctx.Game;
            if (gc?.Player == null) return;
            _playerGrid = new Vector2(gc.Player.GridPosNum.X, gc.Player.GridPosNum.Y);

            // ── Hotkeys ──
            // F9 = add waypoint
            if (ExileCore.Input.IsKeyDown(System.Windows.Forms.Keys.F9))
            {
                // Debounce — only add once per press
                if (!_f9Held)
                {
                    _f9Held = true;
                    _waypoints.Add(_playerGrid);
                    Status = $"Added waypoint #{_waypoints.Count} at ({_playerGrid.X:F0}, {_playerGrid.Y:F0})";
                    ctx.Log($"[PathBenchmark] Waypoint #{_waypoints.Count}: ({_playerGrid.X:F0}, {_playerGrid.Y:F0})");
                }
            }
            else _f9Held = false;

            // F10 = clear all
            if (ExileCore.Input.IsKeyDown(System.Windows.Forms.Keys.F10))
            {
                if (!_f10Held)
                {
                    _f10Held = true;
                    _waypoints.Clear();
                    _legResults.Clear();
                    _running = false;
                    _currentWaypointIndex = 0;
                    ctx.Navigation.Stop(gc);
                    Status = "Waypoints cleared";
                    ctx.Log("[PathBenchmark] Waypoints cleared");
                }
            }
            else _f10Held = false;

            // F11 = start/restart run
            if (ExileCore.Input.IsKeyDown(System.Windows.Forms.Keys.F11))
            {
                if (!_f11Held)
                {
                    _f11Held = true;
                    if (_waypoints.Count < 2)
                    {
                        Status = "Need at least 2 waypoints to run benchmark";
                    }
                    else
                    {
                        StartRun(ctx, gc);
                    }
                }
            }
            else _f11Held = false;

            // ── Navigation tick ──
            _renderNavPath = new List<NavWaypoint>(ctx.Navigation.CurrentNavPath);

            if (!_running) return;

            // Check if current leg is complete
            if (!ctx.Navigation.IsNavigating)
            {
                // Record leg result
                CompleteLeg(ctx, true);

                // Advance to next waypoint
                _currentWaypointIndex++;
                if (_currentWaypointIndex >= _waypoints.Count)
                {
                    // Run complete
                    _running = false;
                    var totalMs = (DateTime.Now - _runStartTime).TotalMilliseconds;
                    var totalStuck = _legResults.Sum(l => l.StuckRecoveries);
                    Status = $"COMPLETE — {_legResults.Count} legs, {totalMs:F0}ms total, {totalStuck} stuck recoveries";
                    ctx.Log($"[PathBenchmark] Run complete: {_legResults.Count} legs, {totalMs:F0}ms, {totalStuck} stucks");
                    LogSummary(ctx);
                    return;
                }

                // Start next leg
                StartLeg(ctx, gc);
            }
            else
            {
                // Navigation in progress — update status
                var target = _waypoints[_currentWaypointIndex];
                var dist = Vector2.Distance(_playerGrid, target);
                var elapsed = (DateTime.Now - _legStartTime).TotalMilliseconds;
                var wpInfo = $"wp {ctx.Navigation.CurrentWaypointIndex + 1}/{_renderNavPath.Count}";
                var stuckInfo = ctx.Navigation.StuckRecoveries > _legStartStuckCount
                    ? $" stuck×{ctx.Navigation.StuckRecoveries - _legStartStuckCount}"
                    : "";
                Status = $"Leg {_currentWaypointIndex}/{_waypoints.Count - 1}: {dist:F0}g away, {wpInfo}, {elapsed:F0}ms{stuckInfo}";

                // Safety timeout — 30s per leg
                if (elapsed > 30000)
                {
                    CompleteLeg(ctx, false);
                    ctx.Navigation.Stop(gc);
                    ctx.Log($"[PathBenchmark] Leg {_currentWaypointIndex - 1}→{_currentWaypointIndex} TIMEOUT");

                    _currentWaypointIndex++;
                    if (_currentWaypointIndex >= _waypoints.Count)
                    {
                        _running = false;
                        Status = "COMPLETE (with timeouts)";
                        LogSummary(ctx);
                        return;
                    }
                    StartLeg(ctx, gc);
                }
            }
        }

        // ── Hotkey debounce state ──
        private bool _f9Held, _f10Held, _f11Held;
        private int _legStartStuckCount;

        private void StartRun(BotContext ctx, GameController gc)
        {
            _currentWaypointIndex = 0;
            _legResults.Clear();
            _running = true;
            _runStartTime = DateTime.Now;
            ctx.Log($"[PathBenchmark] Starting run with {_waypoints.Count} waypoints");

            // Navigate to first waypoint
            StartLeg(ctx, gc);
        }

        private void StartLeg(BotContext ctx, GameController gc)
        {
            var target = _waypoints[_currentWaypointIndex];
            _legStartTime = DateTime.Now;
            _legStartStuckCount = ctx.Navigation.StuckRecoveries;

            var success = ctx.Navigation.NavigateTo(gc, target);
            if (!success)
            {
                ctx.Log($"[PathBenchmark] No path to waypoint {_currentWaypointIndex} ({target.X:F0}, {target.Y:F0})");
                CompleteLeg(ctx, false);
                _currentWaypointIndex++;
                if (_currentWaypointIndex < _waypoints.Count)
                    StartLeg(ctx, gc);
                else
                {
                    _running = false;
                    Status = "Run ended — pathfinding failure";
                    LogSummary(ctx);
                }
                return;
            }

            var from = _currentWaypointIndex > 0 ? _waypoints[_currentWaypointIndex - 1] : _playerGrid;
            Decision = $"Leg {_currentWaypointIndex}: ({from.X:F0},{from.Y:F0}) → ({target.X:F0},{target.Y:F0}) " +
                       $"path={ctx.Navigation.CurrentNavPath.Count}wp, {ctx.Navigation.LastPathfindMs}ms";
            ctx.Log($"[PathBenchmark] {Decision}");
        }

        private void CompleteLeg(BotContext ctx, bool completed)
        {
            var from = _currentWaypointIndex > 0 ? _waypoints[_currentWaypointIndex - 1] : _playerGrid;
            var to = _waypoints[_currentWaypointIndex];
            var stucks = ctx.Navigation.StuckRecoveries - _legStartStuckCount;

            _legResults.Add(new LegResult
            {
                LegIndex = _currentWaypointIndex,
                From = from,
                To = to,
                Distance = Vector2.Distance(from, to),
                ElapsedMs = (DateTime.Now - _legStartTime).TotalMilliseconds,
                PathfindMs = (int)ctx.Navigation.LastPathfindMs,
                WaypointCount = ctx.Navigation.CurrentNavPath.Count,
                StuckRecoveries = stucks,
                Completed = completed,
            });
        }

        private void LogSummary(BotContext ctx)
        {
            ctx.Log("[PathBenchmark] ═══ Summary ═══");
            foreach (var leg in _legResults)
            {
                var status = leg.Completed ? "OK" : "FAIL";
                ctx.Log($"  Leg {leg.LegIndex}: {leg.Distance:F0}g, {leg.ElapsedMs:F0}ms, " +
                        $"{leg.WaypointCount}wp, pathfind={leg.PathfindMs}ms, stuck={leg.StuckRecoveries} [{status}]");
            }
            var total = _legResults.Sum(l => l.ElapsedMs);
            var totalDist = _legResults.Sum(l => l.Distance);
            var totalStuck = _legResults.Sum(l => l.StuckRecoveries);
            var fails = _legResults.Count(l => !l.Completed);
            ctx.Log($"  Total: {totalDist:F0}g in {total:F0}ms ({totalDist / (total / 1000):F0} g/s), " +
                    $"{totalStuck} stucks, {fails} failures");
        }

        public void Render(BotContext ctx)
        {
            var gc = ctx.Game;
            var g = ctx.Graphics;
            if (gc?.Player == null || g == null) return;

            var cam = gc.IngameState.Camera;
            var playerGrid = new Vector2(gc.Player.GridPosNum.X, gc.Player.GridPosNum.Y);

            // ── Draw waypoints ──
            for (int i = 0; i < _waypoints.Count; i++)
            {
                var wp = _waypoints[i];
                var world = Pathfinding.GridToWorld3D(gc, wp);
                var screen = cam.WorldToScreen(world);
                if (screen.X < -200 || screen.X > 2400) continue;

                SharpDX.Color color;
                if (_running && i < _currentWaypointIndex)
                    color = SharpDX.Color.DarkGreen;       // completed
                else if (_running && i == _currentWaypointIndex)
                    color = SharpDX.Color.Cyan;             // current target
                else
                    color = SharpDX.Color.Yellow;           // pending

                // Circle marker
                g.DrawCircleInWorld(world, 15f, color, 2f);

                // Label
                var label = $"WP{i + 1}";
                if (_running && i < _legResults.Count)
                {
                    var leg = _legResults[i];
                    label += $" {leg.ElapsedMs:F0}ms";
                    if (leg.StuckRecoveries > 0) label += $" stuck×{leg.StuckRecoveries}";
                    if (!leg.Completed) label += " FAIL";
                }
                g.DrawText(label, screen + new Vector2(-15, -20), color);
            }

            // ── Draw lines between waypoints ──
            for (int i = 0; i < _waypoints.Count - 1; i++)
            {
                var from = Pathfinding.GridToScreen(gc, _waypoints[i]);
                var to = Pathfinding.GridToScreen(gc, _waypoints[i + 1]);
                if (from.X < -200 || from.X > 2400 || to.X < -200 || to.X > 2400) continue;

                var lineColor = _running && i < _currentWaypointIndex
                    ? SharpDX.Color.DarkGreen
                    : new SharpDX.Color(255, 255, 0, 80); // semi-transparent yellow
                g.DrawLine(from, to, 1f, lineColor);
            }

            // ── Draw active navigation path ──
            if (ctx.Navigation.IsNavigating && _renderNavPath.Count > 1)
            {
                for (int i = ctx.Navigation.CurrentWaypointIndex; i < _renderNavPath.Count - 1; i++)
                {
                    var from = Pathfinding.GridToScreen(gc, _renderNavPath[i].Position);
                    var to = Pathfinding.GridToScreen(gc, _renderNavPath[i + 1].Position);
                    if (from.X < -200 || from.X > 2400 || to.X < -200 || to.X > 2400) continue;

                    var isBlink = _renderNavPath[i + 1].Action == WaypointAction.Blink;
                    var pathColor = isBlink ? SharpDX.Color.Magenta : SharpDX.Color.Orange;
                    g.DrawLine(from, to, isBlink ? 3f : 2f, pathColor);
                }

                // Nav waypoint dots
                for (int i = ctx.Navigation.CurrentWaypointIndex; i < _renderNavPath.Count; i++)
                {
                    var wp = _renderNavPath[i];
                    var screen = Pathfinding.GridToScreen(gc, wp.Position);
                    if (screen.X < -200 || screen.X > 2400) continue;

                    var dotColor = i == ctx.Navigation.CurrentWaypointIndex
                        ? SharpDX.Color.White
                        : wp.Action == WaypointAction.Blink ? SharpDX.Color.Magenta : SharpDX.Color.Orange;

                    var c = new Vector2(screen.X, screen.Y);
                    g.DrawLine(c + new Vector2(-3, 0), c + new Vector2(3, 0), 2, dotColor);
                    g.DrawLine(c + new Vector2(0, -3), c + new Vector2(0, 3), 2, dotColor);
                }
            }

            // ── HUD overlay ──
            float hudX = 20, hudY = 200, lineH = 18;
            g.DrawText("Path Benchmark", new Vector2(hudX, hudY), SharpDX.Color.Cyan);
            hudY += lineH;
            g.DrawText(Status, new Vector2(hudX, hudY), SharpDX.Color.White);
            hudY += lineH;
            g.DrawText($"Waypoints: {_waypoints.Count}  Player: ({playerGrid.X:F0}, {playerGrid.Y:F0})",
                new Vector2(hudX, hudY), SharpDX.Color.Gray);
            hudY += lineH;

            if (_running)
            {
                var totalElapsed = (DateTime.Now - _runStartTime).TotalSeconds;
                g.DrawText($"Run time: {totalElapsed:F1}s  Leg: {_currentWaypointIndex + 1}/{_waypoints.Count}",
                    new Vector2(hudX, hudY), SharpDX.Color.Yellow);
                hudY += lineH;
            }

            // Leg results table
            if (_legResults.Count > 0)
            {
                g.DrawText("── Results ──", new Vector2(hudX, hudY), SharpDX.Color.Gray);
                hudY += lineH;
                foreach (var leg in _legResults)
                {
                    var statusColor = leg.Completed ? SharpDX.Color.LimeGreen : SharpDX.Color.Red;
                    var stuckStr = leg.StuckRecoveries > 0 ? $" stuck×{leg.StuckRecoveries}" : "";
                    g.DrawText($"Leg {leg.LegIndex}: {leg.Distance:F0}g  {leg.ElapsedMs:F0}ms  {leg.WaypointCount}wp  pf={leg.PathfindMs}ms{stuckStr}",
                        new Vector2(hudX, hudY), statusColor);
                    hudY += lineH;
                }

                var totalDist = _legResults.Sum(l => l.Distance);
                var totalMs = _legResults.Sum(l => l.ElapsedMs);
                var speed = totalMs > 0 ? totalDist / (totalMs / 1000) : 0;
                g.DrawText($"Total: {totalDist:F0}g  {totalMs:F0}ms  {speed:F0} g/s",
                    new Vector2(hudX, hudY), SharpDX.Color.Cyan);
                hudY += lineH;
            }

            g.DrawText("F9=add waypoint  F10=clear  F11=start", new Vector2(hudX, hudY),
                new SharpDX.Color(150, 150, 150));
        }
    }
}
