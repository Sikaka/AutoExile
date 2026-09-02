using ExileCore;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using System.Numerics;
using System.Windows.Forms;

namespace AutoExile.Systems
{
    /// <summary>
    /// Flexible combat system that handles threat detection, skill execution,
    /// flask management, and positioning. Modes configure behavior via CombatProfile.
    /// </summary>
    public class CombatSystem
    {
        // ── Public state (read by modes, rendered in debug) ──

        /// <summary>Whether hostile monsters are nearby and combat is active.</summary>
        public bool InCombat { get; private set; }

        /// <summary>Number of alive hostile monsters within CombatRange.</summary>
        public int NearbyMonsterCount { get; private set; }

        /// <summary>
        /// Rarity-weighted density score for nearby monsters.
        /// Normal=1, Magic=2, Rare=5, Unique=8.
        /// Use for density-gated detour decisions: a lone rare (5) triggers
        /// a detour at threshold 5, but 2 normals (2) don't.
        /// </summary>
        public int WeightedDensity { get; private set; }

        /// <summary>
        /// Nearest dormant (alive + hostile but not yet targetable) monster position.
        /// Map bosses and some encounter enemies need proximity to become targetable.
        /// Modes can use this to navigate toward dormant threats.
        /// </summary>
        public Vector2? NearestDormantPos { get; private set; }

        /// <summary>Distance to nearest dormant monster (grid units). float.MaxValue if none.</summary>
        public float NearestDormantDistance { get; private set; } = float.MaxValue;

        /// <summary>Metadata path of the nearest dormant monster (for timeout-based ignore).</summary>
        public string? NearestDormantPath { get; private set; }

        /// <summary>Total number of alive hostile monsters in the entity list (awareness tier).</summary>
        public int CachedMonsterCount { get; private set; }

        /// <summary>Grid position of the nearest alive hostile monster (for mode navigation).</summary>
        public Vector2? NearestMonsterPos { get; private set; }

        /// <summary>Grid position of the nearest monster within CombatRange that failed LOS checks.
        /// Used for repositioning when all nearby enemies are behind terrain.</summary>
        public Vector2? NearestBlockedPos { get; private set; }

        /// <summary>Best target entity (highest priority: rarity + distance).</summary>
        public Entity? BestTarget { get; private set; }

        /// <summary>Center of mass of ALL monster pack (grid coords). Used for skill targeting.</summary>
        public Vector2 PackCenter { get; private set; }

        /// <summary>Position of the monster in the densest cluster within chase radius (grid coords). Used for positioning.</summary>
        public Vector2 DenseClusterCenter { get; private set; }

        /// <summary>Nearest corpse position for offering skills (grid coords).</summary>
        public Vector2? NearestCorpse { get; private set; }


        /// <summary>Player HP percentage (0-1) last tick.</summary>
        public float HpPercent { get; private set; }

        /// <summary>Player ES percentage (0-1) last tick.</summary>
        public float EsPercent { get; private set; }

        /// <summary>Player mana percentage (0-1) last tick.</summary>
        public float ManaPercent { get; private set; }

        /// <summary>Current combat profile set by mode.</summary>
        public CombatProfile Profile { get; private set; } = CombatProfile.Default;

        /// <summary>What the system decided to do last tick.</summary>
        public string LastAction { get; private set; } = "";

        /// <summary>
        /// When true, combat still scans threats but skips repositioning.
        /// Set by modes when another system (e.g. InteractionSystem) is navigating and
        /// combat movement would conflict.
        /// </summary>
        public bool SuppressPositioning { get; set; }

        /// <summary>
        /// When true, combat skips skills that require cursor movement (Enemy/Corpse targeted).
        /// Self-cast skills still fire. Set by modes during loot pickup to prevent cursor
        /// interference with click-based interactions.
        /// </summary>
        public bool SuppressTargetedSkills { get; set; }

        /// <summary>
        /// When true, boss is invulnerable (e.g. boss_life_bar=0 during emergence cinematic).
        /// Suppresses utility flask usage to avoid wasting charges. Skills still fire (traps pre-lay).
        /// Set by boss encounters that detect invulnerability via StateMachine.
        /// </summary>
        public bool BossInvulnerable { get; set; }

        /// <summary>Debug: last skill execution detail.</summary>
        public string LastSkillAction { get; private set; } = "";

        /// <summary>Whether the system wants the player to reposition for combat.</summary>
        public bool WantsToMove { get; private set; }

        /// <summary>Target position the system wants to move toward (world coords).</summary>
        public Vector2 MoveTarget { get; private set; }

        /// <summary>Target position in grid coords (for A* pathfinding by modes).</summary>
        public Vector2 MoveTargetGrid { get; private set; }

        // ── Movement info (exposed for NavigationSystem) ──

        /// <summary>Key for the primary movement (Move Only) binding, or null if not configured.</summary>
        public Keys? PrimaryMoveKey { get; private set; }

        /// <summary>All configured movement skills (dash/blink), sorted by priority.</summary>
        public List<MovementSkillInfo> MovementSkills { get; private set; } = new();

        /// <summary>Whether any movement skill can cross terrain gaps.</summary>
        public bool HasGapCrosser => MovementSkills.Any(m => m.CanCrossTerrain);

        // ── Global enemy blacklist (by render name) ──

