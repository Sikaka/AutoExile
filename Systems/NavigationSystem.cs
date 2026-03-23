using ExileCore;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using System.Numerics;
using System.Windows.Forms;

namespace AutoExile.Systems
{
    /// <summary>
    /// Handles pathfinding and movement execution.
    /// All positions stored in grid coordinates. Converts to world only at WorldToScreen call sites.
    /// All input goes through BotInput for global action gating.
    /// Supports blink skills to jump across gaps detected via the targeting layer.
    /// </summary>
    public class NavigationSystem
    {
        // Movement keys — read from CombatSystem each tick via BotCore sync
        public Keys MoveKey { get; set; } = Keys.T;

        // Movement skills (dash/blink) — synced from CombatSystem
        public List<MovementSkillInfo> MovementSkills { get; set; } = new();

        // Blink-aware pathfinding settings
        public bool BlinkEnabled => MovementSkills.Any(m => m.CanCrossTerrain);
        public int BlinkRange { get; set; } = 25; // max blink distance in grid cells
        public float BlinkCostPenalty { get; set; } = 30f; // only blink if walking detour > this

        // Dash tracking — prevent spamming movement skills mid-animation
        private bool _dashActive;
        private DateTime _dashStartTime = DateTime.MinValue;
        private const int DashAnimationMs = 300; // assume dash animation takes ~300ms

        // Dash-for-speed: use non-gap-crossing movement skills on long straight path segments
        public int DashMinDistance { get; set; } = 25;       // min straight grid distance ahead (0 = disabled)
        private const float DashPathDeviationMax = 0.85f;    // dot product threshold — path must be this aligned

        public bool IsNavigating { get; private set; }
        public bool IsPaused { get; private set; }
        public List<NavWaypoint> CurrentNavPath { get; private set; } = new(); // grid coordinates
        public int CurrentWaypointIndex { get; private set; }
        public Vector2? Destination { get; private set; } // grid coordinates
        public long LastPathfindMs { get; private set; }
        public int BlinkCount { get; private set; }

        // For rendering compatibility — returns grid positions
        public List<Vector2> CurrentPath => CurrentNavPath.Select(w => w.Position).ToList();

        // Stuck detection and recovery (all in grid units)
        private Vector2 _lastPosition; // grid coordinates
        private float _stuckTimer;
        private const float StuckThreshold = 0.3f;   // grid units — ~3 world units
        private const float StuckTimeLimit = 1.0f;
        // Waypoint reach thresholds in grid units
        private const float WaypointReachedGrid = 10f;   // intermediate waypoints
        private const float FinalWaypointGrid = 14f;     // final destination
        private const float BlinkApproachGrid = 4f;      // tight approach before blink

        // Stuck recovery state
        private int _stuckRecoveryCount;
        private int _totalStuckRecoveries; // persists across repaths, only resets on new target
        private const int MaxRecoveriesBeforeRepath = 3;
        private const float InteractSearchRadius = 28f; // grid units to search for interactables
        public int StuckRecoveries => _totalStuckRecoveries;
        public string LastRecoveryAction { get; private set; } = "";
        private static readonly Random _rng = new();

        // Position history — ring buffer of recent positions for backtrack recovery
        private const int PositionHistorySize = 20;      // ~10 seconds at 0.5s intervals
        private const float PositionHistoryIntervalSec = 0.5f;
        private const float BacktrackMinDistance = 8f;    // grid units — minimum distance from current pos to be a useful backtrack target
        private readonly Vector2[] _positionHistory = new Vector2[PositionHistorySize];
        private int _positionHistoryIndex;
        private int _positionHistoryCount;
        private DateTime _lastPositionHistorySample = DateTime.MinValue;
        private int _stuckAtSameSpotCount;               // how many times stuck at approximately the same position
        private Vector2 _lastStuckPosition;

        // Blink tracking — geometry for wall-side detection (grid coordinates)
        private bool _blinkPending;           // true after blink fires, waiting to confirm crossing
        private Vector2 _blinkBoundary;       // walk waypoint before blink (origin side of gap)
        private Vector2 _blinkLanding;        // blink waypoint position (far side of gap)
        private Vector2 _blinkDirection;      // normalized boundary→landing (the crossing vector)
        private Vector2 _blinkWallMidpoint;   // midpoint of gap — used as wall reference for side detection
        private DateTime _blinkPendingStart;
        private const int BlinkCooldownMs = 500;
        private const int BlinkPendingTimeoutMs = 2000;
        private DateTime _lastBlinkTime = DateTime.MinValue;

