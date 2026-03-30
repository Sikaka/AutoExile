using ExileCore;
using ExileCore.PoEMemory;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using AutoExile.Systems;
using System.Numerics;

namespace AutoExile.Modes.BossEncounters
{
    /// <summary>
    /// "An Audience With The King" boss encounter.
    ///
    /// Flow:
    ///   1. Enter arena (Crux of Nothingness) → walk to center → fight boss
    ///   2. Boss teleports player to maze section (same area, grid X > 600)
    ///   3. Navigate maze to PortalToggleableReverseVoodooKing (at ~908,540) → return to arena
    ///   4. Walk to center → fight boss again → boss dies
    ///   5. Wait ~5s for loot drops → Complete (BossMode handles loot + exit)
    ///
    /// Key: maze is NOT a separate area — same "Crux of Nothingness" zone.
    /// Detected by position jump (>200g) and player X > 600 (maze section).
    /// Boss entity (id=15) goes out of range in maze, returns when back in arena.
    ///
    /// Fragment: Metadata/Items/MapFragments/RitualBossFragment
    /// Boss: Metadata/Monsters/LeagueAzmeri/VoodooKingBoss/VoodooKingBoss2@83
    /// Pillars (filter out): VoodooKingBoss2RitualPillar@83 (3x, also Unique rarity)
    /// Exit portal: Metadata/MiscellaneousObjects/RitualBossPortal (AreaTransition)
    /// Maze portal: Metadata/MiscellaneousObjects/PortalToggleableReverseVoodooKing (targetable=false until near)
    /// </summary>
    public class KingEncounter : IBossEncounter
    {
        public string Name => "Audience With The King";
        public string Status { get; private set; } = "";

        private const string FragmentPath = "RitualBossFragment";

        public Func<Element, bool> MapFilter => el =>
        {
            var entity = el.Entity;
            return entity?.Path?.EndsWith(FragmentPath) == true;
        };

        public string? InventoryFragmentPath => FragmentPath;

        // Prismatic Jewel is the key drop from this encounter
        public IReadOnlyList<string> MustLootItems { get; } = new[] { "Prismatic Jewel" };

        // In maze: fire skills at nearby enemies (clear blockers) but don't chase packs
        public bool SuppressCombatPositioning => _phase == KingPhase.InMaze;

        // Maze has tight corridors — use flat-cost A* and relaxed smoothing
        public bool RelaxedPathing => _phase == KingPhase.InMaze;

        // ── Verified entity paths ──
        // Phase 1 boss: VoodooKingBoss2@83, Phase 2 boss: VoodooKingBoss3@83
        private const string BossPathPhase1 = "VoodooKingBoss2@";
        private const string BossPathPhase2 = "VoodooKingBoss3@";
        private const string PillarPath = "VoodooKingBoss2RitualPillar@";
        private const string MazePortalPath = "PortalToggleableReverseVoodooKing";
        private const string DescensionAltarPath = "RitualDescensionObject";

        // Arena center ~(300, 540), player spawns ~(300, 450)
        // Maze spawn ~(1122, 306), maze exit portal ~(908, 540)
        private const float MazeXThreshold = 600f;
        private const float TeleportDetectDistance = 200f;

        // ── State ──
        private KingPhase _phase = KingPhase.Idle;
        private DateTime _phaseStartTime;
        private Entity? _bossEntity;
        private bool _mazeVisited;
        private bool _bossWasAlive;
        private int _exploreFails;
        private Vector2 _lastPlayerGrid;
        private bool _mazeDieSkillUsed;

        private enum KingPhase
        {
            Idle,
            NavigateToCenter,
            Fighting,
            InMaze,
            ReturnedToArena,
            WaitingForLoot,
        }

        public void OnEnterZone(BotContext ctx)
        {
            var gc = ctx.Game;
            InitExploration(ctx, gc);

            _phase = KingPhase.NavigateToCenter;
            _phaseStartTime = DateTime.Now;
            _bossEntity = null;
            _mazeVisited = false;
            _bossWasAlive = false;
            _exploreFails = 0;
            _lastPlayerGrid = new Vector2(gc.Player.GridPosNum.X, gc.Player.GridPosNum.Y);
            Status = "Entered arena — walking to center";
            ctx.Log($"[King] Zone entered at ({_lastPlayerGrid.X:F0}, {_lastPlayerGrid.Y:F0})");
        }

