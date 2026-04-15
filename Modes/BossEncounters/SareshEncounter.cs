using ExileCore;
using ExileCore.PoEMemory;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using AutoExile.Systems;
using System.Numerics;
using System.Windows.Forms;

namespace AutoExile.Modes.BossEncounters
{
    /// <summary>
    /// Saresh, The Black Barya — hardcoded encounter for mine build.
    ///
    /// Arena layout (grid coords):
    ///   Portal entrance:    ~(240, 378)
    ///   Bridge edge (out):  (246, 352) — blink from here into arena
    ///   Arena gap edge (in): (279, 348) — blink from here back to bridge
    ///   Arena combat pos:   (346, 310) — stand here during Emerge
    ///   Arena center:       (345, 345) — boss spawn, loot drop
    ///   Stairs safe spot:   (345, 266) — retreat here during swirl
    ///
    /// Flow:
    ///   1. Walk to bridge edge → blink (T) into arena
    ///   2. Stand at combat pos, combat stacks damage during Emerge
    ///   3. phase_sequence ≥ 1: blink (T) to stairs, cast mines (W) at boss
    ///   4. Boss dies → loot at arena center
    ///   5. Walk to arena gap edge → blink (T) back to bridge → Complete
    /// </summary>
    public class SareshEncounter : IBossEncounter
    {
        public string Name => "Black Barya";
        public string Status { get; private set; } = "";

        private const string FragmentPath = "CurrencyFaridunBossKey";
        private const string BossPath = "FaridunSareshBoss/Saresh_@";
        private const string DescensionAltarPath = "SareshDescensionObject";

        // ── Hardcoded positions (grid) ──
        private static readonly Vector2 BridgeEdge = new(246, 352);     // outside arena, blink from
        private static readonly Vector2 ArenaGapEdge = new(279, 348);   // inside arena, blink out from
        private static readonly Vector2 ArenaCombatPos = new(345, 330); // close enough to trigger boss
        private static readonly Vector2 ArenaCenter = new(345, 345);    // boss spawn / loot
        private static readonly Vector2 StairsPos = new(345, 266);      // safe casting spot

        // Gap crossing direction: bridge → arena gap edge (normalized)
        // Used for dot product to determine which side of the gap the player is on
        private static readonly Vector2 GapCrossingDir = Vector2.Normalize(ArenaGapEdge - BridgeEdge);

        // ── Skill keys (resolved from build settings on zone entry) ──
        private Keys _blinkKey = Keys.T;

        // All Enemy-role skills sorted by priority (highest first) for stairs casting
        private List<StairsCastEntry> _stairsSkills = new();

        // ── Tuning ──
        private const float BlinkThreshold = 15f;
        private const float ArenaThreshold = 20f;
        private const float StairsThreshold = 25f; // generous — blink overshoots
        private const int SidestepDistance = 20;
        private const float MinBossDistance = 75f;   // don't get closer than this to boss from stairs
        private const float MortarDangerRadius = 25f; // sidestep if mortar landing within this range
        private const string MortarPath = "MortarProjectile"; // matches both SareshMortar and FaridunWispMortar

        public Func<Element, bool> MapFilter => el =>
        {
            var entity = el.Entity;
            return entity?.Path?.EndsWith(FragmentPath) == true;
        };

        public string? InventoryFragmentPath => FragmentPath;

        public IReadOnlyList<string> MustLootItems { get; } = new[]
        {
            "Leather Belt", // Soulcord
        };

        // ── Combat/dodge suppression ──
        // Suppress combat during manual-input phases and during StackDamage approach
        public bool SuppressCombat => _phase == Phase.BlinkIn
            || _phase == Phase.Retreat
            || _phase == Phase.StairsCasting
            || _phase == Phase.BlinkOut
            || (_phase == Phase.StackDamage && !_closeEnoughForCombat);

        // Suppress dodge during retreat (blinks fling player wrong direction)
        public bool SuppressDodge => _phase == Phase.Retreat;

        // ── State ──
        private Phase _phase = Phase.Idle;
        private DateTime _phaseStartTime;
        private DateTime _lastBlinkAttempt;
        private DateTime _lastSidestepTime;
        private Entity? _bossEntity;
        private bool _bossWasAlive;
        private bool _blinkFired;
        private bool _closeEnoughForCombat;
        private Vector2? _bossDeathPos;
        private DateTime _lastLootScan;

