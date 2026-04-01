using ExileCore;
using ExileCore.PoEMemory;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.PoEMemory.Components;
using ExileCore.Shared.Enums;
using AutoExile.Systems;
using System.Numerics;

namespace AutoExile.Modes.BossEncounters
{
    /// <summary>
    /// Incarnation of Fear (Anger Boss UBER) encounter.
    ///
    /// Strategy: pre-lay traps during invuln, pop flasks at emerge, orbit boss with
    /// frostblink while spamming traps. After kill, navigate to boss death position
    /// and loot sweep before signaling Complete.
    ///
    /// Re-entry: navigate to AreaTransition (must stay north of it for targetability),
    /// click it to enter boss room proper, then fight.
    ///
    /// Fragment: CurrencyUberBossKeyAnger, cost=4
    /// Boss: AngerBossUBER@85, spawns at (206,306)
    /// AreaTransition: (207,152), targetable only when player Y &lt; ~155
    /// </summary>
    public class FearEncounter : IBossEncounter
    {
        public string Name => "Incarnation of Fear";
        public string Status { get; private set; } = "";

        private const string FragmentPath = "CurrencyUberBossKeyAnger";
        private const string BossPath = "AngerBossUBER@";

        // Pre-lay position SOUTH of boss — traps land directly on boss at (206,306).
        private static readonly Vector2 DpsPosition = new(206, 320);

        // Re-entry: position to stand NORTH of the area transition so it's targetable.
        // Transition at (207,152) — must be at Y < ~155 to click it.
        private static readonly Vector2 TransitionApproachPos = new(207, 145);

        // Orbit
        private const float OrbitRadius = 20f;
        private const float OrbitBlinkIntervalMs = 2500f;

        public Func<Element, bool> MapFilter => el =>
        {
            var entity = el.Entity;
            return entity?.Path?.Contains(FragmentPath) == true;
        };

        public string? InventoryFragmentPath => FragmentPath;
        public int FragmentCost => 4;

        // Suppress combat during first-entry approach (don't chase untargetable boss)
        // and during re-entry approach (need to click area transition, not fight)
        public bool SuppressCombat => _phase == FearPhase.Approaching;

        // Suppress positioning during invuln (stay planted for trap pre-lay)
        // and during loot sweep (stay at death position)
        public bool SuppressCombatPositioning => _phase == FearPhase.WaitForVulnerable
            || _phase == FearPhase.WaitingForLoot;

        // ── State ──
        private FearPhase _phase = FearPhase.Idle;
        private DateTime _phaseStartTime;
        private Entity? _bossEntity;
        private bool _bossWasAlive;
        private bool _bossVulnerable;
        private bool _bossEmerged;
        private bool _isReentry;
        private Vector2? _bossDeathPos;
        private DateTime _lastLootScan;

        // Orbit state
        private float _orbitAngle;
        private DateTime _lastOrbitBlink;

        // Re-entry transition click
        private DateTime _lastTransitionClickTime;
        private int _transitionClickAttempts;

        private enum FearPhase
        {
            Idle,
            Approaching,
            WaitForVulnerable,
            Fighting,
            WaitingForLoot,  // Navigate to death pos, scan loot, wait for timeout → Complete
        }

        public void OnEnterZone(BotContext ctx)
        {
            var gc = ctx.Game;

            var pfGrid = gc.IngameState?.Data?.RawPathfindingData;
            var tgtGrid = gc.IngameState?.Data?.RawTerrainTargetingData;
            if (pfGrid != null && gc.Player != null)
            {
                var playerGrid = new Vector2(gc.Player.GridPosNum.X, gc.Player.GridPosNum.Y);
                ctx.Exploration.Initialize(pfGrid, tgtGrid, playerGrid,
                    ctx.Settings.Build.BlinkRange.Value);
            }

            _isReentry = _bossWasAlive;
            _phase = FearPhase.Approaching;
            _phaseStartTime = DateTime.Now;
            _bossEntity = null;
            _bossVulnerable = false;
            _bossEmerged = false;
            _orbitAngle = 0;
            _lastOrbitBlink = DateTime.MinValue;
            _lastTransitionClickTime = DateTime.MinValue;
            _transitionClickAttempts = 0;
            // Preserve _bossDeathPos across re-entry (loot is still there)
            Status = _isReentry ? "Re-entering Moment of Trauma" : "Entered Moment of Trauma";
            ctx.Log($"[Fear] Zone entered (reentry={_isReentry})");
        }

