using ExileCore;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using System.Numerics;
using System.Windows.Forms;

namespace AutoExile.Systems
{
    /// <summary>
    /// Handles pathfinding and movement execution.
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
        public int DashMinDistance { get; set; } = 60;       // min straight grid distance ahead (0 = disabled)
        private const float DashPathDeviationMax = 0.85f;    // dot product threshold — path must be this aligned

        public bool IsNavigating { get; private set; }
        public bool IsPaused { get; private set; }
        public List<NavWaypoint> CurrentNavPath { get; private set; } = new();
        public int CurrentWaypointIndex { get; private set; }
        public Vector2? Destination { get; private set; }
        public long LastPathfindMs { get; private set; }
        public int BlinkCount { get; private set; }

        // For rendering compatibility
        public List<Vector2> CurrentPath => CurrentNavPath.Select(w => w.Position).ToList();

        // Stuck detection and recovery
        private Vector2 _lastPosition;
        private float _stuckTimer;
        private const float StuckThreshold = 3f;
        private const float StuckTimeLimit = 1.0f;
        // Waypoint reach thresholds in grid units — converted to world at check time
        private const float WaypointReachedGrid = 10f;   // intermediate waypoints
        private const float FinalWaypointGrid = 14f;     // final destination
        private const float BlinkApproachGrid = 4f;      // tight approach before blink

        // Stuck recovery state
        private int _stuckRecoveryCount;
        private int _totalStuckRecoveries; // persists across repaths, only resets on new target
        private const int MaxRecoveriesBeforeRepath = 3;
        private const float InteractSearchRadius = 300f; // world units to search for interactables
        public int StuckRecoveries => _totalStuckRecoveries;
        public string LastRecoveryAction { get; private set; } = "";
        private static readonly Random _rng = new();

        // Blink tracking — geometry for wall-side detection
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

            var playerPos = gc.Player.PosNum;
            var playerWorld = new Vector2(playerPos.X, playerPos.Y);

            // If we fired a blink, check each tick which side of the wall we're on
            if (_blinkPending)
            {
                var side = GetWallSide(playerWorld);
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

            // Check if we've reached the current waypoint
            var currentWp = CurrentNavPath[CurrentWaypointIndex];
            var distToWaypoint = Vector2.Distance(playerWorld, currentWp.Position);

            var isLastWaypoint = CurrentWaypointIndex >= CurrentNavPath.Count - 1;
            var nextIsBlink = !isLastWaypoint && CurrentNavPath[CurrentWaypointIndex + 1].Action == WaypointAction.Blink;
            var reachGrid = isLastWaypoint ? FinalWaypointGrid
                : nextIsBlink ? BlinkApproachGrid
                : WaypointReachedGrid;
            var reachDist = reachGrid * Pathfinding.GridToWorld;

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
                // Uses dot product: if player is past waypoint along the path direction,
                // the waypoint is behind us and should be skipped.
                var advanced = false;
                for (var i = CurrentWaypointIndex; i < CurrentNavPath.Count - 1; i++)
                {
                    var wp = CurrentNavPath[i];
                    var nextWp = CurrentNavPath[i + 1];

                    // Don't skip past blink waypoints — they represent gap crossings
                    // that require precise positioning
                    if (nextWp.Action == WaypointAction.Blink)
                        break;

                    var toNext = nextWp.Position - wp.Position;
                    var toPlayer = playerWorld - wp.Position;

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

            // Stuck detection
            var moved = Vector2.Distance(playerWorld, _lastPosition);
            if (moved < StuckThreshold)
            {
                _stuckTimer += (float)gc.DeltaTime;
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

                    // Try to find and interact with a door/breakable
                    if (TryInteractWithObstacle(gc, playerWorld))
                        return;

                    // Fallback: micro-movement in a random direction
                    MicroMovement(gc, playerWorld);
                }
            }
            else
            {
                _stuckTimer = 0;
                if (moved > StuckThreshold * 5)
                    _stuckRecoveryCount = 0;
            }
            _lastPosition = playerWorld;

            // All input goes through BotInput — if gate is closed, skip this tick
            if (!BotInput.CanAct)
                return;

            // Get current waypoint and determine action
            var waypoint = CurrentNavPath[CurrentWaypointIndex];
            var playerZ = gc.Player.PosNum.Z;
            var windowRect = gc.Window.GetWindowRectangle();

            if (waypoint.Action == WaypointAction.Blink)
            {
                var boundary = CurrentWaypointIndex > 0
                    ? CurrentNavPath[CurrentWaypointIndex - 1].Position
                    : playerWorld;

                var crossDir = waypoint.Position - boundary;
                var crossLen = crossDir.Length();
                if (crossLen < 1f)
                {
                    if (CurrentWaypointIndex < CurrentNavPath.Count - 1)
                        CurrentWaypointIndex++;
                    return;
                }
                var crossDirNorm = crossDir / crossLen;

                var aimDist = BlinkRange * Pathfinding.GridToWorld;
                var aimPos = playerWorld + crossDirNorm * aimDist;

                var blinkScreen = gc.IngameState.Camera.WorldToScreen(
                    new Vector3(aimPos.X, aimPos.Y, playerZ));
                ExecuteBlink(blinkScreen, windowRect, playerWorld, boundary, waypoint.Position, crossDirNorm);
            }
            else
            {
                var screenPos = gc.IngameState.Camera.WorldToScreen(
                    new Vector3(waypoint.Position.X, waypoint.Position.Y, playerZ));

                // Try dash-for-speed on long straight segments
                if (!TryDashForSpeed(gc, playerWorld, playerZ, windowRect))
                    ExecuteWalk(screenPos, windowRect);
            }
        }