        private enum Phase
        {
            Idle,
            WalkToBridge,   // Walk to bridge edge
            BlinkIn,        // Blink across gap into arena
            StackDamage,    // Stand at combat pos, combat system fires
            Retreat,        // Blink to stairs after detonation
            StairsCasting,  // Cast mines from stairs, sidestep projectiles
            WaitForDeath,   // Boss dying
            WaitingForLoot, // Loot sweep
            BlinkOut,       // Walk to arena gap edge, blink back to bridge
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

            // Resolve skill keys from build settings
            var moveSkills = ctx.Settings.Build.GetMovementSkills();
            var blinkSkill = moveSkills.FirstOrDefault(s => s.CanCrossTerrain.Value);
            if (blinkSkill != null)
                _blinkKey = blinkSkill.Key.Value;

            // Build stairs skill list: all Enemy-role skills, sorted by priority (highest first)
            // Respects MinCastInterval so debuffs fire first on cooldown, then damage spam fills in
            _stairsSkills.Clear();
            var sorted = ctx.Settings.Build.AllSkillSlots
                .Where(s => s.Key.Value != Keys.None && s.Role.Value == "Enemy")
                .OrderByDescending(s => s.Priority.Value);
            foreach (var slot in sorted)
            {
                _stairsSkills.Add(new StairsCastEntry
                {
                    Key = slot.Key.Value,
                    MinIntervalMs = slot.MinCastIntervalMs.Value,
                    LastCastAt = DateTime.MinValue,
                });
            }
            ctx.Log($"[Saresh] Keys: blink={_blinkKey}, damage skills={_stairsSkills.Count} ({string.Join(",", _stairsSkills.Select(s => s.Key))})");

            _phase = Phase.WalkToBridge;
            _phaseStartTime = DateTime.Now;
            _bossEntity = null;
            _bossWasAlive = false;
            _blinkFired = false;
            _closeEnoughForCombat = false;
            _bossDeathPos = null;
            _lastBlinkAttempt = DateTime.MinValue;
            _lastSidestepTime = DateTime.MinValue;
            Status = "Entered — walking to bridge";
            ctx.Log("[Saresh] Zone entered");
        }

        public BossEncounterResult Tick(BotContext ctx)
        {
            var gc = ctx.Game;
            if (gc?.Player == null) return BossEncounterResult.InProgress;

            var playerGrid = new Vector2(gc.Player.GridPosNum.X, gc.Player.GridPosNum.Y);
            ctx.Exploration.Update(playerGrid);

            // Always scan for boss
            _bossEntity = FindBoss(gc);
            if (_bossEntity != null && _bossEntity.IsAlive)
                _bossWasAlive = true;

            // Kill detection
            if (_bossWasAlive && _phase != Phase.WaitingForLoot && _phase != Phase.BlinkOut)
            {
                if (IsBossDead() || IsAltarVisible(gc))
                {
                    _phase = Phase.WaitingForLoot;
                    _phaseStartTime = DateTime.Now;
                    _bossDeathPos = _bossEntity != null
                        ? new Vector2(_bossEntity.GridPosNum.X, _bossEntity.GridPosNum.Y)
                        : ArenaCenter;
                    Status = "Boss dead — looting";
                    ctx.Log("[Saresh] Kill detected");
                    return BossEncounterResult.InProgress;
                }
            }

            // Detonation transition
            var phaseSeq = GetPhaseSequence();
            if (phaseSeq >= 1 && _phase == Phase.StackDamage)
            {
                _phase = Phase.Retreat;
                _phaseStartTime = DateTime.Now;
                _blinkFired = false;
                ctx.Log($"[Saresh] Detonation! phase_seq={phaseSeq} — blinking to stairs");
            }

            switch (_phase)
            {
                case Phase.WalkToBridge:    return TickWalkToBridge(ctx, gc, playerGrid);
                case Phase.BlinkIn:         return TickBlinkIn(ctx, gc, playerGrid);
                case Phase.StackDamage:     return TickStackDamage(ctx, gc, playerGrid);
                case Phase.Retreat:         return TickRetreat(ctx, gc, playerGrid);
                case Phase.StairsCasting:   return TickStairsCasting(ctx, gc, playerGrid);
                case Phase.WaitForDeath:    return TickWaitForDeath(ctx, gc, playerGrid);
                case Phase.WaitingForLoot:  return TickWaitingForLoot(ctx, gc, playerGrid);
                case Phase.BlinkOut:        return TickBlinkOut(ctx, gc, playerGrid);
                default: return BossEncounterResult.InProgress;
            }
        }

