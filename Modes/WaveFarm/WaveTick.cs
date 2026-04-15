using System.Numerics;
using AutoExile.Mechanics;
using AutoExile.Modes.Shared;
using AutoExile.Systems;
using ExileCore.PoEMemory.MemoryObjects;

namespace AutoExile.Modes.WaveFarm
{
    /// <summary>
    /// Core tick logic for wave farming. Every tick answers one question:
    /// "What is the single best thing to do right now?"
    ///
    /// Combat is NOT a state — skills fire continuously while the priority list
    /// decides where to move and what to click.
    /// </summary>
    public class WaveTick
    {
        private readonly DirectionTracker _direction = new();
        private readonly DeferredMechanicLog _deferred = new();
        private readonly LootFilter _lootFilter = new();
        private readonly LootPickupTracker _lootTracker = new();
        private readonly ClearPlan _clearPlan = new();
        public ClearPlan ClearPlan => _clearPlan;

        private IFarmPlan _plan = null!;
        private DateTime _lastLootScan = DateTime.MinValue;
        private DateTime _lastMechanicScan = DateTime.MinValue;
        private const int LootScanIntervalMs = 500;
        private const int MechanicScanIntervalMs = 1000;

        // Interactable tracking
        private readonly HashSet<long> _failedInteractables = new();
        private long _pendingInteractableId;
        private int _interactableClickAttempts;

        // Combat engagement lock — once we commit to a pack, finish it
        private bool _engagedInCombat;
        private DateTime _engageStartTime;

        // Dormant approach timeout — if we're close to a dormant entity for too long
        // and it never becomes targetable, tell CombatSystem to ignore that path.
        private DateTime _dormantApproachStart = DateTime.MinValue;
        private string? _dormantApproachPath;
        private const float DormantCloseRange = 10f; // grid — "close enough" threshold
        private const double DormantTimeoutSeconds = 8.0; // seconds before giving up

        // Repath throttle — prevents rapid stop/start of movement key
        private DateTime _lastExplorePath = DateTime.MinValue;

        // Failed explore targets — positions where NavigateTo failed (unreachable).
        // Prevents the bot from repeatedly pathing to the same unreachable position
        // (e.g., stale monsters from wish zones, entities across walls).
        private readonly List<FailedExploreTarget> _failedExploreTargets = new();
        private const float FailedTargetRadius = 30f; // grid — consider nearby targets as "same"
        private const float FailedTargetExpirySeconds = 30f; // forget after this long

        // Action evaluation throttle — re-running the P1..P5 priority list every
        // frame rescans loot candidates and queries ThreatMap twice per tick.
        // We cache the last decision and only re-evaluate at a bounded rate
        // (ActionCooldownMs) or when state materially changes (interaction
        // transition, fresh loot scan, new mechanic). Execute(action) still runs
        // every tick so navigation keeps ticking.
        private WaveAction _cachedAction = WaveAction.None;
        private DateTime _lastEvalTime = DateTime.MinValue;
        private bool _forceReEval = true;

        // Background ThreatMap cleanup — the local Reconcile() only checks chunks within
        // 200g of the player, so distant LeftRange entities can accumulate. A periodic
        // global pass prunes them. ClearPlan's per-chunk stall also handles stale residuals,
        // but this keeps the bot from triggering that path unnecessarily.
        private DateTime _lastFullReconcile = DateTime.MinValue;
        private const double FullReconcileIntervalSeconds = 3.0;

        // Missed valuable loot — items that failed pickup but are worth going back for.
        // Stored as (gridPos, itemName, chaosValue, firstSeen) so we can navigate back.
        private readonly List<MissedLootEntry> _missedLoot = new();
        private const double MissedLootMinValue = 5.0; // chaos — minimum value to remember
        private const float MissedLootMaxAge = 120f; // seconds — forget after this long
        private const int MissedLootMaxAttempts = 2; // drop after this many re-dispatches
        private const float MissedLootBubbleConfirmSeconds = 1.0f; // in-bubble duration before declaring phantom

        // Loot decision logging — tracks why loot wasn't chosen each tick for debugging
        public string LootDebug { get; private set; } = "";

        // Status for overlay
        public string Status { get; private set; } = "";
        public string Decision { get; private set; } = "";
        public DirectionTracker Direction => _direction;
        public DeferredMechanicLog Deferred => _deferred;
        public LootFilter LootMetrics => _lootFilter;
        public LootPickupTracker LootTracker => _lootTracker;

        public void Initialize(IFarmPlan plan)
        {
            _plan = plan;
            Reset();
        }

        public void Reset()
        {
            _direction.Reset();
            _deferred.Reset();
            _lootFilter.Reset();
            _lootTracker.Reset();
            _clearPlan.Reset();
            _failedInteractables.Clear();
            _pendingInteractableId = 0;
            _interactableClickAttempts = 0;
            _engagedInCombat = false;
            _dormantApproachStart = DateTime.MinValue;
            _dormantApproachPath = null;
            _missedLoot.Clear();
            _failedExploreTargets.Clear();
            _lastExplorePath = DateTime.MinValue;
            _lastFullReconcile = DateTime.MinValue;
            _lastLootScan = DateTime.MinValue;
            _lastMechanicScan = DateTime.MinValue;
            _cachedAction = WaveAction.None;
            _lastEvalTime = DateTime.MinValue;
            _forceReEval = true;
            Status = "";
            Decision = "";
        }