        public BossEncounterResult Tick(BotContext ctx)
        {
            var gc = ctx.Game;
            if (gc?.Player == null) return BossEncounterResult.InProgress;

            var playerGrid = new Vector2(gc.Player.GridPosNum.X, gc.Player.GridPosNum.Y);
            ctx.Exploration.Update(playerGrid);

            _bossEntity = FindBoss(gc);
            if (_bossEntity != null && _bossEntity.IsAlive)
                _bossWasAlive = true;

            // Read boss state machine
            if (_bossEntity != null && _bossEntity.TryGetComponent<StateMachine>(out var sm) && sm?.States != null)
            {
                foreach (var s in sm.States)
                {
                    if (s.Name == "boss_life_bar" && s.Value > 0 && !_bossVulnerable)
                    {
                        _bossVulnerable = true;
                        ctx.Log("[Fear] Boss vulnerable! (boss_life_bar=1)");
                    }
                    if (s.Name == "emerge" && s.Value > 0 && !_bossEmerged)
                    {
                        _bossEmerged = true;
                        ctx.Log("[Fear] Boss emerged — flasks active");
                    }
                }
            }

            // Flask suppression — clear at emerge (2.4s before vulnerable)
            ctx.Combat.BossInvulnerable = _bossEntity != null && _bossEntity.IsAlive && !_bossEmerged;

            // Kill detection → WaitingForLoot (NOT Complete — we loot first)
            if (_bossWasAlive && _phase != FearPhase.WaitingForLoot
                && (_bossEntity == null || !_bossEntity.IsAlive))
            {
                // Cache death position for loot navigation
                if (!_bossDeathPos.HasValue && _bossEntity != null)
                    _bossDeathPos = _bossEntity.GridPosNum;
                else if (!_bossDeathPos.HasValue)
                    _bossDeathPos = new Vector2(206, 306); // fallback to spawn pos

                _phase = FearPhase.WaitingForLoot;
                _phaseStartTime = DateTime.Now;
                _lastLootScan = DateTime.MinValue;
                ctx.Log($"[Fear] Kill detected — looting at ({_bossDeathPos.Value.X:F0},{_bossDeathPos.Value.Y:F0})");
            }

            switch (_phase)
            {
                case FearPhase.Approaching:
                    return TickApproaching(ctx, gc, playerGrid);
                case FearPhase.WaitForVulnerable:
                    return TickWaitForVulnerable(ctx, gc, playerGrid);
                case FearPhase.Fighting:
                    return TickFighting(ctx, gc, playerGrid);
                case FearPhase.WaitingForLoot:
                    return TickWaitingForLoot(ctx, gc, playerGrid);
                default:
                    return BossEncounterResult.InProgress;
            }
        }

        private BossEncounterResult TickApproaching(BotContext ctx, GameController gc, Vector2 playerGrid)
        {
            if ((DateTime.Now - _phaseStartTime).TotalSeconds > 60)
            {
                Status = "Timeout: couldn't reach boss";
                return BossEncounterResult.Failed;
            }

            // ── Re-entry path: need to click area transition to get past force field ──
            if (_isReentry)
            {
                // If boss is already dead (we killed it with DoT as we died), go straight to loot
                if (_bossWasAlive && (_bossEntity == null || !_bossEntity.IsAlive))
                    return BossEncounterResult.InProgress; // kill detection above handles transition

                return TickReentryApproach(ctx, gc, playerGrid);
            }

            // ── First entry: walk to DPS position south of boss ──
            var distToDps = Vector2.Distance(playerGrid, DpsPosition);
            if (distToDps > 10)
            {
                if (!ctx.Navigation.IsNavigating)
                    ctx.Navigation.NavigateTo(gc, DpsPosition);
                Status = $"Moving to DPS position ({distToDps:F0}g)";
                return BossEncounterResult.InProgress;
            }

            // At DPS position — transition to pre-lay phase when boss is visible
            if (_bossEntity != null && _bossEntity.IsAlive)
            {
                _phase = FearPhase.WaitForVulnerable;
                _phaseStartTime = DateTime.Now;
                ctx.Log("[Fear] At DPS position — pre-laying traps");
            }
            else
            {
                Status = "At DPS position — waiting for boss";
            }
            return BossEncounterResult.InProgress;
        }