        // ── Phase: Walk to bridge edge ──
        private BossEncounterResult TickWalkToBridge(BotContext ctx, GameController gc, Vector2 playerGrid)
        {
            if ((DateTime.Now - _phaseStartTime).TotalSeconds > 30)
                return Fail("Timeout walking to bridge");

            // Already inside arena (re-entry after death)
            if (IsInsideArena(playerGrid))
            {
                if (_bossEntity != null && _bossEntity.IsAlive)
                {
                    var phaseSeq = GetPhaseSequence();
                    if (phaseSeq >= 1)
                    {
                        _phase = Phase.Retreat;
                        _phaseStartTime = DateTime.Now;
                        _blinkFired = false;
                        ctx.Log("[Saresh] Re-entered during explosion — retreating");
                    }
                    else
                    {
                        _phase = Phase.StackDamage;
                        _phaseStartTime = DateTime.Now;
                        ctx.Log("[Saresh] Re-entered in arena — stacking damage");
                    }
                }
                else
                {
                    _phase = Phase.StackDamage;
                    _phaseStartTime = DateTime.Now;
                    ctx.Log("[Saresh] Already in arena");
                }
                return BossEncounterResult.InProgress;
            }

            var dist = Vector2.Distance(playerGrid, BridgeEdge);
            if (dist < BlinkThreshold)
            {
                _phase = Phase.BlinkIn;
                _phaseStartTime = DateTime.Now;
                _blinkFired = false;
                ctx.Log("[Saresh] At bridge edge — blinking in");
                return BossEncounterResult.InProgress;
            }

            if (!ctx.Navigation.IsNavigating)
                ctx.Navigation.NavigateTo(gc, BridgeEdge);

            Status = $"Walking to bridge ({dist:F0}g)";
            return BossEncounterResult.InProgress;
        }

        // ── Phase: Blink across gap into arena ──
        private BossEncounterResult TickBlinkIn(BotContext ctx, GameController gc, Vector2 playerGrid)
        {
            if ((DateTime.Now - _phaseStartTime).TotalSeconds > 10)
                return Fail("Timeout blinking into arena");

            // Dot product: are we on the arena side of the gap?
            if (IsInsideArena(playerGrid))
            {
                _phase = Phase.StackDamage;
                _phaseStartTime = DateTime.Now;
                var distToArena = Vector2.Distance(playerGrid, ArenaCombatPos);
                ctx.Log($"[Saresh] Crossed gap — {distToArena:F0}g to combat pos");
                return BossEncounterResult.InProgress;
            }

            if (!_blinkFired || (DateTime.Now - _phaseStartTime).TotalSeconds > 2)
            {
                var screenTarget = Pathfinding.GridToScreen(gc, ArenaCombatPos);
                var windowRect = gc.Window.GetWindowRectangle();
                var absPos = new Vector2(windowRect.X + screenTarget.X, windowRect.Y + screenTarget.Y);

                if (BotInput.CursorPressKey(absPos, _blinkKey))
                {
                    _blinkFired = true;
                    _phaseStartTime = DateTime.Now;
                    ctx.Log("[Saresh] Blink fired toward arena");
                }
            }

            Status = "Blinking into arena...";
            return BossEncounterResult.InProgress;
        }

        // ── Phase: Stack damage during Emerge ──
        private BossEncounterResult TickStackDamage(BotContext ctx, GameController gc, Vector2 playerGrid)
        {
            if ((DateTime.Now - _phaseStartTime).TotalSeconds > 45)
                return Fail("Timeout stacking damage");

            var dist = Vector2.Distance(playerGrid, ArenaCombatPos);
            _closeEnoughForCombat = dist <= ArenaThreshold;

            // Not close enough — blink toward combat pos (don't use NavigateTo,
            // SuppressCombat triggers WalkOnly which breaks pathfinding inside arena)
            if (!_closeEnoughForCombat)
            {
                if (!_blinkFired || (DateTime.Now - _lastBlinkAttempt).TotalSeconds > 2)
                {
                    var screenTarget = Pathfinding.GridToScreen(gc, ArenaCombatPos);
                    var windowRect = gc.Window.GetWindowRectangle();
                    var absPos = new Vector2(windowRect.X + screenTarget.X, windowRect.Y + screenTarget.Y);

                    if (BotInput.CursorPressKey(absPos, _blinkKey))
                    {
                        _blinkFired = true;
                        _lastBlinkAttempt = DateTime.Now; // reuse as blink cooldown timer
                        ctx.Log($"[Saresh] Blink toward combat pos ({dist:F0}g away)");
                    }
                }
                Status = $"Approaching boss — {dist:F0}g";
            }
            else
            {
                _blinkFired = false; // reset for retreat phase
                Status = $"Stacking damage — {dist:F0}g";
            }

            return BossEncounterResult.InProgress;
        }