        /// <summary>
        /// Called every tick while in-map. Returns true if the map is complete (should exit).
        /// </summary>
        public bool Tick(BotContext ctx)
        {
            using var _tickScope = ctx.Perf.SectionScope("tick.total");
            var gc = ctx.Game;
            var playerPos = new Vector2(gc.Player.GridPosNum.X, gc.Player.GridPosNum.Y);
            _direction.Update(playerPos);
            var config = _plan.Config;

            // Advance the chunk plan — pops cleared/stalled chunks, updates status.
            // Initialization happens in WaveFarmMode on zone entry / sub-zone settle.
            using (ctx.Perf.SectionScope("clearPlan.update"))
                _clearPlan.Update(ctx, playerPos);

            // ── 1. Active mechanic — tick it first (mechanics manage their own combat) ──
            if (ctx.Mechanics.ActiveMechanic != null)
            {
                using var _s = ctx.Perf.SectionScope("mechanic.tickActive");
                var mechResult = ctx.Mechanics.TickActive(ctx);
                if (mechResult == MechanicResult.InProgress)
                {
                    Status = $"Mechanic: {ctx.Mechanics.ActiveMechanic.Name} — {ctx.Mechanics.ActiveMechanic.Status}";
                    Decision = "Mechanic";
                    return false;
                }
                // Mechanic finished — fall through to pick next action
            }

            // ── 1b. Eldritch Altar handler ── (separate from IMapMechanic — uses its
            // own scoring/click logic against ground labels). Without this tick,
            // altars detected in the entity list never get clicked even though the
            // handler exists. AltarTickResult.Busy means we should yield this frame
            // so the handler can finish its current click sequence.
            Mechanics.AltarTickResult altarResult;
            using (ctx.Perf.SectionScope("altar.tick"))
                altarResult = ctx.AltarHandler.Tick(ctx);
            if (altarResult == Mechanics.AltarTickResult.Busy)
            {
                Status = "Eldritch Altar — interacting";
                Decision = "Altar";
                return false;
            }

            // ── 2. Combat fires continuously ──
            // Skipped above when mechanic is active — mechanics call combat.Tick() themselves
            // with their own SuppressPositioning logic (e.g., Ultimatum uses LeashAnchor).
            // When PauseDensity is active, allow combat positioning so close-range builds
            // (RF, melee) can reposition into packs. Otherwise suppress as before.
            // Interaction in flight always wins: combat must not move the character while
            // InteractionSystem is navigating/clicking an item or world entity.
            var hasDensityEngagement = config.PauseDensity > 0 ||
                ctx.Settings.Farming.MinPackDensity.Value > 0;
            if (ctx.Interaction.IsBusy)
                ctx.Combat.SuppressPositioning = true;
            else if (hasDensityEngagement)
                ctx.Combat.SuppressPositioning = false;
            else
                ctx.Combat.SuppressPositioning = config.SuppressCombatPositioning;
            ctx.Combat.SuppressTargetedSkills = ctx.Interaction.IsBusy;
            using (ctx.Perf.SectionScope("combat.tick"))
                ctx.Combat.Tick(ctx);

            // ── 2b. Density-gated pack engagement ──
            // For close-range builds (RF, melee): when a dense pack is nearby,
            // navigate into it instead of continuing exploration. Uses rarity-weighted
            // density: Normal=1, Magic=2, Rare=5, Unique=8. A lone rare (5) triggers
            // at threshold 5 but 2 normals (2) don't.
            var pauseDensity = config.PauseDensity > 0
                ? config.PauseDensity
                : ctx.Settings.Farming.MinPackDensity.Value;

            // Rare/unique detour. Two paths:
            //   a) In-combat fast path — BestTarget is a rare/unique within range.
            //   b) Awareness path — scan the full entity cache for any alive rare/
            //      unique inside MaxDetourDistance, even when we're NOT InCombat.
            //      A map boss or clustered uniques often sit 60–120g away — well
            //      outside CombatRange (20–30g for RF) but still inside the
            //      network bubble. Without this the bot walks past them.
            bool rareDetour = false;
            Vector2? rareDetourTarget = null;
            if (ctx.Settings.Farming.DetourForRares.Value)
            {
                var maxDetour = ctx.Settings.Farming.MaxDetourDistance.Value;

                // (a) in-combat fast path — keep existing tiebreak on BestTarget
                if (ctx.Combat.BestTarget != null && ctx.Combat.InCombat)
                {
                    var rarity = ctx.Combat.BestTarget.Rarity;
                    if (rarity == ExileCore.Shared.Enums.MonsterRarity.Rare ||
                        rarity == ExileCore.Shared.Enums.MonsterRarity.Unique)
                    {
                        var distToTarget = Vector2.Distance(playerPos, ctx.Combat.BestTarget.GridPosNum);
                        if (distToTarget <= maxDetour)
                        {
                            rareDetour = true;
                            rareDetourTarget = ctx.Combat.BestTarget.GridPosNum;
                        }
                    }
                }

                // (b) awareness path — pick the highest-weight rare/unique cluster.
                // Per-monster weight: Unique=25, Rare=10. Clusters beat lone rares.
                // Uses DenseClusterCenter-style weighted centroid within a small
                // bucket around the strongest anchor so we path into the group.
                if (!rareDetour)
                {
                    Entity? bestAnchor = null;
                    float bestScore = 0f;
                    foreach (var m in ctx.Entities.Monsters)
                    {
                        if (!m.IsAlive || !m.IsHostile || !m.IsTargetable) continue;
                        var r = m.Rarity;
                        if (r != ExileCore.Shared.Enums.MonsterRarity.Rare &&
                            r != ExileCore.Shared.Enums.MonsterRarity.Unique) continue;
                        var mg = new Vector2(m.GridPosNum.X, m.GridPosNum.Y);
                        var d = Vector2.Distance(playerPos, mg);
                        if (d > maxDetour) continue;
                        // Weight: rarity minus small distance penalty so ties go to closer.
                        float w = r == ExileCore.Shared.Enums.MonsterRarity.Unique ? 25f : 10f;
                        w -= d * 0.05f;
                        if (w > bestScore) { bestScore = w; bestAnchor = m; }
                    }
                    if (bestAnchor != null)
                    {
                        // Weighted centroid of all rares/uniques within 25g of the anchor —
                        // this lets 3 clustered uniques pull us toward the group's middle,
                        // not an outlier.
                        var anchorPos = new Vector2(bestAnchor.GridPosNum.X, bestAnchor.GridPosNum.Y);
                        Vector2 sum = Vector2.Zero;
                        float wSum = 0f;
                        foreach (var m in ctx.Entities.Monsters)
                        {
                            if (!m.IsAlive || !m.IsHostile || !m.IsTargetable) continue;
                            var r = m.Rarity;
                            if (r != ExileCore.Shared.Enums.MonsterRarity.Rare &&
                                r != ExileCore.Shared.Enums.MonsterRarity.Unique) continue;
                            var mg = new Vector2(m.GridPosNum.X, m.GridPosNum.Y);
                            if (Vector2.Distance(mg, anchorPos) > 25f) continue;
                            float w = r == ExileCore.Shared.Enums.MonsterRarity.Unique ? 25f : 10f;
                            sum += mg * w;
                            wSum += w;
                        }
                        rareDetour = true;
                        rareDetourTarget = wSum > 0 ? sum / wSum : anchorPos;
                    }
                }
            }

            bool denseEnough = pauseDensity > 0 && ctx.Combat.InCombat &&
                ctx.Combat.WeightedDensity >= pauseDensity;

            // Once we commit to a pack, stay engaged until ALL nearby monsters are dead.
            // Without this, the bot disengages when density drops below threshold (e.g. 2
            // mobs remaining from a pack of 10) and runs off to explore, leaving stragglers.
            if (denseEnough || rareDetour)
            {
                if (!_engagedInCombat)
                    _engageStartTime = DateTime.Now;
                _engagedInCombat = true;
            }
            if (_engagedInCombat && !ctx.Combat.InCombat)
                _engagedInCombat = false; // Pack cleared — release

            // Safety: disengage after 10s to prevent infinite lock on unkillable/unreachable mobs
            if (_engagedInCombat && (DateTime.Now - _engageStartTime).TotalSeconds > 10)
                _engagedInCombat = false;

            // Awareness-range detour: rare/unique visible but we're NOT yet InCombat.
            // Walk toward the cluster anchor until combat triggers; the InCombat
            // branch below then takes over with pack targeting. Runs BEFORE the
            // engagedInCombat block so the detour target wins over exploration.
            if (rareDetour && !ctx.Combat.InCombat && rareDetourTarget.HasValue && !ctx.Interaction.IsBusy)
            {
                ctx.Navigation.MoveToward(gc, rareDetourTarget.Value);
                Status = $"Approaching rare/unique cluster ({Vector2.Distance(playerPos, rareDetourTarget.Value):F0}g)";
                Decision = $"RareDetour @ ({rareDetourTarget.Value.X:F0},{rareDetourTarget.Value.Y:F0})";
                return false;
            }

            if (_engagedInCombat && ctx.Combat.InCombat)
            {
                // Scan and pick up loot while fighting — items drop from kills mid-pack
                if ((DateTime.Now - _lastLootScan).TotalMilliseconds >= LootScanIntervalMs)
                {
                    ctx.Loot.Scan(gc);
                    _lastLootScan = DateTime.Now;
                    _forceReEval = true;
                }
                if (!ctx.Interaction.IsBusy && ctx.Loot.HasLootNearby)
                {
                    var (_, candidate) = ctx.Loot.PickupNext(ctx.Interaction, ctx.Navigation);
                    if (candidate != null && ctx.Interaction.IsBusy)
                    {
                        _lootTracker.SetPending(candidate.Entity.Id, candidate.ItemName, candidate.ChaosValue);
                        Status = $"Pack loot: {candidate.ItemName}";
                    }
                }

                // Always move toward the densest cluster — RF needs to be ON TOP of monsters.
                // No distance threshold — continuous tracking of the shifting cluster center.
                if (!ctx.Interaction.IsBusy)
                {
                    var combatTarget = ctx.Combat.DenseClusterCenter;
                    ctx.Navigation.MoveToward(gc, combatTarget);

                    var reason = rareDetour ? "rare/unique detour" : $"pack (w={ctx.Combat.WeightedDensity})";
                    Status = $"Engaging: {reason} ({ctx.Combat.NearbyMonsterCount} monsters)";
                    Decision = $"PackEngage @ ({combatTarget.X:F0},{combatTarget.Y:F0})";
                }
                else
                {
                    Status = $"In pack, looting ({ctx.Combat.NearbyMonsterCount} monsters)";
                    Decision = "InPack+Loot";
                }
                return false;
            }

            // ── 2c. Approach dormant monsters (alive but not yet targetable) ──
            // Map bosses and some encounter enemies need proximity to become targetable.
            // If we're not in combat and there's a dormant monster within combat range, walk toward it.
            // Timeout: if close for too long without activation, add path to ignore list.
            if (!ctx.Combat.InCombat && !_engagedInCombat &&
                ctx.Combat.NearestDormantPos.HasValue &&
                ctx.Combat.NearestDormantDistance < 60f &&
                !ctx.Interaction.IsBusy)
            {
                var dormantPath = ctx.Combat.NearestDormantPath;

                // Log when we first start approaching a new dormant entity
                if (dormantPath != _dormantApproachPath)
                    ctx.Log($"[Wave] Dormant detected: '{dormantPath}' at {ctx.Combat.NearestDormantDistance:F1}g, pos=({ctx.Combat.NearestDormantPos.Value.X:F0},{ctx.Combat.NearestDormantPos.Value.Y:F0})");

                // Track how long we've been close to this dormant entity.
                // Always ensure timer is initialized when close — previous logic had an edge
                // case where _dormantApproachStart stayed at DateTime.MinValue, causing the
                // timeout to never fire (elapsed overflowed to billions of seconds).
                if (ctx.Combat.NearestDormantDistance < DormantCloseRange)
                {
                    // Ensure timer is always set when close (new entity or first close tick)
                    if (_dormantApproachPath != dormantPath || _dormantApproachStart == DateTime.MinValue)
                    {
                        _dormantApproachStart = DateTime.Now;
                        _dormantApproachPath = dormantPath;
                    }

                    var elapsed = (DateTime.Now - _dormantApproachStart).TotalSeconds;
                    if (elapsed > DormantTimeoutSeconds)
                    {
                        // Timed out — this entity never activated. Learn to ignore its path.
                        if (!string.IsNullOrEmpty(dormantPath))
                        {
                            var pathKey = dormantPath.Contains("/Monsters/")
                                ? dormantPath.Substring(dormantPath.IndexOf("/Monsters/") + 10)
                                : dormantPath.Contains("Metadata/")
                                    ? dormantPath.Substring("Metadata/".Length)
                                    : dormantPath;
                            ctx.Combat.IgnoreDormantPath(pathKey);
                            ctx.Log($"[Wave] Dormant timeout: ignoring '{pathKey}' (was at {ctx.Combat.NearestDormantDistance:F0}g for {DormantTimeoutSeconds}s)");
                        }
                        _dormantApproachStart = DateTime.MinValue;
                        _dormantApproachPath = null;
                        // Fall through to EvaluateBestAction — don't approach anymore
                    }
                    else
                    {
                        // Still within timeout — keep approaching
                        ctx.Navigation.MoveToward(gc, ctx.Combat.NearestDormantPos.Value);
                        Status = $"Approaching dormant monster ({ctx.Combat.NearestDormantDistance:F0}g, {elapsed:F0}s/{DormantTimeoutSeconds:F0}s)";
                        Decision = "ApproachDormant";
                        return false;
                    }
                }
                else
                {
                    // Not close yet — walk toward it, reset close-range timer
                    _dormantApproachStart = DateTime.MinValue;
                    _dormantApproachPath = dormantPath;
                    ctx.Navigation.MoveToward(gc, ctx.Combat.NearestDormantPos.Value);
                    Status = $"Approaching dormant monster ({ctx.Combat.NearestDormantDistance:F0}g)";
                    Decision = "ApproachDormant";
                    return false;
                }
            }
            else
            {
                // No dormant nearby — reset tracking
                _dormantApproachStart = DateTime.MinValue;
                _dormantApproachPath = null;
            }

            // ── 3. Interaction system (pending click) ──
            if (ctx.Interaction.IsBusy)
            {
                InteractionResult result;
                using (ctx.Perf.SectionScope("interaction.tick"))
                    result = ctx.Interaction.Tick(gc);
                var hadPending = _lootTracker.HasPending;
                _lootTracker.HandleResult(result, ctx);

                if (result == InteractionResult.Failed)
                {
                    var reason = ctx.Interaction.LastFailReason;
                    var category = hadPending ? "loot" : "interact";
                    ctx.Perf.RecordFailure(category, string.IsNullOrEmpty(reason) ? "(unknown)" : reason);
                }

                if (hadPending && result == InteractionResult.Succeeded)
                {
                    _lootFilter.RecordSuccess();
                    // Remove from missed loot if we went back for it
                    var pickedId = _lootTracker.PendingEntityId;
                    _missedLoot.RemoveAll(e => e.EntityId == pickedId);
                    ctx.Loot.Scan(gc);
                    _lastLootScan = DateTime.Now;
                }
                else if (hadPending && result == InteractionResult.Failed)
                {
                    _lootFilter.RecordFailure();
                    // Remember valuable failed pickups so we can come back
                    if (_lootTracker.PendingChaosValue >= MissedLootMinValue)
                    {
                        var pendingId = _lootTracker.PendingEntityId;
                        var entity = FindEntity(ctx, pendingId);
                        if (entity != null)
                        {
                            _missedLoot.Add(new MissedLootEntry
                            {
                                GridPos = entity.GridPosNum,
                                EntityId = pendingId,
                                ItemName = _lootTracker.PendingItemName ?? "?",
                                ChaosValue = _lootTracker.PendingChaosValue,
                                FirstSeen = DateTime.Now,
                            });
                            ctx.Log($"[Wave] Remembering missed loot: {_lootTracker.PendingItemName} ({_lootTracker.PendingChaosValue:F0}c) at ({entity.GridPosNum.X:F0},{entity.GridPosNum.Y:F0})");
                        }
                    }
                }

                // Handle interactable results
                if (result != InteractionResult.InProgress && result != InteractionResult.None
                    && _pendingInteractableId != 0)
                {
                    if (result == InteractionResult.Failed)
                        _failedInteractables.Add(_pendingInteractableId);
                    _pendingInteractableId = 0;
                }

                if (ctx.Interaction.IsBusy)
                {
                    if (_lootTracker.HasPending)
                        Status = $"Looting: {_lootTracker.PendingItemName}";
                    return false;
                }

                // Interaction just completed — state changed materially, force re-eval
                _forceReEval = true;
            }

            // ── 4. Periodic scans ──
            if ((DateTime.Now - _lastLootScan).TotalMilliseconds >= LootScanIntervalMs)
            {
                using (ctx.Perf.SectionScope("loot.scan"))
                    ctx.Loot.Scan(gc);
                _lastLootScan = DateTime.Now;
                _forceReEval = true;
            }

            // Label toggle unstick
            if (ctx.Loot.TogglePhase != LootSystem.LabelTogglePhase.Idle)
            {
                if (ctx.Loot.TickLabelToggle(gc))
                {
                    Status = $"Label toggle: {ctx.Loot.ToggleStatus}";
                    return false;
                }
                ctx.Loot.Scan(gc);
                _lastLootScan = DateTime.Now;
                _forceReEval = true;
            }

            // Mechanic detection scan
            if ((DateTime.Now - _lastMechanicScan).TotalMilliseconds >= MechanicScanIntervalMs)
            {
                using var _s = ctx.Perf.SectionScope("mechanic.detect");
                var detected = ctx.Mechanics.DetectAndPrioritize(ctx);
                if (detected != null && detected.AnchorGridPos.HasValue)
                {
                    var coverage = ctx.Exploration.ActiveBlobCoverage;
                    if (_deferred.ShouldDefer(detected, detected.AnchorGridPos.Value,
                            playerPos, _direction, _plan, coverage))
                    {
                        // Deferred — suppress it
                        ctx.Mechanics.ForceCompleteActive();
                        _forceReEval = true;
                        ctx.Log($"[Wave] Deferred {detected.Name} at {detected.AnchorGridPos.Value}");
                    }
                    else
                    {
                        // Engage immediately
                        ctx.Mechanics.SetActive(detected);
                        Status = $"Engaging {detected.Name}";
                        Decision = $"Mechanic: {detected.Name}";
                        return false;
                    }
                }
                _lastMechanicScan = DateTime.Now;
            }

            // ── 5. Evaluate best action ──
            // Throttle priority-list evaluation — the decision rarely changes at
            // 60hz, but rescanning all loot candidates and ThreatMap chunks does.
            // Execute() still runs every tick so navigation keeps moving.
            var evalIntervalMs = ctx.Settings.ActionCooldownMs.Value;
            var sinceEval = (DateTime.Now - _lastEvalTime).TotalMilliseconds;
            if (_forceReEval || sinceEval >= evalIntervalMs)
            {
                using (ctx.Perf.SectionScope("evaluate"))
                    _cachedAction = EvaluateBestAction(ctx, playerPos);
                _lastEvalTime = DateTime.Now;
                _forceReEval = false;
                Decision = _cachedAction.Type.ToString();
            }
            using (ctx.Perf.SectionScope("execute"))
                return Execute(_cachedAction, ctx, gc, playerPos);
        }