        public void Tick(GameController gc)
        {
            if (!IsNavigating || IsPaused || CurrentNavPath.Count == 0)
                return;

            // Clear dash state after animation time
            if (_dashActive && (DateTime.Now - _dashStartTime).TotalMilliseconds > DashAnimationMs)
                _dashActive = false;

            var playerGrid = new Vector2(gc.Player.GridPosNum.X, gc.Player.GridPosNum.Y);

            // Sample position history for backtrack recovery
            if ((DateTime.Now - _lastPositionHistorySample).TotalSeconds >= PositionHistoryIntervalSec)
            {
                _positionHistory[_positionHistoryIndex] = playerGrid;
                _positionHistoryIndex = (_positionHistoryIndex + 1) % PositionHistorySize;
                if (_positionHistoryCount < PositionHistorySize) _positionHistoryCount++;
                _lastPositionHistorySample = DateTime.Now;
            }

            // If we fired a blink, check each tick which side of the wall we're on
            if (_blinkPending)
            {
                var side = GetWallSide(playerGrid);
                var elapsed = (DateTime.Now - _blinkPendingStart).TotalMilliseconds;

                if (side > 0)
                {
                    // Player is on the landing side — blink succeeded
                    _blinkPending = false;
                    _dashActive = false;
                    _stuckTimer = 0;
                    LastRecoveryAction = "Blink crossed";
                    if (Destination.HasValue)
                        NavigateTo(gc, Destination.Value);
                }
                else if (elapsed > BlinkPendingTimeoutMs)
                {
                    _blinkPending = false;
                    _dashActive = false;
                    _stuckTimer = 0;
                    LastRecoveryAction = side < 0
                        ? "Blink timeout (still on origin side), repath"
                        : "Blink timeout (on wall?), repath";
                    if (Destination.HasValue)
                        NavigateTo(gc, Destination.Value);
                }
            }

            // Don't send more movement input while mid-dash
            if (_dashActive)
                return;

            // Check if we've reached the current waypoint (grid distance)
            var currentWp = CurrentNavPath[CurrentWaypointIndex];
            var distToWaypoint = Vector2.Distance(playerGrid, currentWp.Position);

            var isLastWaypoint = CurrentWaypointIndex >= CurrentNavPath.Count - 1;
            var nextIsBlink = !isLastWaypoint && CurrentNavPath[CurrentWaypointIndex + 1].Action == WaypointAction.Blink;
            var reachDist = isLastWaypoint ? FinalWaypointGrid
                : nextIsBlink ? BlinkApproachGrid
                : WaypointReachedGrid;

            if (distToWaypoint < reachDist)
            {
                if (isLastWaypoint)
                {
                    Stop(gc);
                    return;
                }
                CurrentWaypointIndex++;
                _stuckTimer = 0;
            }
            else if (!isLastWaypoint)
            {
                // Pass-through detection: if a dash/blink overshot the current waypoint,
                // skip ahead to the furthest waypoint the player has passed beyond.
                var advanced = false;
                for (var i = CurrentWaypointIndex; i < CurrentNavPath.Count - 1; i++)
                {
                    var wp = CurrentNavPath[i];
                    var nextWp = CurrentNavPath[i + 1];

                    // Don't skip past blink waypoints — they represent gap crossings
                    if (nextWp.Action == WaypointAction.Blink)
                        break;

                    var toNext = nextWp.Position - wp.Position;
                    var toPlayer = playerGrid - wp.Position;

                    if (Vector2.Dot(toNext, toPlayer) > 0)
                    {
                        CurrentWaypointIndex = i + 1;
                        advanced = true;
                    }
                    else
                        break;
                }
                if (advanced)
                    _stuckTimer = 0;
            }

            // Stuck detection (grid distance)
            var moved = Vector2.Distance(playerGrid, _lastPosition);
            if (moved < StuckThreshold)
            {
                _stuckTimer += (float)gc.DeltaTime / 1000f;
                if (_stuckTimer > StuckTimeLimit)
                {
                    _stuckTimer = 0;
                    _stuckRecoveryCount++;
                    _totalStuckRecoveries++;

                    if (_stuckRecoveryCount >= MaxRecoveriesBeforeRepath && Destination.HasValue)
                    {
                        _stuckRecoveryCount = 0;
                        LastRecoveryAction = "Repath";
                        var dest = Destination.Value;
                        NavigateTo(gc, dest);
                        return;
                    }

                    // Track repeated stucks at the same spot
                    if (Vector2.Distance(playerGrid, _lastStuckPosition) < 5f)
                        _stuckAtSameSpotCount++;
                    else
                        _stuckAtSameSpotCount = 1;
                    _lastStuckPosition = playerGrid;

                    // Try to find and interact with a door/breakable
                    if (TryInteractWithObstacle(gc, playerGrid))
                        return;

                    // Try backtracking to a known-good position from history
                    if (TryBacktrack(gc, playerGrid))
                        return;

                    // Fallback: escape probing with escalating distances
                    EscapeProbe(gc, playerGrid);
                }
            }
            else
            {
                _stuckTimer = 0;
                if (moved > StuckThreshold * 5)
                    _stuckRecoveryCount = 0;
            }
            _lastPosition = playerGrid;

            // All input goes through BotInput — if gate is closed, skip this tick
            if (!BotInput.CanAct)
                return;

            // Get current waypoint and determine action
            var waypoint = CurrentNavPath[CurrentWaypointIndex];
            var windowRect = gc.Window.GetWindowRectangle();

            if (waypoint.Action == WaypointAction.Blink)
            {
                var boundary = CurrentWaypointIndex > 0
                    ? CurrentNavPath[CurrentWaypointIndex - 1].Position
                    : playerGrid;

                var crossDir = waypoint.Position - boundary;
                var crossLen = crossDir.Length();
                if (crossLen < 1f)
                {
                    if (CurrentWaypointIndex < CurrentNavPath.Count - 1)
                        CurrentWaypointIndex++;
                    return;
                }
                var crossDirNorm = crossDir / crossLen;

                // Aim past the landing — overshoot in grid then convert to world for screen
                var aimGridPos = playerGrid + crossDirNorm * BlinkRange;
                var blinkScreen = GridToScreen(gc, aimGridPos);
                ExecuteBlink(blinkScreen, windowRect, playerGrid, boundary, waypoint.Position, crossDirNorm);
            }
            else
            {
                var screenPos = GridToScreen(gc, waypoint.Position);

                // If waypoint is too close on screen, the character barely moves (clicks near itself).
                // Skip ahead to the next waypoint, or aim at the destination if this is the last one.
                var screenCenter = new Vector2(windowRect.Width / 2f, windowRect.Height / 2f);
                var screenDist = Vector2.Distance(screenPos, screenCenter);
                const float MinScreenDist = 40f; // pixels — below this, PoE move-clicks produce negligible movement

                if (screenDist < MinScreenDist)
                {
                    // Try to find a further waypoint to aim at
                    Vector2? aimPos = null;
                    for (int i = CurrentWaypointIndex + 1; i < CurrentNavPath.Count; i++)
                    {
                        if (CurrentNavPath[i].Action == WaypointAction.Blink)
                            break; // don't skip past blink waypoints
                        var candidateScreen = GridToScreen(gc, CurrentNavPath[i].Position);
                        if (Vector2.Distance(candidateScreen, screenCenter) >= MinScreenDist)
                        {
                            aimPos = candidateScreen;
                            break;
                        }
                    }

                    if (aimPos.HasValue)
                        screenPos = aimPos.Value;
                    else if (Destination.HasValue)
                        screenPos = GridToScreen(gc, Destination.Value);
                    else
                        return; // everything is too close, skip this tick
                }

                // Try dash-for-speed on long straight segments
                if (!TryDashForSpeed(gc, playerGrid, windowRect))
                    ExecuteWalk(screenPos, windowRect);
            }
        }

