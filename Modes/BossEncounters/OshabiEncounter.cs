using ExileCore;
using ExileCore.PoEMemory;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using AutoExile.Systems;
using System.Numerics;

namespace AutoExile.Modes.BossEncounters
{
    /// <summary>
    /// Oshabi, Avatar of the Grove boss encounter.
    ///
    /// Flow:
    ///   1. Enter The Sacred Grove
    ///   2. Click ground label "Oshabi, Avatar of the Grove" (MonsterStatueCreatorIndicator) to spawn boss
    ///   3. Fight OshabiBoss@83
    ///   4. OshabiDescensionObject appears when boss dies
    ///   5. Wait ~5s for loot → Complete (BossMode handles loot + exit via MultiplexPortal)
    ///
    /// Single zone, no maze, no multi-phase. Very straightforward.
    ///
    /// Fragment: Metadata/Items/MapFragments/CurrencyHarvestBossKey (Sacred Blossom)
    /// Boss: Metadata/Monsters/LeagueHarvest/Oshabi/OshabiBoss@83
    /// Spawner label: MonsterStatueCreatorIndicator (ground label "Oshabi, Avatar of the Grove")
    /// Altar: Metadata/MiscellaneousObjects/Descendancy/OshabiDescensionObject
    /// Exit: Metadata/MiscellaneousObjects/MultiplexPortal (TownPortal type)
    /// Key drops: Forbidden Shako (Great Crown unique), Sacred Crystallised Lifeforce (HarvestSeedBoss)
    /// </summary>
    public class OshabiEncounter : IBossEncounter
    {
        public string Name => "Oshabi";
        public string Status { get; private set; } = "";

        private const string FragmentPath = "CurrencyHarvestBossKey";
        private const string BossPath = "OshabiBoss@";
        private const string SoulTreePath = "Harvest/Objects/SoulTree";
        private const string DescensionAltarPath = "OshabiDescensionObject";

        // Pre-fight position — close enough to click the Soul Tree label
        private static readonly Vector2 StartPosition = new(359, 415);

        public Func<Element, bool> MapFilter => el =>
        {
            var entity = el.Entity;
            return entity?.Path?.EndsWith(FragmentPath) == true;
        };

        public string? InventoryFragmentPath => FragmentPath;

        // Forbidden Shako + Sacred Crystallised Lifeforce
        public IReadOnlyList<string> MustLootItems { get; } = new[]
        {
            "Great Crown",                    // Forbidden Shako (unidentified base name)
            "Sacred Crystallised Lifeforce",
        };

        // Suppress repositioning while waiting for boss — stay near spawn and stack abilities
        public bool SuppressCombatPositioning => _phase == OshabiPhase.WaitForBoss;
        public bool RelaxedPathing => false;

        // ── State ──
        private OshabiPhase _phase = OshabiPhase.Idle;
        private DateTime _phaseStartTime;
        private Entity? _bossEntity;
        private bool _bossWasAlive;
        private bool _spawnerClicked;
        private Vector2? _spawnWaitPos;
        private DateTime _arrivedAtStart = DateTime.MinValue;
        private int _spawnerClickAttempts;
        private DateTime _lastSpawnerClickTime;
        private const float SpawnerClickCooldownMs = 1000;
        private const int MaxSpawnerClickAttempts = 10;
        private const float SpawnerApproachDist = 35f; // get this close before clicking

        private enum OshabiPhase
        {
            Idle,
            ClickSpawner,   // Find and click the ground label to spawn boss
            WaitForBoss,    // Spawner clicked, waiting for boss to appear
            Fighting,       // Boss alive, combat active
            WaitingForLoot, // Boss dead (altar appeared), waiting for drops
        }

        public void OnEnterZone(BotContext ctx)
        {
            var gc = ctx.Game;

            // Initialize exploration (needed for potential nav to spawner)
            var pfGrid = gc.IngameState?.Data?.RawPathfindingData;
            var tgtGrid = gc.IngameState?.Data?.RawTerrainTargetingData;
            if (pfGrid != null && gc.Player != null)
            {
                var playerGrid = new Vector2(gc.Player.GridPosNum.X, gc.Player.GridPosNum.Y);
                ctx.Exploration.Initialize(pfGrid, tgtGrid, playerGrid,
                    ctx.Settings.Build.BlinkRange.Value);
            }

            _phase = OshabiPhase.ClickSpawner;
            _phaseStartTime = DateTime.Now;
            _bossEntity = null;
            _bossWasAlive = false;
            _spawnerClicked = false;
            _spawnWaitPos = null;
            _arrivedAtStart = DateTime.MinValue;
            _spawnerClickAttempts = 0;
            _lastSpawnerClickTime = DateTime.MinValue;
            Status = "Entered Sacred Grove — looking for Heart of the Grove";
            ctx.Log("[Oshabi] Zone entered");
        }