        private WaveAction EvaluateBestAction(BotContext ctx, Vector2 playerPos)
        {
            var config = _plan.Config;
            var coverage = ctx.Exploration.ActiveBlobCoverage;

            // Prune expired failed explore targets
            PruneFailedExploreTargets();

            // P1: Forward loot
            var forwardLoot = _lootFilter.GetForwardLoot(ctx.Loot, playerPos,
                _direction, config.ForwardAngle);
            if (forwardLoot != null)
            {
                LootDebug = $"P1: {forwardLoot.ItemName} ({forwardLoot.ChaosValue:F0}c, {forwardLoot.Distance:F0}g)";
                return WaveAction.PickupLoot(forwardLoot.Entity.Id, forwardLoot.Entity.GridPosNum);
            }

            // P2: Deferred mechanic ready
            var deferredReady = _deferred.GetReadyMechanic(ctx, playerPos, _plan);
            if (deferredReady.HasValue)
                return WaveAction.EngageMechanic(deferredReady.Value);

            // P3: High-value backtrack loot
            var backtrackLoot = _lootFilter.GetBacktrackLoot(ctx.Loot, playerPos,
                _direction, config.ForwardAngle, config.BacktrackLootThreshold);
            if (backtrackLoot != null)
            {
                LootDebug = $"P3: {backtrackLoot.ItemName} ({backtrackLoot.ChaosValue:F0}c backtrack)";
                return WaveAction.PickupLoot(backtrackLoot.Entity.Id, backtrackLoot.Entity.GridPosNum);
            }

            // Log why loot wasn't chosen when there ARE candidates available
            if (ctx.Loot.HasLootNearby)
            {
                var nearest = ctx.Loot.Candidates[0];
                var dist = Vector2.Distance(playerPos, nearest.Entity.GridPosNum);
                var isAhead = _direction.IsAhead(playerPos, nearest.Entity.GridPosNum, config.ForwardAngle);
                LootDebug = $"SKIP: {ctx.Loot.Candidates.Count} items, nearest={nearest.ItemName} ({nearest.ChaosValue:F0}c, {dist:F0}g, ahead={isAhead}, btThresh={config.BacktrackLootThreshold})";
            }
            else
            {
                LootDebug = $"No candidates (failed={ctx.Loot.FailedCount}, scan={ctx.Loot.LootableCount})";
            }

            // P3b: Missed valuable loot — items we tried to pick up but failed.
            // Navigate back to their last known position so they re-enter scan range.
            //
            // Three kill conditions, in order:
            //   1. Entity no longer valid in cache → drop (original behavior).
            //   2. We've dispatched PickupLoot for this entry >= MissedLootMaxAttempts times
            //      → drop + blacklist via LootSystem.MarkFailed. Stops the
            //      wait-leave-comeback loop when the entity reference is stale.
            //   3. We're within the network bubble of the last-known pos for >= 1s AND
            //      no loot candidate currently exists for that EntityId → the item is
            //      gone from the world even though the cached Entity may still claim
            //      IsValid. Drop + blacklist.
            PruneMissedLoot();
            if (_missedLoot.Count > 0)
            {
                var best = _missedLoot[0]; // highest value first (sorted on insert)
                var entity = FindEntity(ctx, best.EntityId);
                if (entity == null || !entity.IsValid)
                {
                    _missedLoot.RemoveAt(0);
                }
                else if (best.Attempts >= MissedLootMaxAttempts)
                {
                    ctx.Log($"[Wave] Dropping missed loot {best.ItemName} (id={best.EntityId}) after {best.Attempts} failed attempts");
                    ctx.Loot.MarkFailed(best.EntityId, "phantom — max retries");
                    _missedLoot.RemoveAt(0);
                }
                else
                {
                    float distToMissed = Vector2.Distance(playerPos, best.GridPos);
                    bool inBubble = distToMissed <= Pathfinding.NetworkBubbleRadius;

                    if (inBubble && best.InBubbleSince == DateTime.MinValue)
                    {
                        best.InBubbleSince = DateTime.Now;
                        _missedLoot[0] = best;
                    }
                    else if (!inBubble)
                    {
                        best.InBubbleSince = DateTime.MinValue;
                        _missedLoot[0] = best;
                    }

                    bool inBubbleLongEnough = best.InBubbleSince != DateTime.MinValue &&
                        (DateTime.Now - best.InBubbleSince).TotalSeconds >= MissedLootBubbleConfirmSeconds;

                    if (inBubbleLongEnough && !LootCandidateExists(ctx, best.EntityId))
                    {
                        ctx.Log($"[Wave] Dropping missed loot {best.ItemName} (id={best.EntityId}) — in bubble {MissedLootBubbleConfirmSeconds:F1}s with no label, item gone");
                        ctx.Loot.MarkFailed(best.EntityId, "phantom — in bubble, no label");
                        _missedLoot.RemoveAt(0);
                    }
                    else
                    {
                        // Count this tick's dispatch as an attempt. Actual pickup
                        // may fail/succeed; the outer interaction-result handler
                        // will clear this entry on success or let us loop to the
                        // retry cap on continued failure.
                        best.Attempts++;
                        _missedLoot[0] = best;
                        return WaveAction.PickupLoot(best.EntityId, best.GridPos);
                    }
                }
            }

            // Periodic ThreatMap cleanup — local Reconcile() only sweeps within 200g of the
            // player, so distant LeftRange entities can accumulate. A periodic global pass
            // keeps alive counts honest, which matters for ClearPlan's per-chunk clearing check.
            if (ctx.ThreatMap.IsInitialized &&
                (DateTime.Now - _lastFullReconcile).TotalSeconds > FullReconcileIntervalSeconds)
            {
                int beforeAlive = ctx.ThreatMap.TotalAlive;
                using (ctx.Perf.SectionScope("threatmap.reconcileAll"))
                    ctx.ThreatMap.ReconcileAll(ctx.Entities);
                int afterAlive = ctx.ThreatMap.TotalAlive;
                if (beforeAlive != afterAlive)
                    ctx.Log($"[Wave] Background reconcile: alive {beforeAlive} -> {afterAlive}");
                _lastFullReconcile = DateTime.Now;
            }

            // P4 — Chunk-queue exploration. ClearPlan owns "where to go next": it commits to
            // one chunk at a time, waits for it to clear (or stall out), then pops. Combat,
            // loot, and mechanics ran as interrupts above this. When the queue empties,
            // IsComplete fires and we fall straight through to P5/P7.
            if (_clearPlan.IsInitialized && _clearPlan.CurrentTarget.HasValue)
            {
                var chunkTarget = _clearPlan.CurrentTarget.Value;
                if (!IsFailedExploreTarget(chunkTarget))
                    return WaveAction.Explore(chunkTarget);
                // Nav refused this chunk — pop it and try again next tick.
                _clearPlan.SkipCurrent(ctx);
            }

            // Dormant map-boss approach. ThreatMap doesn't track non-hostile-alive entities,
            // so the ClearPlan won't route us to a dormant boss — handle it explicitly.
            if (ctx.Combat.NearestDormantPos.HasValue &&
                !IsFailedExploreTarget(ctx.Combat.NearestDormantPos.Value))
                return WaveAction.Explore(ctx.Combat.NearestDormantPos.Value);

            // Terrain fallback — only fires if ClearPlan failed to initialize (no active
            // blob at zone entry). Picks nearest unexplored walkable cell.
            if (!_clearPlan.IsInitialized)
            {
                var terrainTarget = ctx.Exploration.GetNextExplorationTarget(playerPos);
                if (terrainTarget.HasValue && !IsFailedExploreTarget(terrainTarget.Value))
                    return WaveAction.Explore(terrainTarget.Value);
            }

            // P4b: Post-observation hunt. Every region was observed but stragglers
            // outside combat range didn't get engaged. Chase the densest remaining
            // ThreatMap chunk — the pack-engagement lock + target-focus timeout
            // in CombatSystem keep each chase bounded, so this can't loop.
            // Stops when ThreatMap has no alive chunks OR every remaining cluster
            // sits in a failed-target zone.
            if (_clearPlan.IsInitialized && _clearPlan.IsComplete && ctx.ThreatMap.IsInitialized)
            {
                var dense = ctx.ThreatMap.GetDensestAliveChunk(playerPos);
                if (dense.HasValue && !IsFailedExploreTarget(dense.Value))
                    return WaveAction.Explore(dense.Value);
            }

            // P5: Post-clear plan actions (ritual shop, wish collection, etc.)
            var planAction = _plan.GetPostClearAction(ctx, _deferred);
            if (planAction.HasValue)
                return planAction.Value;

            // P6: Label toggle for hidden items
            if (!ctx.Loot.HasLootNearby && ctx.Loot.ShouldToggleLabels(ctx.Game))
            {
                ctx.Loot.StartLabelToggle(ctx.Game);
                return WaveAction.None;
            }

            // P7: Done
            return WaveAction.ExitMap;
        }