        public BossEncounterResult Tick(BotContext ctx)
        {
            var gc = ctx.Game;
            if (gc?.Player == null) return BossEncounterResult.InProgress;

            var playerGrid = new Vector2(gc.Player.GridPosNum.X, gc.Player.GridPosNum.Y);

            // ── Teleport detection (maze is same area, different grid region) ──
            var jumpDist = Vector2.Distance(playerGrid, _lastPlayerGrid);
            if (jumpDist > TeleportDetectDistance && _phase != KingPhase.Idle)
            {
                if (playerGrid.X > MazeXThreshold && _phase != KingPhase.InMaze)
                {
                    // Teleported TO maze
                    ctx.Navigation.Stop(gc);
                    InitExploration(ctx, gc);
                    _phase = KingPhase.InMaze;
                    _phaseStartTime = DateTime.Now;
                    _bossEntity = null;
                    _mazeDieSkillUsed = false;
                    Status = "Teleported to maze — finding exit";
                    ctx.Log($"[King] Maze teleport detected: ({_lastPlayerGrid.X:F0},{_lastPlayerGrid.Y:F0}) → ({playerGrid.X:F0},{playerGrid.Y:F0})");
                }
                else if (playerGrid.X < MazeXThreshold && _phase == KingPhase.InMaze)
                {
                    // Teleported BACK to arena
                    ctx.Navigation.Stop(gc);
                    InitExploration(ctx, gc);
                    _mazeVisited = true;
                    _phase = KingPhase.ReturnedToArena;
                    _phaseStartTime = DateTime.Now;
                    _bossEntity = null;
                    Status = "Back in arena — finding boss";
                    ctx.Log($"[King] Returned to arena: ({playerGrid.X:F0},{playerGrid.Y:F0})");
                }
            }
            _lastPlayerGrid = playerGrid;

            // Update exploration
            ctx.Exploration.Update(playerGrid);

            // Scan for boss (not in maze, not waiting for loot)
            if (_phase != KingPhase.InMaze && _phase != KingPhase.WaitingForLoot)
            {
                _bossEntity = FindBoss(gc);

                if (_bossEntity != null && _bossEntity.IsAlive)
                    _bossWasAlive = true;

                // Detect kill via Descension Altar appearance (reliable signal for encounter complete)
                if (_bossWasAlive && IsDescensionAltarVisible(gc))
                {
                    _phase = KingPhase.WaitingForLoot;
                    _phaseStartTime = DateTime.Now;
                    Status = "Altar appeared — waiting for loot drops";
                    ctx.Log("[King] Descension Altar visible, boss confirmed dead");
                    return BossEncounterResult.InProgress;
                }
            }

            switch (_phase)
            {
                case KingPhase.NavigateToCenter:
                case KingPhase.ReturnedToArena:
                    return TickNavigateToCenter(ctx, gc, playerGrid);
                case KingPhase.Fighting:
                    return TickFighting(ctx, gc, playerGrid);
                case KingPhase.InMaze:
                    return TickMaze(ctx, gc, playerGrid);
                case KingPhase.WaitingForLoot:
                    return TickWaitingForLoot();
                default:
                    return BossEncounterResult.InProgress;
            }
        }

        private BossEncounterResult TickNavigateToCenter(BotContext ctx, GameController gc, Vector2 playerGrid)
        {
            if ((DateTime.Now - _phaseStartTime).TotalSeconds > 60)
            {
                Status = "Timeout navigating to center";
                return BossEncounterResult.Failed;
            }

            if (_bossEntity != null && _bossEntity.IsAlive)
            {
                _phase = KingPhase.Fighting;
                _phaseStartTime = DateTime.Now;
                ctx.Log($"[King] Boss found: {_bossEntity.RenderName}");
                return BossEncounterResult.InProgress;
            }

            if (!ctx.Navigation.IsNavigating)
            {
                var target = ctx.Exploration.GetNextExplorationTarget(playerGrid);
                if (target.HasValue)
                {
                    if (!ctx.Navigation.NavigateTo(gc, target.Value))
                    {
                        _exploreFails++;
                        ctx.Exploration.MarkRegionFailed(target.Value);
                        if (_exploreFails > 10)
                            return BossEncounterResult.Failed;
                    }
                }
            }

            var coverage = ctx.Exploration.ActiveBlobCoverage;
            Status = _phase == KingPhase.ReturnedToArena
                ? $"Back in arena — exploring ({coverage:P0})"
                : $"Walking to center ({coverage:P0})";
            return BossEncounterResult.InProgress;
        }

        private BossEncounterResult TickFighting(BotContext ctx, GameController gc, Vector2 playerGrid)
        {
            if ((DateTime.Now - _phaseStartTime).TotalSeconds > 180)
            {
                Status = "Fight timeout";
                return BossEncounterResult.Failed;
            }

            if (_bossEntity != null && _bossEntity.IsAlive)
            {
                var bossGrid = new Vector2(_bossEntity.GridPosNum.X, _bossEntity.GridPosNum.Y);
                var dist = Vector2.Distance(playerGrid, bossGrid);

                if (dist > 30 && !ctx.Navigation.IsNavigating)
                    ctx.Navigation.NavigateTo(gc, bossGrid);

                Status = $"Fighting {_bossEntity.RenderName} — dist={dist:F0}";
            }
            else
            {
                Status = "Fighting — waiting for boss entity";
            }

            return BossEncounterResult.InProgress;
        }

