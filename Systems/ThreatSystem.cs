using ExileCore;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using System.Numerics;

namespace AutoExile.Systems
{
    /// <summary>
    /// Per-monster threat tracking state. Exposed publicly for debug rendering.
    /// </summary>
    public class MonsterThreat
    {
        public long EntityId;
        public Entity? Entity;
        public Vector2 GridPos;
        public string AnimationName = "";
        public string SkillName = "";
        public int AnimationStage;
        public float AnimationProgress;
        public float TimeRemainingMs;
        public float AnimationSpeed;
        public Vector2 CastDestination;
        public bool HasCast;
        public bool DodgeSignaled;
        public DateTime CastStartTime;

        // Change detection (internal)
        internal string PrevSkillName = "";
        internal float PrevProgress = 1f;
    }

    /// <summary>
    /// Scans Unique/Rare monsters for dangerous skill casts and produces dodge signals.
    /// Reads Actor component data: Animation, CurrentAction (skill + locked destination),
    /// and AnimationController (progress/timing).
    ///
    /// Key finding from research: monster CurrentAction.Destination locks to the player's
    /// position at cast start and never updates mid-cast. Moving perpendicular during the
    /// windup (AnimationProgress 0.15-0.50) causes the attack to miss.
    /// </summary>
    public class ThreatSystem
    {
        private readonly Dictionary<long, MonsterThreat> _tracked = new();

        // ── Output: the most urgent dodge signal this tick ──

        /// <summary>True when a dangerous cast is detected and dodge should execute.</summary>
        public bool DodgeUrgent { get; private set; }

        /// <summary>Normalized direction to dodge (perpendicular to attack vector, grid coords).</summary>
        public Vector2 DodgeDirection { get; private set; }

        /// <summary>Grid position the attack is aimed at (locked at cast start).</summary>
        public Vector2 ThreatDestination { get; private set; }

        /// <summary>Name of the threatening skill.</summary>
        public string ThreatSkillName { get; private set; } = "";

        /// <summary>The monster casting the threat.</summary>
        public Entity? ThreatSource { get; private set; }

        /// <summary>Animation progress of the threatening cast (0-1).</summary>
        public float ThreatProgress { get; private set; }

        /// <summary>Milliseconds until the threatening animation completes.</summary>
        public float ThreatTimeRemainingMs { get; private set; }

        // ── Debug / stats ──

        public IReadOnlyDictionary<long, MonsterThreat> TrackedMonsters => _tracked;
        public int DodgesTriggered { get; private set; }
        public int CastsDetected { get; private set; }
        public string LastAction { get; private set; } = "";

        // ── Config (synced from settings each tick) ──

        public bool Enabled { get; set; }
        public float ThreatRadius { get; set; } = 60f;
        public float DodgeTriggerDistance { get; set; } = 15f;
        public float DodgeMinProgress { get; set; } = 0.15f;
        public float DodgeMaxProgress { get; set; } = 0.50f;
        public bool MonitorRares { get; set; } = true;

        // Animations that never need dodging
        private static readonly HashSet<string> SafeAnimations = new()
        {
            "Run", "Idle", "Death", "Spawn", "Stunned", "Flinch"
        };