        /// <summary>
        /// Determine which side of the wall the player is on.
        /// Returns: positive = landing side (crossed), negative = origin side, ~0 = on the wall.
        /// </summary>
        private float GetWallSide(Vector2 playerWorld)
        {
            var offset = playerWorld - _blinkWallMidpoint;
            return Vector2.Dot(offset, _blinkDirection);
        }

        private void ExecuteWalk(Vector2 screenPos, SharpDX.RectangleF windowRect)
        {
            Vector2 absPos;
            if (screenPos.X > 0 && screenPos.X < windowRect.Width &&
                screenPos.Y > 0 && screenPos.Y < windowRect.Height)
            {
                absPos = new Vector2(windowRect.X + screenPos.X, windowRect.Y + screenPos.Y);
            }
            else
            {
                var center = new Vector2(windowRect.Width / 2f, windowRect.Height / 2f);
                var dir = new Vector2(screenPos.X, screenPos.Y) - center;
                if (dir.Length() < 1f) return;
                dir = Vector2.Normalize(dir);
                var edgePoint = center + dir * Math.Min(center.X, center.Y) * 0.8f;
                absPos = new Vector2(windowRect.X + edgePoint.X, windowRect.Y + edgePoint.Y);
            }

            BotInput.CursorPressKey(absPos, MoveKey);
        }

        private void ExecuteBlink(Vector2 screenPos, SharpDX.RectangleF windowRect,
            Vector2 playerWorld, Vector2 boundary, Vector2 landing, Vector2 crossDirNorm)
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