        public BossEncounterResult Tick(BotContext ctx)
        {
            var gc = ctx.Game;
            if (gc?.Player == null) return BossEncounterResult.InProgress;

            var playerGrid = new Vector2(gc.Player.GridPosNum.X, gc.Player.GridPosNum.Y);
            ctx.Exploration.Update(playerGrid);

            // Scan for boss (after spawner clicked)
            if (_phase == OshabiPhase.WaitForBoss || _phase == OshabiPhase.Fighting)
            {
                _bossEntity = FindBoss(gc);
                if (_bossEntity != null && _bossEntity.IsAlive)
                    _bossWasAlive = true;
            }

            // Detect kill via Descension Altar becoming targetable (only happens after boss dies)
            if (_bossWasAlive && _phase == OshabiPhase.Fighting && IsAltarVisible(gc))
            {
                _phase = OshabiPhase.WaitingForLoot;
                _phaseStartTime = DateTime.Now;
                Status = "Altar activated — waiting for loot";
                ctx.Log("[Oshabi] Kill detected: altar targetable");
                return BossEncounterResult.InProgress;
            }

            switch (_phase)
            {
                case OshabiPhase.ClickSpawner:
                    return TickClickSpawner(ctx, gc, playerGrid);
                case OshabiPhase.WaitForBoss:
                    return TickWaitForBoss(ctx, gc);
                case OshabiPhase.Fighting:
                    return TickFighting(ctx, gc, playerGrid);
                case OshabiPhase.WaitingForLoot:
                    return TickWaitingForLoot();
                default:
                    return BossEncounterResult.InProgress;
            }
        }