        public void Tick(GameController gc)
        {
            DodgeUrgent = false;
            ThreatSource = null;

            if (!Enabled) return;

            var player = gc.Player;
            if (player == null || !player.IsAlive) return;

            var playerGrid = new Vector2(player.GridPosNum.X, player.GridPosNum.Y);

            // Mark all tracked as stale (Entity ref cleared, re-set if still visible)
            foreach (var mt in _tracked.Values)
                mt.Entity = null;

            float bestThreatScore = float.MaxValue;

            foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
            {
                if (entity.Type != EntityType.Monster) continue;
                if (!entity.IsHostile || !entity.IsAlive || !entity.IsTargetable) continue;

                // Only monitor Unique (always) and Rare (if enabled)
                if (entity.Rarity != MonsterRarity.Unique &&
                    !(MonitorRares && entity.Rarity == MonsterRarity.Rare))
                    continue;

                var entityGrid = new Vector2(entity.GridPosNum.X, entity.GridPosNum.Y);
                var dist = Vector2.Distance(entityGrid, playerGrid);
                if (dist > ThreatRadius) continue;

                // Get or create tracking state
                if (!_tracked.TryGetValue(entity.Id, out var mt))
                {
                    mt = new MonsterThreat { EntityId = entity.Id };
                    _tracked[entity.Id] = mt;
                }

                mt.Entity = entity;
                mt.GridPos = entityGrid;

                // Read Actor component
                var actor = entity.GetComponent<Actor>();
                if (actor == null) continue;

                var currentAction = actor.CurrentAction;
                var animController = actor.AnimationController;

                var skillName = currentAction?.Skill?.Name ?? "";
                var animName = actor.Animation.ToString();
                var progress = animController?.AnimationProgress ?? 0f;
                var timeRemaining = (float)(animController?.AnimationCompletesIn.TotalMilliseconds ?? 0);
                var stage = animController?.CurrentAnimationStage ?? 0;
                var speed = animController?.AnimationSpeed ?? 1f;

                mt.AnimationName = animName;
                mt.SkillName = skillName;
                mt.AnimationStage = stage;
                mt.AnimationProgress = progress;
                mt.TimeRemainingMs = timeRemaining;
                mt.AnimationSpeed = speed;

                // ── Detect new cast ──
                bool isNewCast = false;
                if (!string.IsNullOrEmpty(skillName))
                {
                    if (skillName != mt.PrevSkillName)
                    {
                        // Different skill = new cast
                        isNewCast = true;
                    }
                    else if (progress < 0.15f && mt.PrevProgress > 0.5f)
                    {
                        // Same skill, progress reset = re-cast
                        isNewCast = true;
                    }
                }

                if (isNewCast)
                {
                    mt.HasCast = true;
                    mt.DodgeSignaled = false;
                    mt.CastStartTime = DateTime.Now;
                    mt.CastDestination = new Vector2(
                        currentAction?.DestinationX ?? 0,
                        currentAction?.DestinationY ?? 0);
                    CastsDetected++;
                }
                else if (string.IsNullOrEmpty(skillName) && mt.HasCast)
                {
                    // Skill cleared (between casts)
                    mt.HasCast = false;
                    mt.DodgeSignaled = false;
                }

                // Update destination during ongoing cast
                if (mt.HasCast && currentAction != null)
                {
                    mt.CastDestination = new Vector2(
                        currentAction.DestinationX,
                        currentAction.DestinationY);
                }

                mt.PrevSkillName = skillName;
                mt.PrevProgress = progress;

                // ── Evaluate dodge need ──
                if (mt.HasCast && !mt.DodgeSignaled && !SafeAnimations.Contains(animName))
                {
                    // Only dodge in the valid window: after dest locks, before damage fires
                    if (progress >= DodgeMinProgress && progress <= DodgeMaxProgress)
                    {
                        var destDist = Vector2.Distance(mt.CastDestination, playerGrid);
                        if (destDist < DodgeTriggerDistance)
                        {
                            // This cast is aimed at us and we're in the dodge window
                            float threatScore = destDist + progress * 10f;
                            if (threatScore < bestThreatScore)
                            {
                                bestThreatScore = threatScore;
                                DodgeDirection = CalcDodgeDirection(mt.GridPos, mt.CastDestination, playerGrid);
                                DodgeUrgent = true;
                                ThreatDestination = mt.CastDestination;
                                ThreatSkillName = skillName;
                                ThreatSource = entity;
                                ThreatProgress = progress;
                                ThreatTimeRemainingMs = timeRemaining;

                                mt.DodgeSignaled = true;
                                DodgesTriggered++;
                            }
                        }
                    }
                }
            }

            // Clean up stale entries
            var staleIds = new List<long>();
            foreach (var kv in _tracked)
            {
                if (kv.Value.Entity == null)
                    staleIds.Add(kv.Key);
            }
            foreach (var id in staleIds)
                _tracked.Remove(id);

            // Status
            int castingCount = 0;
            foreach (var mt in _tracked.Values)
                if (mt.HasCast) castingCount++;

            LastAction = DodgeUrgent
                ? $"DODGE! {ThreatSkillName} prog={ThreatProgress:F2} dest=({ThreatDestination.X:F0},{ThreatDestination.Y:F0})"
                : $"Tracking {_tracked.Count}, {castingCount} casting";
        }

        /// <summary>
        /// Calculate dodge direction: perpendicular to the attack vector,
        /// choosing the side the player is already on (don't cross through the attack).
        /// </summary>
        private static Vector2 CalcDodgeDirection(Vector2 monsterPos, Vector2 castDest, Vector2 playerPos)
        {
            var attackDir = castDest - monsterPos;
            var attackLen = attackDir.Length();

            if (attackLen < 0.01f)
            {
                // Melee on top of us — move away from monster
                var away = playerPos - monsterPos;
                return away.Length() > 0.01f ? Vector2.Normalize(away) : Vector2.UnitX;
            }

            attackDir /= attackLen;
            var perpA = new Vector2(-attackDir.Y, attackDir.X);
            var perpB = new Vector2(attackDir.Y, -attackDir.X);

            // Pick the perpendicular that moves player further from the cast destination
            var playerOffset = playerPos - castDest;
            return Vector2.Dot(playerOffset, perpA) >= 0 ? perpA : perpB;
        }

        public void Reset()
        {
            _tracked.Clear();
            DodgeUrgent = false;
            ThreatSource = null;
            ThreatSkillName = "";
            DodgesTriggered = 0;
            CastsDetected = 0;
            LastAction = "";
        }
    }
}
