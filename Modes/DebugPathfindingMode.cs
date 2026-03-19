using ExileCore;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using AutoExile.Systems;
using System.Numerics;

namespace AutoExile.Modes
{
    /// <summary>
    /// Debug mode for testing pathfinding, movement, and threat dodge.
    /// Set a target position, then navigate to it. Draws path and debug info.
    /// Threat detection: monitors boss skill casts and executes dodge movements.
    /// </summary>
    public class DebugPathfindingMode : IBotMode
    {
        public string Name => "Debug Pathfinding";

        private Vector2? _targetGridPos;
        private bool _navigating;
        private string _status = "Ready";

        // Tile search
        private string _tileSearchText = "";
        private List<(string Key, List<Vector2> Positions)> _tileSearchResults = new();

        // Rendering data (cached for Render())
        private List<NavWaypoint> _renderNavPath = new();
        private Vector2 _playerPos;

        // Dodge state
        private bool _dodging;
        private DateTime _lastDodgeAt = DateTime.MinValue;
        private Vector2 _dodgeTarget; // grid coords
        private string _lastDodgeSkill = "";

        public void OnEnter(BotContext ctx)
        {
            _status = "Ready — use Set Target button in settings panel";
            ctx.Log("Debug pathfinding mode active");
        }

        public void OnExit()
        {
            _targetGridPos = null;
            _navigating = false;
            _renderNavPath.Clear();
        }

        public void Tick(BotContext ctx)
        {
            var playerPos = ctx.Game.Player.PosNum;
            _playerPos = new Vector2(playerPos.X, playerPos.Y);

            // Tick combat system if profile is enabled
            if (ctx.Combat.Profile.Enabled)
                ctx.Combat.Tick(ctx);

            // Tick threat detection
            ctx.Threat.Tick(ctx.Game);

            // Auto dodge: when threat system signals, move perpendicular to attack
            if (ctx.Threat.DodgeUrgent && ctx.Settings.Threat.AutoDodge.Value)
            {
                var dodgeCooldown = ctx.Settings.Threat.DodgeCooldownMs.Value;
                if ((DateTime.Now - _lastDodgeAt).TotalMilliseconds >= dodgeCooldown)
                {
                    var playerGrid = new Vector2(
                        ctx.Game.Player.GridPosNum.X,
                        ctx.Game.Player.GridPosNum.Y);
                    var dodgeDist = ctx.Settings.Threat.DodgeDistance.Value;
                    _dodgeTarget = playerGrid + ctx.Threat.DodgeDirection * dodgeDist;

                    if (ctx.Navigation.NavigateTo(ctx.Game, _dodgeTarget))
                    {
                        _dodging = true;
                        _navigating = true;
                        _lastDodgeAt = DateTime.Now;
                        _lastDodgeSkill = ctx.Threat.ThreatSkillName;
                        _status = $"DODGING {ctx.Threat.ThreatSkillName}!";
                        ctx.Log($"Dodge: {ctx.Threat.ThreatSkillName} prog={ctx.Threat.ThreatProgress:F2}");
                    }
                }
            }

            // Clear dodge state when navigation completes
            if (_dodging && !ctx.Navigation.IsNavigating)
                _dodging = false;

            // Cache path for rendering
            _renderNavPath = new List<NavWaypoint>(ctx.Navigation.CurrentNavPath);

            if (_navigating && !ctx.Navigation.IsNavigating)
            {
                _navigating = false;
                var playerGrid = new Vector2(ctx.Game.Player.GridPosNum.X, ctx.Game.Player.GridPosNum.Y);
                var dist = _targetGridPos.HasValue
                    ? Vector2.Distance(playerGrid, _targetGridPos.Value)
                    : 0;
                _status = $"Navigation complete (dist to target: {dist:F0})";
                ctx.Log("Navigation complete");
            }

            if (_navigating)
            {
                var blinkInfo = ctx.Navigation.BlinkCount > 0
                    ? $" ({ctx.Navigation.BlinkCount} blinks)"
                    : "";
                _status = $"Navigating — waypoint {ctx.Navigation.CurrentWaypointIndex + 1}/{_renderNavPath.Count}{blinkInfo}";
            }

            // Tick interaction system
            if (ctx.Interaction.IsBusy)
            {
                var result = ctx.Interaction.Tick(ctx.Game);
                if (result == InteractionResult.Succeeded)
                {
                    _status = $"Interaction: {ctx.Interaction.Status}";
                    ctx.Log(ctx.Interaction.Status);
                }
                else if (result == InteractionResult.Failed)
                {
                    _status = $"Interaction FAILED: {ctx.Interaction.Status}";
                    ctx.Log($"Interaction failed: {ctx.Interaction.Status}");
                    _lootingAll = false;
                }
                else
                {
                    _status = $"Interaction: {ctx.Interaction.Status}";
                }
            }

            // Tick map device system
            if (ctx.MapDevice.IsBusy)
            {
                var result = ctx.MapDevice.Tick(ctx.Game, ctx.Navigation);
                _status = $"MapDevice: {ctx.MapDevice.Status}";
                if (result == MapDeviceResult.Succeeded)
                {
                    ctx.Log("Map device: entered map");
                }
                else if (result == MapDeviceResult.Failed)
                {
                    ctx.Log($"Map device failed: {ctx.MapDevice.Status}");
                }
            }

            // Loot-all loop: when not busy, pick up next item
            if (_lootingAll && !ctx.Interaction.IsBusy)
            {
                ctx.Loot.Scan(ctx.Game);
                var (_, candidate) = ctx.Loot.PickupNext(ctx.Interaction, ctx.Navigation);
                if (candidate == null)
                {
                    _lootingAll = false;
                    _status = "Loot all complete";
                    ctx.Log("Loot all complete");
                }
                else
                {
                    _status = $"Looting — {ctx.Loot.LootableCount} remaining";
                }
            }
        }