        // ── Phase: Blink toward stairs, then walk the rest ──
        private BossEncounterResult TickRetreat(BotContext ctx, GameController gc, Vector2 playerGrid)
        {
            if ((DateTime.Now - _phaseStartTime).TotalSeconds > 20)
                return Fail("Timeout retreating");

            var distToStairs = Vector2.Distance(playerGrid, StairsPos);
            if (distToStairs < StairsThreshold)
            {
                _phase = Phase.StairsCasting;
                _phaseStartTime = DateTime.Now;
                if (ctx.Navigation.IsNavigating)
                    ctx.Navigation.Stop(gc);
                ctx.Log("[Saresh] At stairs — casting mines");
                return BossEncounterResult.InProgress;
            }

            // First blink toward stairs to cover most of the distance
            if (!_blinkFired)
            {
                if (ctx.Navigation.IsNavigating)
                    ctx.Navigation.Stop(gc);

                var screenTarget = Pathfinding.GridToScreen(gc, StairsPos);
                var windowRect = gc.Window.GetWindowRectangle();
                var absPos = new Vector2(windowRect.X + screenTarget.X, windowRect.Y + screenTarget.Y);

                if (BotInput.CursorPressKey(absPos, _blinkKey))
                {
                    _blinkFired = true;
                    ctx.Log($"[Saresh] Blink fired toward stairs (dist={distToStairs:F0}g)");
                }

                Status = $"Blinking to stairs — {distToStairs:F0}g";
                return BossEncounterResult.InProgress;
            }

            // After blink, walk the remaining distance (blink overshoots/undershoots)
            if (!ctx.Navigation.IsNavigating)
                ctx.Navigation.NavigateTo(gc, StairsPos);

            Status = $"Walking to stairs — {distToStairs:F0}g";
            return BossEncounterResult.InProgress;
        }