        /// <summary>Enemy render names to ignore globally. Synced from settings each tick.</summary>
        public HashSet<string> BlacklistedEnemies { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        // ── Default entity path blacklist (never targetable / untargetable decorations) ──

        /// <summary>Entity path substrings to always exclude from combat targeting.
        /// These are monsters that appear hostile but are never actually attackable,
        /// or are attackable but blow up for heavy damage on death.</summary>
        private static readonly string[] DefaultPathBlacklist =
        {
            "MysticFetish",                     // King in the Mist totems — never targetable
            "VoodooKingBoss2RitualPillar",      // King encounter pillars — Unique rarity but decorative
            "Volatile",                         // All "Volatile" monsters — explode on death, never worth killing
        };

        // ── Dormant entity path ignore list ──
        // Paths learned at runtime when dormant entities never activate after proximity timeout.
        // Pre-seeded with known non-activatable entity paths (critters, volatiles, etc.).

        /// <summary>Entity path substrings to ignore for dormant tracking.
        /// Populated via timeout (modes call IgnoreDormantPath) and pre-seeded with known junk.</summary>
        private readonly HashSet<string> _ignoredDormantPaths = new(StringComparer.OrdinalIgnoreCase)
        {
            "Critters/",   // Pigeons, rats, etc. — decorative, permanently untargetable
            "Daemon/",     // Shrine daemons, effect daemons — visual-only, never targetable
            "Volatile",    // Explode on death — don't dormant-approach them either
        };

        /// <summary>Number of dormant paths learned this session (not counting pre-seeded).</summary>
        public int LearnedDormantIgnoreCount { get; private set; }

        /// <summary>Add a path substring to the dormant ignore list. Future scans skip matching entities.</summary>
        public void IgnoreDormantPath(string pathSubstring)
        {
            if (_ignoredDormantPaths.Add(pathSubstring))
                LearnedDormantIgnoreCount++;
        }

        // ── Attack connectivity tracking ──
        // Monitors whether attacks are actually connecting (target HP changing).
        // After a timeout with no damage, blacklists the entire cluster as unreachable.

        /// <summary>Unreachable monster cluster anchors (entity IDs). Excluded from threat scan.</summary>
        private readonly HashSet<long> _unreachableMonsters = new();

        /// <summary>Number of unreachable clusters blacklisted this session.</summary>
        public int UnreachableClusterCount { get; private set; }

        private long _lastAttackTargetId;
        private float _lastAttackTargetHp = -1f;
        private DateTime _attackConnectivityStart = DateTime.MinValue;
        private const float BaseAttackConnectTimeoutSec = 2.5f;

        // ── Target focus timeout — deprioritize targets we've been stuck on too long ──
        // Tracks per-entity focus time. After the rarity-based timeout, the entity gets a
        // large negative score penalty so other targets win. Cleared when entity dies or
        // on area change. Different from unreachable blacklist (which is for zero-damage).
        private readonly Dictionary<long, DateTime> _targetFocusStart = new();
        private readonly HashSet<long> _deprioritizedTargets = new();

        /// <summary>Number of targets deprioritized this session (for diagnostics).</summary>
        public int DeprioritizedCount => _deprioritizedTargets.Count;
        /// <summary>Extra seconds added to server-response timeouts. Synced from settings.</summary>
        public float ExtraLatencySec { get; set; }
        private const float UnreachableClusterRadius = 45f; // grid units — blacklist all monsters in this radius

        // ── Timing ──

        private DateTime _lastSkillUseAt = DateTime.MinValue;
        private DateTime _lastFlaskUseAt = DateTime.MinValue;
        private const int MinSkillIntervalMs = 80;
        private const int MinFlaskIntervalMs = 200;

        // ── Channel tracking ──
        /// <summary>Currently channeling skill entry, or null if not channeling.</summary>
        private SkillBarEntry? _activeChannel;

        /// <summary>True if a channeling skill is currently being held.</summary>
        public bool IsChanneling => _activeChannel != null && BotInput.IsHeld(_activeChannel.Key);

        // ── Cached skill bar data ──

        private List<SkillBarEntry> _skillBar = new();
        private SkillBarEntry? _primaryMovementEntry;
        private List<SkillBarEntry> _movementSkillEntries = new();
        private DateTime _lastSkillBarReadAt = DateTime.MinValue;

        // Preserve per-skill cast timestamps across skill bar refreshes
        private readonly Dictionary<Keys, DateTime> _lastCastByKey = new();
        private const int SkillBarRefreshMs = 500;

        // ── Flask tracking ──

        private readonly DateTime[] _lastFlaskPress = new DateTime[5];

        private static readonly Keys[] DefaultFlaskKeys = { Keys.D1, Keys.D2, Keys.D3, Keys.D4, Keys.D5 };

        /// <summary>Actual flask key bindings read from in-game settings. Falls back to defaults.</summary>
        private Keys[] FlaskKeys = { Keys.D1, Keys.D2, Keys.D3, Keys.D4, Keys.D5 };

        /// <summary>Actual skill slot key bindings read from in-game settings. Index 0-7 = bar positions.</summary>
        private readonly Keys[] _slotKeys = new Keys[8];
        private bool _keybindsLoaded;

        private DateTime _lastIntersperseAttackAt = DateTime.MinValue;
        private const int IntersperseAttackIntervalMs = 1000; // pulse attack every 1s while moving
        private DateTime _lastPeriodicRepositionAt = DateTime.MinValue;
        
        // ═══════════════════════════════════════════════════
        // Public API
        // ═══════════════════════════════════════════════════

        /// <summary>Set the combat profile. Called by modes on enter or when behavior changes.</summary>
        public void SetProfile(CombatProfile profile)
        {
            Profile = profile;
        }

        /// <summary>
        /// Main tick. Scans threats, executes skills, manages flasks, repositions.
        /// Returns true if combat actions were taken this tick.
        /// </summary>
        public bool Tick(BotContext ctx)
        {
            var gc = ctx.Game;
            var settings = ctx.Settings.Build;

            WantsToMove = false;

            if (!Profile.Enabled)
            {
                if (_activeChannel != null)
                {
                    BotInput.ReleaseKey(_activeChannel.Key);
                    _activeChannel = null;
                }
                InCombat = false;
                NearbyMonsterCount = 0;
                WeightedDensity = 0;
                BestTarget = null;
                LastAction = "disabled";
                return false;
            }

            // Read vitals
            ReadVitals(gc);

            // Scan threats (monsters in range)
            ScanThreats(gc, settings, ctx.Entities);

            // Check if active channel should be released
            ReleaseChannelIfNeeded(gc, settings);

            // Refresh skill bar periodically
            RefreshSkillBar(gc, settings);

            // Flasks
            TickFlasks(gc, settings);

            // Self-cast skills (buffs, guards, summons)
            bool usedSkill = TickSelfSkills(gc, settings);
            if (usedSkill) return true;

            // If AlwaysAttack is enabled or monsters are in combat range, execute attacks
            bool alwaysAttack = settings.AlwaysAttack.Value;
            if (!InCombat && !alwaysAttack)
            {
                if (NearestBlockedPos.HasValue && !SuppressPositioning && BotInput.CanAct)
                {
                    WantsToMove = true;
                    MoveTargetGrid = NearestBlockedPos.Value;
                    MoveTarget = ToWorld(NearestBlockedPos.Value);
                    LastAction = $"no LOS — moving toward ({NearestBlockedPos.Value.X:F0},{NearestBlockedPos.Value.Y:F0})";
                    return false;
                }
                LastAction = "no threats";
                return false;
            }

            // Execute attack/targeted skills
            usedSkill = TickSkills(gc, settings);
            if (usedSkill) return true;

            // Track attack connectivity
            TickAttackConnectivity(gc);

            // Positioning
            if (!SuppressPositioning && BotInput.CanAct)
                TickPositioning(ctx);

            return false;
        }

        /// <summary>Reset state (call on mode exit).</summary>
        public void Reset()
        {
            InCombat = false;
            NearbyMonsterCount = 0;
            WeightedDensity = 0;
            CachedMonsterCount = 0;
            NearestMonsterPos = null;
            NearestBlockedPos = null;
            BestTarget = null;
            PackCenter = Vector2.Zero;
            DenseClusterCenter = Vector2.Zero;
            NearestCorpse = null;
            WantsToMove = false;
            LastAction = "";
            LastSkillAction = "";
            _walkableMonsterWeighted.Clear();
            _allMonsterWeighted.Clear();
            BestTargetHasLOS = false;
            _skillBar.Clear();
            _primaryMovementEntry = null;
            _movementSkillEntries.Clear();
            PrimaryMoveKey = null;
            MovementSkills.Clear();
            _lastSkillBarReadAt = DateTime.MinValue;
            _lastCastByKey.Clear();
            Profile = CombatProfile.Default;
            ClearUnreachable();
        }

        // ═══════════════════════════════════════════════════
        // Vitals
        // ═══════════════════════════════════════════════════

        private void ReadVitals(GameController gc)
        {
            var life = gc.Player?.GetComponent<Life>();
            if (life != null)
            {
                HpPercent = life.HPPercentage;
                EsPercent = life.ESPercentage;
                ManaPercent = life.MPPercentage;
            }
        }

        // ═══════════════════════════════════════════════════
        // Threat scanning
        // ═══════════════════════════════════════════════════

        /// <summary>Cluster radius for density calculation — monsters within this grid distance count as neighbors.</summary>
        private const float DensityClusterRadius = 25f;

        /// <summary>Positions of all nearby monsters within chase radius (reused buffer).</summary>
        private readonly List<Vector2> _nearbyMonsterPositions = new();

        /// <summary>Only monsters reachable via straight-line walk (pf LOS), with rarity weight for density.</summary>
        private readonly List<(Vector2 pos, float weight)> _walkableMonsterWeighted = new();

        /// <summary>All alive hostile monsters with rarity weight, regardless of range or LOS. Used for Aggressive positioning.</summary>
        private readonly List<(Vector2 pos, float weight)> _allMonsterWeighted = new();

        // Spatial grids for O(1) density queries (rebuilt during ScanThreats)
        private readonly SpatialGrid<long> _allMonsterGrid = new();
        private readonly SpatialGrid<long> _walkableMonsterGrid = new();

        /// <summary>Spatial grid of all nearby monsters. Available for external queries.</summary>
        public SpatialGrid<long> MonsterGrid => _allMonsterGrid;

        /// <summary>Whether BestTarget can be hit from current position via targeting LOS.</summary>
        public bool BestTargetHasLOS { get; private set; }

        // Rate limit: full threat scan is expensive (entity iteration + LOS checks).
        // Cache results for ~50ms (3 ticks at 60fps). Positions update fast enough.
        private DateTime _lastThreatScan = DateTime.MinValue;
        private const double ThreatScanIntervalMs = 50;

        private void ScanThreats(GameController gc, BotSettings.BuildSettings settings,
            EntityCache? entityCache = null)
        {
            if ((DateTime.Now - _lastThreatScan).TotalMilliseconds < ThreatScanIntervalMs)
                return;
            _lastThreatScan = DateTime.Now;

            var playerGrid = gc.Player.GridPosNum;
            float combatRange = settings.CombatRange.Value;

            var pfGrid = gc.IngameState.Data.RawFramePathfindingData;
            var tgtGrid = gc.IngameState.Data.RawTerrainTargetingData;
            int px = (int)playerGrid.X, py = (int)playerGrid.Y;

            Entity? bestTarget = null;
            float bestScore = float.MinValue;
            int combatCount = 0;
            int weightedDensity = 0;
            int cachedCount = 0;
            var combatSum = Vector2.Zero;
            float nearestDormantDist = float.MaxValue;
            Vector2? nearestDormantPos = null;
            string? nearestDormantPath = null;
            float nearestCorpseDist = float.MaxValue;
            Vector2? nearestCorpse = null;
            float nearestMonsterDist = float.MaxValue;
            Vector2? nearestMonsterPos = null;
            float nearestBlockedDist = float.MaxValue;
            Vector2? nearestBlockedPos = null;

            _nearbyMonsterPositions.Clear();
            _walkableMonsterWeighted.Clear();
            _allMonsterWeighted.Clear();

            IEnumerable<Entity> monsters = entityCache != null
                ? entityCache.Monsters
                : gc.EntityListWrapper.OnlyValidEntities.Where(e => e.Type == EntityType.Monster);

            foreach (var entity in monsters)
            {
                if (!entity.IsHostile) continue;

                var path = entity.Path ?? "";

                // 1. Skip non-combat engine daemons and critters
                if (path.Contains("Daemon", StringComparison.OrdinalIgnoreCase) ||
                    path.Contains("Critters", StringComparison.OrdinalIgnoreCase))
                    continue;

                var dist = Vector2.Distance(entity.GridPosNum, playerGrid);

                // 2. Corpse check
                if (!entity.IsAlive)
                {
                    if (dist < combatRange && dist < nearestCorpseDist)
                    {
                        nearestCorpseDist = dist;
                        nearestCorpse = entity.GridPosNum;
                    }
                    continue;
                }

                // 3. Strict targetable check: entity must be targetable and not invulnerable
                if (!entity.IsTargetable)
                    continue;

                var targetable = entity.GetComponent<Targetable>();
                if (targetable != null && !targetable.isTargeted && !targetable.isTargetable)
                    continue;

                // 4. Verify entity has valid, positive health
                var life = entity.GetComponent<Life>();
                if (life == null || life.MaxHP <= 0 || life.CurHP <= 0)
                    continue;

                if (IsInsideMonolith(entity)) continue;
                if (BlacklistedEnemies.Count > 0 && !string.IsNullOrEmpty(entity.RenderName) &&
                    BlacklistedEnemies.Contains(entity.RenderName)) continue;
                if (_unreachableMonsters.Contains(entity.Id)) continue;

                bool isBlockedPath = false;
                foreach (var blocked in DefaultPathBlacklist)
                {
                    if (path.Contains(blocked, StringComparison.OrdinalIgnoreCase))
                    {
                        isBlockedPath = true;
                        break;
                    }
                }
                if (isBlockedPath) continue;

                cachedCount++;

                float rarityWeight = entity.Rarity switch
                {
                    MonsterRarity.Magic => 2f,
                    MonsterRarity.Rare => 10f,
                    MonsterRarity.Unique => 25f,
                    _ => 1f
                };
                if (IsPriorityTarget(entity))
                    rarityWeight = 100f;

                _allMonsterWeighted.Add((entity.GridPosNum, rarityWeight));

                if (dist > combatRange) continue;

                // 5. Line-of-Sight Check: only shoot monsters that projectiles can hit
                bool hasTargetingLOS = tgtGrid == null || Pathfinding.HasTargetingLOS(tgtGrid, px, py, (int)entity.GridPosNum.X, (int)entity.GridPosNum.Y);

                if (!hasTargetingLOS)
                {
                    if (dist < nearestBlockedDist)
                    {
                        nearestBlockedDist = dist;
                        nearestBlockedPos = entity.GridPosNum;
                    }
                    continue;
                }

                if (dist < nearestMonsterDist)
                {
                    nearestMonsterDist = dist;
                    nearestMonsterPos = entity.GridPosNum;
                }

                combatCount++;
                combatSum += entity.GridPosNum;
                _nearbyMonsterPositions.Add(entity.GridPosNum);

                weightedDensity += entity.Rarity switch
                {
                    MonsterRarity.Magic => 2,
                    MonsterRarity.Rare => 5,
                    MonsterRarity.Unique => 8,
                    _ => 1
                };

                _walkableMonsterWeighted.Add((entity.GridPosNum, rarityWeight));

                float score = rarityWeight - dist * 0.25f;

                if (_deprioritizedTargets.Contains(entity.Id))
                    score -= 200f;

                if (Profile.DefenseAnchor.HasValue)
                {
                    float distToObjective = Vector2.Distance(entity.GridPosNum, Profile.DefenseAnchor.Value);
                    score += MathF.Max(0f, 60f - distToObjective);
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    bestTarget = entity;
                }
            }

            NearbyMonsterCount = combatCount;
            WeightedDensity = weightedDensity;
            CachedMonsterCount = cachedCount;
            NearestDormantPos = nearestDormantPos;
            NearestDormantDistance = nearestDormantDist;
            NearestDormantPath = nearestDormantPath;
            BestTarget = bestTarget;
            InCombat = combatCount > 0;
            PackCenter = combatCount > 0 ? combatSum / combatCount : playerGrid;
            NearestCorpse = nearestCorpse;
            NearestMonsterPos = nearestMonsterPos;
            NearestBlockedPos = nearestBlockedPos;

            BestTargetHasLOS = bestTarget != null;

            RebuildGrids(playerGrid, combatRange);

            // Compute dense cluster using all monsters in awareness range so aggressive positioning
            // pathfinds to distant monster packs instead of being trapped by small local groups
            if (bestTarget != null && IsPriorityTarget(bestTarget))
                DenseClusterCenter = bestTarget.GridPosNum;
            else if (_allMonsterGrid.TotalItems > 0)
                DenseClusterCenter = _allMonsterGrid.FindDensestPosition(playerGrid);
            else if (_walkableMonsterGrid.TotalItems > 0)
                DenseClusterCenter = _walkableMonsterGrid.FindDensestPosition(playerGrid);
            else
                DenseClusterCenter = playerGrid;
        }

        private readonly List<(long, Vector2, float)> _gridBuildBuffer = new(256);

        private void RebuildGrids(Vector2 playerGrid, float combatRange)
        {
            // All monsters grid
            _gridBuildBuffer.Clear();
            foreach (var (pos, weight) in _allMonsterWeighted)
                _gridBuildBuffer.Add((0, pos, weight));
            _allMonsterGrid.Rebuild(_gridBuildBuffer, playerGrid, combatRange * 2f);

            // Walkable monsters grid
            _gridBuildBuffer.Clear();
            foreach (var (pos, weight) in _walkableMonsterWeighted)
                _gridBuildBuffer.Add((0, pos, weight));
            _walkableMonsterGrid.Rebuild(_gridBuildBuffer, playerGrid, combatRange);
        }

        /// <summary>
        /// Find the position in the list that has the most neighbors within DensityClusterRadius.
        /// Returns the position with highest neighbor count (density champion).
        /// Falls back to centroid for 0-2 monsters.
        /// </summary>
        private Vector2 FindDensestPosition(List<Vector2> positions, Vector2 fallback)
        {
            if (positions.Count == 0) return fallback;
            if (positions.Count <= 2)
            {
                var sum = Vector2.Zero;
                foreach (var p in positions) sum += p;
                return sum / positions.Count;
            }

            int bestNeighborCount = -1;
            Vector2 bestPos = positions[0];

            for (int i = 0; i < positions.Count; i++)
            {
                int neighborCount = 0;
                for (int j = 0; j < positions.Count; j++)
                {
                    if (i == j) continue;
                    if (Vector2.DistanceSquared(positions[i], positions[j]) <=
                        DensityClusterRadius * DensityClusterRadius)
                        neighborCount++;
                }

                if (neighborCount > bestNeighborCount)
                {
                    bestNeighborCount = neighborCount;
                    bestPos = positions[i];
                }
            }

            return bestPos;
        }

        /// <summary>
        /// Find the position with highest weighted density — rarity-weighted neighbor contribution.
        /// A unique (25) near other monsters contributes 25x a white (1) to density score.
        /// Falls back to weighted centroid for 0-2 monsters.
        /// </summary>
        private Vector2 FindWeightedDensestPosition(List<(Vector2 pos, float weight)> monsters, Vector2 fallback)
        {
            if (monsters.Count == 0) return fallback;
            if (monsters.Count <= 2)
            {
                var sum = Vector2.Zero;
                float totalWeight = 0f;
                foreach (var (pos, weight) in monsters)
                {
                    sum += pos * weight;
                    totalWeight += weight;
                }
                return totalWeight > 0f ? sum / totalWeight : fallback;
            }

            float bestScore = -1f;
            Vector2 bestPos = monsters[0].pos;

            for (int i = 0; i < monsters.Count; i++)
            {
                float score = monsters[i].weight; // self-weight included
                for (int j = 0; j < monsters.Count; j++)
                {
                    if (i == j) continue;
                    if (Vector2.DistanceSquared(monsters[i].pos, monsters[j].pos) <=
                        DensityClusterRadius * DensityClusterRadius)
                        score += monsters[j].weight;
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    bestPos = monsters[i].pos;
                }
            }

            return bestPos;
        }

        // ═══════════════════════════════════════════════════
        // Skill bar reading
        // ═══════════════════════════════════════════════════

        /// <summary>
        /// Refresh skill bar data from user Build Settings and in-game SkillBarIds.
        /// Resolves PrimaryMovement (Move Only) key to match the actual bound key (e.g. Keys.Q).
        /// </summary>
        public void RefreshSkillBar(GameController gc, BotSettings.BuildSettings settings)
        {
            // Update movement skill readiness every tick
            foreach (var ms in MovementSkills)
                ms.IsReady = ms.ActorSkill?.CanBeUsed ?? true;

            if ((DateTime.Now - _lastSkillBarReadAt).TotalMilliseconds < SkillBarRefreshMs && (_skillBar.Count > 0 || _primaryMovementEntry != null))
                return;

            _lastSkillBarReadAt = DateTime.Now;

            // Save cast timestamps before clearing
            foreach (var entry in _skillBar)
                _lastCastByKey[entry.Key] = entry.LastCastAt;

            _skillBar.Clear();
            _primaryMovementEntry = null;
            _movementSkillEntries.Clear();

            Keys detectedMoveKey = Keys.None;

            // 1. Scan in-game SkillBarIds to find which key has the "move" skill (e.g. Keys.Q)
            var actor = gc.Player?.GetComponent<Actor>();
            if (actor?.ActorSkills != null)
            {
                var barIds = gc.IngameState?.ServerData?.SkillBarIds;
                if (barIds != null)
                {
                    for (int bi = 0; bi < 8 && bi < barIds.Count; bi++)
                    {
                        var barKey = KeyForSlot(bi);
                        if (barKey == Keys.None) continue;
                        if (barIds[bi] != 0)
                        {
                            var skill = actor.ActorSkills.FirstOrDefault(s => s.Id == barIds[bi]);
                            if (skill != null)
                            {
                                var internalName = skill.InternalName ?? "";
                                var name = skill.Name ?? "";
                                if (internalName.Equals("move", StringComparison.OrdinalIgnoreCase) ||
                                    name.Equals("Move", StringComparison.OrdinalIgnoreCase) ||
                                    name.Equals("Move Only", StringComparison.OrdinalIgnoreCase))
                                {
                                    detectedMoveKey = barKey;
                                    break;
                                }
                            }
                        }
                    }
                }
            }

            // 2. Scan user-configured slots in Build Settings for explicitly assigned PrimaryMovement role
            Keys configMoveKey = Keys.None;
            foreach (var slotConfig in settings.AllSkillSlots)
            {
                var key = slotConfig.Key.Value;
                if (key == Keys.None) continue;

                var roleStr = (slotConfig.Role.Value ?? "").Trim();
                if (string.Equals(roleStr, "Move", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(roleStr, "Move Only", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(roleStr, "PrimaryMovement", StringComparison.OrdinalIgnoreCase))
                {
                    configMoveKey = key;
                    break;
                }
            }

            // Assign PrimaryMoveKey: prefer explicitly configured non-default key or detected in-game key
            if (configMoveKey != Keys.None)
                PrimaryMoveKey = configMoveKey;
            else if (detectedMoveKey != Keys.None)
                PrimaryMoveKey = detectedMoveKey;

            // 3. Iterate user-configured slots for attack/buff/movement skills
            foreach (var slotConfig in settings.AllSkillSlots)
            {
                var key = slotConfig.Key.Value;
                if (key == Keys.None) continue;

                var roleStr = (slotConfig.Role.Value ?? "").Trim();

                SkillRole role;
                if (string.Equals(roleStr, "Attack", StringComparison.OrdinalIgnoreCase) || 
                    string.Equals(roleStr, "Enemy", StringComparison.OrdinalIgnoreCase))
                {
                    role = SkillRole.Enemy;
                }
                else if (string.Equals(roleStr, "Buff", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(roleStr, "Guard", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(roleStr, "Vaal", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(roleStr, "Summon", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(roleStr, "Self", StringComparison.OrdinalIgnoreCase))
                {
                    role = SkillRole.Self;
                }
                else if (string.Equals(roleStr, "Blink", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(roleStr, "MovementSkill", StringComparison.OrdinalIgnoreCase))
                {
                    role = SkillRole.MovementSkill;
                }
                else if (string.Equals(roleStr, "Move", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(roleStr, "Move Only", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(roleStr, "PrimaryMovement", StringComparison.OrdinalIgnoreCase))
                {
                    role = SkillRole.PrimaryMovement;
                }
                else if (!Enum.TryParse<SkillRole>(roleStr, true, out role))
                {
                    role = SkillRole.Disabled;
                }

                if (role == SkillRole.Disabled) continue;

                if (role == SkillRole.PrimaryMovement)
                {
                    _primaryMovementEntry = new SkillBarEntry
                    {
                        Skill = null,
                        Key = key,
                        Role = role,
                        Priority = 0,
                    };
                    continue;
                }

                ActorSkill? matchedSkill = null;
                if (actor?.ActorSkills != null)
                {
                    var barIds = gc.IngameState?.ServerData?.SkillBarIds;
                    if (barIds != null)
                    {
                        for (int bi = 0; bi < 8 && bi < barIds.Count; bi++)
                        {
                            if (KeyForSlot(bi) == key && barIds[bi] != 0)
                            {
                                matchedSkill = actor.ActorSkills.FirstOrDefault(s => s.Id == barIds[bi]);
                                if (matchedSkill != null) break;
                            }
                        }
                    }

                    if (matchedSkill == null)
                    {
                        matchedSkill = actor.ActorSkills.FirstOrDefault(s => KeyForSlot(s.SkillSlotIndex) == key);
                    }
                }

                Enum.TryParse<SkillTargetFilter>(slotConfig.TargetFilter.Value, true, out var targetFilter);

                var entry = new SkillBarEntry
                {
                    Skill = matchedSkill,
                    Key = key,
                    Role = role,
                    Priority = slotConfig.Priority.Value > 0 ? slotConfig.Priority.Value : 5,
                    CanCrossTerrain = slotConfig.CanCrossTerrain.Value,
                    TargetFilter = targetFilter,
                    MinNearbyEnemies = slotConfig.MinNearbyEnemies.Value,
                    MaxTargetRange = slotConfig.MaxTargetRange.Value,
                    OnlyWhenBuffMissing = slotConfig.OnlyWhenBuffMissing.Value,
                    OnlyOnLowLife = slotConfig.OnlyOnLowLife.Value,
                    SummonRecast = slotConfig.SummonRecast.Value,
                    BuffDebuffName = slotConfig.BuffDebuffName.Value ?? "",
                    MinCastIntervalMs = slotConfig.MinCastIntervalMs.Value,
                    RequireTargetable = slotConfig.RequireTargetable.Value,
                    IsChannel = slotConfig.IsChannel.Value,
                };

                if (matchedSkill != null)
                {
                    try
                    {
                        var stats = matchedSkill.Stats;
                        if (stats != null && stats.TryGetValue(GameStat.ActiveSkillBaseRadius, out var radius))
                            entry.AoeRadius = radius;
                    }
                    catch { }
                }

                if (_lastCastByKey.TryGetValue(key, out var prevCastAt))
                    entry.LastCastAt = prevCastAt;

                if (role == SkillRole.MovementSkill)
                {
                    _movementSkillEntries.Add(entry);
                }
                else
                {
                    _skillBar.Add(entry);
                }
            }

            _skillBar.Sort((a, b) => b.Priority.CompareTo(a.Priority));

            var prevUsedTimes = new Dictionary<Keys, DateTime>();
            foreach (var ms in MovementSkills)
                prevUsedTimes[ms.Key] = ms.LastUsedAt;

            MovementSkills.Clear();
            foreach (var entry in _movementSkillEntries)
            {
                prevUsedTimes.TryGetValue(entry.Key, out var lastUsed);
                MovementSkills.Add(new MovementSkillInfo
                {
                    Key = entry.Key,
                    CanCrossTerrain = entry.CanCrossTerrain,
                    IsReady = entry.Skill?.CanBeUsed ?? true,
                    ActorSkill = entry.Skill,
                    MinCastIntervalMs = entry.MinCastIntervalMs,
                    LastUsedAt = lastUsed,
                });
            }

            _movementSkillEntries.Sort((a, b) =>
            {
                int crossCompare = b.CanCrossTerrain.CompareTo(a.CanCrossTerrain);
                return crossCompare != 0 ? crossCompare : b.Priority.CompareTo(a.Priority);
            });
        }

        /// <summary>
        /// Default key for a POE skill slot index (used to match ActorSkills to user key configs).
        /// Returns Keys.None for unknown/unmapped slots.
        /// </summary>
        /// <summary>
        /// Default key for a POE skill bar position.
        /// Bar positions come from ServerData.SkillBarIds array indices:
        /// 0=LMB, 1=RMB, 2=MMB, 3=Q, 4=W, 5=E, 6=R, 7=T
        /// Note: ActorSkill.SkillSlotIndex uses a DIFFERENT numbering
        /// (even indices for keyboard slots). This method maps bar positions.
        /// </summary>
        public static Keys DefaultKeyForSlot(int barPosition) => barPosition switch
        {
            0 => Keys.None,        // LMB — move only
            1 => Keys.RButton,     // RMB
            2 => Keys.MButton,     // Middle mouse
            3 => Keys.Q,
            4 => Keys.W,
            5 => Keys.E,
            6 => Keys.R,
            7 => Keys.T,
            _ => Keys.None
        };

        /// <summary>
        /// Get the actual key for a skill bar position, using in-game keybindings if loaded.
        /// </summary>
        public Keys KeyForSlot(int barPosition)
        {
            if (!_keybindsLoaded || barPosition < 0 || barPosition >= _slotKeys.Length)
                return DefaultKeyForSlot(barPosition);
            var key = _slotKeys[barPosition];
            return key != Keys.None ? key : DefaultKeyForSlot(barPosition);
        }

        /// <summary>
        /// Read actual keybindings from IngameState.ShortcutSettings.Shortcuts.
        /// Call on startup and area change.
        /// </summary>
        public void RefreshKeybindings(GameController gc)
        {
            try
            {
                var shortcuts = gc.IngameState?.ShortcutSettings?.Shortcuts;
                if (shortcuts == null || shortcuts.Count < 15) return;

                // Skill slots: Shortcuts indices 7-14 map to Skill1-Skill8
                // Bar positions 0-7 correspond to Skill1-Skill8
                // Shortcuts index = bar position + 7
                for (int bar = 0; bar < 8; bar++)
                {
                    var shortcut = shortcuts[bar + 7];
                    var consoleKey = shortcut.MainKey;
                    _slotKeys[bar] = ConsoleKeyToKeys(consoleKey, bar);
                }

                // Flask keys: Shortcuts indices 0-4 = Flask1-Flask5
                for (int f = 0; f < 5; f++)
                {
                    var shortcut = shortcuts[f];
                    var mapped = ConsoleKeyToKeys(shortcut.MainKey, -1);
                    if (mapped != Keys.None)
                        FlaskKeys[f] = mapped;
                }

                _keybindsLoaded = true;
            }
            catch
            {
                // Fall back to defaults if anything fails
            }
        }

        /// <summary>
        /// Convert System.ConsoleKey to System.Windows.Forms.Keys.
        /// For mouse buttons (bar positions 0-2), use special mapping since
        /// ConsoleKey uses raw values 1/2/4 for LMB/RMB/MMB.
        /// </summary>
        private static Keys ConsoleKeyToKeys(ConsoleKey consoleKey, int barPosition)
        {
            // Mouse button slots (bar positions 0-2)
            if (barPosition >= 0 && barPosition <= 2)
            {
                return (int)consoleKey switch
                {
                    1 => Keys.None,      // LMB — move only, don't bind
                    2 => Keys.RButton,   // RMB
                    4 => Keys.MButton,   // MMB
                    _ => Keys.None
                };
            }

            // ConsoleKey and Keys share the same underlying values for most keys
            var intVal = (int)consoleKey;

            // Quick validation — ConsoleKey values map to Keys for common ranges
            if (intVal >= 48 && intVal <= 57) return (Keys)intVal;   // D0-D9
            if (intVal >= 65 && intVal <= 90) return (Keys)intVal;   // A-Z
            if (intVal >= 112 && intVal <= 123) return (Keys)intVal; // F1-F12

            // Named keys with matching values
            return consoleKey switch
            {
                ConsoleKey.Spacebar => Keys.Space,
                ConsoleKey.Tab => Keys.Tab,
                ConsoleKey.Enter => Keys.Enter,
                ConsoleKey.Escape => Keys.Escape,
                ConsoleKey.Insert => Keys.Insert,
                ConsoleKey.Delete => Keys.Delete,
                ConsoleKey.Home => Keys.Home,
                ConsoleKey.End => Keys.End,
                ConsoleKey.PageUp => Keys.PageUp,
                ConsoleKey.PageDown => Keys.PageDown,
                ConsoleKey.UpArrow => Keys.Up,
                ConsoleKey.DownArrow => Keys.Down,
                ConsoleKey.LeftArrow => Keys.Left,
                ConsoleKey.RightArrow => Keys.Right,
                ConsoleKey.Backspace => Keys.Back,
                ConsoleKey.OemComma => Keys.Oemcomma,
                ConsoleKey.OemPeriod => Keys.OemPeriod,
                ConsoleKey.OemMinus => Keys.OemMinus,
                ConsoleKey.OemPlus => Keys.Oemplus,
                _ => (Keys)intVal // Direct cast as fallback — values match for most keys
            };
        }

        // ═══════════════════════════════════════════════════
        // Skill execution
        // ═══════════════════════════════════════════════════

        /// <summary>
        /// Fire Self-role skills (buffs, guards, summons) independently of InCombat.
        /// These don't need a target or cursor movement — just a key press.
        /// </summary>
        private bool TickSelfSkills(GameController gc, BotSettings.BuildSettings settings)
        {
            if (!BotInput.CanAct) return false;
            if ((DateTime.Now - _lastSkillUseAt).TotalMilliseconds < MinSkillIntervalMs) return false;

            foreach (var entry in _skillBar)
            {
                if (entry.Role != SkillRole.Self) continue;
                if (entry.Skill != null && !entry.Skill.CanBeUsed) continue;
                if (!CheckSkillConditions(gc, entry, settings)) continue;

                UseSkill(gc, entry, null);
                return true;
            }
            return false;
        }

        internal bool TickSkills(GameController gc, BotSettings.BuildSettings settings)
        {
            if (_activeChannel != null && BotInput.IsHeld(_activeChannel.Key))
            {
                var targetGridPos = GetSkillTargetGrid(gc, _activeChannel);
                if (targetGridPos.HasValue)
                {
                    UseSkill(gc, _activeChannel, targetGridPos);
                }
                else if (settings.AlwaysAttack.Value && InCombat)
                {
                    var forward = gc.Player != null ? gc.Player.GridPosNum + Vector2.UnitY * 20f : Vector2.Zero;
                    UseSkill(gc, _activeChannel, forward);
                }
            }

            if (!BotInput.CanAct)
            {
                LastSkillAction = "waiting for input gate (CanAct=false)";
                return false;
            }

            if ((DateTime.Now - _lastSkillUseAt).TotalMilliseconds < MinSkillIntervalMs)
            {
                LastSkillAction = "min skill interval cooldown";
                return false;
            }

            if (_skillBar.Count == 0)
            {
                LastSkillAction = "skill bar empty (no skills parsed)";
                return false;
            }

            foreach (var entry in _skillBar)
            {
                if (entry.Role == SkillRole.Self) continue;

                if (SuppressTargetedSkills && (entry.Role == SkillRole.Enemy || entry.Role == SkillRole.Corpse))
                {
                    LastSkillAction = "targeted skills suppressed (interaction busy)";
                    continue;
                }

                // Attack target selection
                if (entry.Role == SkillRole.Enemy)
                {
                    Vector2? targetPos = null;
                    bool isDistantIntersperse = false;

                    if (BestTarget != null && BestTarget.IsAlive)
                    {
                        targetPos = BestTarget.GridPosNum;
                    }
                    else if (NearestMonsterPos.HasValue)
                    {
                        targetPos = NearestMonsterPos.Value;
                    }
                    else if (settings.AlwaysAttack.Value && gc.Player != null)
                    {
                        // AlwaysAttack enabled with no monsters in combat range:
                        // Pulse attack periodically (every 1000ms) while traveling so leech stays active
                        var elapsedIntersperse = (DateTime.Now - _lastIntersperseAttackAt).TotalMilliseconds;
                        if (elapsedIntersperse >= IntersperseAttackIntervalMs)
                        {
                            targetPos = gc.Player.GridPosNum + Vector2.UnitY * 20f;
                            isDistantIntersperse = true;
                        }
                    }

                    if (!targetPos.HasValue)
                    {
                        LastSkillAction = "no enemy target in range";
                        continue;
                    }

                    if (isDistantIntersperse)
                        _lastIntersperseAttackAt = DateTime.Now;

                    UseSkill(gc, entry, targetPos.Value);
                    return true;
                }

                if (entry.Role == SkillRole.Corpse && !NearestCorpse.HasValue)
                {
                    LastSkillAction = "no corpse nearby";
                    continue;
                }

                if (!CheckSkillConditions(gc, entry, settings))
                {
                    LastSkillAction = $"conditions not met for {entry.Key}";
                    continue;
                }

                var targetGridPos = GetSkillTargetGrid(gc, entry);
                if (!targetGridPos.HasValue)
                {
                    LastSkillAction = "no valid target position";
                    continue;
                }

                UseSkill(gc, entry, targetGridPos);
                return true;
            }

            LastSkillAction = "no skill ready";
            return false;
        }

        /// <summary>
        /// Check per-skill user-configured conditions. Returns false to skip the skill.
        /// </summary>
        private bool CheckSkillConditions(GameController gc, SkillBarEntry entry, BotSettings.BuildSettings settings)
        {
            // Per-skill cast interval — prevents debuffs from overriding primary attacks
            if (entry.MinCastIntervalMs > 0 &&
                (DateTime.Now - entry.LastCastAt).TotalMilliseconds < entry.MinCastIntervalMs)
                return false;

            // Require targetable — skip if target is invulnerable/untargetable
            if (entry.RequireTargetable && BestTarget != null && !BestTarget.IsTargetable)
                return false;

            // Target filter — restrict to certain rarities
            if (entry.TargetFilter != SkillTargetFilter.Any && BestTarget != null)
            {
                var rarity = BestTarget.Rarity;
                if (entry.TargetFilter == SkillTargetFilter.RareOrAbove &&
                    rarity != MonsterRarity.Rare && rarity != MonsterRarity.Unique)
                    return false;
                if (entry.TargetFilter == SkillTargetFilter.UniqueOnly &&
                    rarity != MonsterRarity.Unique)
                    return false;
            }

            // Min nearby enemies
            if (entry.MinNearbyEnemies > 0 && NearbyMonsterCount < entry.MinNearbyEnemies)
                return false;

            // Buff/debuff presence check
            if (entry.OnlyWhenBuffMissing)
            {
                // For Self-targeted: check player buffs
                // For Enemy-targeted: check target's debuffs
                if (entry.Role == SkillRole.Enemy && BestTarget != null)
                {
                    if (HasDebuffOnTarget(BestTarget, entry.BuffDebuffName.Length > 0 ? entry.BuffDebuffName : entry.Skill?.InternalName ?? ""))
                        return false;
                }
                else
                {
                    if (HasBuff(gc, entry.Skill, entry.BuffDebuffName))
                        return false;
                }
            }

            // Only on low life
            if (entry.OnlyOnLowLife && HpPercent >= settings.GuardHpThreshold.Value)
                return false;

            // Close enemies / max target range
            if (entry.MaxTargetRange > 0 && BestTarget != null)
            {
                var playerGrid = gc.Player.GridPosNum;
                var dist = Vector2.Distance(playerGrid, BestTarget.GridPosNum);
                if (dist > entry.MaxTargetRange)
                    return false;
            }

            // Summon recast — skip if enough minions deployed nearby
            if (entry.SummonRecast && entry.Skill != null)
            {
                if (!ShouldResummon(entry.Skill, entry.Role, settings))
                    return false;
            }

            return true;
        }

        private Vector2? GetSkillTargetGrid(GameController gc, SkillBarEntry entry)
        {
            return entry.Role switch
            {
                SkillRole.Enemy => BestTarget?.GridPosNum,
                SkillRole.Corpse => NearestCorpse,
                SkillRole.Self => null,
                _ => null
            };
        }

        private void UseSkill(GameController gc, SkillBarEntry entry, Vector2? gridTarget)
        {
            bool acted;

            if (entry.IsChannel)
            {
                // ── Channel skill — hold key down continuously ──
                // The key stays held while in combat. Cursor position is updated each
                // tick by normal combat targeting — no gate reservation needed.
                // This lets the game cast as fast as possible and allows dodge/blink
                // skills to interrupt naturally (they call ReleaseAllKeys).
                if (_activeChannel == entry && BotInput.IsHeld(entry.Key))
                {
                    // Already channeling — just update cursor toward target
                    if (gridTarget.HasValue)
                    {
                        var screenPos = Pathfinding.GridToScreen(gc, gridTarget.Value);
                        var windowRect = gc.Window.GetWindowRectangle();
                        if (screenPos.X >= 0 && screenPos.X <= windowRect.Width &&
                            screenPos.Y >= 0 && screenPos.Y <= windowRect.Height)
                        {
                            var absPos = new Vector2(windowRect.X + screenPos.X, windowRect.Y + screenPos.Y);
                            Input.SetCursorPos(absPos);
                        }
                    }
                    return;
                }

                // Start new channel — lightweight key-down, no gate reservation.
                // Combat cursor positioning handles where the cursor points each tick.
                BotInput.SuspendMovement();
                if (!BotInput.IsHeld(entry.Key))
                {
                    Input.KeyDown(entry.Key);
                    BotInput.TrackHeldKey(entry.Key);
                }
                _activeChannel = entry;
                acted = true;

                // Move cursor to target for the initial key-down frame
                if (gridTarget.HasValue)
                {
                    var screenPos = Pathfinding.GridToScreen(gc, gridTarget.Value);
                    var windowRect = gc.Window.GetWindowRectangle();
                    if (screenPos.X >= 0 && screenPos.X <= windowRect.Width &&
                        screenPos.Y >= 0 && screenPos.Y <= windowRect.Height)
                    {
                        var absPos = new Vector2(windowRect.X + screenPos.X, windowRect.Y + screenPos.Y);
                        Input.SetCursorPos(absPos);
                    }
                }
            }
            else
            {
                // ── Normal skill — press and release ──
                if (gridTarget.HasValue)
                {
                    // Targeted skill — needs cursor, suspends movement
                    var screenPos = Pathfinding.GridToScreen(gc, gridTarget.Value);
                    var windowRect = gc.Window.GetWindowRectangle();
                    if (screenPos.X < 0 || screenPos.X > windowRect.Width ||
                        screenPos.Y < 0 || screenPos.Y > windowRect.Height)
                    {
                        LastSkillAction = $"{entry.Skill?.Name ?? entry.Key.ToString()}: target off-screen";
                        return;
                    }
                    var absPos = new Vector2(windowRect.X + screenPos.X, windowRect.Y + screenPos.Y);
                    acted = BotInput.CursorPressKey(absPos, entry.Key);
                }
                else
                {
                    // Self-cast skill — fires alongside movement without interrupting it
                    acted = BotInput.PressKeyOverlay(entry.Key);
                }

                if (!acted) return;
            }

            _lastSkillUseAt = DateTime.Now;
            entry.LastCastAt = DateTime.Now;
            var skillName = entry.Skill?.Name ?? entry.Key.ToString();
            var channelTag = entry.IsChannel ? " [HOLD]" : "";
            LastSkillAction = $"{skillName} ({entry.Key}, {entry.Role}){channelTag}";
            LastAction = $"skill: {skillName}{channelTag}";
        }

        /// <summary>
        /// Release the active channeling skill if conditions are no longer met.
        /// Called at the start of each combat tick.
        /// </summary>
        private void ReleaseChannelIfNeeded(GameController gc, BotSettings.BuildSettings settings)
        {
            if (_activeChannel == null) return;

            // Channel key was released externally (by BotInput.ReleaseAllKeys, another action, etc.)
            if (!BotInput.IsHeld(_activeChannel.Key))
            {
                _activeChannel = null;
                return;
            }

            bool shouldRelease = false;

            // No more targets in range
            if (_activeChannel.Role == SkillRole.Enemy && BestTarget == null && !InCombat)
                shouldRelease = true;

            // Conditions no longer met (target died, moved out of range, etc.)
            if (!shouldRelease && !CheckSkillConditions(gc, _activeChannel, settings))
                shouldRelease = true;

            // Combat suppressed
            if (SuppressTargetedSkills &&
                (_activeChannel.Role == SkillRole.Enemy || _activeChannel.Role == SkillRole.Corpse))
                shouldRelease = true;

            if (shouldRelease)
            {
                BotInput.ReleaseKey(_activeChannel.Key);
                var name = _activeChannel.Skill?.Name ?? _activeChannel.Key.ToString();
                LastAction = $"released channel: {name}";
                _activeChannel = null;
            }
        }

        // ═══════════════════════════════════════════════════
        // Buff / debuff checking
        // ═══════════════════════════════════════════════════

        /// <summary>
        /// Check if the player has a buff matching the skill.
        /// Uses configured BuffDebuffName if set, otherwise falls back to skill internal name.
        /// </summary>
        private bool HasBuff(GameController gc, ActorSkill? skill, string configuredName = "")
        {
            var searchName = !string.IsNullOrEmpty(configuredName) ? configuredName : skill?.InternalName;
            if (string.IsNullOrEmpty(searchName)) return false;

            return EntityHasBuff(gc.Player, searchName);
        }

        /// <summary>
        /// Check if a target entity has a debuff matching the search name.
        /// Returns true if any buff on the entity contains the search string.
        /// </summary>
        private bool HasDebuffOnTarget(Entity? target, string searchName)
        {
            if (target == null || string.IsNullOrEmpty(searchName)) return false;
            return EntityHasBuff(target, searchName);
        }

        /// <summary>
        /// Check if any buff on the entity matches the search name (substring, case-insensitive).
        /// </summary>
        private static bool EntityHasBuff(Entity entity, string searchName)
        {
            try
            {
                var buffs = entity.Buffs;
                if (buffs == null) return false;

                foreach (var buff in buffs)
                {
                    if (buff.Name == null) continue;
                    if (buff.Name.Contains(searchName, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            catch { }

            return false;
        }

        // ═══════════════════════════════════════════════════
        // Summon checking
        // ═══════════════════════════════════════════════════

        private bool ShouldResummon(ActorSkill skill, SkillRole role, BotSettings.BuildSettings settings)
        {
            try
            {
                var deployed = skill.DeployedObjects;
                if (deployed == null) return true;

                var expected = settings.SummonExpectedCount.Value;

                // Below count — always resummon
                if (deployed.Count < expected)
                    return true;

                // At cap — for targeted summons (totems/ballistae), check if ALL
                // are near the combat target. If any are out of range, recast to
                // place fresh totems on top of the current target.
                if (role == SkillRole.Enemy)
                {
                    var referencePos = BestTarget?.GridPosNum ?? PackCenter;
                    float effectiveRange = settings.CombatRange.Value;
                    foreach (var d in deployed)
                    {
                        try
                        {
                            if (d.Entity != null &&
                                Vector2.Distance(d.Entity.GridPosNum, referencePos) > effectiveRange)
                                return true; // at least one totem is out of range — recast
                        }
                        catch { }
                    }
                    return false; // all totems are in range
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        // ═══════════════════════════════════════════════════
        // Flask management
        // ═══════════════════════════════════════════════════

        internal void TickFlasks(GameController gc, BotSettings.BuildSettings settings)
        {
            if (!settings.FlasksEnabled.Value) return;
            if (!BotInput.CanAct) return;
            if ((DateTime.Now - _lastFlaskUseAt).TotalMilliseconds < MinFlaskIntervalMs) return;

            // Life flask
            if (HpPercent < settings.LifeFlaskHpThreshold.Value && settings.LifeFlaskSlot.Value > 0)
            {
                TryFlask(settings.LifeFlaskSlot.Value - 1);
                return;
            }

            // Mana flask
            if (ManaPercent < settings.ManaFlaskManaThreshold.Value && settings.ManaFlaskSlot.Value > 0)
            {
                TryFlask(settings.ManaFlaskSlot.Value - 1);
                return;
            }

            // Utility flasks (use in combat when nearby enemies are targetable and damageable)
            if (InCombat && BestTarget?.IsTargetable == true && !BossInvulnerable)
            {
                for (int i = 0; i < 5; i++)
                {
                    if (i == (settings.LifeFlaskSlot.Value - 1) || i == (settings.ManaFlaskSlot.Value - 1))
                        continue;

                    var elapsed = (DateTime.Now - _lastFlaskPress[i]).TotalMilliseconds;
                    if (elapsed > settings.UtilityFlaskIntervalMs.Value)
                    {
                        TryFlask(i);
                        return;
                    }
                }
            }
        }

        private void TryFlask(int index)
        {
            if (index < 0 || index >= 5) return;
            // Flasks fire alongside movement — never interrupt continuous movement
            if (!BotInput.PressKeyOverlay(FlaskKeys[index])) return;

            _lastFlaskPress[index] = DateTime.Now;
            _lastFlaskUseAt = DateTime.Now;
        }

        // ═══════════════════════════════════════════════════
        // Positioning — uses cursor + move key, never clicks
        // ═══════════════════════════════════════════════════

        private void TickPositioning(BotContext ctx)
        {
            var gc = ctx.Game;
            var settings = ctx.Settings.Build;

            if (CachedMonsterCount == 0 && NearbyMonsterCount == 0) return;

            var playerGrid = gc.Player.GridPosNum;
            float dist = Vector2.Distance(playerGrid, DenseClusterCenter);
            float fightRange = settings.FightRange.Value;

            // ── Periodic micro-repositioning for ground effect / corpse explosion avoidance ──
            bool periodicTriggered = false;
            if (settings.EnablePeriodicReposition.Value && InCombat)
            {
                var elapsedRepose = (DateTime.Now - _lastPeriodicRepositionAt).TotalMilliseconds;
                if (elapsedRepose >= settings.PeriodicRepositionIntervalMs.Value)
                {
                    periodicTriggered = true;
                    _lastPeriodicRepositionAt = DateTime.Now;
                }
            }

            Vector2? desiredGridPos = null;

            if (periodicTriggered)
            {
                // Select a step-aside position perpendicular to the cluster center
                var toCluster = DenseClusterCenter - playerGrid;
                if (toCluster.Length() < 1f) toCluster = Vector2.UnitY;
                var perp = new Vector2(-toCluster.Y, toCluster.X);
                perp = SafeNormalize(perp);

                // Alternate left/right side steps
                if ((DateTime.Now.Ticks & 1) == 0) perp = -perp;

                desiredGridPos = playerGrid + perp * settings.PeriodicRepositionDistance.Value;
                LastAction = $"periodic reposition: stepping out of ground effects/corpses";
            }
            else
            {
                switch (Profile.Positioning)
                {
                    case CombatPositioning.Aggressive:
                        if (dist > 15f)
                        {
                            WantsToMove = true;
                            MoveTargetGrid = DenseClusterCenter;
                            MoveTarget = ToWorld(DenseClusterCenter);
                            LastAction = $"aggressive: pathfind to density @ ({DenseClusterCenter.X:F0},{DenseClusterCenter.Y:F0}) dist={dist:F0}";
                            return;
                        }
                        if (dist > 3f)
                            desiredGridPos = DenseClusterCenter;
                        break;

                    case CombatPositioning.Melee:
                        if (dist > fightRange)
                            desiredGridPos = DenseClusterCenter;
                        break;

                    case CombatPositioning.Ranged:
                        if (dist > fightRange * 1.5f)
                        {
                            desiredGridPos = DenseClusterCenter;
                        }
                        else if (dist > fightRange * 0.5f)
                        {
                            var toPack = DenseClusterCenter - playerGrid;
                            var perpendicular = new Vector2(-toPack.Y, toPack.X);
                            perpendicular = SafeNormalize(perpendicular);
                            desiredGridPos = playerGrid + perpendicular * fightRange * 0.5f;
                        }
                        break;
                }
            }

            if (!desiredGridPos.HasValue) return;

            if (Profile.LeashAnchor.HasValue)
            {
                var anchor = Profile.LeashAnchor.Value;
                var radius = Profile.LeashRadius;
                var distFromAnchor = Vector2.Distance(desiredGridPos.Value, anchor);
                if (distFromAnchor > radius)
                {
                    var toDesired = desiredGridPos.Value - anchor;
                    toDesired = SafeNormalize(toDesired);
                    desiredGridPos = anchor + toDesired * radius;
                }
            }

            var pfGrid2 = gc.IngameState.Data.RawFramePathfindingData;
            bool canWalkToDesired = pfGrid2 != null &&
                Pathfinding.HasLineOfSight(pfGrid2, playerGrid, desiredGridPos.Value);

            Vector2? validPos;
            if (canWalkToDesired)
                validPos = ctx.Navigation.FindWalkableWithLOS(gc, desiredGridPos.Value, DenseClusterCenter);
            else
                validPos = ctx.Navigation.FindWalkableWithLOS(gc, playerGrid, DenseClusterCenter, 20);

            if (!validPos.HasValue) return;

            if (pfGrid2 != null && !Pathfinding.HasLineOfSight(pfGrid2, playerGrid, validPos.Value))
                return;

            WantsToMove = true;
            MoveTargetGrid = validPos.Value;
            MoveTarget = ToWorld(validPos.Value);
            ExecuteMove(gc, validPos.Value);
        }

        /// <summary>
        /// Move toward a grid position using cursor + move key or dash skill for aggressive repositioning.
        /// Only dashes when straight-line walkable LOS is clear; uses Move Only on narrow bridges/stairs.
        /// </summary>
        private void ExecuteMove(GameController gc, Vector2 gridTarget)
        {
            var screenPos = Pathfinding.GridToScreen(gc, gridTarget);
            var windowRect = gc.Window.GetWindowRectangle();
            var center = new Vector2(windowRect.Width / 2f, windowRect.Height / 2f);

            Vector2 absPos;
            if (screenPos.X > 0 && screenPos.X < windowRect.Width &&
                screenPos.Y > 0 && screenPos.Y < windowRect.Height)
            {
                var dir = screenPos - center;
                if (dir.Length() < NavigationSystem.MinScreenDist)
                {
                    if (dir.Length() < 1f) return;
                    screenPos = center + Vector2.Normalize(dir) * NavigationSystem.MinScreenDist;
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

            var playerGrid = gc.Player.GridPosNum;
            var pfGrid = gc.IngameState.Data.RawFramePathfindingData;

            // Only dash if we have verified unobstructed walkable LOS — never dash into railings or narrow bridge corners
            bool hasClearWalkableLOS = pfGrid != null && Pathfinding.HasLineOfSight(pfGrid, playerGrid, gridTarget);

            if (hasClearWalkableLOS && Vector2.Distance(playerGrid, gridTarget) > 18f)
            {
                var readyDash = MovementSkills.FirstOrDefault(m => m.IsReady &&
                    (m.MinCastIntervalMs <= 0 || (DateTime.Now - m.LastUsedAt).TotalMilliseconds >= m.MinCastIntervalMs));

                if (readyDash != null && BotInput.CanAct)
                {
                    if (BotInput.CursorPressKey(absPos, readyDash.Key))
                    {
                        readyDash.LastUsedAt = DateTime.Now;
                        return;
                    }
                }
            }

            var moveKey = PrimaryMoveKey ?? Keys.T;
            if (BotInput.IsMovementActive && !BotInput.IsMovementSuspended)
                BotInput.UpdateMovementCursor(absPos);
            else
                BotInput.StartMovement(absPos, moveKey);
        }

        // ═══════════════════════════════════════════════════
        // Attack connectivity tracking
        // ═══════════════════════════════════════════════════

        /// <summary>
        /// Track whether attacks are connecting by monitoring BestTarget's HP.
        /// If HP doesn't change for BaseAttackConnectTimeoutSec + ExtraLatencySec, blacklist the entire
        /// cluster of monsters near the target as unreachable.
        /// </summary>
        private void TickAttackConnectivity(GameController gc)
        {
            // Clean up focus tracking for dead entities
            if (_targetFocusStart.Count > 0)
            {
                var deadIds = new List<long>();
                foreach (var id in _targetFocusStart.Keys)
                {
                    var ent = gc.EntityListWrapper.OnlyValidEntities.FirstOrDefault(e => e.Id == id);
                    if (ent == null || !ent.IsAlive)
                        deadIds.Add(id);
                }
                foreach (var id in deadIds)
                {
                    _targetFocusStart.Remove(id);
                    _deprioritizedTargets.Remove(id);
                }
            }

            if (BestTarget == null || !InCombat)
            {
                _lastAttackTargetId = 0;
                _lastAttackTargetHp = -1f;
                return;
            }

            var targetId = BestTarget.Id;
            float targetHp;
            try
            {
                var life = BestTarget.GetComponent<Life>();
                if (life == null) return;
                targetHp = life.CurHP + life.CurES;
            }
            catch { return; }

            // ── Focus timeout — deprioritize targets we've been stuck on ──
            if (!_targetFocusStart.ContainsKey(targetId))
                _targetFocusStart[targetId] = DateTime.Now;

            if (!_deprioritizedTargets.Contains(targetId))
            {
                var focusDuration = (DateTime.Now - _targetFocusStart[targetId]).TotalSeconds;
                var focusTimeout = BestTarget.Rarity switch
                {
                    MonsterRarity.Unique => 10.0,
                    MonsterRarity.Rare => 5.0,
                    MonsterRarity.Magic => 3.0,
                    _ => 2.0
                };

                if (focusDuration >= focusTimeout)
                {
                    _deprioritizedTargets.Add(targetId);
                    LastAction = $"Deprioritized {BestTarget.RenderName ?? "?"} ({BestTarget.Rarity}) after {focusDuration:F1}s";
                }
            }

            // ── Attack connectivity — detect zero-damage (unreachable) ──
            // New target — start tracking
            if (targetId != _lastAttackTargetId)
            {
                _lastAttackTargetId = targetId;
                _lastAttackTargetHp = targetHp;
                _attackConnectivityStart = DateTime.Now;
                return;
            }

            // HP changed — attacks are connecting, reset timer
            if (Math.Abs(targetHp - _lastAttackTargetHp) > 1f)
            {
                _lastAttackTargetHp = targetHp;
                _attackConnectivityStart = DateTime.Now;
                return;
            }

            // HP hasn't changed — check timeout
            if ((DateTime.Now - _attackConnectivityStart).TotalSeconds < BaseAttackConnectTimeoutSec + ExtraLatencySec)
                return;

            // Never blacklist Unique-rarity monsters as unreachable.
            // Boss invulnerability phases are a normal game mechanic — the boss IS reachable,
            // it just can't take damage temporarily. Deprioritization handles target switching.
            if (BestTarget.Rarity == MonsterRarity.Unique)
            {
                // Reset timer so we don't spam this check every tick
                _attackConnectivityStart = DateTime.Now;
                return;
            }

            // Attacks not connecting for too long — blacklist the cluster
            var anchorPos = BestTarget.GridPosNum;
            var playerGrid = gc.Player.GridPosNum;
            int blacklisted = 0;

            foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
            {
                if (entity.Type != EntityType.Monster || !entity.IsAlive || !entity.IsHostile)
                    continue;
                if (_unreachableMonsters.Contains(entity.Id)) continue;

                if (Vector2.Distance(entity.GridPosNum, anchorPos) <= UnreachableClusterRadius)
                {
                    _unreachableMonsters.Add(entity.Id);
                    blacklisted++;
                }
            }

            UnreachableClusterCount++;
            LastAction = $"Blacklisted {blacklisted} unreachable monsters near ({anchorPos.X:F0},{anchorPos.Y:F0})";

            // Reset tracking so we pick a new target
            _lastAttackTargetId = 0;
            _lastAttackTargetHp = -1f;
        }

        /// <summary>
        /// Clear the unreachable blacklist. Call on area change — new zone, fresh state.
        /// Some monsters become targetable after encounter mechanics trigger.
        /// </summary>
        public void ClearUnreachable()
        {
            _unreachableMonsters.Clear();
            UnreachableClusterCount = 0;
            _lastAttackTargetId = 0;
            _lastAttackTargetHp = -1f;
            _targetFocusStart.Clear();
            _deprioritizedTargets.Clear();

            // Clear learned dormant ignores (keep pre-seeded ones).
            // New zone may have different dormant entities that are valid.
            _ignoredDormantPaths.Clear();
            _ignoredDormantPaths.Add("Critters/");
            LearnedDormantIgnoreCount = 0;
        }

        // ═══════════════════════════════════════════════════
        // Helpers
        // ═══════════════════════════════════════════════════

        /// <summary>
        /// Entities that must be killed before nearby monsters can be damaged.
        /// These get massive targeting priority regardless of rarity or distance.
        /// </summary>
        private static bool IsPriorityTarget(Entity entity)
        {
            var path = entity.Metadata;
            if (path == null) return false;

            // "Allies Cannot Die" totems — grants invulnerability to all nearby monsters
            if (path.Contains("CannotDie", StringComparison.OrdinalIgnoreCase))
                return true;

            return false;
        }

        private static bool IsInsideMonolith(Entity entity)
        {
            try
            {
                var stats = entity.GetComponent<Stats>();
                if (stats?.StatDictionary != null &&
                    stats.StatDictionary.TryGetValue(GameStat.MonsterInsideMonolith, out var val) && val > 0)
                    return true;
            }
            catch { }
            return false;
        }

        private static Vector2 ToWorld(Vector2 gridPos)
        {
            return gridPos * Pathfinding.GridToWorld;
        }

        private static Vector2 SafeNormalize(Vector2 v)
        {
            var len = v.Length();
            return len > 0.001f ? v / len : Vector2.UnitY;
        }

        // ═══════════════════════════════════════════════════
        // Internal types
        // ═══════════════════════════════════════════════════

        private class SkillBarEntry
        {
            public ActorSkill? Skill;  // null if no ActorSkill match found (still fires key)
            public Keys Key;
            public SkillRole Role;
            public int Priority;
            public int AoeRadius;
            public bool CanCrossTerrain;

            // Per-skill conditions
            public SkillTargetFilter TargetFilter;
            public int MinNearbyEnemies;
            public int MaxTargetRange;
            public bool OnlyWhenBuffMissing;
            public bool OnlyOnLowLife;
            public bool SummonRecast;
            public string BuffDebuffName = "";
            public int MinCastIntervalMs;
            public bool RequireTargetable;
            public bool IsChannel;
            public DateTime LastCastAt = DateTime.MinValue;
        }
    }

    /// <summary>
    /// Info about a configured movement skill, exposed to NavigationSystem.
    /// </summary>
    public class MovementSkillInfo
    {
        public Keys Key { get; set; }
        public bool CanCrossTerrain { get; set; }
        public bool IsReady { get; set; }
        public ActorSkill? ActorSkill { get; set; }
        public int MinCastIntervalMs { get; set; }
        public DateTime LastUsedAt { get; set; } = DateTime.MinValue;
    }

    // ═══════════════════════════════════════════════════
    // Enums
    // ═══════════════════════════════════════════════════

    /// <summary>
    /// What a skill slot does. Non-combat roles (PrimaryMovement, MovementSkill) are handled
    /// specially. Combat roles define WHERE the cursor aims — all "when to fire" logic is
    /// handled by per-skill conditions.
    /// </summary>
    public enum SkillRole
    {
        Disabled,           // Skip entirely
        PrimaryMovement,    // Move-only key — used by NavigationSystem and combat positioning
        MovementSkill,      // Dash/blink — used by navigation for gap crossing and speed
        Enemy,              // Aim at best hostile target (attacks, curses, debuffs, warcries)
        Corpse,             // Aim at nearest corpse (offerings, detonate dead)
        Self,               // No cursor needed — self-cast (buffs, guards, vaal, summons)
    }

    public enum SkillTargetFilter
    {
        Any,            // Use on any valid target
        RareOrAbove,    // Only rare, unique monsters
        UniqueOnly,     // Only unique monsters
    }

    public enum CombatPositioning
    {
        Aggressive,     // Walk into densest pack (RF, melee builds that want to be hit)
        Melee,          // Get within fight range of pack (attack skills, close-range casters)
        Ranged,         // Orbit around pack at fight range (ranged, summoner, DoT)
    }

    /// <summary>
    /// Combat behavior profile set by modes. Controls what the combat system does.
    /// </summary>
    public class CombatProfile
    {
        /// <summary>Whether combat is active at all.</summary>
        public bool Enabled { get; set; }

        /// <summary>How to position relative to monsters.</summary>
        public CombatPositioning Positioning { get; set; } = CombatPositioning.Aggressive;

        /// <summary>
        /// Optional leash anchor in grid coordinates. When set, combat positioning
        /// will never move the player beyond LeashRadius of this point.
        /// Used by mechanics like Ultimatum that require staying in a bounded area.
        /// </summary>
        public Vector2? LeashAnchor { get; set; }

        /// <summary>Leash radius in grid units. Only used when LeashAnchor is set.</summary>
        public float LeashRadius { get; set; }

        /// <summary>
        /// Optional defense anchor in grid coordinates. When set, target scoring
        /// heavily favors monsters closer to this point (protect-the-objective priority).
        /// Used by blight sweep to prioritize monsters threatening the pump hub.
        /// </summary>
        public Vector2? DefenseAnchor { get; set; }

        public static CombatProfile Default => new() { Enabled = false };
    }
}