                // Store wall geometry for side detection
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
        /// Prefers non-gap-crossing skills (Dash, Shield Charge). Falls back to gap-crossing
        /// skills (Blink, Leap Slam) when no upcoming blink waypoint needs them reserved.
        /// Returns true if a skill was fired.
        /// </summary>
        private bool TryDashForSpeed(GameController gc, Vector2 playerWorld, float playerZ,
            SharpDX.RectangleF windowRect)
        {
            if (DashMinDistance <= 0)
                return false;

            // Measure straight-line distance from the CURRENT waypoint forward along the path.
            // Also detect if any blink waypoints lie ahead (gap crossings that need a blink skill).
            var idx = CurrentWaypointIndex;
            if (idx >= CurrentNavPath.Count)
                return false;

            var startWp = CurrentNavPath[idx].Position;

            // Direction from current waypoint to next (the travel direction at dash point)
            Vector2 travelDir;
            if (idx + 1 < CurrentNavPath.Count)
            {
                travelDir = CurrentNavPath[idx + 1].Position - startWp;
                if (travelDir.Length() < 1f)
                    return false;
                travelDir = Vector2.Normalize(travelDir);
            }
            else
            {
                // Only one waypoint left — use player-to-waypoint direction
                travelDir = startWp - playerWorld;
                if (travelDir.Length() < 1f)
                    return false;
                travelDir = Vector2.Normalize(travelDir);
            }

            // Walk forward from current waypoint, accumulating distance while path stays straight.
            // Also scan the full remaining path for any blink waypoints.
            var straightDist = 0f;
            var straightMeasured = false; // true once we've hit a deviation or blink
            var hasUpcomingBlink = false;
            var prev = startWp;
            for (var i = idx + 1; i < CurrentNavPath.Count; i++)
            {
                var wp = CurrentNavPath[i];

                if (wp.Action == WaypointAction.Blink)
                {
                    hasUpcomingBlink = true;
                    straightMeasured = true; // stop accumulating straight distance at blink
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
                // Once we've found a blink we can stop scanning entirely
                else if (hasUpcomingBlink)
                {
                    break;
                }

                prev = wp.Position;
            }

            // Not enough straight distance ahead — don't waste the skill
            var minStraightWorld = DashMinDistance * Pathfinding.GridToWorld;
            if (straightDist < minStraightWorld)
                return false;

            // Find a movement skill to use.
            // Prefer non-gap-crossing skills. Use gap-crossing skills only when
            // no blink waypoints ahead need them reserved for gap traversal.
            MovementSkillInfo? dashSkill = null;
            foreach (var ms in MovementSkills)
            {
                if (!ms.IsReady)
                    continue;
                if (ms.MinCastIntervalMs > 0 &&
                    (DateTime.Now - ms.LastUsedAt).TotalMilliseconds < ms.MinCastIntervalMs)
                    continue;
                if (ms.CanCrossTerrain && hasUpcomingBlink)
                    continue; // reserve gap-crosser for upcoming blink waypoint

                // Prefer non-gap-crossing; take gap-crossing only if we haven't found one yet
                if (dashSkill == null || (dashSkill.CanCrossTerrain && !ms.CanCrossTerrain))
                    dashSkill = ms;

                // Found a non-gap-crossing skill — that's ideal, stop looking
                if (!ms.CanCrossTerrain)
                    break;
            }
            if (dashSkill == null)
                return false;

            // Aim along the travel direction
            var aimTarget = playerWorld + travelDir * minStraightWorld;
            var aimScreen = gc.IngameState.Camera.WorldToScreen(
                new Vector3(aimTarget.X, aimTarget.Y, playerZ));

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

        public bool NavigateTo(GameController gc, Vector2 worldTarget, int maxNodes = 0)
        {
            var playerPos = gc.Player.PosNum;
            var start = new Vector2(playerPos.X, playerPos.Y);
            var pfGrid = gc.IngameState.Data.RawFramePathfindingData;

            if (pfGrid == null || pfGrid.Length == 0)
                return false;

            // Auto-scale node budget based on grid size.
            // Atlas maps (~800x800) need ~200K, campaign zones (~1800x1800) need ~500K+.
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
                    pfGrid, tgtGrid, start, worldTarget,
                    BlinkRange, BlinkCostPenalty, maxNodes);

                // Fallback: blink scanning is expensive on large grids and may exhaust
                // the node budget before reaching the goal. Retry without blinks —
                // regular A* is much cheaper per node and can reach farther.
                // If the path truly needs a blink crossing, navigation will repath
                // when it reaches the gap (closer = smaller search space).
                if (rawPath.Count == 0)
                {
                    var simplePath = Pathfinding.FindPath(pfGrid, start, worldTarget, maxNodes);
                    rawPath = simplePath.Select(p => new NavWaypoint(p, WaypointAction.Walk)).ToList();
                }
            }
            else
            {
                var simplePath = Pathfinding.FindPath(pfGrid, start, worldTarget, maxNodes);
                rawPath = simplePath.Select(p => new NavWaypoint(p, WaypointAction.Walk)).ToList();
            }

            sw.Stop();
            LastPathfindMs = sw.ElapsedMilliseconds;

            if (rawPath.Count == 0)
                return false;

            CurrentNavPath = Pathfinding.SmoothNavPath(pfGrid, rawPath);
            CurrentWaypointIndex = 0;

            // Forward-trim: skip walk waypoints the player has already passed.
            // Prevents stutter-stepping backward when repathing to a moving target.
            // Uses dot product to check if player is on the "far side" of each waypoint
            // (i.e., closer to the next waypoint than back toward this one).
            for (int i = 0; i < CurrentNavPath.Count - 1; i++)
            {
                // Don't skip past blink approach points
                if (CurrentNavPath[i + 1].Action == WaypointAction.Blink)
                    break;

                var toNext = CurrentNavPath[i + 1].Position - CurrentNavPath[i].Position;
                var toPlayer = start - CurrentNavPath[i].Position;

                if (Vector2.Dot(toNext, toPlayer) > 0)
                    CurrentWaypointIndex = i + 1;
                else
                    break;
            }

            if (!Destination.HasValue || Vector2.Distance(Destination.Value, worldTarget) > 100f)
                _totalStuckRecoveries = 0;
            Destination = worldTarget;
            IsNavigating = true;
            BlinkCount = CurrentNavPath.Count(w => w.Action == WaypointAction.Blink);
            _blinkPending = false;
            _stuckTimer = 0;
            _lastPosition = start;

            return true;
        }