        public void SetTarget(BotContext ctx)
        {
            var pos = ctx.Game.Player.GridPosNum;
            _targetGridPos = new Vector2(pos.X, pos.Y);
            _navigating = false;
            ctx.Navigation.Stop(ctx.Game);
            _status = $"Target set at grid ({pos.X:F0}, {pos.Y:F0})";
            ctx.Log($"Target set at grid ({pos.X:F0}, {pos.Y:F0})");
        }

        public void Navigate(BotContext ctx)
        {
            if (_targetGridPos == null)
            {
                _status = "No target set";
                return;
            }

            _status = "Computing path...";

            var success = ctx.Navigation.NavigateTo(ctx.Game, _targetGridPos.Value);

            if (success)
            {
                _navigating = true;
                var path = ctx.Navigation.CurrentNavPath;
                var blinkInfo = ctx.Navigation.BlinkCount > 0
                    ? $", {ctx.Navigation.BlinkCount} blinks"
                    : "";
                _status = $"Path found — {path.Count} waypoints{blinkInfo}, {ctx.Navigation.LastPathfindMs}ms";
                ctx.Log($"Path: {path.Count} waypoints{blinkInfo}, {ctx.Navigation.LastPathfindMs}ms");
            }
            else
            {
                _status = "No path found!";
                ctx.Log("Pathfinding failed — no path to target");
            }
        }

        public void StopNavigation(BotContext ctx)
        {
            _navigating = false;
            ctx.Navigation.Stop(ctx.Game);
            _status = _targetGridPos != null ? "Stopped — target still set" : "Stopped";
        }

        public void SearchTiles(BotContext ctx)
        {
            if (string.IsNullOrWhiteSpace(_tileSearchText))
            {
                _tileSearchResults.Clear();
                _status = "Enter a tile search string";
                return;
            }

            _tileSearchResults = ctx.TileMap.SearchTiles(_tileSearchText);
            _status = $"Tile search: {_tileSearchResults.Count} matches for '{_tileSearchText}'";
            ctx.Log(_status);
        }