        // ── Phase: Cast mines from stairs ──
        private BossEncounterResult TickStairsCasting(BotContext ctx, GameController gc, Vector2 playerGrid)
        {
            if ((DateTime.Now - _phaseStartTime).TotalSeconds > 30)
                return Fail("Timeout casting from stairs");

            var phaseSeq = GetPhaseSequence();
            if (phaseSeq >= 3)
            {
                _phase = Phase.WaitForDeath;
                _phaseStartTime = DateTime.Now;
                ctx.Log("[Saresh] Explosion finished — waiting for death");
                return BossEncounterResult.InProgress;
            }

            // Stay at minimum distance from boss — walk back to stairs if too close
            var bossPos = _bossEntity != null
                ? new Vector2(_bossEntity.GridPosNum.X, _bossEntity.GridPosNum.Y)
                : ArenaCenter;
            var distToBoss = Vector2.Distance(playerGrid, bossPos);
            if (distToBoss < MinBossDistance)
            {
                if (!ctx.Navigation.IsNavigating)
                    ctx.Navigation.NavigateTo(gc, StairsPos);
                Status = $"Too close to boss ({distToBoss:F0}g) — backing up";
                return BossEncounterResult.InProgress;
            }

            // Scan for incoming mortar projectiles — read TravelTarget to see where they'll land
            if ((DateTime.Now - _lastSidestepTime).TotalMilliseconds > 800)
            {
                var mortarLanding = FindIncomingMortar(gc, playerGrid);
                if (mortarLanding.HasValue)
                {
                    // Sidestep east/west away from the landing spot
                    var landingDir = mortarLanding.Value.X - playerGrid.X;
                    var sideDir = landingDir >= 0 ? -1f : 1f; // dodge away from landing
                    var sideTarget = new Vector2(playerGrid.X + sideDir * SidestepDistance, playerGrid.Y);
                    var sideScreen = Pathfinding.GridToScreen(gc, sideTarget);
                    var windowRect = gc.Window.GetWindowRectangle();
                    var absPos = new Vector2(windowRect.X + sideScreen.X, windowRect.Y + sideScreen.Y);

                    // Force — survival trumps input cadence (mine casting blocks CanAct)
                    if (BotInput.ForceCursorPressKey(absPos, _blinkKey))
                    {
                        _lastSidestepTime = DateTime.Now;
                        ctx.Log($"[Saresh] Sidestep for mortar landing at ({mortarLanding.Value.X:F0},{mortarLanding.Value.Y:F0})");
                    }
                    return BossEncounterResult.InProgress;
                }
            }

            // Cast skills toward boss — iterate by priority, respect per-skill cooldowns
            if (BotInput.CanAct)
            {
                var targetGrid = _bossEntity != null
                    ? new Vector2(_bossEntity.GridPosNum.X, _bossEntity.GridPosNum.Y)
                    : ArenaCenter;

                var targetScreen = Pathfinding.GridToScreen(gc, targetGrid);
                var windowRect = gc.Window.GetWindowRectangle();
                var absTarget = new Vector2(windowRect.X + targetScreen.X, windowRect.Y + targetScreen.Y);

                for (int i = 0; i < _stairsSkills.Count; i++)
                {
                    var skill = _stairsSkills[i];
                    if (skill.MinIntervalMs > 0 &&
                        (DateTime.Now - skill.LastCastAt).TotalMilliseconds < skill.MinIntervalMs)
                        continue;

                    if (BotInput.CursorPressKey(absTarget, skill.Key))
                    {
                        skill.LastCastAt = DateTime.Now;
                        _stairsSkills[i] = skill; // struct — write back
                        break;
                    }
                }
            }

            // Drift back if sidestepped too far
            var distToStairs = Vector2.Distance(playerGrid, StairsPos);
            if (distToStairs > StairsThreshold * 2 && !ctx.Navigation.IsNavigating)
                ctx.Navigation.NavigateTo(gc, StairsPos);

            Status = $"Casting mines — phase_seq={phaseSeq}";
            return BossEncounterResult.InProgress;
        }

        // ── Phase: Wait for death ──
        private BossEncounterResult TickWaitForDeath(BotContext ctx, GameController gc, Vector2 playerGrid)
        {
            if ((DateTime.Now - _phaseStartTime).TotalSeconds > 15)
                return Fail("Death timeout");

            // Keep casting skills
            if (BotInput.CanAct && _bossEntity != null)
            {
                var targetGrid = new Vector2(_bossEntity.GridPosNum.X, _bossEntity.GridPosNum.Y);
                var targetScreen = Pathfinding.GridToScreen(gc, targetGrid);
                var windowRect = gc.Window.GetWindowRectangle();
                var absTarget = new Vector2(windowRect.X + targetScreen.X, windowRect.Y + targetScreen.Y);

                for (int i = 0; i < _stairsSkills.Count; i++)
                {
                    var skill = _stairsSkills[i];
                    if (skill.MinIntervalMs > 0 &&
                        (DateTime.Now - skill.LastCastAt).TotalMilliseconds < skill.MinIntervalMs)
                        continue;

                    if (BotInput.CursorPressKey(absTarget, skill.Key))
                    {
                        skill.LastCastAt = DateTime.Now;
                        _stairsSkills[i] = skill;
                        break;
                    }
                }
            }

            Status = "Boss dying — casting mines";
            return BossEncounterResult.InProgress;
        }