        /// <summary>
        /// Convert a grid position to screen coordinates using terrain height.
        /// This is the ONLY place grid→world→screen conversion should happen for navigation.
        /// </summary>
        private static Vector2 GridToScreen(GameController gc, Vector2 gridPos)
        {
            var heightGrid = gc.IngameState?.Data?.RawTerrainHeightData;
            var gx = (int)gridPos.X;
            var gy = (int)gridPos.Y;
            var z = Pathfinding.GetTerrainHeight(heightGrid, gx, gy, gc.Player.PosNum.Z);
            return gc.IngameState.Camera.WorldToScreen(
                new Vector3(gridPos.X * Pathfinding.GridToWorld, gridPos.Y * Pathfinding.GridToWorld, z));
        }

        /// <summary>
        /// Determine which side of the wall the player is on (grid coordinates).
        /// Returns: positive = landing side (crossed), negative = origin side, ~0 = on the wall.
        /// </summary>
        private float GetWallSide(Vector2 playerGrid)
        {
            var offset = playerGrid - _blinkWallMidpoint;
            return Vector2.Dot(offset, _blinkDirection);
        }

        /// <summary>Minimum screen distance (pixels) from center for effective move-clicks.</summary>
        internal const float MinScreenDist = 40f;

        private void ExecuteWalk(Vector2 screenPos, SharpDX.RectangleF windowRect)
        {
            var center = new Vector2(windowRect.Width / 2f, windowRect.Height / 2f);

            Vector2 absPos;
            if (screenPos.X > 0 && screenPos.X < windowRect.Width &&
                screenPos.Y > 0 && screenPos.Y < windowRect.Height)
            {
                // If target is too close to screen center, push it outward along the same
                // direction so the click produces meaningful movement in PoE.
                var dir = screenPos - center;
                if (dir.Length() < MinScreenDist)
                {
                    if (dir.Length() < 1f) return; // target is exactly on player
                    screenPos = center + Vector2.Normalize(dir) * MinScreenDist;
                }
                absPos = new Vector2(windowRect.X + screenPos.X, windowRect.Y + screenPos.Y);
            }
            else
            {
                var dir = new Vector2(screenPos.X, screenPos.Y) - center;
                if (dir.Length() < 1f) return;
                dir = Vector2.Normalize(dir);
                var edgePoint = center + dir * Math.Min(center.X, center.Y) * 0.8f;
                absPos = new Vector2(windowRect.X + edgePoint.X, windowRect.Y + edgePoint.Y);
            }

            BotInput.CursorPressKey(absPos, MoveKey);
        }

