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

        /// <summary>Total number of alive hostile monsters in the entity list (awareness tier).</summary>
        public int CachedMonsterCount { get; private set; }

        /// <summary>Grid position of the nearest alive hostile monster (for mode navigation).</summary>
        public Vector2? NearestMonsterPos { get; private set; }

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

        /// <summary>Debug: last skill execution detail.</summary>
        public string LastSkillAction { get; private set; } = "";

        /// <summary>Whether the system wants the player to reposition for combat.</summary>
        public bool WantsToMove { get; private set; }

        /// <summary>Target position the system wants to move toward (world coords).</summary>
        public Vector2 MoveTarget { get; private set; }

        // ── Movement info (exposed for NavigationSystem) ──

        /// <summary>Key for the primary movement (Move Only) binding, or null if not configured.</summary>
        public Keys? PrimaryMoveKey { get; private set; }

        /// <summary>All configured movement skills (dash/blink), sorted by priority.</summary>
        public List<MovementSkillInfo> MovementSkills { get; private set; } = new();

        /// <summary>Whether any movement skill can cross terrain gaps.</summary>
        public bool HasGapCrosser => MovementSkills.Any(m => m.CanCrossTerrain);

        // ── Timing ──

        private DateTime _lastSkillUseAt = DateTime.MinValue;
        private DateTime _lastFlaskUseAt = DateTime.MinValue;
        private const int MinSkillIntervalMs = 80;
        private const int MinFlaskIntervalMs = 200;

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

        private static readonly Keys[] FlaskKeys = { Keys.D1, Keys.D2, Keys.D3, Keys.D4, Keys.D5 };

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
                InCombat = false;
                NearbyMonsterCount = 0;
                BestTarget = null;
                LastAction = "disabled";
                return false;
            }

            // Read vitals
            ReadVitals(gc);

            // Scan threats
            ScanThreats(gc, settings);

            // Refresh skill bar data periodically
            RefreshSkillBar(gc, settings);

            // Flasks (always check, even out of combat)
            TickFlasks(gc, settings);

            if (!InCombat)
            {
                LastAction = "no threats";
                return false;
            }

            // Execute skills by priority
            bool usedSkill = TickSkills(gc, settings);

            // Positioning — uses cursor + move key (same as NavigationSystem)
            // Suppressed when another system is navigating (e.g. loot pickup)
            if (!SuppressPositioning)
                TickPositioning(ctx);

            return usedSkill;
        }

        /// <summary>Reset state (call on mode exit).</summary>
        public void Reset()
        {
            InCombat = false;
            NearbyMonsterCount = 0;
            CachedMonsterCount = 0;
            NearestMonsterPos = null;
            BestTarget = null;
            PackCenter = Vector2.Zero;
            DenseClusterCenter = Vector2.Zero;
            NearestCorpse = null;
            WantsToMove = false;
            LastAction = "";
            LastSkillAction = "";
            _skillBar.Clear();
            _primaryMovementEntry = null;
            _movementSkillEntries.Clear();
            PrimaryMoveKey = null;
            MovementSkills.Clear();
            _lastSkillBarReadAt = DateTime.MinValue;
            _lastCastByKey.Clear();
            Profile = CombatProfile.Default;
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

        private void ScanThreats(GameController gc, BotSettings.BuildSettings settings)
        {
            var playerGrid = gc.Player.GridPosNum;
            float combatRange = settings.CombatRange.Value;

            Entity? bestTarget = null;
            float bestScore = float.MinValue;
            int combatCount = 0;    // within CombatRange
            int cachedCount = 0;    // all alive hostiles
            var combatSum = Vector2.Zero;
            float nearestCorpseDist = float.MaxValue;
            Vector2? nearestCorpse = null;
            float nearestMonsterDist = float.MaxValue;
            Vector2? nearestMonsterPos = null;

            _nearbyMonsterPositions.Clear();

            foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
            {
                if (entity.Type != EntityType.Monster) continue;
                if (!entity.IsHostile) continue;

                var dist = Vector2.Distance(entity.GridPosNum, playerGrid);

                // Corpse detection (dead but still in world)
                if (!entity.IsAlive && dist < combatRange)
                {
                    if (dist < nearestCorpseDist)
                    {
                        nearestCorpseDist = dist;
                        nearestCorpse = entity.GridPosNum;
                    }
                    continue;
                }

                if (!entity.IsAlive || !entity.IsTargetable) continue;

                // Skip monsters trapped inside essence monoliths (cannot be damaged until released)
                if (IsInsideMonolith(entity)) continue;

                cachedCount++;

                // Track nearest monster (for awareness-tier navigation)
                if (dist < nearestMonsterDist)
                {
                    nearestMonsterDist = dist;
                    nearestMonsterPos = entity.GridPosNum;
                }

                // Only count monsters within CombatRange for combat decisions
                if (dist > combatRange) continue;

                combatCount++;
                combatSum += entity.GridPosNum;
                _nearbyMonsterPositions.Add(entity.GridPosNum);

                // Score: rarity weight - distance penalty
                float rarityWeight = entity.Rarity switch
                {
                    MonsterRarity.Magic => 2f,
                    MonsterRarity.Rare => 10f,
                    MonsterRarity.Unique => 25f,
                    _ => 1f
                };
                if (IsPriorityTarget(entity))
                    rarityWeight = 100f;

                float score = rarityWeight - dist * 0.1f;

                // Defense anchor: heavily favor monsters closer to the objective
                if (Profile.DefenseAnchor.HasValue)
                {
                    float distToObjective = Vector2.Distance(entity.GridPosNum, Profile.DefenseAnchor.Value);
                    // Monsters within 30 grid units of objective get large bonus,
                    // scaling down with distance. This dominates over rarity for nearby threats.
                    score += MathF.Max(0f, 60f - distToObjective);
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    bestTarget = entity;
                }
            }

            NearbyMonsterCount = combatCount;
            CachedMonsterCount = cachedCount;
            BestTarget = bestTarget;
            InCombat = combatCount > 0;
            PackCenter = combatCount > 0 ? combatSum / combatCount : playerGrid;
            NearestCorpse = nearestCorpse;
            NearestMonsterPos = nearestMonsterPos;

            // If best target is a priority target, override positioning
            if (bestTarget != null && IsPriorityTarget(bestTarget))
                DenseClusterCenter = bestTarget.GridPosNum;
            else
                DenseClusterCenter = FindDensestPosition(_nearbyMonsterPositions, playerGrid);
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

        // ═══════════════════════════════════════════════════
        // Skill bar reading
        // ═══════════════════════════════════════════════════

        /// <summary>
        /// Refresh skill bar data — updates MovementSkills readiness every tick,
        /// full rebuild every 500ms. Public so BotCore can call it even when combat is disabled
        /// (NavigationSystem needs MovementSkills for dash-for-speed).
        /// </summary>
        public void RefreshSkillBar(GameController gc, BotSettings.BuildSettings settings)
        {
            // Update movement skill readiness every tick (cheap check)
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

            // Iterate user-configured slots (key-based, not slot-index-based)
            foreach (var slotConfig in settings.AllSkillSlots)
            {
                var key = slotConfig.Key.Value;
                if (key == Keys.None) continue;

                if (!Enum.TryParse<SkillRole>(slotConfig.Role.Value, out var role))
                    role = SkillRole.Disabled;
                if (role == SkillRole.Disabled) continue;

                // PrimaryMovement doesn't need an ActorSkill match — it's just a key
                if (role == SkillRole.PrimaryMovement)
                {
                    _primaryMovementEntry = new SkillBarEntry
                    {
                        Skill = null,
                        Key = key,
                        Role = role,
                        Priority = 0,
                    };
                    PrimaryMoveKey = key;
                    continue;
                }

                // Try to find the matching ActorSkill on the skill bar for status reading
                ActorSkill? matchedSkill = null;
                var actor = gc.Player?.GetComponent<Actor>();
                if (actor?.ActorSkills != null)
                {
                    foreach (var skill in actor.ActorSkills)
                    {
                        if (!skill.IsOnSkillBar) continue;
                        var slotKey = DefaultKeyForSlot(skill.SkillSlotIndex);
                        if (slotKey == key)
                        {
                            matchedSkill = skill;
                            break;
                        }
                    }
                }

                // Parse condition settings
                Enum.TryParse<SkillTargetFilter>(slotConfig.TargetFilter.Value, out var targetFilter);

                var entry = new SkillBarEntry
                {
                    Skill = matchedSkill,
                    Key = key,
                    Role = role,
                    Priority = slotConfig.Priority.Value,
                    CanCrossTerrain = slotConfig.CanCrossTerrain.Value,
                    TargetFilter = targetFilter,
                    MinNearbyEnemies = slotConfig.MinNearbyEnemies.Value,
                    MaxTargetRange = slotConfig.MaxTargetRange.Value,
                    OnlyWhenBuffMissing = slotConfig.OnlyWhenBuffMissing.Value,
                    OnlyOnLowLife = slotConfig.OnlyOnLowLife.Value,
                    SummonRecast = slotConfig.SummonRecast.Value,
                    BuffDebuffName = slotConfig.BuffDebuffName.Value ?? "",
                    MinCastIntervalMs = slotConfig.MinCastIntervalMs.Value,
                };

                // Auto-detect properties from skill stats
                if (matchedSkill != null)
                {
                    try
                    {
                        var stats = matchedSkill.Stats;
                        if (stats != null)
                        {
                            if (stats.TryGetValue(GameStat.ActiveSkillBaseRadius, out var radius))
                                entry.AoeRadius = radius;
                        }
                    }
                    catch { }
                }

                // Restore per-skill cast timestamp from previous refresh
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

            // Sort combat skills by priority (higher first)
            _skillBar.Sort((a, b) => b.Priority.CompareTo(a.Priority));

            // Build movement skill info list for NavigationSystem
            // Snapshot LastUsedAt before clearing — preserve across rebuilds
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
            // Sort: gap-crossers first, then by priority
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
        private static Keys DefaultKeyForSlot(int slotIndex) => slotIndex switch
        {
            0 => Keys.None,        // LMB — move only, not a combat skill
            1 => Keys.RButton,     // RMB
            2 => Keys.MButton,     // Middle mouse
            3 => Keys.Q,
            4 => Keys.W,
            5 => Keys.E,
            6 => Keys.R,
            7 => Keys.T,
            _ => Keys.None
        };

        // ═══════════════════════════════════════════════════
        // Skill execution
        // ═══════════════════════════════════════════════════

        private bool TickSkills(GameController gc, BotSettings.BuildSettings settings)
        {
            if (!BotInput.CanAct) return false;
            if ((DateTime.Now - _lastSkillUseAt).TotalMilliseconds < MinSkillIntervalMs) return false;

            foreach (var entry in _skillBar)
            {
                // Universal gate: game says skill can't be used (cooldown/mana/souls)
                if (entry.Skill != null && !entry.Skill.CanBeUsed) continue;

                // Suppress cursor-moving skills during loot pickup to avoid cursor interference
                if (SuppressTargetedSkills && (entry.Role == SkillRole.Enemy || entry.Role == SkillRole.Corpse))
                    continue;

                // Targeting prerequisite: Enemy needs a target, Corpse needs a corpse
                if (entry.Role == SkillRole.Enemy && (BestTarget == null || !InCombat)) continue;
                if (entry.Role == SkillRole.Corpse && !NearestCorpse.HasValue) continue;

                // All "when to fire" logic is in conditions
                if (!CheckSkillConditions(gc, entry, settings)) continue;

                var targetPos = GetSkillTarget(gc, entry);
                UseSkill(gc, entry, targetPos);
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

            // Summon recast — skip if enough minions deployed
            if (entry.SummonRecast && entry.Skill != null)
            {
                if (!ShouldResummon(entry.Skill, settings))
                    return false;
            }

            return true;
        }

        private Vector2? GetSkillTarget(GameController gc, SkillBarEntry entry)
        {
            return entry.Role switch
            {
                SkillRole.Enemy => BestTarget != null ? ToWorld(BestTarget.GridPosNum) : null,
                SkillRole.Corpse => NearestCorpse.HasValue ? ToWorld(NearestCorpse.Value) : null,
                SkillRole.Self => null,
                _ => null
            };
        }

        private void UseSkill(GameController gc, SkillBarEntry entry, Vector2? worldTarget)
        {
            bool acted;

            if (worldTarget.HasValue)
            {
                // Targeted skill — cursor + key press through BotInput
                var camera = gc.IngameState.Camera;
                var screenPos = camera.WorldToScreen(
                    new Vector3(worldTarget.Value.X, worldTarget.Value.Y, gc.Player.PosNum.Z));

                var windowRect = gc.Window.GetWindowRectangle();

                // Bounds check — don't fire if target is off-screen
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
                // Self-cast — just press key (no cursor move needed)
                acted = BotInput.PressKey(entry.Key);
            }

            if (!acted) return;

            _lastSkillUseAt = DateTime.Now;
            entry.LastCastAt = DateTime.Now;
            var skillName = entry.Skill?.Name ?? entry.Key.ToString();
            LastSkillAction = $"{skillName} ({entry.Key}, {entry.Role})";
            LastAction = $"skill: {skillName}";
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

        private bool ShouldResummon(ActorSkill skill, BotSettings.BuildSettings settings)
        {
            try
            {
                var deployed = skill.DeployedObjects;
                if (deployed == null) return true;
                return deployed.Count < settings.SummonExpectedCount.Value;
            }
            catch
            {
                return false;
            }
        }

        // ═══════════════════════════════════════════════════
        // Flask management
        // ═══════════════════════════════════════════════════

        private void TickFlasks(GameController gc, BotSettings.BuildSettings settings)
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

            // Utility flasks (use in combat when available)
            if (InCombat)
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
            if (!BotInput.PressKey(FlaskKeys[index])) return;

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

            if (BestTarget == null || NearbyMonsterCount == 0) return;

            var playerGrid = gc.Player.GridPosNum;
            float dist = Vector2.Distance(playerGrid, DenseClusterCenter);
            float fightRange = settings.FightRange.Value;

            Vector2? desiredGridPos = null;

            switch (Profile.Positioning)
            {
                case CombatPositioning.Aggressive:
                    // Always walk into the densest pack
                    if (dist > 3f)
                        desiredGridPos = DenseClusterCenter;
                    break;

                case CombatPositioning.Melee:
                    // Walk toward pack until within fight range
                    if (dist > fightRange)
                        desiredGridPos = DenseClusterCenter;
                    break;

                case CombatPositioning.Ranged:
                    // Orbit around pack at roughly fight range
                    // Move perpendicular to pack direction — never retreat, just circle
                    if (dist > fightRange * 1.5f)
                    {
                        // Too far — close in
                        desiredGridPos = DenseClusterCenter;
                    }
                    else if (dist > fightRange * 0.5f)
                    {
                        // In orbit zone — move perpendicular to pack direction
                        var toPack = DenseClusterCenter - playerGrid;
                        var perpendicular = new Vector2(-toPack.Y, toPack.X); // 90 degree rotation
                        perpendicular = SafeNormalize(perpendicular);
                        desiredGridPos = playerGrid + perpendicular * fightRange * 0.5f;
                    }
                    // else: inside fight range — don't retreat, let skills fire
                    break;
            }

            if (!desiredGridPos.HasValue) return;

            // Enforce leash constraint — clamp desired position to stay within anchor radius
            if (Profile.LeashAnchor.HasValue)
            {
                var anchor = Profile.LeashAnchor.Value;
                var radius = Profile.LeashRadius;
                var distFromAnchor = Vector2.Distance(desiredGridPos.Value, anchor);
                if (distFromAnchor > radius)
                {
                    // Pull position back toward anchor to stay within radius
                    var toDesired = desiredGridPos.Value - anchor;
                    toDesired = SafeNormalize(toDesired);
                    desiredGridPos = anchor + toDesired * radius;
                }
            }

            // Validate the desired position: must be walkable and have targeting LOS to monsters
            var validPos = ctx.Navigation.FindWalkableWithLOS(gc, desiredGridPos.Value, DenseClusterCenter);
            if (!validPos.HasValue) return;

            WantsToMove = true;
            MoveTarget = ToWorld(validPos.Value);
            ExecuteMove(gc, MoveTarget);
        }

        /// <summary>
        /// Move toward a world position using cursor + move key (same as NavigationSystem).
        /// Never clicks — prevents accidental item/entity interactions.
        /// </summary>
        private void ExecuteMove(GameController gc, Vector2 worldTarget)
        {
            var camera = gc.IngameState.Camera;
            var playerZ = gc.Player.PosNum.Z;
            var screenPos = camera.WorldToScreen(new Vector3(worldTarget.X, worldTarget.Y, playerZ));
            var windowRect = gc.Window.GetWindowRectangle();

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

            var moveKey = PrimaryMoveKey ?? Keys.T;
            BotInput.CursorPressKey(absPos, moveKey);
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