        private BossEncounterResult TickClickSpawner(BotContext ctx, GameController gc, Vector2 playerGrid)
        {
            if ((DateTime.Now - _phaseStartTime).TotalSeconds > 60)
            {
                Status = "Timeout: couldn't start encounter";
                return BossEncounterResult.Failed;
            }

            if (_spawnerClickAttempts >= MaxSpawnerClickAttempts)
            {
                Status = $"Failed: spawner click didn't work after {MaxSpawnerClickAttempts} attempts";
                return BossEncounterResult.Failed;
            }

            // Check if boss is already alive (re-entering after death)
            var boss = FindBoss(gc);
            if (boss != null && boss.IsAlive)
            {
                _bossEntity = boss;
                _bossWasAlive = true;
                _phase = OshabiPhase.Fighting;
                _phaseStartTime = DateTime.Now;
                ctx.Log("[Oshabi] Boss already alive on entry");
                return BossEncounterResult.InProgress;
            }

            // Find the Soul Tree entity — search ALL (including non-targetable after click)
            Entity? soulTree = null;
            bool soulTreeTargetable = false;
            foreach (var entity in gc.EntityListWrapper.ValidEntitiesByType[EntityType.MiscellaneousObjects])
            {
                if (!entity.Path.Contains(SoulTreePath)) continue;
                soulTree = entity;
                soulTreeTargetable = entity.IsTargetable;
                break;
            }

            if (soulTree == null)
            {
                // Not in entity list at all — explore to find it
                if (!ctx.Navigation.IsNavigating)
                {
                    var target = ctx.Exploration.GetNextExplorationTarget(playerGrid);
                    if (target.HasValue)
                        ctx.Navigation.NavigateTo(gc, target.Value);
                }
                Status = "Looking for Heart of the Grove...";
                return BossEncounterResult.InProgress;
            }

            // Stale map detection: if oshabi_death=1, boss already killed in this instance.
            // This happens when accidentally clicking portal back into a completed map.
            if (soulTree.TryGetComponent<ExileCore.PoEMemory.Components.StateMachine>(out var staleCheck))
            {
                var death = staleCheck.States.FirstOrDefault(s => s.Name == "oshabi_death");
                if (death != null && death.Value > 0)
                {
                    Status = "Stale map — boss already dead, exiting";
                    ctx.Log("[Oshabi] Stale map detected (oshabi_death=1), signaling complete to exit");
                    return BossEncounterResult.Complete;
                }
            }

            var treeGrid = new Vector2(soulTree.GridPosNum.X, soulTree.GridPosNum.Y);

            if (!_spawnWaitPos.HasValue)
            {
                _spawnWaitPos = StartPosition;
            }

            // If we clicked and the tree is no longer targetable — click worked!
            if (_spawnerClicked && !soulTreeTargetable)
            {
                _phase = OshabiPhase.WaitForBoss;
                _phaseStartTime = DateTime.Now;
                ctx.Log("[Oshabi] Soul Tree no longer targetable — boss spawning");
                return BossEncounterResult.InProgress;
            }

            // Navigate to start position (close enough to click the label)
            var distToStart = Vector2.Distance(playerGrid, StartPosition);
            var distToTree = Vector2.Distance(playerGrid, treeGrid);

            if (distToStart > SpawnerApproachDist)
            {
                if (!ctx.Navigation.IsNavigating)
                    ctx.Navigation.NavigateTo(gc, StartPosition);
                _arrivedAtStart = DateTime.MinValue;
                Status = $"Walking to start position ({distToStart:F0}g)";
                return BossEncounterResult.InProgress;
            }

            // Arrived — stop nav and settle before clicking
            if (ctx.Navigation.IsNavigating)
            {
                ctx.Navigation.Stop(gc);
                _arrivedAtStart = DateTime.Now;
                Status = "Arrived — settling before click";
                return BossEncounterResult.InProgress;
            }
            if (_arrivedAtStart != DateTime.MinValue &&
                (DateTime.Now - _arrivedAtStart).TotalMilliseconds < 500)
            {
                Status = "Settling...";
                return BossEncounterResult.InProgress;
            }

            // Close enough and settled — find and click the label
            if ((DateTime.Now - _lastSpawnerClickTime).TotalMilliseconds < SpawnerClickCooldownMs)
            {
                Status = $"Waiting to retry click (attempt {_spawnerClickAttempts})";
                return BossEncounterResult.InProgress;
            }

            // After clicking, verify via StateMachine or wait for cooldown to retry
            if (_spawnerClicked && soulTreeTargetable)
            {
                // Check StateMachine for emerge signal
                if (soulTree.TryGetComponent<ExileCore.PoEMemory.Components.StateMachine>(out var sm))
                {
                    var emerge = sm.States.FirstOrDefault(s => s.Name == "oshabi_emerge");
                    if (emerge != null && emerge.Value > 0)
                    {
                        _phase = OshabiPhase.WaitForBoss;
                        _phaseStartTime = DateTime.Now;
                        ctx.Log("[Oshabi] oshabi_emerge confirmed — waiting for boss");
                        return BossEncounterResult.InProgress;
                    }
                }

                // Wait for cooldown before retrying
                if ((DateTime.Now - _lastSpawnerClickTime).TotalMilliseconds < SpawnerClickCooldownMs)
                {
                    Status = $"Verifying click (attempt {_spawnerClickAttempts})...";
                    return BossEncounterResult.InProgress;
                }

                ctx.Log($"[Oshabi] Click didn't register (attempt {_spawnerClickAttempts}), retrying");
            }

            // Find the label and click it
            ExileCore.PoEMemory.Elements.LabelOnGround? treeLabel = null;
            foreach (var label in gc.IngameState.IngameUi.ItemsOnGroundLabelElement.LabelsOnGround)
            {
                if (label.ItemOnGround == null) continue;
                if (label.ItemOnGround.Id != soulTree.Id) continue;
                if (label.Label == null || !label.Label.IsVisible) continue;
                treeLabel = label;
                break;
            }

            if (treeLabel != null && BotInput.CanAct)
            {
                var rect = treeLabel.Label.GetClientRect();
                // Verify rect is on screen (not clipped)
                var windowRect = gc.Window.GetWindowRectangle();
                if (rect.X >= 0 && rect.Y >= 0 && rect.X + rect.Width <= windowRect.Width
                    && rect.Y + rect.Height <= windowRect.Height)
                {
                    ctx.Navigation.Stop(gc);
                    var clickPos = new Vector2(rect.Center.X, rect.Center.Y);
                    var absPos = new Vector2(windowRect.X + clickPos.X, windowRect.Y + clickPos.Y);
                    BotInput.Click(absPos);
                    _spawnerClicked = true;
                    _spawnerClickAttempts++;
                    _lastSpawnerClickTime = DateTime.Now;
                    Status = $"Clicking Heart of the Grove (attempt {_spawnerClickAttempts})";
                    ctx.Log($"[Oshabi] Click attempt {_spawnerClickAttempts} at ({rect.Center.X:F0},{rect.Center.Y:F0})");
                }
                else
                {
                    Status = "Label off-screen — moving closer";
                }
            }
            else if (treeLabel == null)
            {
                Status = $"Label not visible — dist={distToTree:F0}g";
            }
            else
            {
                Status = "Waiting for input gate";
            }

            return BossEncounterResult.InProgress;
        }