        private void ExecuteBlink(Vector2 screenPos, SharpDX.RectangleF windowRect,
            Vector2 playerGrid, Vector2 boundary, Vector2 landing, Vector2 crossDirNorm)
        {
            if ((DateTime.Now - _lastBlinkTime).TotalMilliseconds < BlinkCooldownMs)
                return;

            var gapCrosser = MovementSkills.FirstOrDefault(m => m.CanCrossTerrain && m.IsReady);
            if (gapCrosser == null)
                return;

            if (screenPos.X > 0 && screenPos.X < windowRect.Width &&
                screenPos.Y > 0 && screenPos.Y < windowRect.Height)
            {
                var absPos = new Vector2(windowRect.X + screenPos.X, windowRect.Y + screenPos.Y);
                if (!BotInput.CursorPressKey(absPos, gapCrosser.Key))
                    return; // gate closed

                _lastBlinkTime = DateTime.Now;
                _dashActive = true;
                _dashStartTime = DateTime.Now;

                // Store wall geometry for side detection (grid coords)
                _blinkPending = true;
                _blinkPendingStart = DateTime.Now;
                _blinkBoundary = boundary;
                _blinkLanding = landing;
                _blinkDirection = crossDirNorm;
                _blinkWallMidpoint = (boundary + landing) / 2f;
            }
            else
            {
                ExecuteWalk(screenPos, windowRect);
            }
        }

        /// <summary>
        /// Use a movement skill to speed up travel when the path ahead is long and straight.
        /// Measures distance from PLAYER through remaining waypoints (not just waypoint-to-waypoint).
        /// All distance calculations in grid units.
        /// </summary>
        private bool TryDashForSpeed(GameController gc, Vector2 playerGrid,
            SharpDX.RectangleF windowRect)
        {
            if (DashMinDistance <= 0)
                return false;

            var idx = CurrentWaypointIndex;
            if (idx >= CurrentNavPath.Count)
                return false;

            var startWp = CurrentNavPath[idx].Position;

            // If the next waypoint is a blink, don't dash — let blink logic handle it
            if (CurrentNavPath[idx].Action == WaypointAction.Blink)
                return false;

            // Travel direction: player → current waypoint
            var travelDir = startWp - playerGrid;
            if (travelDir.Length() < 1f)
                return false;
            travelDir = Vector2.Normalize(travelDir);

            // Start with distance from player to current waypoint
            var straightDist = Vector2.Distance(playerGrid, startWp);
            var straightMeasured = false;
            var hasUpcomingBlink = false;

            // Continue accumulating through subsequent waypoints while path stays straight
            var prev = startWp;
            for (var i = idx + 1; i < CurrentNavPath.Count; i++)
            {
                var wp = CurrentNavPath[i];

                if (wp.Action == WaypointAction.Blink)
                {
                    hasUpcomingBlink = true;
                    straightMeasured = true;
                }

                if (!straightMeasured)
                {
                    var segDir = wp.Position - prev;
                    var segLen = segDir.Length();
                    if (segLen < 1f)
                    {
                        prev = wp.Position;
                        continue;
                    }

                    var dot = Vector2.Dot(Vector2.Normalize(segDir), travelDir);
                    if (dot < DashPathDeviationMax)
                    {
                        straightMeasured = true;
                    }
                    else
                    {
                        straightDist += segLen;
                    }
                }
                else if (hasUpcomingBlink)
                {
                    break;
                }

                prev = wp.Position;
            }

            // Not enough straight distance ahead (grid units)
            if (straightDist < DashMinDistance)
                return false;

            // Find a movement skill to use.
            MovementSkillInfo? dashSkill = null;
            foreach (var ms in MovementSkills)
            {
                if (!ms.IsReady)
                    continue;
                if (ms.MinCastIntervalMs > 0 &&
                    (DateTime.Now - ms.LastUsedAt).TotalMilliseconds < ms.MinCastIntervalMs)
                    continue;
                if (ms.CanCrossTerrain && hasUpcomingBlink)
                    continue;

                if (dashSkill == null || (dashSkill.CanCrossTerrain && !ms.CanCrossTerrain))
                    dashSkill = ms;

                if (!ms.CanCrossTerrain)
                    break;
            }
            if (dashSkill == null)
                return false;

            // Aim along the travel direction (grid coords → screen)
            var aimTarget = playerGrid + travelDir * DashMinDistance;
            var aimScreen = GridToScreen(gc, aimTarget);

            if (aimScreen.X <= 0 || aimScreen.X >= windowRect.Width ||
                aimScreen.Y <= 0 || aimScreen.Y >= windowRect.Height)
                return false;

            var absPos = new Vector2(windowRect.X + aimScreen.X, windowRect.Y + aimScreen.Y);
            if (!BotInput.CursorPressKey(absPos, dashSkill.Key))
                return false;

            dashSkill.LastUsedAt = DateTime.Now;
            _dashActive = true;
            _dashStartTime = DateTime.Now;
            return true;
        }

