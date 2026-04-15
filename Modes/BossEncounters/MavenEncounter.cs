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
    /// The Maven boss encounter — multi-phase fight with arena mechanics.
    ///
    /// Recording analysis (7 fights):
    ///   - Phase flow: Emerge → Brain Blast → Cyclone(lt+1) → Maven Fight → repeat 3x → Final Phase
    ///   - Brain Blast: 6.0s cast, lethal within 100g of center. Early warning: VoidsandDaemon spawn 4-5s before.
    ///   - Memory Game: pause_orbit=1 + Maven LongOpen. Can't detect pattern. Survive at edge.
    ///   - Maven entities: TheMaven@84 (48.8M HP), TheMavenEnraged_@84 (90.2M HP, final phase)
    ///   - Kill: MavenEnraged dies → flappy_bird=0 → envoy_shield=1 → Nucleus dies
    ///
    /// Fragment: CurrencyMavenKey (Maven's Writ), cost=1
    /// </summary>
    public class MavenEncounter : IBossEncounter
    {
        public string Name => "The Maven";
        public string Status { get; private set; } = "";

        private const string FragmentPath = "CurrencyMavenKey";
        // Match both TheMaven@ and TheMavenEnraged_@ via common prefix
        private const string MavenPath = "MavenBoss/The";
        private const string NucleusPath = "MavenBrainBoss@";
        private const string VoidsandDaemonPath = "MavenBrainVoidsandDaemon";
        private const string GravityWellPath = "TrapNormal2";

        // Arena geometry
        private static readonly Vector2 ArenaCenter = new(229, 229);

        // Brain Blast safe position — 119g from center on the entry side (walkable terrain confirmed)
        // Player spawns at ~(131,230), so this path is always clear
        private static readonly Vector2 BrainBlastSafePos = new(110, 229);

        // Safe positions at arena edge for Memory Game
        private static readonly Vector2 SafeEdgeTop = new(229, 160);
        private static readonly Vector2 SafeEdgeLeft = new(160, 229);
        private static readonly Vector2 SafeEdgeRight = new(300, 229);
        private static readonly Vector2 SafeEdgeBottom = new(229, 300);

        // Approach target
        private static readonly Vector2 ApproachTarget = new(200, 229);

        private const float DpsRange = 30f;
        private const float DangerZoneRadius = 8f;

        public Func<Element, bool> MapFilter => el =>
        {
            var entity = el.Entity;
            return entity?.Path?.Contains(FragmentPath) == true;
        };

        public string? InventoryFragmentPath => FragmentPath;
        public int FragmentCost => 1;

        public IReadOnlyList<string> MustLootItems { get; } = new[]
        {
            "Elevated Sextant",
            "Maven's Orb",
            "Orb of Dominance",
        };

        // Suppress combat during escape/memory phases — need full cursor freedom for blink
        public bool SuppressCombat => _phase == MavenPhase.BrainBlastEscape
            || _phase == MavenPhase.BrainBlastWaiting
            || _phase == MavenPhase.MemoryGame;

        // Suppress positioning during pre-trap (fire skills at center, don't chase)
        public bool SuppressCombatPositioning => _phase == MavenPhase.PreTrap;

        // ── State ──
        private MavenPhase _phase = MavenPhase.Idle;
        private DateTime _phaseStartTime;

        // Entity tracking
        private Entity? _mavenEntity;
        private Entity? _nucleusEntity;
        private bool _mavenWasAlive;
        private bool _voidsandDaemonDetected;
        private int _lastLifeThreshold = -1;
        private bool _lastFlappyBird;

        // Hazard avoidance
        private readonly List<Vector2> _blockedPositions = new();
        private DateTime _lastHazardScan;
        private const float HazardScanIntervalMs = 200f;

        // Nucleus HP tracking
        private long _lastNucleusHp;
        private long _nucleusMaxHp;

        // Loot
        private DateTime _lastLootScan;
        // Maven loot drops at SpecificLootSurrogate position (291,230)
        private static readonly Vector2 MavenLootPos = new(291, 230);

        private enum MavenPhase
        {
            Idle,
            Approaching,        // Walk into arena
            MavenFight,         // DPS Maven (TheMaven or TheMavenEnraged_)
            BrainBlastEscape,   // VoidsandDaemon detected → RUN to 110g+ from center
            BrainBlastWaiting,  // At safe distance, wait for blast to finish
            BrainDps,           // After blast, DPS Nucleus during HookIn/Idle
            MemoryGame,         // pause_orbit=1 → flee to edge, wait
            PreTrap,            // Cyclone detected → position at center, pre-lay traps
            FinalPhase,         // flappy_bird=1, DPS MavenEnraged
            WaitingForLoot,
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

            _phase = MavenPhase.Approaching;
            _phaseStartTime = DateTime.Now;
            _mavenEntity = null;
            _nucleusEntity = null;
            _mavenWasAlive = false;
            _voidsandDaemonDetected = false;
            _lastLifeThreshold = -1;
            _lastFlappyBird = false;
            _lastNucleusHp = 0;
            _nucleusMaxHp = 0;
            _blockedPositions.Clear();
            Status = "Entered Maven arena";
            ctx.Log("[Maven] Zone entered");
        }

        public BossEncounterResult Tick(BotContext ctx)
        {
            var gc = ctx.Game;
            if (gc?.Player == null) return BossEncounterResult.InProgress;

            var playerGrid = new Vector2(gc.Player.GridPosNum.X, gc.Player.GridPosNum.Y);
            ctx.Exploration.Update(playerGrid);

            // ── Entity scanning ──
            ScanEntities(gc);
            ScanHazards(ctx, gc, playerGrid);

            // ── Read Nucleus state machine ──
            int lifeThreshold = 0;
            bool flappyBird = false;
            int pauseOrbit = 0;
            int nucleusShield = 0;
            int envoyShield = 0;
            if (_nucleusEntity != null && _nucleusEntity.TryGetComponent<StateMachine>(out var nSm))
            {
                foreach (var s in nSm.States)
                {
                    switch (s.Name)
                    {
                        case "life_threshold": lifeThreshold = (int)s.Value; break;
                        case "flappy_bird": flappyBird = s.Value > 0; break;
                        case "pause_orbit": pauseOrbit = (int)s.Value; break;
                        case "shield": nucleusShield = (int)s.Value; break;
                        case "envoy_shield": envoyShield = (int)s.Value; break;
                    }
                }
            }

            // Track Nucleus HP
            if (_nucleusEntity != null)
            {
                var nLife = _nucleusEntity.GetComponent<Life>();
                if (nLife != null)
                {
                    _lastNucleusHp = nLife.CurHP;
                    if (_nucleusMaxHp == 0) _nucleusMaxHp = nLife.MaxHP;
                }
            }

            // Check Maven boss_life_bar for invulnerability (suppresses utility flasks)
            bool mavenVulnerable = false;
            if (_mavenEntity != null && _mavenEntity.IsAlive)
            {
                if (_mavenEntity.TryGetComponent<StateMachine>(out var mSm) && mSm?.States != null)
                {
                    foreach (var s in mSm.States)
                    {
                        if (s.Name == "boss_life_bar" && s.Value > 0)
                        { mavenVulnerable = true; break; }
                    }
                }
            }
            ctx.Combat.BossInvulnerable = _mavenEntity != null && _mavenEntity.IsAlive && !mavenVulnerable;

            // ── Kill detection (highest priority) ──
            // Primary: Nucleus envoy_shield=1 (fires ~4s after MavenEnraged death)
            // Secondary: Nucleus alive=False
            if (envoyShield > 0 || (_nucleusEntity != null && !_nucleusEntity.IsAlive && _lastLifeThreshold >= 3))
            {
                _phase = MavenPhase.WaitingForLoot;
                _phaseStartTime = DateTime.Now;
                Status = "Maven killed!";
                ctx.Log("[Maven] KILL DETECTED — envoy_shield or Nucleus dead");
                return BossEncounterResult.Complete;
            }

            // ── Log phase transitions ──
            if (lifeThreshold != _lastLifeThreshold && _lastLifeThreshold >= 0)
                ctx.Log($"[Maven] life_threshold: {_lastLifeThreshold} → {lifeThreshold}");
            _lastLifeThreshold = lifeThreshold;

            if (flappyBird && !_lastFlappyBird)
            {
                _phase = MavenPhase.FinalPhase;
                _phaseStartTime = DateTime.Now;
                ctx.Log("[Maven] FINAL PHASE — flappy_bird=1");
            }
            _lastFlappyBird = flappyBird;

            // Maven alive tracking
            bool mavenAlive = _mavenEntity != null && _mavenEntity.IsAlive && _mavenEntity.IsTargetable;
            if (mavenAlive) _mavenWasAlive = true;

            // ── Brain Blast early warning — VoidsandDaemon detection ──
            // VoidsandDaemons spawn 4-5s BEFORE Nucleus SpellAreaOfEffect (Brain Blast)
            // This is our earliest and most reliable warning signal
            if (_voidsandDaemonDetected && _phase != MavenPhase.BrainBlastEscape
                && _phase != MavenPhase.BrainBlastWaiting && _phase != MavenPhase.Approaching)
            {
                _phase = MavenPhase.BrainBlastEscape;
                _phaseStartTime = DateTime.Now;
                _mavenWasAlive = false; // Maven dies before Brain Blast
                ctx.Log("[Maven] VoidsandDaemon detected — BRAIN BLAST INCOMING! Escaping!");
            }

            // ── Memory Game detection ──
            if (pauseOrbit == 1 && _phase != MavenPhase.MemoryGame
                && _phase != MavenPhase.BrainBlastEscape && _phase != MavenPhase.BrainBlastWaiting)
            {
                _phase = MavenPhase.MemoryGame;
                _phaseStartTime = DateTime.Now;
                ctx.Log("[Maven] Memory Game detected (pause_orbit=1)");
            }
            else if (pauseOrbit == 0 && _phase == MavenPhase.MemoryGame)
            {
                // Memory game ended — return to appropriate fight phase
                _phase = flappyBird ? MavenPhase.FinalPhase : MavenPhase.MavenFight;
                _phaseStartTime = DateTime.Now;
                ctx.Log("[Maven] Memory Game ended");
            }

            // ── Pre-trap detection — Cyclone (shield=2) means new Maven spawning ──
            if (nucleusShield == 2 && _phase == MavenPhase.BrainDps)
            {
                _phase = MavenPhase.PreTrap;
                _phaseStartTime = DateTime.Now;
                ctx.Log("[Maven] Cyclone detected (shield=2) — pre-trap at center");
            }

            // ── Phase transitions ──

            // Approaching → MavenFight when Maven appears
            if (_phase == MavenPhase.Approaching && mavenAlive)
            {
                _phase = MavenPhase.MavenFight;
                _phaseStartTime = DateTime.Now;
                ctx.Log("[Maven] Maven appeared — fighting");
            }

            // BrainBlastEscape → BrainBlastWaiting when at safe distance
            if (_phase == MavenPhase.BrainBlastEscape)
            {
                var distFromCenter = Vector2.Distance(playerGrid, ArenaCenter);
                if (distFromCenter >= 105)
                {
                    _phase = MavenPhase.BrainBlastWaiting;
                    _phaseStartTime = DateTime.Now;
                    ctx.Log($"[Maven] Safe from Brain Blast ({distFromCenter:F0}g from center)");
                }
            }

            // BrainBlastWaiting → BrainDps when blast finishes (Nucleus leaves center or HookOut/Cyclone)
            if (_phase == MavenPhase.BrainBlastWaiting)
            {
                var elapsed = (DateTime.Now - _phaseStartTime).TotalSeconds;
                bool nucleusAtCenter = _nucleusEntity != null
                    && Vector2.Distance(_nucleusEntity.GridPosNum, ArenaCenter) < 15;
                bool blastOver = !nucleusAtCenter || nucleusShield > 0 || elapsed > 15;

                if (blastOver)
                {
                    _voidsandDaemonDetected = false; // reset for next cycle
                    _phase = MavenPhase.BrainDps;
                    _phaseStartTime = DateTime.Now;
                    ctx.Log("[Maven] Brain Blast over — DPS Nucleus");
                }
            }

            // BrainDps → MavenFight when Maven reappears (after Cyclone/HookIn)
            if (_phase == MavenPhase.BrainDps && mavenAlive)
            {
                _phase = MavenPhase.MavenFight;
                _phaseStartTime = DateTime.Now;
                ctx.Log("[Maven] Maven reappeared — switching to Maven DPS");
            }

            // PreTrap → MavenFight after timeout or Maven appears
            if (_phase == MavenPhase.PreTrap)
            {
                var elapsed = (DateTime.Now - _phaseStartTime).TotalSeconds;
                if (mavenAlive || elapsed > 4)
                {
                    _phase = mavenAlive
                        ? (flappyBird ? MavenPhase.FinalPhase : MavenPhase.MavenFight)
                        : MavenPhase.BrainDps;
                    _phaseStartTime = DateTime.Now;
                    ctx.Log("[Maven] Pre-trap done");
                }
            }

            // MavenFight → VoidsandDaemon or Brain Blast when Maven dies
            if ((_phase == MavenPhase.MavenFight || _phase == MavenPhase.FinalPhase)
                && !mavenAlive && _mavenWasAlive && !flappyBird)
            {
                // Maven died — Brain Blast sequence starting (Voidsand Daemons next)
                // If we haven't detected daemons yet, preemptively start escaping
                _mavenWasAlive = false;
                if (_phase != MavenPhase.BrainBlastEscape)
                {
                    _phase = MavenPhase.BrainBlastEscape;
                    _phaseStartTime = DateTime.Now;
                    ctx.Log("[Maven] Maven died — escaping for Brain Blast");
                }
            }

            // Final Phase kill — MavenEnraged died
            if (_phase == MavenPhase.FinalPhase && !mavenAlive && _mavenWasAlive && flappyBird)
            {
                // Don't immediately signal complete — wait for envoy_shield confirmation
                // (handled by kill detection above)
                _mavenWasAlive = false;
                ctx.Log("[Maven] MavenEnraged died — waiting for kill confirmation");
            }

            // Overall timeout
            if ((DateTime.Now - _phaseStartTime).TotalSeconds > 600)
            {
                Status = "Fight timeout (10min)";
                return BossEncounterResult.Failed;
            }

            // ── Tick current phase ──
            switch (_phase)
            {
                case MavenPhase.Approaching: return TickApproaching(ctx, gc, playerGrid);
                case MavenPhase.MavenFight: return TickDpsMaven(ctx, gc, playerGrid);
                case MavenPhase.FinalPhase: return TickDpsMaven(ctx, gc, playerGrid);
                case MavenPhase.BrainBlastEscape: return TickBrainBlastEscape(ctx, gc, playerGrid);
                case MavenPhase.BrainBlastWaiting: return TickBrainBlastWaiting(ctx, gc, playerGrid);
                case MavenPhase.BrainDps: return TickDpsNucleus(ctx, gc, playerGrid);
                case MavenPhase.MemoryGame: return TickMemoryGame(ctx, gc, playerGrid);
                case MavenPhase.PreTrap: return TickPreTrap(ctx, gc, playerGrid);
                case MavenPhase.WaitingForLoot: return TickWaitingForLoot(ctx, gc, playerGrid);
                default: return BossEncounterResult.InProgress;
            }
        }

        // ── Phase Ticks ──

        private BossEncounterResult TickApproaching(BotContext ctx, GameController gc, Vector2 playerGrid)
        {
            var dist = Vector2.Distance(playerGrid, ApproachTarget);
            if (dist > 15)
            {
                if (!ctx.Navigation.IsNavigating)
                    ctx.Navigation.NavigateTo(gc, ApproachTarget);
                Status = $"Approaching arena ({dist:F0}g)";
            }
            else
            {
                Status = "In arena — waiting for fight";
            }
            return BossEncounterResult.InProgress;
        }

        private BossEncounterResult TickDpsMaven(BotContext ctx, GameController gc, Vector2 playerGrid)
        {
            if (_mavenEntity == null || !_mavenEntity.IsAlive || !_mavenEntity.IsTargetable)
            {
                var phaseName = _phase == MavenPhase.FinalPhase ? "Final Phase" : "Maven Phase";
                Status = $"{phaseName} — Maven transitioning";
                return BossEncounterResult.InProgress;
            }

            var mavenGrid = _mavenEntity.GridPosNum;
            var distToMaven = Vector2.Distance(playerGrid, mavenGrid);
            var dpsTarget = GetSafePositionNear(playerGrid, mavenGrid, DpsRange);

            if (Vector2.Distance(playerGrid, dpsTarget) > 10 && !ctx.Navigation.IsNavigating)
                ctx.Navigation.NavigateTo(gc, dpsTarget);

            var hp = _mavenEntity.GetComponent<Life>();
            var hpPct = hp != null ? (hp.CurHP * 100 / Math.Max(1, hp.MaxHP)) : 0;
            var phaseLbl = _phase == MavenPhase.FinalPhase ? "FINAL" : $"Maven(lt={_lastLifeThreshold})";
            Status = $"{phaseLbl} — HP:{hpPct}% dist={distToMaven:F0}g";
            return BossEncounterResult.InProgress;
        }

        private BossEncounterResult TickDpsNucleus(BotContext ctx, GameController gc, Vector2 playerGrid)
        {
            if (_nucleusEntity == null || !_nucleusEntity.IsAlive)
            {
                Status = "Brain Phase — Nucleus not visible";
                return BossEncounterResult.InProgress;
            }

            var nucleusGrid = _nucleusEntity.GridPosNum;
            var distToNucleus = Vector2.Distance(playerGrid, nucleusGrid);
            var dpsTarget = GetSafePositionNear(playerGrid, nucleusGrid, DpsRange);

            if (Vector2.Distance(playerGrid, dpsTarget) > 10 && !ctx.Navigation.IsNavigating)
                ctx.Navigation.NavigateTo(gc, dpsTarget);

            var hp = _nucleusEntity.GetComponent<Life>();
            var hpPct = hp != null ? (hp.CurHP * 100 / Math.Max(1, hp.MaxHP)) : 0;
            Status = $"Brain DPS — Nucleus HP:{hpPct}% dist={distToNucleus:F0}g";
            return BossEncounterResult.InProgress;
        }

        private BossEncounterResult TickBrainBlastEscape(BotContext ctx, GameController gc, Vector2 playerGrid)
        {
            var distToSafe = Vector2.Distance(playerGrid, BrainBlastSafePos);

            if (distToSafe > 10)
            {
                if (!ctx.Navigation.IsNavigating)
                    ctx.Navigation.NavigateTo(gc, BrainBlastSafePos);
            }

            var distFromCenter = Vector2.Distance(playerGrid, ArenaCenter);
            Status = $"BRAIN BLAST — ESCAPE! {distFromCenter:F0}g from center (need 105+)";
            return BossEncounterResult.InProgress;
        }

        private BossEncounterResult TickBrainBlastWaiting(BotContext ctx, GameController gc, Vector2 playerGrid)
        {
            var distFromCenter = Vector2.Distance(playerGrid, ArenaCenter);
            var elapsed = (DateTime.Now - _phaseStartTime).TotalSeconds;

            // Stay at safe distance
            if (distFromCenter < 100 && !ctx.Navigation.IsNavigating)
                ctx.Navigation.NavigateTo(gc, BrainBlastSafePos);

            Status = $"Brain Blast — safe ({distFromCenter:F0}g from center, {elapsed:F0}s)";
            return BossEncounterResult.InProgress;
        }

        private BossEncounterResult TickMemoryGame(BotContext ctx, GameController gc, Vector2 playerGrid)
        {
            var distFromCenter = Vector2.Distance(playerGrid, ArenaCenter);
            var safePos = GetNearestSafeEdge(playerGrid);
            var distToSafe = Vector2.Distance(playerGrid, safePos);

            if (distToSafe > 8 && !ctx.Navigation.IsNavigating)
                ctx.Navigation.NavigateTo(gc, safePos);

            var elapsed = (DateTime.Now - _phaseStartTime).TotalSeconds;
            Status = $"MEMORY GAME — edge! {distFromCenter:F0}g from center ({elapsed:F0}s)";
            return BossEncounterResult.InProgress;
        }

        private BossEncounterResult TickPreTrap(BotContext ctx, GameController gc, Vector2 playerGrid)
        {
            var distToCenter = Vector2.Distance(playerGrid, ArenaCenter);
            if (distToCenter > 15 && !ctx.Navigation.IsNavigating)
                ctx.Navigation.NavigateTo(gc, ArenaCenter);

            var elapsed = (DateTime.Now - _phaseStartTime).TotalSeconds;
            Status = $"Pre-trap at center ({elapsed:F1}s)";
            return BossEncounterResult.InProgress;
        }

        private BossEncounterResult TickWaitingForLoot(BotContext ctx, GameController gc, Vector2 playerGrid)
        {
            var timeout = ctx.Settings.Run.LootSweepTimeoutSeconds.Value;
            var elapsed = (DateTime.Now - _phaseStartTime).TotalSeconds;

            if (elapsed > timeout)
            {
                ctx.Log("[Maven] Loot sweep timeout — signaling Complete");
                return BossEncounterResult.Complete;
            }

            var remaining = timeout - elapsed;
            var countdown = $"({remaining:F0}s left)";

            var distToLoot = Vector2.Distance(playerGrid, MavenLootPos);
            if (distToLoot > 15 && !ctx.Navigation.IsNavigating)
                ctx.Navigation.NavigateTo(gc, MavenLootPos);

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

        // ── Entity Scanning ──

        private void ScanEntities(GameController gc)
        {
            _mavenEntity = null;
            _nucleusEntity = null;
            _voidsandDaemonDetected = false;

            try
            {
                foreach (var entity in gc.EntityListWrapper.ValidEntitiesByType[EntityType.Monster])
                {
                    if (!entity.IsHostile) continue;
                    var path = entity.Path;
                    if (path == null) continue;

                    if (entity.Rarity == MonsterRarity.Unique)
                    {
                        if (path.Contains(MavenPath))
                            _mavenEntity = entity;
                        else if (path.Contains(NucleusPath))
                            _nucleusEntity = entity;
                    }

                    // VoidsandDaemon — any rarity, alive = Brain Blast early warning
                    if (entity.IsAlive && path.Contains(VoidsandDaemonPath))
                        _voidsandDaemonDetected = true;
                }
            }
            catch (IndexOutOfRangeException) { }
        }

        // ── Hazard Avoidance ──

        private void ScanHazards(BotContext ctx, GameController gc, Vector2 playerGrid)
        {
            if ((DateTime.Now - _lastHazardScan).TotalMilliseconds < HazardScanIntervalMs)
                return;
            _lastHazardScan = DateTime.Now;

            _blockedPositions.Clear();

            try
            {
                foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
                {
                    if (entity.DistancePlayer > 100) continue;
                    var path = entity.Path;
                    if (path == null) continue;

                    if (path.Contains(GravityWellPath) && entity.IsAlive)
                        _blockedPositions.Add(entity.GridPosNum);

                    if (path.Contains("Sleepable") && entity.DistancePlayer < 60)
                        _blockedPositions.Add(entity.GridPosNum);
                }
            }
            catch (InvalidOperationException) { }

            ctx.Navigation.SetBlockedPositions(_blockedPositions);
        }

        private Vector2 GetSafePositionNear(Vector2 playerGrid, Vector2 target, float maxRange)
        {
            var distToTarget = Vector2.Distance(playerGrid, target);

            if (distToTarget <= maxRange && !IsNearHazard(playerGrid))
                return playerGrid;

            var directPos = distToTarget > maxRange
                ? target + Vector2.Normalize(playerGrid - target) * (maxRange * 0.7f)
                : playerGrid;

            if (!IsNearHazard(directPos))
                return directPos;

            float bestDist = float.MaxValue;
            Vector2 bestPos = directPos;
            for (int i = 0; i < 8; i++)
            {
                float angle = i * MathF.PI / 4f;
                var candidate = target + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * (maxRange * 0.7f);
                if (IsNearHazard(candidate)) continue;
                var d = Vector2.Distance(playerGrid, candidate);
                if (d < bestDist) { bestDist = d; bestPos = candidate; }
            }
            return bestPos;
        }

        private bool IsNearHazard(Vector2 pos)
        {
            foreach (var bp in _blockedPositions)
                if (Vector2.Distance(pos, bp) < DangerZoneRadius)
                    return true;
            return false;
        }

        private Vector2 GetNearestSafeEdge(Vector2 playerGrid)
        {
            Vector2[] edges = { SafeEdgeTop, SafeEdgeLeft, SafeEdgeRight, SafeEdgeBottom };
            float bestDist = float.MaxValue;
            var bestEdge = SafeEdgeLeft;

            foreach (var edge in edges)
            {
                if (IsNearHazard(edge)) continue;
                var d = Vector2.Distance(playerGrid, edge);
                if (d < bestDist) { bestDist = d; bestEdge = edge; }
            }

            if (bestDist == float.MaxValue)
            {
                foreach (var edge in edges)
                {
                    var d = Vector2.Distance(playerGrid, edge);
                    if (d < bestDist) { bestDist = d; bestEdge = edge; }
                }
            }
            return bestEdge;
        }

        // ── Render ──

        public void Render(BotContext ctx)
        {
            var gc = ctx.Game;
            var g = ctx.Graphics;
            if (gc?.Player == null || g == null) return;

            var cam = gc.IngameState.Camera;

            if (_mavenEntity != null)
            {
                var screen = cam.WorldToScreen(_mavenEntity.BoundsCenterPosNum);
                if (screen.X > -200 && screen.X < 2400)
                {
                    var color = _mavenEntity.IsAlive ? SharpDX.Color.Purple : SharpDX.Color.Gray;
                    g.DrawText("MAVEN", screen + new Vector2(-25, -30), color);
                }
            }

            if (_nucleusEntity != null)
            {
                var screen = cam.WorldToScreen(_nucleusEntity.BoundsCenterPosNum);
                if (screen.X > -200 && screen.X < 2400)
                    g.DrawText("NUCLEUS", screen + new Vector2(-30, -30), SharpDX.Color.Cyan);
            }

            foreach (var bp in _blockedPositions)
            {
                var bpWorld = new Vector3(bp.X * 10.88f, bp.Y * 10.88f, 0);
                var screen = cam.WorldToScreen(bpWorld);
                if (screen.X > 0 && screen.X < 2400)
                    g.DrawText("X", screen + new Vector2(-4, -6), SharpDX.Color.Red);
            }

            float hudX = 20, hudY = 250, lineH = 18;
            var phaseColor = _phase switch
            {
                MavenPhase.MavenFight => SharpDX.Color.Purple,
                MavenPhase.BrainDps => SharpDX.Color.Cyan,
                MavenPhase.BrainBlastEscape or MavenPhase.BrainBlastWaiting => SharpDX.Color.Red,
                MavenPhase.MemoryGame => SharpDX.Color.Yellow,
                MavenPhase.FinalPhase => SharpDX.Color.OrangeRed,
                MavenPhase.PreTrap => SharpDX.Color.LimeGreen,
                MavenPhase.WaitingForLoot => SharpDX.Color.LimeGreen,
                _ => SharpDX.Color.White,
            };

            g.DrawText($"Maven: {_phase}", new Vector2(hudX, hudY), phaseColor);
            hudY += lineH;
            g.DrawText(Status, new Vector2(hudX, hudY), SharpDX.Color.Gray);
            hudY += lineH;
            g.DrawText($"Hazards: {_blockedPositions.Count} | lt={_lastLifeThreshold} fb={_lastFlappyBird}",
                new Vector2(hudX, hudY), SharpDX.Color.Gray);
        }

        public void Reset()
        {
            _phase = MavenPhase.Idle;
            _mavenEntity = null;
            _nucleusEntity = null;
            _mavenWasAlive = false;
            _voidsandDaemonDetected = false;
            _lastLifeThreshold = -1;
            _lastFlappyBird = false;
            _lastNucleusHp = 0;
            _nucleusMaxHp = 0;
            _blockedPositions.Clear();
            Status = "";
        }
    }
}