        private BossEncounterResult TickReentryApproach(BotContext ctx, GameController gc, Vector2 playerGrid)
        {
            // Find the area transition
            Entity? transition = null;
            foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
            {
                if (entity.Type != EntityType.AreaTransition) continue;
                if (entity.Path?.Contains("AreaTransition") != true) continue;
                transition = entity;
                break;
            }

            if (transition == null)
            {
                // No transition found — we might already be in the boss room (past the door)
                if (_bossEntity != null && _bossEntity.IsAlive && _bossEntity.DistancePlayer < 60)
                {
                    _phase = FearPhase.Fighting;
                    _phaseStartTime = DateTime.Now;
                    _bossVulnerable = true;
                    _bossEmerged = true;
                    ctx.Log("[Fear] Re-entry — already in boss room, fighting");
                    return BossEncounterResult.InProgress;
                }
                Status = "Re-entry — looking for area transition";
                return BossEncounterResult.InProgress;
            }

            // Transition exists — navigate to approach position NORTH of it
            var distToApproach = Vector2.Distance(playerGrid, TransitionApproachPos);

            if (!transition.IsTargetable)
            {
                // Too close / too far south — back up north
                if (playerGrid.Y > 150)
                {
                    if (!ctx.Navigation.IsNavigating)
                        ctx.Navigation.NavigateTo(gc, TransitionApproachPos);
                    Status = "Backing up — transition not targetable";
                    return BossEncounterResult.InProgress;
                }
                // At north position but still not targetable — wait
                Status = "Waiting for transition to become targetable";
                return BossEncounterResult.InProgress;
            }

            // Transition is targetable — click it
            if (distToApproach > 15)
            {
                if (!ctx.Navigation.IsNavigating)
                    ctx.Navigation.NavigateTo(gc, TransitionApproachPos);
                Status = $"Moving to transition ({distToApproach:F0}g)";
                return BossEncounterResult.InProgress;
            }

            // In position, click the transition
            if ((DateTime.Now - _lastTransitionClickTime).TotalMilliseconds > 1500)
            {
                if (!ctx.Interaction.IsBusy)
                {
                    ctx.Interaction.InteractWithEntity(transition, ctx.Navigation, requireProximity: true);
                    _lastTransitionClickTime = DateTime.Now;
                    _transitionClickAttempts++;
                    ctx.Log($"[Fear] Clicking area transition (attempt {_transitionClickAttempts})");
                }
                Status = $"Clicking transition (attempt {_transitionClickAttempts})";
            }
            else
            {
                Status = "Waiting to retry transition click";
            }

            // After successful transition, bot will be in boss room — detect via position jump
            // or boss becoming close. The area doesn't change so OnEnterZone won't fire again.
            if (_bossEntity != null && _bossEntity.DistancePlayer < 80)
            {
                _phase = FearPhase.Fighting;
                _phaseStartTime = DateTime.Now;
                _bossVulnerable = true;
                _bossEmerged = true;
                ctx.Log("[Fear] Through transition — fighting");
            }

            return BossEncounterResult.InProgress;
        }

        private BossEncounterResult TickWaitForVulnerable(BotContext ctx, GameController gc, Vector2 playerGrid)
        {
            if (_bossVulnerable)
            {
                _phase = FearPhase.Fighting;
                _phaseStartTime = DateTime.Now;
                if (_bossEntity != null)
                {
                    var toBoss = _bossEntity.GridPosNum - playerGrid;
                    _orbitAngle = MathF.Atan2(toBoss.Y, toBoss.X) + MathF.PI;
                }
                _lastOrbitBlink = DateTime.Now;
                ctx.Log("[Fear] Boss vulnerable — orbiting with frostblink!");
                return BossEncounterResult.InProgress;
            }

            var distToDps = Vector2.Distance(playerGrid, DpsPosition);
            if (distToDps > 15 && !ctx.Navigation.IsNavigating)
                ctx.Navigation.NavigateTo(gc, DpsPosition);

            var elapsed = (DateTime.Now - _phaseStartTime).TotalSeconds;
            Status = _bossEmerged
                ? $"Emerged — flasks active! ({elapsed:F1}s)"
                : $"Pre-laying traps ({elapsed:F1}s)";

            if (elapsed > 12)
            {
                _phase = FearPhase.Fighting;
                _phaseStartTime = DateTime.Now;
                ctx.Log("[Fear] Invuln timeout — forcing fight");
            }

            return BossEncounterResult.InProgress;
        }