        public void NavigateToTile(BotContext ctx)
        {
            if (string.IsNullOrWhiteSpace(_tileSearchText))
            {
                _status = "Enter a tile search string";
                return;
            }

            if (!ctx.TileMap.IsLoaded)
            {
                _status = "TileMap not loaded — waiting for area data";
                return;
            }

            _status = "Computing path to tile...";
            var success = ctx.Navigation.NavigateToTile(ctx.Game, ctx.TileMap, _tileSearchText);

            if (success)
            {
                _navigating = true;
                var path = ctx.Navigation.CurrentNavPath;
                var blinkInfo = ctx.Navigation.BlinkCount > 0
                    ? $", {ctx.Navigation.BlinkCount} blinks"
                    : "";
                _status = $"Path to tile — {path.Count} waypoints{blinkInfo}, {ctx.Navigation.LastPathfindMs}ms";
                ctx.Log($"Tile nav: {path.Count} waypoints{blinkInfo}, {ctx.Navigation.LastPathfindMs}ms");
            }
            else
            {
                _status = $"No path to tile '{_tileSearchText}'";
                ctx.Log($"Tile navigation failed — no path or tile not found");
            }
        }

        // Expose tile search text for ImGui binding
        public string TileSearchText
        {
            get => _tileSearchText;
            set => _tileSearchText = value;
        }

        public IReadOnlyList<(string Key, List<Vector2> Positions)> TileSearchResults => _tileSearchResults;

        // --- Interaction testing ---

        public void InteractNearest(BotContext ctx, string entityType)
        {
            if (ctx.Interaction.IsBusy)
            {
                _status = "Already interacting";
                return;
            }

            EntityType? filter = entityType switch
            {
                "Chest" => EntityType.Chest,
                "Shrine" => EntityType.Shrine,
                _ => null
            };

            Entity? best = null;
            float bestDist = float.MaxValue;

            foreach (var e in ctx.Game.EntityListWrapper.OnlyValidEntities)
            {
                if (filter.HasValue && e.Type != filter.Value)
                    continue;
                if (!filter.HasValue && e.Type != EntityType.Chest && e.Type != EntityType.Shrine
                    && e.Type != EntityType.AreaTransition)
                    continue;
                if (!e.IsTargetable)
                    continue;
                if (e.Type == EntityType.Chest && e.IsOpened)
                    continue;
                if (e.DistancePlayer < bestDist)
                {
                    bestDist = e.DistancePlayer;
                    best = e;
                }
            }

            if (best == null)
            {
                _status = $"No targetable {entityType ?? "entity"} nearby";
                return;
            }

            ctx.Interaction.InteractWithEntity(best, ctx.Navigation, requireProximity: true);
            _status = $"Interacting with {best.RenderName ?? best.Path} (dist={bestDist:F0})";
            ctx.Log(_status);
        }

        public void PickupNearestItem(BotContext ctx)
        {
            if (ctx.Interaction.IsBusy)
            {
                _status = "Already interacting";
                return;
            }

            // Find closest visible ground item label
            try
            {
                var labels = ctx.Game.IngameState.IngameUi.ItemsOnGroundLabelElement.VisibleGroundItemLabels;
                Entity? bestEntity = null;
                float bestDist = float.MaxValue;
                string bestName = "";

                foreach (var label in labels)
                {
                    if (label.Label == null || !label.Label.IsVisible)
                        continue;
                    if (label.Entity == null)
                        continue;
                    var dist = label.Entity.DistancePlayer;
                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        bestEntity = label.Entity;
                        bestName = label.Label.Text ?? "?";
                    }
                }

                if (bestEntity == null)
                {
                    _status = "No visible ground items nearby";
                    return;
                }

                ctx.Interaction.PickupGroundItem(bestEntity, ctx.Navigation, requireProximity: true);
                _status = $"Picking up: {bestName} (dist={bestDist:F0})";
                ctx.Log(_status);
            }
            catch (Exception ex)
            {
                _status = $"Error: {ex.Message}";
            }
        }

        public void CancelInteraction(BotContext ctx)
        {
            ctx.Interaction.Cancel(ctx.Game);
            _status = "Interaction cancelled";
        }

        // --- Loot testing ---