        private BossEncounterResult TickWaitForBoss(BotContext ctx, GameController gc)
        {
            if ((DateTime.Now - _phaseStartTime).TotalSeconds > 30)
            {
                // Boss didn't spawn — maybe spawner click didn't work, retry
                _phase = OshabiPhase.ClickSpawner;
                _phaseStartTime = DateTime.Now;
                _spawnerClicked = false;
                Status = "Boss didn't appear — retrying spawner";
                ctx.Log("[Oshabi] Boss spawn timeout, retrying");
                return BossEncounterResult.InProgress;
            }

            if (_bossEntity != null && _bossEntity.IsAlive)
            {
                _phase = OshabiPhase.Fighting;
                _phaseStartTime = DateTime.Now;
                ctx.Log($"[Oshabi] Boss spawned: {_bossEntity.RenderName}");
                return BossEncounterResult.InProgress;
            }

            // Stay near the spawn point — midpoint between Soul Tree and portal
            var playerGrid = new Vector2(gc.Player.GridPosNum.X, gc.Player.GridPosNum.Y);
            if (_spawnWaitPos.HasValue)
            {
                var distToWait = Vector2.Distance(playerGrid, _spawnWaitPos.Value);
                if (distToWait > 15 && !ctx.Navigation.IsNavigating)
                {
                    ctx.Navigation.NavigateTo(gc, _spawnWaitPos.Value);
                }
            }
            else
            {
                // Calculate wait position — find Soul Tree and portal, use midpoint
                Vector2? treePos = null;
                Vector2? portalPos = null;
                foreach (var e in gc.EntityListWrapper.ValidEntitiesByType[EntityType.MiscellaneousObjects])
                {
                    if (e.Path.Contains(SoulTreePath) && e.IsTargetable)
                        treePos = new Vector2(e.GridPosNum.X, e.GridPosNum.Y);
                }
                var portal = FindPortal(gc);
                if (portal != null)
                    portalPos = new Vector2(portal.GridPosNum.X, portal.GridPosNum.Y);

                if (treePos.HasValue && portalPos.HasValue)
                    _spawnWaitPos = (treePos.Value + portalPos.Value) / 2;
                else if (treePos.HasValue)
                    _spawnWaitPos = treePos.Value; // fallback: near tree
            }

            var elapsed = (DateTime.Now - _phaseStartTime).TotalSeconds;
            Status = $"Waiting for Oshabi to spawn ({elapsed:F0}s) — holding position";
            return BossEncounterResult.InProgress;
        }

        private Entity? FindPortal(GameController gc)
        {
            foreach (var e in gc.EntityListWrapper.ValidEntitiesByType[EntityType.TownPortal])
            {
                if (e.IsTargetable) return e;
            }
            return null;
        }