        /// <summary>
        /// Look for interactable entities (doors, breakables) between player and next waypoint.
        /// </summary>
        private bool TryInteractWithObstacle(GameController gc, Vector2 playerWorld)
        {
            var waypoint = CurrentNavPath[CurrentWaypointIndex];
            var dirToWaypoint = Vector2.Normalize(waypoint.Position - playerWorld);

            Entity? bestTarget = null;
            float bestScore = float.MaxValue;

            foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
            {
                if (!IsInteractableObstacle(entity))
                    continue;

                var entityPos = new Vector2(entity.PosNum.X, entity.PosNum.Y);
                var dist = Vector2.Distance(playerWorld, entityPos);

                if (dist > InteractSearchRadius || dist < 10f)
                    continue;

                var dirToEntity = Vector2.Normalize(entityPos - playerWorld);
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

            var targetPos = new Vector2(bestTarget.PosNum.X, bestTarget.PosNum.Y);
            var playerZ = gc.Player.PosNum.Z;
            var screenPos = gc.IngameState.Camera.WorldToScreen(
                new Vector3(targetPos.X, targetPos.Y, playerZ));
            var windowRect = gc.Window.GetWindowRectangle();

            if (screenPos.X > 0 && screenPos.X < windowRect.Width &&
                screenPos.Y > 0 && screenPos.Y < windowRect.Height)
            {
                var absPos = new Vector2(windowRect.X + screenPos.X, windowRect.Y + screenPos.Y);
                BotInput.Click(absPos);
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

        private void MicroMovement(GameController gc, Vector2 playerWorld)
        {
            var waypoint = CurrentNavPath[CurrentWaypointIndex];
            var dirToWaypoint = waypoint.Position - playerWorld;
            if (dirToWaypoint.Length() > 0)
                dirToWaypoint = Vector2.Normalize(dirToWaypoint);

            var angle = (float)(_rng.NextDouble() * Math.PI - Math.PI / 2);
            var cos = (float)Math.Cos(angle);
            var sin = (float)Math.Sin(angle);
            var nudgeDir = new Vector2(
                dirToWaypoint.X * cos - dirToWaypoint.Y * sin,
                dirToWaypoint.X * sin + dirToWaypoint.Y * cos);

            var nudgeTarget = playerWorld + nudgeDir * 200f;
            var playerZ = gc.Player.PosNum.Z;
            var screenPos = gc.IngameState.Camera.WorldToScreen(
                new Vector3(nudgeTarget.X, nudgeTarget.Y, playerZ));
            var windowRect = gc.Window.GetWindowRectangle();

            ExecuteWalk(screenPos, windowRect);
            LastRecoveryAction = "Micro-move";
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
        /// Check targeting-layer LOS between two grid positions.
        /// Returns true if skills/projectiles can pass between A and B (targeting > 0 along line).
        /// Returns false if terrain data unavailable.
        /// </summary>
        /// <summary>
        /// Check walkable LOS between two world positions using the pathfinding grid.
        /// Returns true if a straight walk is safe (no walls, no fringe cells).
        /// Returns true on degraded data (graceful fallback — assume open).
        /// </summary>
        public bool HasWalkableLOS(GameController gc, Vector2 worldA, Vector2 worldB)
        {
            var pfGrid = gc.IngameState.Data.RawFramePathfindingData;
            if (pfGrid == null) return true;
            return Pathfinding.HasLineOfSight(pfGrid, worldA, worldB);
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

            var worldTarget = TileMap.GridToWorld(tileGridPos.Value);
            return NavigateTo(gc, worldTarget, maxNodes: 500000);
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
                var pos = gc.Player.PosNum;
                _lastPosition = new Vector2(pos.X, pos.Y);
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
        }

        /// <summary>
        /// Direct movement toward a world position — no pathfinding, no waypoints.
        /// Use when LOS is clear and the caller is managing movement each tick.
        /// Clears any active navigation. Returns false if BotInput gate is blocked.
        /// Tries movement skills for speed on long distances before falling back to walk.
        /// </summary>
        public bool MoveToward(GameController gc, Vector2 worldTarget)
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

            var playerPos = gc.Player.PosNum;
            var playerWorld = new Vector2(playerPos.X, playerPos.Y);
            var playerZ = playerPos.Z;
            var windowRect = gc.Window.GetWindowRectangle();

            // Try movement skills for speed if distance is long enough
            if (TryDirectDash(gc, playerWorld, worldTarget, playerZ, windowRect))
                return true;

            var screenPos = gc.IngameState.Camera.WorldToScreen(
                new Vector3(worldTarget.X, worldTarget.Y, playerZ));

            ExecuteWalk(screenPos, windowRect);
            return true;
        }

        /// <summary>
        /// Try to use a movement skill for direct LOS movement (no path waypoints).
        /// Similar to TryDashForSpeed but for pathless movement — uses raw distance to target.
        /// No blink reservation needed since there are no blink waypoints in direct movement.
        /// </summary>
        private bool TryDirectDash(GameController gc, Vector2 playerWorld, Vector2 worldTarget,
            float playerZ, SharpDX.RectangleF windowRect)
        {
            if (DashMinDistance <= 0)
                return false;

            var dist = Vector2.Distance(playerWorld, worldTarget);
            var minStraightWorld = DashMinDistance * Pathfinding.GridToWorld;
            if (dist < minStraightWorld)
                return false;

            // Find any ready movement skill — no blink reservation since there's no path with gaps
            MovementSkillInfo? dashSkill = null;
            foreach (var ms in MovementSkills)
            {
                if (!ms.IsReady)
                    continue;
                if (ms.MinCastIntervalMs > 0 &&
                    (DateTime.Now - ms.LastUsedAt).TotalMilliseconds < ms.MinCastIntervalMs)
                    continue;

                // Prefer non-gap-crossing; take gap-crossing only if nothing better
                if (dashSkill == null || (dashSkill.CanCrossTerrain && !ms.CanCrossTerrain))
                    dashSkill = ms;

                if (!ms.CanCrossTerrain)
                    break;
            }
            if (dashSkill == null)
                return false;

            // Aim toward the target
            var travelDir = Vector2.Normalize(worldTarget - playerWorld);
            var aimTarget = playerWorld + travelDir * minStraightWorld;
            var aimScreen = gc.IngameState.Camera.WorldToScreen(
                new Vector3(aimTarget.X, aimTarget.Y, playerZ));

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
        /// If LOS is clear to the new target, grafts a direct waypoint onto the current path.
        /// If LOS is blocked, computes a new path with forward-trim to avoid backtracking.
        /// Returns false if not currently navigating or destination hasn't drifted enough.
        /// </summary>
        public bool UpdateDestination(GameController gc, Vector2 worldTarget, float driftThreshold = 0f)
        {
            if (!IsNavigating || CurrentNavPath.Count == 0)
                return false;

            if (driftThreshold <= 0f)
                driftThreshold = 14f * Pathfinding.GridToWorld;

            var currentDest = Destination ?? Vector2.Zero;
            if (Vector2.Distance(currentDest, worldTarget) < driftThreshold)
                return false;

            var playerPos = gc.Player.PosNum;
            var playerWorld = new Vector2(playerPos.X, playerPos.Y);
            var pfGrid = gc.IngameState.Data.RawFramePathfindingData;

            if (pfGrid == null || pfGrid.Length == 0)
                return false;

            if (Pathfinding.HasLineOfSight(pfGrid, playerWorld, worldTarget))
            {
                // LOS clear — truncate path at current waypoint, append direct waypoint
                var truncated = new List<NavWaypoint>();

                if (CurrentWaypointIndex < CurrentNavPath.Count)
                    truncated.Add(CurrentNavPath[CurrentWaypointIndex]);

                truncated.Add(new NavWaypoint(worldTarget, WaypointAction.Walk));

                CurrentNavPath = truncated;
                CurrentWaypointIndex = 0;
                Destination = worldTarget;
                BlinkCount = 0;
                return true;
            }

            // No LOS — full repath (NavigateTo already does forward-trim)
            return NavigateTo(gc, worldTarget);
        }
    }
}