        public void ScanLoot(BotContext ctx)
        {
            ctx.Loot.Scan(ctx.Game);
            _status = $"Loot scan: {ctx.Loot.LootableCount} items";
            if (!string.IsNullOrEmpty(ctx.Loot.LastSkipReason))
                _status += $" | {ctx.Loot.LastSkipReason}";
            ctx.Log(_status);
        }

        public void PickupNextLoot(BotContext ctx)
        {
            if (ctx.Interaction.IsBusy)
            {
                _status = "Already interacting";
                return;
            }

            ctx.Loot.Scan(ctx.Game);
            var (_, lootCandidate) = ctx.Loot.PickupNext(ctx.Interaction, ctx.Navigation);
            if (lootCandidate != null)
            {
                _status = $"Picking up loot ({ctx.Loot.LootableCount} remaining)";
                ctx.Log(_status);
            }
            else
            {
                _status = "No loot to pick up";
            }
        }

        public void LootAll(BotContext ctx)
        {
            // This just starts the first pickup — subsequent pickups happen via Tick
            _lootingAll = true;
            ctx.Loot.Scan(ctx.Game);
            _status = $"Looting all — {ctx.Loot.LootableCount} items";
            ctx.Log(_status);
        }

        private bool _lootingAll;

        // --- Map device testing ---

        public void StartBlightMap(BotContext ctx)
        {
            if (ctx.MapDevice.IsBusy)
            {
                _status = "Map device already busy";
                return;
            }
            ctx.MapDevice.Start(MapDeviceSystem.IsAnyBlightMap);
            _status = "Starting blight map creation";
            ctx.Log(_status);
        }

        public void StartStandardMap(BotContext ctx)
        {
            if (ctx.MapDevice.IsBusy)
            {
                _status = "Map device already busy";
                return;
            }
            ctx.MapDevice.Start(MapDeviceSystem.IsStandardMap);
            _status = "Starting standard map creation";
            ctx.Log(_status);
        }

        public void CancelMapDevice(BotContext ctx)
        {
            ctx.MapDevice.Cancel(ctx.Game, ctx.Navigation);
            _status = "Map device cancelled";
        }