        private BossEncounterResult TickFighting(BotContext ctx, GameController gc, Vector2 playerGrid)
        {
            if ((DateTime.Now - _phaseStartTime).TotalSeconds > 300)
            {
                Status = "Fight timeout (5min)";
                return BossEncounterResult.Failed;
            }

            if (_bossEntity == null || !_bossEntity.IsAlive)
            {
                Status = "Boss not visible — waiting";
                return BossEncounterResult.InProgress;
            }

            // Cache boss position continuously (for death pos)
            _bossDeathPos = _bossEntity.GridPosNum;

            var bossGrid = _bossEntity.GridPosNum;
            var distToBoss = Vector2.Distance(playerGrid, bossGrid);

            // Frostblink orbit
            if ((DateTime.Now - _lastOrbitBlink).TotalMilliseconds >= OrbitBlinkIntervalMs)
            {
                var blinkSkill = FindReadyMovementSkill(ctx);
                if (blinkSkill != null)
                {
                    _orbitAngle += MathF.PI / 2f;
                    var orbitTarget = bossGrid + new Vector2(
                        MathF.Cos(_orbitAngle) * OrbitRadius,
                        MathF.Sin(_orbitAngle) * OrbitRadius);

                    var screenPos = Systems.Pathfinding.GridToScreen(gc, orbitTarget);
                    var windowRect = gc.Window.GetWindowRectangle();

                    if (screenPos.X > 0 && screenPos.X < windowRect.Width &&
                        screenPos.Y > 0 && screenPos.Y < windowRect.Height)
                    {
                        var absPos = new Vector2(windowRect.X + screenPos.X, windowRect.Y + screenPos.Y);
                        BotInput.ForceCursorPressKey(absPos, blinkSkill.Key);
                        blinkSkill.LastUsedAt = DateTime.Now;
                        _lastOrbitBlink = DateTime.Now;
                    }
                }
            }

            var currentOrbitPos = bossGrid + new Vector2(
                MathF.Cos(_orbitAngle) * OrbitRadius,
                MathF.Sin(_orbitAngle) * OrbitRadius);

            if (Vector2.Distance(playerGrid, currentOrbitPos) > 10 && !ctx.Navigation.IsNavigating)
                ctx.Navigation.NavigateTo(gc, currentOrbitPos);

            var hp = _bossEntity.GetComponent<Life>();
            var hpPct = hp != null ? (hp.CurHP * 100 / Math.Max(1, hp.MaxHP)) : 0;
            Status = $"Orbiting + DPS — Boss HP:{hpPct}% dist={distToBoss:F0}g";
            return BossEncounterResult.InProgress;
        }

        private BossEncounterResult TickWaitingForLoot(BotContext ctx, GameController gc, Vector2 playerGrid)
        {
            var timeout = ctx.Settings.Boss.LootSweepTimeoutSeconds.Value;
            var elapsed = (DateTime.Now - _phaseStartTime).TotalSeconds;

            if (elapsed > timeout)
            {
                Status = "Loot sweep done";
                ctx.Log("[Fear] Loot sweep timeout — signaling Complete");
                return BossEncounterResult.Complete;
            }

            var remaining = timeout - elapsed;
            var countdown = $"({remaining:F0}s left)";

            // Navigate to boss death position
            var lootPos = _bossDeathPos ?? new Vector2(206, 306);
            var distToLoot = Vector2.Distance(playerGrid, lootPos);
            if (distToLoot > 15 && !ctx.Navigation.IsNavigating)
                ctx.Navigation.NavigateTo(gc, lootPos);

            // Scan for loot
            if ((DateTime.Now - _lastLootScan).TotalMilliseconds >= 500)
            {
                ctx.Loot.Scan(gc);
                _lastLootScan = DateTime.Now;
            }

            // Pick up items
            if (ctx.Interaction.IsBusy)
            {
                Status = $"Picking up loot {countdown}";
                return BossEncounterResult.InProgress;
            }

            if (ctx.Loot.HasLootNearby)
            {
                var (_, candidate) = ctx.Loot.PickupNext(ctx.Interaction, ctx.Navigation);
                if (candidate != null)
                {
                    Status = $"Looting: {candidate.ItemName} {countdown}";
                    return BossEncounterResult.InProgress;
                }
            }

            // Label toggle if needed
            if (ctx.Loot.TogglePhase != LootSystem.LabelTogglePhase.Idle)
            {
                ctx.Loot.TickLabelToggle(gc);
                Status = $"Label toggle {countdown}";
                return BossEncounterResult.InProgress;
            }
            if (ctx.Loot.ShouldToggleLabels(gc))
            {
                ctx.Loot.StartLabelToggle(gc);
                return BossEncounterResult.InProgress;
            }

            Status = $"Waiting for loot at boss position {countdown}";
            return BossEncounterResult.InProgress;
        }