        private const float MazeInteractRange = 20f; // close enough to click the portal

        private BossEncounterResult TickMaze(BotContext ctx, GameController gc, Vector2 playerGrid)
        {
            if ((DateTime.Now - _phaseStartTime).TotalSeconds > 120)
            {
                Status = "Maze timeout";
                return BossEncounterResult.Failed;
            }

            // Find maze exit portal
            Entity? mazePortal = null;
            foreach (var entity in gc.EntityListWrapper.ValidEntitiesByType[EntityType.AreaTransition])
            {
                if (!entity.Path.Contains(MazePortalPath)) continue;
                mazePortal = entity;
                break;
            }

            if (mazePortal != null)
            {
                var portalGrid = new Vector2(mazePortal.GridPosNum.X, mazePortal.GridPosNum.Y);
                var dist = Vector2.Distance(playerGrid, portalGrid);

                // Close enough and targetable — click it
                if (dist < MazeInteractRange && mazePortal.IsTargetable)
                {
                    if (!ctx.Interaction.IsBusy)
                    {
                        ctx.Navigation.Stop(gc);
                        ctx.Interaction.InteractWithEntity(mazePortal, ctx.Navigation);
                        Status = $"Clicking exit ({dist:F0}g)";
                    }
                    else
                    {
                        Status = $"Clicking exit ({ctx.Interaction.Status})";
                    }
                }
                else
                {
                    // Navigate directly to portal — don't explore, we know where it is
                    if (!ctx.Navigation.IsNavigating ||
                        (ctx.Navigation.Destination.HasValue &&
                         Vector2.Distance(ctx.Navigation.Destination.Value, portalGrid) > 20f))
                    {
                        ctx.Navigation.NavigateTo(gc, portalGrid);
                        ctx.Log($"[King] Maze: pathing to portal at ({portalGrid.X:F0},{portalGrid.Y:F0}) dist={dist:F0}");
                    }
                    Status = $"Navigating to exit ({dist:F0}g) targetable={mazePortal.IsTargetable}";
                }
            }
            else
            {
                // Portal not in entity list — stand still and let the fog kill us.
                // Using a skill ensures we're not in "grace period" immunity.
                ctx.Navigation.Stop(gc);
                if (!_mazeDieSkillUsed && BotInput.CanAct)
                {
                    // Press any attack skill to break grace period
                    BotInput.PressKey(System.Windows.Forms.Keys.Q);
                    _mazeDieSkillUsed = true;
                    ctx.Log("[King] Maze: no portal found, using skill to break immunity");
                }
                Status = "In maze — no portal, waiting for fog death";
            }

            return BossEncounterResult.InProgress;
        }

        private BossEncounterResult TickWaitingForLoot()
        {
            Status = "Boss complete!";
            return BossEncounterResult.Complete;
        }

        private Entity? FindBoss(GameController gc)
        {
            Entity? best = null;
            foreach (var entity in gc.EntityListWrapper.ValidEntitiesByType[EntityType.Monster])
            {
                if (!entity.IsHostile) continue;
                if (entity.Rarity != MonsterRarity.Unique) continue;
                if (entity.Path.Contains(PillarPath)) continue;

                // Match both phase 1 (VoodooKingBoss2) and phase 2 (VoodooKingBoss3)
                if (!entity.Path.Contains(BossPathPhase1) && !entity.Path.Contains(BossPathPhase2))
                    continue;

                // Prefer alive, prefer phase 2 over phase 1
                if (best == null)
                    best = entity;
                else if (entity.IsAlive && !best.IsAlive)
                    best = entity;
                else if (entity.IsAlive && best.IsAlive && entity.Path.Contains(BossPathPhase2))
                    best = entity;
            }
            return best;
        }

        private bool IsDescensionAltarVisible(GameController gc)
        {
            foreach (var entity in gc.EntityListWrapper.ValidEntitiesByType[EntityType.IngameIcon])
            {
                if (!entity.Path.Contains(DescensionAltarPath)) continue;
                if (!entity.IsTargetable) continue;
                // Check StateMachine visible=1 for extra safety
                if (entity.TryGetComponent<ExileCore.PoEMemory.Components.StateMachine>(out var sm))
                {
                    var visible = sm.States.FirstOrDefault(s => s.Name == "visible");
                    if (visible != null && visible.Value > 0) return true;
                }
                return true; // targetable is sufficient
            }
            return false;
        }