        /// <summary>Execute the chosen action. Returns true if map is done (ExitMap).</summary>
        private bool Execute(WaveAction action, BotContext ctx, ExileCore.GameController gc, Vector2 playerPos)
        {
            switch (action.Type)
            {
                case WaveActionType.Explore:
                    // Navigate to exploration target — only repath if target changed significantly
                    // AND enough time has passed. Frequent repaths cause movement key spam
                    // (stop+start each time) which can trigger anti-cheat disconnects.
                    var currentDest = ctx.Navigation.Destination ?? Vector2.Zero;
                    var distToNewTarget = Vector2.Distance(currentDest, action.TargetGridPos);
                    var timeSinceRepath = (DateTime.Now - _lastExplorePath).TotalMilliseconds;

                    if (!ctx.Navigation.IsNavigating ||
                        (distToNewTarget > 30f && timeSinceRepath > 500))
                    {
                        bool pathOk = ctx.Navigation.NavigateTo(gc, action.TargetGridPos);
                        _lastExplorePath = DateTime.Now;

                        if (!pathOk)
                        {
                            // Pathfinding failed — target is unreachable. Mark it so we don't
                            // keep retrying the same position every tick.
                            AddFailedExploreTarget(action.TargetGridPos);
                            ctx.Perf.RecordFailure("explore", "no path");
                            ctx.Log($"[Wave] Explore target unreachable ({action.TargetGridPos.X:F0},{action.TargetGridPos.Y:F0}) — {_failedExploreTargets.Count} failed targets");

                            // Immediate fallback: try standard exploration (terrain-based, always reachable)
                            var fallback = ctx.Exploration.GetNextExplorationTarget(playerPos);
                            if (fallback.HasValue)
                                ctx.Navigation.NavigateTo(gc, fallback.Value);
                        }
                    }
                    ctx.Navigation.Tick(gc);
                    Status = $"Exploring ({ctx.Exploration.ActiveBlobCoverage:P0})";
                    return false;

                case WaveActionType.PickupLoot:
                    // Navigate toward item, then InteractionSystem picks it up
                    _lootFilter.RecordAttempt();
                    var lootEntity = FindEntity(ctx, action.TargetEntityId);
                    if (lootEntity != null)
                    {
                        var candidate = FindCandidate(ctx.Loot, action.TargetEntityId);
                        ctx.Interaction.PickupGroundItem(lootEntity, ctx.Navigation,
                            requireProximity: lootEntity.DistancePlayer > ctx.Interaction.InteractRadius);
                        if (candidate != null)
                            _lootTracker.SetPending(candidate.Entity.Id, candidate.ItemName, candidate.ChaosValue);
                        Status = $"Picking up: {candidate?.ItemName ?? "item"}";
                    }
                    return false;

                case WaveActionType.Interact:
                    var interactEntity = FindEntity(ctx, action.TargetEntityId);
                    if (interactEntity != null)
                    {
                        ctx.Interaction.InteractWithEntity(interactEntity, ctx.Navigation);
                        _pendingInteractableId = action.TargetEntityId;
                        _interactableClickAttempts = 0;
                        Status = $"Interacting: {interactEntity.RenderName}";
                    }
                    return false;

                case WaveActionType.EngageMechanic:
                    if (action.MechanicEntry.HasValue)
                    {
                        var entry = action.MechanicEntry.Value;
                        _deferred.MarkEngaged(entry.Mechanic);

                        // Navigate to mechanic position — detection will re-trigger on arrival
                        var dist = Vector2.Distance(playerPos, entry.GridPos);
                        if (dist > 30f)
                        {
                            ctx.Navigation.NavigateTo(gc, entry.GridPos);
                            ctx.Navigation.Tick(gc);
                            Status = $"Moving to deferred {entry.Mechanic.Name}";
                        }
                        else
                        {
                            // Close enough — let mechanic detection pick it up next scan
                            _lastMechanicScan = DateTime.MinValue; // force immediate rescan
                        }
                    }
                    return false;

                case WaveActionType.NavigateToMechanic:
                    ctx.Navigation.NavigateTo(gc, action.TargetGridPos);
                    ctx.Navigation.Tick(gc);
                    Status = "Navigating to mechanic";
                    return false;

                case WaveActionType.ExitMap:
                    Status = "Map complete";
                    return true;

                case WaveActionType.None:
                default:
                    // Tick navigation if we're already moving
                    if (ctx.Navigation.IsNavigating)
                        ctx.Navigation.Tick(gc);
                    return false;
            }
        }