        public void Render(BotContext ctx)
        {
            var gfx = ctx.Graphics;
            if (gfx == null) return;

            var camera = ctx.Game.IngameState.Camera;
            var yOffset = 100f;

            // Status text
            gfx.DrawText(_status, new Vector2(100, yOffset), SharpDX.Color.White);
            yOffset += 20;

            // Grid info
            var grid = ctx.Game.IngameState.Data.RawFramePathfindingData;
            if (grid != null && grid.Length > 0)
            {
                var (pgx, pgy) = Pathfinding.WorldToGridPos(_playerPos);
                var cellVal = (pgy >= 0 && pgy < grid.Length && pgx >= 0 && pgx < grid[0].Length)
                    ? grid[pgy][pgx] : -1;
                gfx.DrawText($"Grid: {grid[0].Length}x{grid.Length} | Player grid: ({pgx},{pgy}) val={cellVal}",
                    new Vector2(100, yOffset), SharpDX.Color.Gray);
                yOffset += 20;
            }

            if (ctx.Navigation.IsNavigating)
            {
                var wpIdx = ctx.Navigation.CurrentWaypointIndex;
                var wpAction = wpIdx < _renderNavPath.Count ? _renderNavPath[wpIdx].Action.ToString() : "?";
                gfx.DrawText($"Waypoint {wpIdx + 1}/{_renderNavPath.Count} ({wpAction})",
                    new Vector2(100, yOffset), SharpDX.Color.Cyan);
                yOffset += 20;

                if (ctx.Navigation.StuckRecoveries > 0 || !string.IsNullOrEmpty(ctx.Navigation.LastRecoveryAction))
                {
                    gfx.DrawText($"Stuck recoveries: {ctx.Navigation.StuckRecoveries} | Last: {ctx.Navigation.LastRecoveryAction}",
                        new Vector2(100, yOffset), SharpDX.Color.Orange);
                    yOffset += 20;
                }
            }

            // Blink settings info
            var blinkKey = ctx.Navigation.MovementSkills.FirstOrDefault(m => m.CanCrossTerrain)?.Key.ToString() ?? "none";
            gfx.DrawText($"Blink: {(ctx.Navigation.BlinkEnabled ? "ON" : "OFF")} | Key: {blinkKey} | Range: {ctx.Navigation.BlinkRange}",
                new Vector2(100, yOffset), SharpDX.Color.Gray);
            yOffset += 20;

            var playerZ = ctx.Game.Player.PosNum.Z;

            // Draw target marker
            if (_targetGridPos != null)
            {
                var gtw = (float)Pathfinding.GridToWorld;
                var targetScreen = camera.WorldToScreen(
                    new System.Numerics.Vector3(_targetGridPos.Value.X * gtw, _targetGridPos.Value.Y * gtw, playerZ));

                if (IsOnScreen(targetScreen, ctx.Game))
                {
                    var ts = new Vector2(targetScreen.X, targetScreen.Y);
                    gfx.DrawLine(ts + new Vector2(-12, -12), ts + new Vector2(12, 12), 3, SharpDX.Color.Red);
                    gfx.DrawLine(ts + new Vector2(12, -12), ts + new Vector2(-12, 12), 3, SharpDX.Color.Red);
                    gfx.DrawText("TARGET", ts + new Vector2(14, -8), SharpDX.Color.Red);
                }
            }

            // Draw path
            if (_renderNavPath.Count > 1)
            {
                for (var i = 0; i < _renderNavPath.Count - 1; i++)
                {
                    var a = _renderNavPath[i].Position;
                    var b = _renderNavPath[i + 1].Position;
                    var sa = camera.WorldToScreen(new System.Numerics.Vector3(a.X, a.Y, playerZ));
                    var sb = camera.WorldToScreen(new System.Numerics.Vector3(b.X, b.Y, playerZ));

                    if (!IsOnScreen(sa, ctx.Game) && !IsOnScreen(sb, ctx.Game))
                        continue;

                    SharpDX.Color color;
                    if (i < ctx.Navigation.CurrentWaypointIndex)
                        color = SharpDX.Color.DarkGreen; // completed
                    else if (_renderNavPath[i + 1].Action == WaypointAction.Blink)
                        color = SharpDX.Color.Magenta; // blink segment
                    else
                        color = SharpDX.Color.Yellow; // walk segment

                    var thickness = _renderNavPath[i + 1].Action == WaypointAction.Blink ? 3 : 2;
                    gfx.DrawLine(new Vector2(sa.X, sa.Y), new Vector2(sb.X, sb.Y), thickness, color);
                }

                // Waypoint dots
                for (var i = 0; i < _renderNavPath.Count; i++)
                {
                    var wp = _renderNavPath[i];
                    var sw = camera.WorldToScreen(new System.Numerics.Vector3(wp.Position.X * Pathfinding.GridToWorld, wp.Position.Y * Pathfinding.GridToWorld, playerZ));
                    if (!IsOnScreen(sw, ctx.Game))
                        continue;

                    SharpDX.Color dotColor;
                    if (i == ctx.Navigation.CurrentWaypointIndex)
                        dotColor = SharpDX.Color.Cyan; // current
                    else if (wp.Action == WaypointAction.Blink)
                        dotColor = SharpDX.Color.Magenta; // blink landing
                    else
                        dotColor = SharpDX.Color.Yellow; // walk

                    var c = new Vector2(sw.X, sw.Y);
                    var size = wp.Action == WaypointAction.Blink ? 6 : 4;
                    gfx.DrawLine(c + new Vector2(-size, 0), c + new Vector2(size, 0), 3, dotColor);
                    gfx.DrawLine(c + new Vector2(0, -size), c + new Vector2(0, size), 3, dotColor);

                    // Label blink waypoints
                    if (wp.Action == WaypointAction.Blink)
                        gfx.DrawText("BLINK", c + new Vector2(size + 2, -8), SharpDX.Color.Magenta);
                }
            }

            // ── Threat overlays ──
            if (ctx.Threat.Enabled)
            {
                var playerGrid = new Vector2(
                    ctx.Game.Player.GridPosNum.X,
                    ctx.Game.Player.GridPosNum.Y);
                var gtw = (float)Pathfinding.GridToWorld;

                foreach (var kv in ctx.Threat.TrackedMonsters)
                {
                    var mt = kv.Value;
                    if (mt.Entity == null) continue;

                    var monWorld = mt.Entity.BoundsCenterPosNum;
                    var monScreen = camera.WorldToScreen(monWorld);
                    if (!IsOnScreen(monScreen, ctx.Game)) continue;

                    var ms = new Vector2(monScreen.X, monScreen.Y);

                    if (mt.HasCast)
                    {
                        // Draw line from monster to cast destination
                        var destWorld3 = new System.Numerics.Vector3(
                            mt.CastDestination.X * gtw,
                            mt.CastDestination.Y * gtw,
                            playerZ);
                        var destScreen = camera.WorldToScreen(destWorld3);
                        var ds = new Vector2(destScreen.X, destScreen.Y);

                        var lineColor = mt.DodgeSignaled
                            ? SharpDX.Color.Orange
                            : SharpDX.Color.Red;
                        gfx.DrawLine(ms, ds, 2, lineColor);

                        // X marker at cast destination
                        if (IsOnScreen(destScreen, ctx.Game))
                        {
                            gfx.DrawLine(ds + new Vector2(-8, -8), ds + new Vector2(8, 8), 2, lineColor);
                            gfx.DrawLine(ds + new Vector2(8, -8), ds + new Vector2(-8, 8), 2, lineColor);
                        }

                        // Progress bar above monster
                        var barY = ms.Y - 30;
                        var barWidth = 60f;
                        var barX = ms.X - barWidth / 2;
                        gfx.DrawLine(
                            new Vector2(barX, barY),
                            new Vector2(barX + barWidth, barY),
                            4, SharpDX.Color.DarkGray);
                        gfx.DrawLine(
                            new Vector2(barX, barY),
                            new Vector2(barX + barWidth * mt.AnimationProgress, barY),
                            4, mt.AnimationProgress <= ctx.Settings.Threat.DodgeMaxProgress.Value
                                ? SharpDX.Color.Yellow
                                : SharpDX.Color.Gray);

                        // Skill name + stage
                        gfx.DrawText(
                            $"{mt.SkillName} stg={mt.AnimationStage}",
                            new Vector2(ms.X - 40, ms.Y - 45),
                            SharpDX.Color.White);
                    }
                    else
                    {
                        // Not casting — show animation name in gray
                        gfx.DrawText(mt.AnimationName, ms + new Vector2(10, -10), SharpDX.Color.Gray);
                    }
                }

                // Draw dodge direction arrow when actively dodging
                if (_dodging)
                {
                    var dodgeWorld3 = new System.Numerics.Vector3(
                        _dodgeTarget.X * gtw,
                        _dodgeTarget.Y * gtw,
                        playerZ);
                    var dodgeScreen = camera.WorldToScreen(dodgeWorld3);
                    if (IsOnScreen(dodgeScreen, ctx.Game))
                    {
                        var dts = new Vector2(dodgeScreen.X, dodgeScreen.Y);
                        gfx.DrawLine(
                            new Vector2(_playerPos.X, _playerPos.Y),
                            dts, 3, SharpDX.Color.LimeGreen);
                        gfx.DrawText("DODGE", dts + new Vector2(5, -8), SharpDX.Color.LimeGreen);
                    }
                }

                // Threat status text
                gfx.DrawText(
                    $"Threats: {ctx.Threat.TrackedMonsters.Count} tracked | {ctx.Threat.CastsDetected} casts | {ctx.Threat.DodgesTriggered} dodges",
                    new Vector2(100, yOffset), SharpDX.Color.Yellow);
                yOffset += 20;
            }
        }

        private static bool IsOnScreen(Vector2 pos, GameController gc)
        {
            var rect = gc.Window.GetWindowRectangle();
            return pos.X > 0 && pos.X < rect.Width && pos.Y > 0 && pos.Y < rect.Height;
        }
    }
}