        private BossEncounterResult TickFighting(BotContext ctx, GameController gc, Vector2 playerGrid)
        {
            if ((DateTime.Now - _phaseStartTime).TotalSeconds > 300)
            {
                Status = "Fight timeout (5min)";
                return BossEncounterResult.Failed;
            }

            // Check for vine barrier adds — navigate into the ring to kill them
            Entity? nearestVine = null;
            float nearestVineDist = float.MaxValue;
            foreach (var entity in gc.EntityListWrapper.ValidEntitiesByType[EntityType.Monster])
            {
                if (!entity.IsAlive || !entity.IsHostile) continue;
                if (!entity.Path.Contains("VineBarrier")) continue;
                var d = Vector2.Distance(playerGrid, entity.GridPosNum);
                if (d < nearestVineDist)
                {
                    nearestVineDist = d;
                    nearestVine = entity;
                }
            }

            if (nearestVine != null && nearestVineDist > 20)
            {
                // Vine barriers are up — navigate toward nearest one so totems can hit them
                if (!ctx.Navigation.IsNavigating)
                    ctx.Navigation.NavigateTo(gc, nearestVine.GridPosNum);
                Status = $"Clearing vine barrier ({nearestVineDist:F0}g)";
                return BossEncounterResult.InProgress;
            }

            if (_bossEntity != null && _bossEntity.IsAlive)
            {
                var bossGrid = new Vector2(_bossEntity.GridPosNum.X, _bossEntity.GridPosNum.Y);
                var dist = Vector2.Distance(playerGrid, bossGrid);

                // Always follow Oshabi — loot drops where she dies
                if (dist > 30 && !ctx.Navigation.IsNavigating)
                    ctx.Navigation.NavigateTo(gc, bossGrid);

                Status = $"Fighting Oshabi — dist={dist:F0}";
            }
            else
            {
                // Boss submerged — stay put, she'll re-emerge nearby
                Status = "Boss submerged — waiting";
            }

            return BossEncounterResult.InProgress;
        }

        private BossEncounterResult TickWaitingForLoot()
        {
            // Return Complete immediately — BossMode's LootSweep handles the
            // settle time (configured via LootSweepTimeoutSeconds setting)
            Status = "Oshabi complete!";
            return BossEncounterResult.Complete;
        }

        private Entity? FindBoss(GameController gc)
        {
            foreach (var entity in gc.EntityListWrapper.ValidEntitiesByType[EntityType.Monster])
            {
                if (!entity.IsHostile) continue;
                if (entity.Rarity != MonsterRarity.Unique) continue;
                if (!entity.Path.Contains(BossPath)) continue;
                return entity;
            }
            return null;
        }

        private bool IsAltarVisible(GameController gc)
        {
            // The altar entity always exists in the map but starts with targetable=false.
            // It flips to targetable=true only after the boss dies.
            // NOTE: MinimapIcon.IsVisible is always true — DO NOT use it for detection.
            foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
            {
                if (!entity.Path.Contains(DescensionAltarPath)) continue;
                if (entity.IsTargetable)
                    return true;
            }
            return false;
        }

        public void Render(BotContext ctx)
        {
            var gc = ctx.Game;
            var g = ctx.Graphics;
            if (gc?.Player == null || g == null) return;

            var cam = gc.IngameState.Camera;

            // Boss marker
            if (_bossEntity != null)
            {
                var world = _bossEntity.BoundsCenterPosNum;
                var screen = cam.WorldToScreen(world);
                if (screen.X > -200 && screen.X < 2400)
                {
                    var color = _bossEntity.IsAlive ? SharpDX.Color.Red : SharpDX.Color.LimeGreen;
                    g.DrawText(_bossEntity.IsAlive ? "OSHABI" : "OSHABI (dead)",
                        screen + new Vector2(-25, -30), color);
                }
            }

            // Nav path
            if (ctx.Navigation.IsNavigating)
            {
                var path = ctx.Navigation.CurrentNavPath;
                for (int i = ctx.Navigation.CurrentWaypointIndex; i < path.Count - 1; i++)
                {
                    var from = Pathfinding.GridToScreen(gc, path[i].Position);
                    var to = Pathfinding.GridToScreen(gc, path[i + 1].Position);
                    if (from.X < -200 || from.X > 2400 || to.X < -200 || to.X > 2400) continue;
                    g.DrawLine(from, to, 2f, SharpDX.Color.Orange);
                }
            }

            // HUD
            float hudX = 20, hudY = 250, lineH = 18;
            var phaseColor = _phase switch
            {
                OshabiPhase.Fighting => SharpDX.Color.Red,
                OshabiPhase.WaitingForLoot => SharpDX.Color.LimeGreen,
                _ => SharpDX.Color.White,
            };
            g.DrawText($"Oshabi: {_phase}", new Vector2(hudX, hudY), phaseColor);
            hudY += lineH;
            g.DrawText(Status, new Vector2(hudX, hudY), SharpDX.Color.Gray);
        }

        public void Reset()
        {
            _phase = OshabiPhase.Idle;
            _bossEntity = null;
            _bossWasAlive = false;
            _spawnerClicked = false;
            _spawnWaitPos = null;
            _spawnerClickAttempts = 0;
            _lastSpawnerClickTime = DateTime.MinValue;
            Status = "";
        }
    }
}