        private void InitExploration(BotContext ctx, GameController gc)
        {
            var pfGrid = gc.IngameState?.Data?.RawPathfindingData;
            var tgtGrid = gc.IngameState?.Data?.RawTerrainTargetingData;
            if (pfGrid != null && gc.Player != null)
            {
                var playerGrid = new Vector2(gc.Player.GridPosNum.X, gc.Player.GridPosNum.Y);
                ctx.Exploration.Initialize(pfGrid, tgtGrid, playerGrid,
                    ctx.Settings.Build.BlinkRange.Value);
            }
        }

        public void Render(BotContext ctx)
        {
            var gc = ctx.Game;
            var g = ctx.Graphics;
            if (gc?.Player == null || g == null) return;

            var cam = gc.IngameState.Camera;
            var playerGrid = new Vector2(gc.Player.GridPosNum.X, gc.Player.GridPosNum.Y);

            // Boss marker
            if (_bossEntity != null)
            {
                var world = _bossEntity.BoundsCenterPosNum;
                var screen = cam.WorldToScreen(world);
                if (screen.X > -200 && screen.X < 2400)
                {
                    var color = _bossEntity.IsAlive ? SharpDX.Color.Red : SharpDX.Color.LimeGreen;
                    g.DrawText(_bossEntity.IsAlive ? "BOSS" : "BOSS (dead)",
                        screen + new Vector2(-20, -30), color);
                }
            }

            // Maze portal marker
            if (_phase == KingPhase.InMaze)
            {
                foreach (var entity in gc.EntityListWrapper.ValidEntitiesByType[EntityType.AreaTransition])
                {
                    if (!entity.Path.Contains(MazePortalPath)) continue;
                    var portalWorld = Pathfinding.GridToWorld3D(gc,
                        new Vector2(entity.GridPosNum.X, entity.GridPosNum.Y));
                    g.DrawCircleInWorld(portalWorld, 30f,
                        entity.IsTargetable ? SharpDX.Color.LimeGreen : SharpDX.Color.Yellow, 2f);
                    var portalScreen = cam.WorldToScreen(portalWorld);
                    if (portalScreen.X > -200 && portalScreen.X < 2400)
                    {
                        var dist = Vector2.Distance(playerGrid, entity.GridPosNum);
                        var label = entity.IsTargetable ? $"EXIT ({dist:F0}g)" : $"EXIT [locked] ({dist:F0}g)";
                        g.DrawText(label, portalScreen + new Vector2(-25, -25),
                            entity.IsTargetable ? SharpDX.Color.LimeGreen : SharpDX.Color.Yellow);
                    }
                    break;
                }
            }

            // Navigation path
            if (ctx.Navigation.IsNavigating)
            {
                var path = ctx.Navigation.CurrentNavPath;
                for (int i = ctx.Navigation.CurrentWaypointIndex; i < path.Count - 1; i++)
                {
                    var from = Pathfinding.GridToScreen(gc, path[i].Position);
                    var to = Pathfinding.GridToScreen(gc, path[i + 1].Position);
                    if (from.X < -200 || from.X > 2400 || to.X < -200 || to.X > 2400) continue;
                    var isBlink = path[i + 1].Action == WaypointAction.Blink;
                    g.DrawLine(from, to, isBlink ? 3f : 2f,
                        isBlink ? SharpDX.Color.Magenta : SharpDX.Color.Orange);
                }
            }

            // HUD
            float hudX = 20, hudY = 250, lineH = 18;
            var phaseColor = _phase switch
            {
                KingPhase.Fighting => SharpDX.Color.Red,
                KingPhase.InMaze => SharpDX.Color.Yellow,
                KingPhase.WaitingForLoot => SharpDX.Color.LimeGreen,
                _ => SharpDX.Color.White,
            };
            g.DrawText($"King: {_phase}", new Vector2(hudX, hudY), phaseColor);
            hudY += lineH;
            g.DrawText(Status, new Vector2(hudX, hudY), SharpDX.Color.Gray);
            hudY += lineH;
            if (ctx.Navigation.IsNavigating)
            {
                g.DrawText($"Nav: wp {ctx.Navigation.CurrentWaypointIndex + 1}/{ctx.Navigation.CurrentNavPath.Count} stuck={ctx.Navigation.StuckRecoveries}",
                    new Vector2(hudX, hudY), SharpDX.Color.Orange);
                hudY += lineH;
            }
            g.DrawText($"Player: ({playerGrid.X:F0}, {playerGrid.Y:F0}) maze={playerGrid.X > MazeXThreshold}",
                new Vector2(hudX, hudY), SharpDX.Color.DarkGray);
        }

        public void Reset()
        {
            _phase = KingPhase.Idle;
            _bossEntity = null;
            _mazeVisited = false;
            _bossWasAlive = false;
            _exploreFails = 0;
            _lastPlayerGrid = Vector2.Zero;
            _mazeDieSkillUsed = false;
            Status = "";
        }
    }
}