        // ── Phase: Loot ──
        private BossEncounterResult TickWaitingForLoot(BotContext ctx, GameController gc, Vector2 playerGrid)
        {
            var timeout = ctx.Settings.Run.LootSweepTimeoutSeconds.Value;
            var elapsed = (DateTime.Now - _phaseStartTime).TotalSeconds;

            if (elapsed > timeout)
            {
                // Done looting — blink out of arena before signaling Complete
                _phase = Phase.BlinkOut;
                _phaseStartTime = DateTime.Now;
                _blinkFired = false;
                ctx.Log("[Saresh] Loot done — blinking out of arena");
                return BossEncounterResult.InProgress;
            }

            var remaining = timeout - elapsed;
            var countdown = $"({remaining:F0}s left)";

            var lootPos = _bossDeathPos ?? ArenaCenter;
            var distToLoot = Vector2.Distance(playerGrid, lootPos);
            if (distToLoot > 15 && !ctx.Navigation.IsNavigating)
                ctx.Navigation.NavigateTo(gc, lootPos);

            if ((DateTime.Now - _lastLootScan).TotalMilliseconds >= 500)
            {
                ctx.Loot.Scan(gc);
                _lastLootScan = DateTime.Now;
            }

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

            if (ctx.Loot.TogglePhase != LootSystem.LabelTogglePhase.Idle)
            {
                ctx.Loot.TickLabelToggle(gc);
                return BossEncounterResult.InProgress;
            }
            if (ctx.Loot.ShouldToggleLabels(gc))
            {
                ctx.Loot.StartLabelToggle(gc);
                return BossEncounterResult.InProgress;
            }

            Status = $"Waiting for loot {countdown}";
            return BossEncounterResult.InProgress;
        }

        // ── Phase: Blink out of arena back to bridge side ──
        private BossEncounterResult TickBlinkOut(BotContext ctx, GameController gc, Vector2 playerGrid)
        {
            if ((DateTime.Now - _phaseStartTime).TotalSeconds > 20)
            {
                // Timeout — signal Complete anyway, BossMode will try portal key as fallback
                ctx.Log("[Saresh] BlinkOut timeout — signaling Complete anyway");
                return BossEncounterResult.Complete;
            }

            // Dot product: are we on the bridge side of the gap?
            if (!IsInsideArena(playerGrid))
            {
                ctx.Log("[Saresh] Back on bridge — Complete");
                return BossEncounterResult.Complete;
            }

            // Step 1: Walk to arena gap edge
            var distToGapEdge = Vector2.Distance(playerGrid, ArenaGapEdge);
            if (distToGapEdge > BlinkThreshold)
            {
                if (!ctx.Navigation.IsNavigating)
                    ctx.Navigation.NavigateTo(gc, ArenaGapEdge);

                Status = $"Walking to gap edge ({distToGapEdge:F0}g)";
                return BossEncounterResult.InProgress;
            }

            // Step 2: Blink toward bridge
            if (!_blinkFired || (DateTime.Now - _phaseStartTime).TotalSeconds > 5)
            {
                if (ctx.Navigation.IsNavigating)
                    ctx.Navigation.Stop(gc);

                var screenTarget = Pathfinding.GridToScreen(gc, BridgeEdge);
                var windowRect = gc.Window.GetWindowRectangle();
                var absPos = new Vector2(windowRect.X + screenTarget.X, windowRect.Y + screenTarget.Y);

                if (BotInput.CursorPressKey(absPos, _blinkKey))
                {
                    _blinkFired = true;
                    _phaseStartTime = DateTime.Now;
                    ctx.Log("[Saresh] Blink fired toward bridge");
                }
            }

            Status = "Blinking out of arena...";
            return BossEncounterResult.InProgress;
        }

        // ── Helpers ──

        private BossEncounterResult Fail(string reason)
        {
            Status = reason;
            return BossEncounterResult.Failed;
        }

        private struct StairsCastEntry
        {
            public Keys Key;
            public int MinIntervalMs; // 0 = spam every tick
            public DateTime LastCastAt;
        }

        /// <summary>
        /// Dot product test: is the player on the arena side of the gap?
        /// Projects (player - bridgeEdge) onto the gap crossing direction.
        /// Positive = arena side, negative = bridge side.
        /// The gap midpoint is ~16 units along the crossing direction,
        /// so anything > 20 means we're clearly inside the arena.
        /// </summary>
        private bool IsInsideArena(Vector2 playerGrid)
        {
            var offset = playerGrid - BridgeEdge;
            return Vector2.Dot(offset, GapCrossingDir) > 20f;
        }