        /// <summary>
        /// Navigate to a grid position using A* pathfinding.
        /// </summary>
        public bool NavigateTo(GameController gc, Vector2 gridTarget, int maxNodes = 0)
        {
            var playerGrid = new Vector2(gc.Player.GridPosNum.X, gc.Player.GridPosNum.Y);
            var pfGrid = gc.IngameState.Data.RawFramePathfindingData;

            if (pfGrid == null || pfGrid.Length == 0)
                return false;

            // Auto-scale node budget based on grid size.
            if (maxNodes <= 0)
            {
                var gridArea = (long)pfGrid.Length * pfGrid[0].Length;
                maxNodes = gridArea > 2_000_000 ? 500_000 : 200_000;
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();

            List<NavWaypoint> rawPath;
            if (BlinkEnabled)
            {
                var tgtGrid = gc.IngameState.Data.RawTerrainTargetingData;
                rawPath = Pathfinding.FindPathWithBlinks(
                    pfGrid, tgtGrid, playerGrid, gridTarget,
                    BlinkRange, BlinkCostPenalty, maxNodes);

                // Fallback: blink scanning is expensive on large grids and may exhaust
                // the node budget. Retry without blinks.
                if (rawPath.Count == 0)
                {
                    var simplePath = Pathfinding.FindPath(pfGrid, playerGrid, gridTarget, maxNodes);
                    rawPath = simplePath.Select(p => new NavWaypoint(p, WaypointAction.Walk)).ToList();
                }
            }
            else
            {
                var simplePath = Pathfinding.FindPath(pfGrid, playerGrid, gridTarget, maxNodes);
                rawPath = simplePath.Select(p => new NavWaypoint(p, WaypointAction.Walk)).ToList();
            }

            sw.Stop();
            LastPathfindMs = sw.ElapsedMilliseconds;

            if (rawPath.Count == 0)
                return false;

            CurrentNavPath = Pathfinding.SmoothNavPath(pfGrid, rawPath);
            CurrentWaypointIndex = 0;

            // Forward-trim: skip walk waypoints the player has already passed.
            for (int i = 0; i < CurrentNavPath.Count - 1; i++)
            {
                if (CurrentNavPath[i + 1].Action == WaypointAction.Blink)
                    break;

                var toNext = CurrentNavPath[i + 1].Position - CurrentNavPath[i].Position;
                var toPlayer = playerGrid - CurrentNavPath[i].Position;

                if (Vector2.Dot(toNext, toPlayer) > 0)
                    CurrentWaypointIndex = i + 1;
                else
                    break;
            }

            if (!Destination.HasValue || Vector2.Distance(Destination.Value, gridTarget) > 10f)
                _totalStuckRecoveries = 0;
            Destination = gridTarget;
            IsNavigating = true;
            BlinkCount = CurrentNavPath.Count(w => w.Action == WaypointAction.Blink);
            _blinkPending = false;
            _stuckTimer = 0;
            _lastPosition = playerGrid;

            return true;
        }

        /// <summary>
        /// Look for interactable entities (doors, breakables) between player and next waypoint.
        /// </summary>
        private bool TryInteractWithObstacle(GameController gc, Vector2 playerGrid)
        {
            var waypoint = CurrentNavPath[CurrentWaypointIndex];
            var dirToWaypoint = Vector2.Normalize(waypoint.Position - playerGrid);

            Entity? bestTarget = null;
            float bestScore = float.MaxValue;

            foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
            {
                if (!IsInteractableObstacle(entity))
                    continue;

                var entityGrid = new Vector2(entity.GridPosNum.X, entity.GridPosNum.Y);
                var dist = Vector2.Distance(playerGrid, entityGrid);

                if (dist > InteractSearchRadius || dist < 1f)
                    continue;

                var dirToEntity = Vector2.Normalize(entityGrid - playerGrid);
                var dot = Vector2.Dot(dirToEntity, dirToWaypoint);

                if (dot < 0f)
                    continue;

                var score = dist * (2f - dot);
                if (score < bestScore)
                {
                    bestScore = score;
                    bestTarget = entity;
                }
            }

            if (bestTarget == null)
                return false;

            if (BotInput.ClickEntity(gc, bestTarget))
            {
                LastRecoveryAction = $"Interact: {bestTarget.Path?.Split('/').LastOrDefault() ?? "?"}";
                return true;
            }

            return false;
        }

        private static bool IsInteractableObstacle(Entity entity)
        {
            if (entity.Path == null)
                return false;

            var path = entity.Path;
            if (path.Contains("Door") || path.Contains("Blockage") ||
                path.Contains("Breakable") || path.Contains("Switch"))
            {
                if (entity.IsTargetable)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Try to backtrack to a known-good position from history.
        /// Only picks positions that are CLOSER to the destination than where we are now,
        /// to avoid oscillating away from the goal. Clears history after backtracking
        /// to prevent ping-pong between two stuck positions.
        /// </summary>
        private bool TryBacktrack(GameController gc, Vector2 playerGrid)
        {
            if (_positionHistoryCount == 0 || !Destination.HasValue) return false;

            var dest = Destination.Value;
            var currentDistToDest = Vector2.Distance(playerGrid, dest);

            // Scan history for a position that is:
            //  1) Far enough from current pos to be useful (BacktrackMinDistance)
            //  2) CLOSER to the destination than we are now
            // Among qualifying positions, pick the one closest to the destination.
            Vector2? bestBacktrack = null;
            float bestDistToDest = currentDistToDest;

            for (int i = 1; i <= _positionHistoryCount; i++)
            {
                var idx = (_positionHistoryIndex - i + PositionHistorySize) % PositionHistorySize;
                var histPos = _positionHistory[idx];
                var distFromPlayer = Vector2.Distance(playerGrid, histPos);
                var distToDest = Vector2.Distance(histPos, dest);

                if (distFromPlayer > BacktrackMinDistance && distToDest < bestDistToDest)
                {
                    bestDistToDest = distToDest;
                    bestBacktrack = histPos;
                }
            }

            if (!bestBacktrack.HasValue) return false;

            // Clear history to prevent ping-pong oscillation
            _positionHistoryCount = 0;
            _positionHistoryIndex = 0;

            var screenPos = GridToScreen(gc, bestBacktrack.Value);
            var windowRect = gc.Window.GetWindowRectangle();
            ExecuteWalk(screenPos, windowRect);
            LastRecoveryAction = $"Backtrack ({Vector2.Distance(playerGrid, bestBacktrack.Value):F0}g, {currentDistToDest - bestDistToDest:F0}g closer)";
            return true;
        }

        /// <summary>
        /// Escape probing — try random directional probes at escalating distances.
        /// When stuck at the same spot repeatedly, uses larger probe distances.
        /// </summary>
        private void EscapeProbe(GameController gc, Vector2 playerGrid)
        {
            var waypoint = CurrentNavPath[CurrentWaypointIndex];
            var dirToWaypoint = waypoint.Position - playerGrid;
            if (dirToWaypoint.Length() > 0)
                dirToWaypoint = Vector2.Normalize(dirToWaypoint);

            // Escalating probe distances based on how many times stuck at same spot
            float probeDistance = _stuckAtSameSpotCount switch
            {
                <= 1 => 18f,
                2 => 30f,
                3 => 50f,
                _ => 70f,
            };

            var angle = (float)(_rng.NextDouble() * Math.PI - Math.PI / 2);
            var cos = (float)Math.Cos(angle);
            var sin = (float)Math.Sin(angle);
            var nudgeDir = new Vector2(
                dirToWaypoint.X * cos - dirToWaypoint.Y * sin,
                dirToWaypoint.X * sin + dirToWaypoint.Y * cos);

            var nudgeTarget = playerGrid + nudgeDir * probeDistance;
            var screenPos = GridToScreen(gc, nudgeTarget);
            var windowRect = gc.Window.GetWindowRectangle();

            ExecuteWalk(screenPos, windowRect);
            LastRecoveryAction = $"Escape probe ({probeDistance:F0}g, attempt #{_stuckAtSameSpotCount})";
        }

        // ═══════════════════════════════════════════════════
        // Terrain queries — used by CombatSystem for position validation
        // ═══════════════════════════════════════════════════

        /// <summary>
        /// Check if a grid cell is walkable (pathfinding value >= 3).
        /// Returns false if terrain data is unavailable.
        /// </summary>
        public bool IsWalkable(GameController gc, int gx, int gy)
        {
            var pfGrid = gc.IngameState.Data.RawFramePathfindingData;
            if (pfGrid == null) return false;
            return Pathfinding.IsWalkableCell(pfGrid, gx, gy);
        }

        /// <summary>
        /// Check walkable LOS between two grid positions using the pathfinding grid.
        /// Returns true if a straight walk is safe (no walls, no fringe cells).
        /// Returns true on degraded data (graceful fallback — assume open).
        /// </summary>
        public bool HasWalkableLOS(GameController gc, Vector2 gridA, Vector2 gridB)
        {
            var pfGrid = gc.IngameState.Data.RawFramePathfindingData;
            if (pfGrid == null) return true;
            return Pathfinding.HasLineOfSight(pfGrid, gridA, gridB);
        }

        public bool HasTargetingLOS(GameController gc, Vector2 gridA, Vector2 gridB)
        {
            var tgtGrid = gc.IngameState.Data.RawTerrainTargetingData;
            if (tgtGrid == null) return true; // graceful degradation
            return Pathfinding.HasTargetingLOS(tgtGrid,
                (int)gridA.X, (int)gridA.Y, (int)gridB.X, (int)gridB.Y);
        }

        /// <summary>
        /// Find the nearest walkable cell to a grid position within searchRadius.
        /// Returns null if nothing walkable found or terrain data unavailable.
        /// </summary>
        public Vector2? FindNearestWalkable(GameController gc, Vector2 gridPos, int searchRadius = 10)
        {
            var pfGrid = gc.IngameState.Data.RawFramePathfindingData;
            if (pfGrid == null) return null;
            var result = Pathfinding.FindNearestWalkableCell(pfGrid, (int)gridPos.X, (int)gridPos.Y, searchRadius);
            if (result == null) return null;
            return new Vector2(result.Value.x, result.Value.y);
        }

        /// <summary>
        /// Find the nearest walkable cell that also has targeting LOS to a target grid position.
        /// Used by CombatSystem to find valid attack positions.
        /// </summary>
        public Vector2? FindWalkableWithLOS(GameController gc, Vector2 gridPos, Vector2 losTarget, int searchRadius = 10)
        {
            var pfGrid = gc.IngameState.Data.RawFramePathfindingData;
            var tgtGrid = gc.IngameState.Data.RawTerrainTargetingData;
            if (pfGrid == null) return null;

            int gx = (int)gridPos.X, gy = (int)gridPos.Y;
            int tx = (int)losTarget.X, ty = (int)losTarget.Y;

            // Check the position itself first
            if (Pathfinding.IsWalkableCell(pfGrid, gx, gy) &&
                (tgtGrid == null || Pathfinding.HasTargetingLOS(tgtGrid, gx, gy, tx, ty)))
                return gridPos;

            // Search expanding rings
            float bestDist = float.MaxValue;
            Vector2? best = null;
            for (int r = 1; r <= searchRadius; r++)
            {
                for (int dx = -r; dx <= r; dx++)
                {
                    for (int dy = -r; dy <= r; dy++)
                    {
                        if (Math.Abs(dx) != r && Math.Abs(dy) != r) continue;
                        int cx = gx + dx, cy = gy + dy;
                        if (!Pathfinding.IsWalkableCell(pfGrid, cx, cy)) continue;
                        if (tgtGrid != null && !Pathfinding.HasTargetingLOS(tgtGrid, cx, cy, tx, ty)) continue;
                        float dist = dx * dx + dy * dy;
                        if (dist < bestDist)
                        {
                            bestDist = dist;
                            best = new Vector2(cx, cy);
                        }
                    }
                }
                if (best.HasValue) return best; // found on this ring, closest possible
            }
            return best;
        }

        public bool NavigateToTile(GameController gc, TileMap tileMap, string searchString)
        {
            var playerGridPos = gc.Player.GridPosNum;
            var tileGridPos = tileMap.FindTilePosition(searchString, playerGridPos);
            if (tileGridPos == null)
                return false;

            return NavigateTo(gc, tileGridPos.Value, maxNodes: 500000);
        }

        /// <summary>
        /// Temporarily pause navigation — path is preserved but Tick does nothing.
        /// Used by combat to take over movement without losing the nav path.
        /// </summary>
        public void Pause()
        {
            if (IsNavigating)
                IsPaused = true;
        }

        /// <summary>
        /// Resume paused navigation. Resets stuck timer to avoid false stuck detection.
        /// </summary>
        public void Resume(GameController gc)
        {
            if (IsPaused)
            {
                IsPaused = false;
                // Reset stuck timer — we moved during combat
                _lastPosition = new Vector2(gc.Player.GridPosNum.X, gc.Player.GridPosNum.Y);
                _stuckTimer = 0;
                _stuckRecoveryCount = 0;
            }
        }

        public void Stop(GameController gc)
        {
            IsNavigating = false;
            IsPaused = false;
            CurrentNavPath.Clear();
            CurrentWaypointIndex = 0;
            Destination = null;
            BlinkCount = 0;
            _blinkPending = false;
            _dashActive = false;
            _blinkDirection = Vector2.Zero;
            _blinkWallMidpoint = Vector2.Zero;
            _stuckRecoveryCount = 0;
            _totalStuckRecoveries = 0;
            LastRecoveryAction = "";
            _positionHistoryCount = 0;
            _positionHistoryIndex = 0;
            _stuckAtSameSpotCount = 0;
        }

        /// <summary>
        /// Direct movement toward a grid position — no pathfinding, no waypoints.
        /// Use when LOS is clear and the caller is managing movement each tick.
        /// Clears any active navigation. Returns false if BotInput gate is blocked.
        /// </summary>
        public bool MoveToward(GameController gc, Vector2 gridTarget)
        {
            // Clear any active path — caller is driving movement directly
            if (IsNavigating)
            {
                IsNavigating = false;
                IsPaused = false;
                CurrentNavPath.Clear();
                CurrentWaypointIndex = 0;
                Destination = null;
                BlinkCount = 0;
                _blinkPending = false;
                _dashActive = false;
            }

            // Don't send input while mid-dash animation
            if (_dashActive)
            {
                if ((DateTime.Now - _dashStartTime).TotalMilliseconds > DashAnimationMs)
                    _dashActive = false;
                else
                    return false;
            }

            if (!BotInput.CanAct)
                return false;

            var playerGrid = new Vector2(gc.Player.GridPosNum.X, gc.Player.GridPosNum.Y);
            var windowRect = gc.Window.GetWindowRectangle();

            // Try movement skills for speed if distance is long enough
            if (TryDirectDash(gc, playerGrid, gridTarget, windowRect))
                return true;

            var screenPos = GridToScreen(gc, gridTarget);

            ExecuteWalk(screenPos, windowRect);
            return true;
        }

        /// <summary>
        /// Try to use a movement skill for direct LOS movement (no path waypoints).
        /// </summary>
        private bool TryDirectDash(GameController gc, Vector2 playerGrid, Vector2 gridTarget,
            SharpDX.RectangleF windowRect)
        {
            if (DashMinDistance <= 0)
                return false;

            var dist = Vector2.Distance(playerGrid, gridTarget);
            if (dist < DashMinDistance)
                return false;

            // Find any ready movement skill
            MovementSkillInfo? dashSkill = null;
            foreach (var ms in MovementSkills)
            {
                if (!ms.IsReady)
                    continue;
                if (ms.MinCastIntervalMs > 0 &&
                    (DateTime.Now - ms.LastUsedAt).TotalMilliseconds < ms.MinCastIntervalMs)
                    continue;

                if (dashSkill == null || (dashSkill.CanCrossTerrain && !ms.CanCrossTerrain))
                    dashSkill = ms;

                if (!ms.CanCrossTerrain)
                    break;
            }
            if (dashSkill == null)
                return false;

            // Aim toward the target
            var travelDir = Vector2.Normalize(gridTarget - playerGrid);
            var aimTarget = playerGrid + travelDir * DashMinDistance;
            var aimScreen = GridToScreen(gc, aimTarget);

            if (aimScreen.X <= 0 || aimScreen.X >= windowRect.Width ||
                aimScreen.Y <= 0 || aimScreen.Y >= windowRect.Height)
                return false;

            var absPos = new Vector2(windowRect.X + aimScreen.X, windowRect.Y + aimScreen.Y);
            if (!BotInput.CursorPressKey(absPos, dashSkill.Key))
                return false;

            dashSkill.LastUsedAt = DateTime.Now;
            _dashActive = true;
            _dashStartTime = DateTime.Now;
            return true;
        }

        /// <summary>
        /// Update the destination of an active navigation for a moving target.
        /// All positions in grid coordinates.
        /// </summary>
        public bool UpdateDestination(GameController gc, Vector2 gridTarget, float driftThreshold = 14f)
        {
            if (!IsNavigating || CurrentNavPath.Count == 0)
                return false;

            var currentDest = Destination ?? Vector2.Zero;
            if (Vector2.Distance(currentDest, gridTarget) < driftThreshold)
                return false;

            var playerGrid = new Vector2(gc.Player.GridPosNum.X, gc.Player.GridPosNum.Y);
            var pfGrid = gc.IngameState.Data.RawFramePathfindingData;

            if (pfGrid == null || pfGrid.Length == 0)
                return false;

            if (Pathfinding.HasLineOfSight(pfGrid, playerGrid, gridTarget))
            {
                // LOS clear — truncate path at current waypoint, append direct waypoint
                var truncated = new List<NavWaypoint>();

                if (CurrentWaypointIndex < CurrentNavPath.Count)
                    truncated.Add(CurrentNavPath[CurrentWaypointIndex]);

                truncated.Add(new NavWaypoint(gridTarget, WaypointAction.Walk));

                CurrentNavPath = truncated;
                CurrentWaypointIndex = 0;
                Destination = gridTarget;
                BlinkCount = 0;
                return true;
            }

            // No LOS — full repath
            return NavigateTo(gc, gridTarget);
        }
    }
}
