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

        /// <summary>Number of alive hostile monsters in the network bubble.</summary>
        public int NearbyMonsterCount { get; private set; }

        /// <summary>Number of monsters within chase radius (used for positioning decisions).</summary>
        public int NearbyChaseCount { get; private set; }

        /// <summary>Best target entity (highest priority: rarity + distance).</summary>
        public Entity? BestTarget { get; private set; }

        /// <summary>Center of mass of ALL monster pack (grid coords). Used for skill targeting.</summary>
        public Vector2 PackCenter { get; private set; }

        /// <summary>Center of mass of nearby monsters within chase radius (grid coords). Used for positioning.</summary>
        public Vector2 NearbyPackCenter { get; private set; }

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
        /// When true, combat still scans threats and fires skills but skips repositioning.
        /// Set by modes when another system (e.g. InteractionSystem) is navigating and
        /// combat movement would conflict.
        /// </summary>
        public bool SuppressPositioning { get; set; }

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
        public bool Tick(GameController gc, BotSettings.BuildSettings settings)
        {
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
                TickPositioning(gc, settings);

            return usedSkill;
        }

        /// <summary>Reset state (call on mode exit).</summary>
        public void Reset()
        {
            InCombat = false;
            NearbyMonsterCount = 0;
            NearbyChaseCount = 0;
            BestTarget = null;
            PackCenter = Vector2.Zero;
            NearbyPackCenter = Vector2.Zero;
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

        /// <summary>
        /// Minimum monsters within chase radius before combat repositioning kicks in.
        /// Prevents chasing lone stragglers across the map.
        /// </summary>
        private const int MinChaseMonsters = 3;

        /// <summary>
        /// Max grid distance to reposition toward monsters.
        /// Beyond this, skills fire opportunistically but we don't walk toward them.
        /// </summary>
        private const float ChaseRadius = 80f;

        private void ScanThreats(GameController gc, BotSettings.BuildSettings settings)
        {
            var playerGrid = gc.Player.GridPosNum;

            Entity? bestTarget = null;
            float bestScore = float.MinValue;
            int count = 0;
            int nearbyCount = 0; // monsters within chase radius
            var packSum = Vector2.Zero;
            var nearbyPackSum = Vector2.Zero; // only within chase radius
            float nearestCorpseDist = float.MaxValue;
            Vector2? nearestCorpse = null;

            foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
            {
                if (entity.Type != EntityType.Monster) continue;
                if (!entity.IsHostile) continue;

                var dist = Vector2.Distance(entity.GridPosNum, playerGrid);

                // Corpse detection (dead but still in world)
                if (!entity.IsAlive && dist < settings.CorpseSearchRadius.Value)
                {
                    if (dist < nearestCorpseDist)
                    {
                        nearestCorpseDist = dist;
                        nearestCorpse = entity.GridPosNum;
                    }
                    continue;
                }

                if (!entity.IsAlive || !entity.IsTargetable) continue;

                count++;
                packSum += entity.GridPosNum;

                if (dist <= ChaseRadius)
                {
                    nearbyCount++;
                    nearbyPackSum += entity.GridPosNum;
                }

                // Score: rarity weight - distance penalty
                float rarityWeight = entity.Rarity switch
                {
                    MonsterRarity.Magic => 2f,
                    MonsterRarity.Rare => 10f,
                    MonsterRarity.Unique => 25f,
                    _ => 1f
                };
                float score = rarityWeight - dist * 0.1f;

                if (score > bestScore)
                {
                    bestScore = score;
                    bestTarget = entity;
                }
            }

            NearbyMonsterCount = count;
            NearbyChaseCount = nearbyCount;
            BestTarget = bestTarget;
            InCombat = count > 0;
            PackCenter = count > 0 ? packSum / count : playerGrid;
            NearbyPackCenter = nearbyCount > 0 ? nearbyPackSum / nearbyCount : PackCenter;
            NearestCorpse = nearestCorpse;
        }

        // ═══════════════════════════════════════════════════
        // Skill bar reading
        // ═══════════════════════════════════════════════════

        private void RefreshSkillBar(GameController gc, BotSettings.BuildSettings settings)
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
            MovementSkills.Clear();
            foreach (var entry in _movementSkillEntries)
            {
                MovementSkills.Add(new MovementSkillInfo
                {
                    Key = entry.Key,
                    CanCrossTerrain = entry.CanCrossTerrain,
                    IsReady = entry.Skill?.CanBeUsed ?? true,
                    ActorSkill = entry.Skill,
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

        private void TickPositioning(GameController gc, BotSettings.BuildSettings settings)
        {
            if (BestTarget == null) return;

            // Aggressive mode chases ANY monster (retaliatory builds need to be on top of mobs).
            // Other modes require a minimum pack size or rare/unique to avoid chasing stragglers.
            bool worthChasing = Profile.Positioning == CombatPositioning.Aggressive
                ? NearbyChaseCount > 0
                : NearbyChaseCount >= MinChaseMonsters
                    || BestTarget.Rarity == MonsterRarity.Rare
                    || BestTarget.Rarity == MonsterRarity.Unique;
            if (!worthChasing) return;

            var playerGrid = gc.Player.GridPosNum;
            // Use NearbyPackCenter for positioning (only monsters within chase radius)
            // to avoid being pulled toward distant cached entities
            float dist = Vector2.Distance(playerGrid, NearbyPackCenter);

            Vector2? desiredGridPos = null;

            switch (Profile.Positioning)
            {
                case CombatPositioning.Aggressive:
                    // Walk directly into the pack — retaliatory builds need to be on top of mobs.
                    // Use a tight threshold (3 grid ≈ 33 world) so we stay glued to the pack center.
                    if (dist > 3f)
                        desiredGridPos = NearbyPackCenter;
                    break;

                case CombatPositioning.Orbit:
                    float orbitDist = settings.OrbitRange.Value;
                    if (dist < orbitDist * 0.6f)
                    {
                        // Too close — move away
                        var awayDir = SafeNormalize(playerGrid - NearbyPackCenter);
                        desiredGridPos = playerGrid + awayDir * orbitDist * 0.4f;
                    }
                    else if (dist > orbitDist * 1.4f)
                    {
                        // Too far — move toward pack
                        desiredGridPos = NearbyPackCenter;
                    }
                    break;

                case CombatPositioning.Ranged:
                    float safeRange = settings.RangedRange.Value;
                    if (dist < safeRange * 0.5f)
                    {
                        // Too close — retreat
                        var awayDir = SafeNormalize(playerGrid - NearbyPackCenter);
                        desiredGridPos = playerGrid + awayDir * safeRange * 0.5f;
                    }
                    break;

                case CombatPositioning.Stationary:
                    break;
            }

            if (desiredGridPos.HasValue)
            {
                WantsToMove = true;
                MoveTarget = ToWorld(desiredGridPos.Value);
                ExecuteMove(gc, MoveTarget);
            }
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
            public float MaxTargetRange;
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
        Aggressive,     // Walk into densest pack (RF, melee)
        Orbit,          // Medium range orbit (summoner, DoT)
        Ranged,         // Max range kiting (caster, archer)
        Stationary,     // Stand at current position (blight tower phase)
    }

    /// <summary>
    /// Combat behavior profile set by modes. Controls what the combat system does.
    /// </summary>
    public class CombatProfile
    {
        /// <summary>Whether combat is active at all.</summary>
        public bool Enabled { get; set; }

        /// <summary>Override engage radius (0 = use settings default).</summary>
        public float EngageRadius { get; set; }

        /// <summary>How to position relative to monsters.</summary>
        public CombatPositioning Positioning { get; set; } = CombatPositioning.Aggressive;

        public static CombatProfile Default => new() { Enabled = false };
    }
}