        /// <summary>
        /// Scan for mortar projectiles whose TravelTarget lands near the player.
        /// Returns the landing position if danger detected, null if safe.
        /// </summary>
        private Vector2? FindIncomingMortar(GameController gc, Vector2 playerGrid)
        {
            foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
            {
                if (!entity.Path.Contains(MortarPath)) continue;
                if (!entity.TryGetComponent<ExileCore.PoEMemory.Components.Positioned>(out var pos))
                    continue;

                var target = pos.TravelTarget;
                var landing = new Vector2(target.X, target.Y);
                // Convert to grid if TravelTarget is in world coords — check magnitude
                // Grid coords are typically < 1000, world coords are ~3000+
                if (landing.X > 1000)
                    landing /= 10.88f; // world to grid

                var distToPlayer = Vector2.Distance(landing, playerGrid);
                if (distToPlayer < MortarDangerRadius)
                    return landing;
            }
            return null;
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

        private int GetPhaseSequence()
        {
            if (_bossEntity == null) return -1;
            if (!_bossEntity.TryGetComponent<ExileCore.PoEMemory.Components.StateMachine>(out var sm))
                return -1;
            var state = sm.States.FirstOrDefault(s => s.Name == "phase_sequence");
            return state != null ? (int)state.Value : -1;
        }

        private bool IsBossDead()
        {
            if (_bossEntity == null) return false;
            if (!_bossEntity.TryGetComponent<ExileCore.PoEMemory.Components.StateMachine>(out var sm))
                return false;
            var hasdied = sm.States.FirstOrDefault(s => s.Name == "hasdied");
            return hasdied != null && hasdied.Value > 0;
        }

        private bool IsAltarVisible(GameController gc)
        {
            foreach (var entity in gc.EntityListWrapper.ValidEntitiesByType[EntityType.IngameIcon])
            {
                if (!entity.Path.Contains(DescensionAltarPath)) continue;
                if (entity.TryGetComponent<ExileCore.PoEMemory.Components.StateMachine>(out var sm))
                {
                    var enabled = sm.States.FirstOrDefault(s => s.Name == "enabled");
                    if (enabled != null && enabled.Value > 0) return true;
                }
            }
            return false;
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
                    g.DrawText(_bossEntity.IsAlive ? "SARESH" : "SARESH (dead)",
                        screen + new Vector2(-25, -30), color);
                }
            }

            // Waypoint markers
            void DrawMarker(Vector2 gridPos, string label, SharpDX.Color color)
            {
                var w = Pathfinding.GridToWorld3D(gc, gridPos);
                var s = cam.WorldToScreen(w);
                if (s.X > -200 && s.X < 2400)
                    g.DrawText(label, s + new Vector2(-20, -15), color);
            }

            if (_phase == Phase.WalkToBridge || _phase == Phase.BlinkIn)
                DrawMarker(BridgeEdge, "BRIDGE", SharpDX.Color.Cyan);
            if (_phase == Phase.Retreat || _phase == Phase.StairsCasting)
                DrawMarker(StairsPos, "STAIRS", SharpDX.Color.Cyan);
            if (_phase == Phase.StackDamage)
                DrawMarker(ArenaCombatPos, "STAND HERE", SharpDX.Color.Yellow);
            if (_phase == Phase.BlinkOut)
                DrawMarker(ArenaGapEdge, "EXIT", SharpDX.Color.Cyan);

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
                Phase.StackDamage => SharpDX.Color.Yellow,
                Phase.Retreat => SharpDX.Color.Red,
                Phase.StairsCasting => SharpDX.Color.Orange,
                Phase.WaitForDeath => SharpDX.Color.Orange,
                Phase.WaitingForLoot => SharpDX.Color.LimeGreen,
                Phase.BlinkOut => SharpDX.Color.Cyan,
                _ => SharpDX.Color.White,
            };
            g.DrawText($"Saresh: {_phase}", new Vector2(hudX, hudY), phaseColor);
            hudY += lineH;
            g.DrawText(Status, new Vector2(hudX, hudY), SharpDX.Color.Gray);
            hudY += lineH;

            if (_bossEntity != null && _bossEntity.IsAlive)
            {
                var dist = Vector2.Distance(playerGrid, _bossEntity.GridPosNum);
                var seq = GetPhaseSequence();
                g.DrawText($"Boss dist={dist:F0}g phase_seq={seq}",
                    new Vector2(hudX, hudY), SharpDX.Color.DarkGray);
            }
        }

        public void Reset()
        {
            _phase = Phase.Idle;
            _bossEntity = null;
            _bossWasAlive = false;
            _blinkFired = false;
            _closeEnoughForCombat = false;
            _bossDeathPos = null;
            _lastLootScan = DateTime.MinValue;
            _lastBlinkAttempt = DateTime.MinValue;
            _lastSidestepTime = DateTime.MinValue;
            Status = "";
        }
    }
}