        private static ExileCore.PoEMemory.MemoryObjects.Entity? FindEntity(
            BotContext ctx, long entityId)
        {
            // O(1) lookup via entity cache
            return ctx.Entities.Get(entityId);
        }

        /// <summary>True if LootSystem currently reports a visible candidate for the
        /// given entity id. Used by the missed-loot pass to drop phantom entries
        /// when the entity is supposedly cached-live but no label exists anywhere.</summary>
        private static bool LootCandidateExists(BotContext ctx, long entityId)
        {
            foreach (var c in ctx.Loot.Candidates)
                if (c.Entity.Id == entityId) return true;
            return false;
        }

        private static LootCandidate? FindCandidate(LootSystem loot, long entityId)
        {
            foreach (var c in loot.Candidates)
            {
                if (c.Entity.Id == entityId) return c;
            }
            return null;
        }

        /// <summary>Remove expired or picked-up entries from missed loot memory.</summary>
        private void PruneMissedLoot()
        {
            var now = DateTime.Now;
            _missedLoot.RemoveAll(e =>
                (now - e.FirstSeen).TotalSeconds > MissedLootMaxAge);
        }

        /// <summary>Check if a position is near any recently failed explore target.</summary>
        private bool IsFailedExploreTarget(Vector2 pos)
        {
            foreach (var f in _failedExploreTargets)
            {
                if (Vector2.Distance(pos, f.GridPos) < FailedTargetRadius)
                    return true;
            }
            return false;
        }

        /// <summary>Add a position to the failed explore targets list.</summary>
        private void AddFailedExploreTarget(Vector2 pos)
        {
            // Don't add duplicates
            if (!IsFailedExploreTarget(pos))
            {
                _failedExploreTargets.Add(new FailedExploreTarget
                {
                    GridPos = pos,
                    FailedAt = DateTime.Now,
                });
            }
        }

        /// <summary>Remove expired failed explore targets.</summary>
        private void PruneFailedExploreTargets()
        {
            var now = DateTime.Now;
            _failedExploreTargets.RemoveAll(f =>
                (now - f.FailedAt).TotalSeconds > FailedTargetExpirySeconds);
        }
    }

    internal struct FailedExploreTarget
    {
        public Vector2 GridPos;
        public DateTime FailedAt;
    }

    internal struct MissedLootEntry
    {
        public Vector2 GridPos;
        public long EntityId;
        public string ItemName;
        public double ChaosValue;
        public DateTime FirstSeen;
        public int Attempts;      // times we've dispatched P3b for this entry
        public DateTime InBubbleSince; // first tick we were within network bubble of GridPos; MinValue = never
    }
}