        private MovementSkillInfo? FindReadyMovementSkill(BotContext ctx)
        {
            foreach (var ms in ctx.Navigation.MovementSkills)
            {
                if (!ms.IsReady) continue;
                if (ms.MinCastIntervalMs > 0 &&
                    (DateTime.Now - ms.LastUsedAt).TotalMilliseconds < ms.MinCastIntervalMs)
                    continue;
                return ms;
            }
            return null;
        }

        private Entity? FindBoss(GameController gc)
        {
            try
            {
                foreach (var entity in gc.EntityListWrapper.ValidEntitiesByType[EntityType.Monster])
                {
                    if (!entity.IsHostile) continue;
                    if (entity.Rarity != MonsterRarity.Unique) continue;
                    if (entity.Path?.Contains(BossPath) != true) continue;
                    return entity;
                }
            }
            catch (IndexOutOfRangeException) { }
            return null;
        }

        public void Render(BotContext ctx)
        {
            var gc = ctx.Game;
            var g = ctx.Graphics;
            if (gc?.Player == null || g == null) return;

            var cam = gc.IngameState.Camera;

            if (_bossEntity != null)
            {
                var screen = cam.WorldToScreen(_bossEntity.BoundsCenterPosNum);
                if (screen.X > -200 && screen.X < 2400)
                {
                    var color = _bossEntity.IsAlive
                        ? (_bossVulnerable ? SharpDX.Color.Red : SharpDX.Color.Yellow)
                        : SharpDX.Color.LimeGreen;
                    var label = _bossVulnerable ? "FEAR BOSS (DPS!)" :
                        _bossEmerged ? "FEAR BOSS (flasks!)" :
                        _bossEntity.IsAlive ? "FEAR BOSS (invuln)" : "FEAR BOSS (dead)";
                    g.DrawText(label, screen + new Vector2(-40, -30), color);
                }
            }

            // Loot position marker
            if (_phase == FearPhase.WaitingForLoot && _bossDeathPos.HasValue)
            {
                var world = new Vector3(_bossDeathPos.Value.X * 10.88f, _bossDeathPos.Value.Y * 10.88f, 0);
                var screen = cam.WorldToScreen(world);
                if (screen.X > 0 && screen.X < 2400)
                    g.DrawText("LOOT HERE", screen + new Vector2(-30, -20), SharpDX.Color.Gold);
            }

            float hudX = 20, hudY = 250, lineH = 18;
            var phaseColor = _phase switch
            {
                FearPhase.WaitForVulnerable => SharpDX.Color.Yellow,
                FearPhase.Fighting => SharpDX.Color.Red,
                FearPhase.WaitingForLoot => SharpDX.Color.Gold,
                _ => SharpDX.Color.White,
            };
            g.DrawText($"Fear: {_phase}", new Vector2(hudX, hudY), phaseColor);
            hudY += lineH;
            g.DrawText(Status, new Vector2(hudX, hudY), SharpDX.Color.Gray);
        }

        public void Reset()
        {
            _phase = FearPhase.Idle;
            _bossEntity = null;
            _bossWasAlive = false;
            _bossVulnerable = false;
            _bossEmerged = false;
            _isReentry = false;
            _bossDeathPos = null;
            _orbitAngle = 0;
            _lastOrbitBlink = DateTime.MinValue;
            _lastTransitionClickTime = DateTime.MinValue;
            _transitionClickAttempts = 0;
            Status = "";
        }
    }
}
