using ExileCore;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using AutoExile.Systems;
using AutoExile.Modes.Shared;
using System.Numerics;

namespace AutoExile.Modes
{
    /// <summary>
    /// Simulacrum farming loop:
    /// Hideout: stash items → insert simulacrum fragment → enter portal
    /// In map: find monolith → wave cycle (fight/loot/stash between waves) → exit after wave 15 or abort
    /// Death: revive (handled by BotCore) → re-enter map if portals remain
    /// </summary>
    public class SimulacrumMode : IBotMode
    {
        public string Name => "Simulacrum";

        private SimulacrumState _state = new();
        private SimPhase _phase = SimPhase.Idle;
        private DateTime _phaseStartTime = DateTime.Now;
        private DateTime _lastActionTime = DateTime.MinValue;

        // Settings reference
        private BotSettings.SimulacrumSettings _settings = new();

        // Hideout/loop tracking
        private bool _mapCompleted;
        private string _lastAreaName = "";

        // Loot tracking — only record on confirmed pickup
        private DateTime _lastLootScan = DateTime.MinValue;
        private const float LootScanIntervalMs = 500;
        private readonly LootPickupTracker _lootTracker = new();

        // Hideout flow
        private readonly HideoutFlow _hideoutFlow = new();

        // Between-wave stash tracking
        private bool _isStashing;

        // Wave transition tracking — reset exploration seen state each wave so we re-sweep for new spawns
        private int _lastKnownWave;
        // Track whether we were searching (no monsters) last tick — reset exploration when
        // transitioning from searching → combat, so the next search re-sweeps the whole map
        private bool _wasSearching;

        // Wave start retry tracking — bail if we can't start the next wave
        private int _waveStartAttempts;
        private const int MaxWaveStartAttempts = 10;
        private DateTime _betweenWaveStartTime = DateTime.MinValue;
        private const float BetweenWaveTimeoutSeconds = 120f;

        // Combat stuck detection — if fighting same monsters too long, move on
        private DateTime _combatEngageTime = DateTime.MinValue;
        private int _combatEngageCount;
        private const float CombatStuckSeconds = 15f;

        // Monster blacklist — temporarily ignore monsters we can't kill so we reposition via explore
        private readonly Dictionary<long, DateTime> _blacklistedMonsters = new();
        private const float MonsterBlacklistSeconds = 10f;


        // Action cooldown
        private const float MajorActionCooldownMs = 500f;

        // Public for ImGui display
        public SimulacrumState State => _state;
        public SimPhase Phase => _phase;
        public string StatusText { get; private set; } = "";
        public string Decision { get; private set; } = "";

        public void OnEnter(BotContext ctx)
        {
            _settings = ctx.Settings.Simulacrum;
            _mapCompleted = false;
            _lastAreaName = "";
            _isStashing = false;
            _lootTracker.Reset();
            _lastKnownWave = 0;
            _wasSearching = false;
            _waveStartAttempts = 0;
            _betweenWaveStartTime = DateTime.MinValue;

            _combatEngageTime = DateTime.MinValue;
            _combatEngageCount = 0;
            _blacklistedMonsters.Clear();

            // Enable combat
            ModeHelpers.EnableDefaultCombat(ctx);

            // Determine starting phase based on location
            var gc = ctx.Game;
            if (gc.Area.CurrentArea.IsHideout || gc.Area.CurrentArea.IsTown)
            {
                _phase = SimPhase.InHideout;
                _phaseStartTime = DateTime.Now;
                StatusText = "In hideout — preparing";
            }
            else
            {
                // Already in a map — try to find monolith
                _state.Reset();
                _phase = SimPhase.FindMonolith;
                _phaseStartTime = DateTime.Now;
                StatusText = "In map — finding monolith";

                // Initialize exploration if BotCore missed it (plugin reload mid-game)
                if (!ctx.Exploration.IsInitialized)
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
            }
        }

        public void OnExit()
        {
            _state.Reset();
            _phase = SimPhase.Idle;
            _isStashing = false;
        }

        public void Tick(BotContext ctx)
        {
            var gc = ctx.Game;

            // Detect area changes
            var currentArea = gc.Area?.CurrentArea?.Name ?? "";
            if (!string.IsNullOrEmpty(currentArea) && currentArea != _lastAreaName)
            {
                OnAreaChanged(ctx, currentArea);
                _lastAreaName = currentArea;
            }

            // Always tick state when in map; combat only during active phases
            bool inMap = gc.Area?.CurrentArea != null &&
                         !gc.Area.CurrentArea.IsHideout &&
                         !gc.Area.CurrentArea.IsTown;
            if (inMap)
            {
                _state.Tick(gc, _settings.MinWaveDelaySeconds.Value);

                // Disable combat during LootSweep/ExitMap — we need to navigate freely
                // to pick up remaining items and reach the portal without being dragged into fights
                bool combatAllowed = _phase != SimPhase.LootSweep && _phase != SimPhase.ExitMap;
                if (combatAllowed)
                {
                    // Suppress cursor-moving skills when interaction is busy picking up loot
                    ctx.Combat.SuppressPositioning = ctx.Interaction.IsBusy;
                    ctx.Combat.SuppressTargetedSkills = ctx.Interaction.IsBusy;
                    ctx.Combat.Tick(ctx);
                }
            }

            // Tick interaction system
            var interactionResult = ctx.Interaction.Tick(gc);

            switch (_phase)
            {
                // --- Hideout phases ---
                case SimPhase.InHideout:
                case SimPhase.StashItems:
                case SimPhase.OpenMap:
                case SimPhase.EnterPortal:
                    var signal = _hideoutFlow.Tick(ctx);
                    StatusText = _hideoutFlow.Status;
                    if (signal == HideoutSignal.PortalTimeout)
                    {
                        _state.Reset();
                        _phase = SimPhase.InHideout;
                        _phaseStartTime = DateTime.Now;
                        _hideoutFlow.Start(MapDeviceSystem.IsSimulacrum);
                        StatusText = "No portal found — starting new run";
                    }
                    break;

                // --- Map phases ---
                case SimPhase.FindMonolith:
                    TickFindMonolith(ctx);
                    break;
                case SimPhase.NavigateToMonolith:
                    TickNavigateToMonolith(ctx);
                    break;
                case SimPhase.WaveCycle:
                    TickWaveCycle(ctx, interactionResult);
                    break;
                case SimPhase.BetweenWaveStash:
                    TickBetweenWaveStash(ctx, interactionResult);
                    break;
                case SimPhase.LootSweep:
                    TickLootSweep(ctx, interactionResult);
                    break;
                case SimPhase.ExitMap:
                    TickExitMap(ctx);
                    break;
                case SimPhase.Done:
                    StatusText = "Simulacrum complete";
                    break;
                case SimPhase.Idle:
                    StatusText = "Idle";
                    break;
            }
        }

        // =================================================================
        // Area change detection
        // =================================================================

        private void OnAreaChanged(BotContext ctx, string newArea)
        {
            var gc = ctx.Game;

            // Cancel any in-flight systems
            ModeHelpers.CancelAllSystems(ctx);
            _hideoutFlow.Cancel();
            _isStashing = false;

            if (gc.Area.CurrentArea.IsHideout || gc.Area.CurrentArea.IsTown)
            {
                if (_mapCompleted)
                {
                    // Map completed — start new cycle
                    _state.RecordRunComplete();
                    _state.Reset();
                    _phase = SimPhase.InHideout;
                    _phaseStartTime = DateTime.Now;
                    _mapCompleted = false;
                    _lootTracker.ResetCount();
                    _hideoutFlow.Start(MapDeviceSystem.IsSimulacrum);
                    StatusText = "Back in hideout — starting new run";
                }
                else if (_state.DeathCount > 0 && _state.DeathCount < _settings.MaxDeaths.Value)
                {
                    // Died — try to re-enter
                    _phase = SimPhase.EnterPortal;
                    _phaseStartTime = DateTime.Now;
                    _hideoutFlow.StartPortalReentry();
                    StatusText = $"Revived (death {_state.DeathCount}) — re-entering map";
                }
                else if (_state.DeathCount >= _settings.MaxDeaths.Value)
                {
                    // Too many deaths — start fresh
                    _state.RecordRunComplete();
                    _state.Reset();
                    _phase = SimPhase.InHideout;
                    _phaseStartTime = DateTime.Now;
                    _lootTracker.ResetCount();
                    _hideoutFlow.Start(MapDeviceSystem.IsSimulacrum);
                    StatusText = "Too many deaths — starting new run";
                }
                else
                {
                    _phase = SimPhase.InHideout;
                    _phaseStartTime = DateTime.Now;
                    _hideoutFlow.Start(MapDeviceSystem.IsSimulacrum);
                }
            }
            else
            {
                // Entered map
                var deathCount = _state.DeathCount;
                _state.OnAreaChanged();
                _state.DeathCount = deathCount;
                _phase = SimPhase.FindMonolith;
                _phaseStartTime = DateTime.Now;
    
                _lootTracker.ResetCount();
                StatusText = "Entered map — finding monolith";
            }
        }

        // =================================================================
        // Map phases
        // =================================================================

        private void TickFindMonolith(BotContext ctx)
        {
            if (_state.MonolithPosition.HasValue)
            {
                _phase = SimPhase.NavigateToMonolith;
                _phaseStartTime = DateTime.Now;
                StatusText = "Monolith found — navigating";
                return;
            }

            var gc = ctx.Game;
            var elapsed = (DateTime.Now - _phaseStartTime).TotalSeconds;

            // Wait 2s for entity list to settle after zone load
            if (elapsed < 2)
            {
                StatusText = "Searching for monolith...";
                return;
            }

            // Explore the map until the monolith entity enters the network bubble.
            // Previous approach used hardcoded map center coordinates, but these are
            // fragile (can land on unwalkable terrain, wrong for map variants).
            if (ctx.Exploration.IsInitialized)
            {
                ctx.Exploration.Update(gc.Player.GridPosNum);
                var playerPos = gc.Player.GridPosNum;

                if (!ctx.Navigation.IsNavigating)
                {
                    var target = ctx.Exploration.GetNextExplorationTarget(playerPos);
                    if (target.HasValue)
                    {
                        ctx.Navigation.NavigateTo(gc,
                            target.Value);
                    }
                }
            }

            StatusText = "Exploring to find monolith...";

            if (elapsed > 60)
            {
                StatusText = "No monolith found — timeout";
                _phase = SimPhase.Done;
            }
        }

        private void TickNavigateToMonolith(BotContext ctx)
        {
            if (!_state.MonolithPosition.HasValue)
            {
                _phase = SimPhase.FindMonolith;
                return;
            }

            // If wave is already active (re-entry after death), go straight to wave cycle
            if (_state.IsWaveActive)
            {
                ctx.Navigation.Stop(ctx.Game);
                _phase = SimPhase.WaveCycle;
                _phaseStartTime = DateTime.Now;
                StatusText = "Wave already active — joining combat";
                return;
            }

            var playerPos = ctx.Game.Player.GridPosNum;
            var dist = Vector2.Distance(playerPos, _state.MonolithPosition.Value);

            if (dist < 18f)
            {
                ctx.Navigation.Stop(ctx.Game);
                _phase = SimPhase.WaveCycle;
                _phaseStartTime = DateTime.Now;
                StatusText = "Near monolith — entering wave cycle";
                return;
            }

            if (!ctx.Navigation.IsNavigating)
            {
                var success = ctx.Navigation.NavigateTo(ctx.Game,
                    _state.MonolithPosition.Value);
                if (!success)
                {
                    StatusText = "No path to monolith";
                    _phase = SimPhase.Done;
                    return;
                }
            }

            StatusText = $"Navigating to monolith (dist: {dist:F0})";
        }

        // =================================================================
        // Wave cycle — the main decision loop
        // =================================================================

        private void TickWaveCycle(BotContext ctx, InteractionResult interactionResult)
        {
            var gc = ctx.Game;
            var playerPos = gc.Player.GridPosNum;

            // Handle pending loot pickup results
            _lootTracker.HandleResult(interactionResult, ctx);

            // --- Wave transition: reset exploration so we re-sweep for new spawns ---
            if (_state.CurrentWave != _lastKnownWave)
            {
                _lastKnownWave = _state.CurrentWave;
                ctx.Exploration.SeenRadiusOverride = 0; // restore normal radius for new wave
                ctx.Exploration.ResetSeen();
                ctx.Loot.ClearFailed(); // items that failed in earlier waves may be pickable now
                _blacklistedMonsters.Clear(); // new wave = fresh monster spawns
                _wasSearching = false;
                _waveStartAttempts = 0;
                _betweenWaveStartTime = DateTime.MinValue;
            }

            // --- Priority 0: Don't interrupt active loot pickup ---
            // If interaction is busy (navigating to or clicking an item), let it finish.
            // Without this guard, exploration/combat navigation overwrites the loot path.
            if (ctx.Interaction.IsBusy && _lootTracker.HasPending)
            {
                Decision = $"Loot pickup in progress: {_lootTracker.PendingItemName}";
                StatusText = $"Picking up {_lootTracker.PendingItemName}";
                return;
            }

            // --- Priority 1: Pick up nearby loot (during active waves only) ---
            // Between waves, loot is handled exclusively by Priority 4 which blocks
            // all lower priorities until loot is fully cleared.
            if (_state.IsWaveActive)
            {
                if ((DateTime.Now - _lastLootScan).TotalMilliseconds >= LootScanIntervalMs)
                {
                    ctx.Loot.Scan(gc);
                    _lastLootScan = DateTime.Now;
                }

                if (ctx.Loot.HasLootNearby && !ctx.Interaction.IsBusy)
                {
                    var (wasInRadius, candidate) = ctx.Loot.PickupNext(ctx.Interaction, ctx.Navigation);
                    if (candidate != null && ctx.Interaction.IsBusy)
                    {
                        _lootTracker.SetPending(candidate.Entity.Id, candidate.ItemName, candidate.ChaosValue);
                        Decision = $"Loot: {candidate.ItemName}";
                        StatusText = $"Picking up {candidate.ItemName}";
                        return;
                    }
                }
            }

            // --- Priority 2: Wave timeout check ---
            // Sweep loot before exiting — wave timeout shouldn't abandon items on the ground
            if (_state.IsWaveActive &&
                (DateTime.Now - _state.WaveStartedAt).TotalMinutes > _settings.WaveTimeoutMinutes.Value)
            {
                Decision = "Wave timeout → LootSweep";
                _phase = SimPhase.LootSweep;
                _phaseStartTime = DateTime.Now;
                _sweepNearMonolith = false;
                _lastEmptyScanAt = DateTime.MinValue;
                StatusText = $"Wave {_state.CurrentWave} timed out — sweeping loot before exit";
                return;
            }

            // --- Priority 3: Wave active — fight and explore ---
            if (_state.IsWaveActive)
            {
                // NearbyMonsterCount = within CombatRange — monsters close enough to fight
                if (ctx.Combat.NearbyMonsterCount > 0)
                {
                    // Combat stuck detection: if monster count isn't decreasing, we're
                    // probably fighting unreachable/unkillable monsters — move on
                    if (_combatEngageTime == DateTime.MinValue || ctx.Combat.NearbyMonsterCount < _combatEngageCount)
                    {
                        // First engagement or making progress — reset timer
                        _combatEngageTime = DateTime.Now;
                        _combatEngageCount = ctx.Combat.NearbyMonsterCount;
                    }

                    var combatElapsed = (DateTime.Now - _combatEngageTime).TotalSeconds;
                    if (combatElapsed > CombatStuckSeconds)
                    {
                        // Stuck fighting same monsters too long — blacklist nearby monsters and explore elsewhere
                        _combatEngageTime = DateTime.MinValue;
                        _combatEngageCount = 0;
                        BlacklistNearbyMonsters(gc, gc.Player.GridPosNum, ctx.Settings.Build.CombatRange.Value);
                        ctx.Navigation.Stop(gc);
                        if (!_wasSearching)
                        {
                            _wasSearching = true;
                            ctx.Exploration.SeenRadiusOverride = 40;
                            ctx.Exploration.ResetSeen();
                        }
                        Decision = $"Wave {_state.CurrentWave} — combat stuck ({combatElapsed:F0}s), blacklisted {_blacklistedMonsters.Count} monsters";
                        TickExploreForMonsters(ctx);
                    }
                    else
                    {
                        if (_wasSearching)
                        {
                            _wasSearching = false;
                            // Stop stale navigation from patrolling — combat handles movement now
                            ctx.Navigation.Stop(gc);
                            // Restore normal seen radius now that we're fighting
                            ctx.Exploration.SeenRadiusOverride = 0;
                        }

                        // Aggressive positioning: CombatSystem signals WantsToMove with a dense
                        // cluster target, but defers A* pathfinding to the mode. Without this,
                        // the bot stands still fighting a few nearby monsters while ignoring
                        // a much denser pack farther away.
                        if (ctx.Combat.WantsToMove &&
                            ctx.Combat.Profile.Positioning == CombatPositioning.Aggressive &&
                            !ctx.Interaction.IsBusy)
                        {
                            var combatTarget = ctx.Combat.MoveTargetGrid;
                            // Only repath if not navigating or current destination is far from new target
                            var navPath = ctx.Navigation.CurrentNavPath;
                            var currentDest = navPath.Count > 0 ? navPath[navPath.Count - 1].Position : playerPos;
                            if (!ctx.Navigation.IsNavigating ||
                                Vector2.Distance(currentDest, combatTarget) > 20f)
                            {
                                ctx.Navigation.Stop(gc);
                                ctx.Navigation.NavigateTo(gc, combatTarget);
                            }
                            Decision = $"Wave {_state.CurrentWave} — aggressive: pathing to density @ ({combatTarget.X:F0},{combatTarget.Y:F0})";
                        }
                        else
                        {
                            Decision = $"Wave {_state.CurrentWave} — fighting ({ctx.Combat.NearbyMonsterCount} nearby, {ctx.Combat.CachedMonsterCount} total)";
                        }
                        StatusText = $"Wave {_state.CurrentWave}/15 — fighting {ctx.Combat.NearbyMonsterCount} monsters";
                    }
                }
                else
                {
                    // Transition from fighting → searching: reset exploration and use small
                    // seen radius so the bot must physically visit each region. Simulacrum maps
                    // are tiny (~15K cells) — the default network bubble (radius 180) covers
                    // the entire map, making exploration targets useless without this.
                    if (!_wasSearching)
                    {
                        _wasSearching = true;
                        ctx.Exploration.SeenRadiusOverride = 40;
                        ctx.Exploration.ResetSeen();
                    }
                    _combatEngageTime = DateTime.MinValue;
                    _combatEngageCount = 0;

                    Decision = $"Wave {_state.CurrentWave} — patrolling ({ctx.Combat.CachedMonsterCount} distant)";
                    TickExploreForMonsters(ctx);
                }
                return;
            }

            // --- Between waves ---

            // Priority 4: Loot must be fully cleared before anything else between waves.
            // Any visible loot (not blacklisted) resets the wave delay timer — we keep
            // looting until everything is picked up or blacklisted, then wait the full
            // delay for more drops before starting the next wave.
            // Also blocks if interaction is busy (mid-pickup) — stay at spawn zone, don't
            // wander to monolith.
            if (!_state.IsWaveActive)
            {
                // Force a fresh scan every tick between waves (loot can drop at any time)
                ctx.Loot.Scan(gc);
                _lastLootScan = DateTime.Now;

                bool hasLoot = ctx.Loot.HasLootNearby;
                bool pickingUp = ctx.Interaction.IsBusy && _lootTracker.HasPending;

                if (hasLoot || pickingUp)
                {
                    if (hasLoot)
                    {
                        // Loot exists — reset wave delay (items may still be dropping)
                        _state.ResetWaveDelay(_settings.MinWaveDelaySeconds.Value);
                    }

                    if (hasLoot && !ctx.Interaction.IsBusy)
                    {
                        var (wasInRadius, candidate) = ctx.Loot.PickupNext(ctx.Interaction, ctx.Navigation);
                        if (candidate != null && ctx.Interaction.IsBusy)
                        {
                            _lootTracker.SetPending(candidate.Entity.Id, candidate.ItemName, candidate.ChaosValue);
                            Decision = $"Between waves — loot: {candidate.ItemName}";
                            StatusText = $"Picking up {candidate.ItemName} (between waves)";
                            return;
                        }
                    }

                    // Either picking up or waiting — stay near spawn zones, don't wander to monolith
                    if (!ctx.Interaction.IsBusy)
                        IdleNearMonolith(ctx);
                    Decision = pickingUp ? "Between waves — picking up loot" : "Between waves — clearing loot";
                    StatusText = pickingUp ? $"Picking up loot (between waves)" : "Loot nearby — clearing before next wave";
                    return;
                }
            }

            // Priority 5: Stash items if inventory above threshold
            // Don't start StashSystem here — TickBetweenWaveStash navigates to the
            // cached stash position first so the entity loads into the entity list.
            if (_state.StashPosition.HasValue && !ctx.Interaction.IsBusy)
            {
                var invCount = (StashSystem.GetInventorySlotItems(gc)?.Count ?? 0);
                bool shouldStartStashing = invCount >= _settings.StashItemThreshold.Value;
                bool shouldContinueStashing = _isStashing && invCount > 0;

                if (shouldStartStashing || shouldContinueStashing)
                {
                    _isStashing = true;
                    Decision = $"Between waves → Stash ({invCount} items)";
                    _phase = SimPhase.BetweenWaveStash;
                    _phaseStartTime = DateTime.Now;
                    StatusText = $"Stashing items ({invCount} in inventory)";
                    return;
                }
                _isStashing = false;
            }

            // Priority 6: Wave 15 complete — sweep remaining loot and exit
            if (_state.CurrentWave >= 15 && !_state.IsWaveActive)
            {
                Decision = "Wave 15 complete → LootSweep";
                _phase = SimPhase.LootSweep;
                _phaseStartTime = DateTime.Now;
                _sweepNearMonolith = false;
                _lastEmptyScanAt = DateTime.MinValue;
                StatusText = "Wave 15 complete — sweeping loot";
                return;
            }

            // Priority 7: Start next wave (loot is clear AND delay has passed)
            // If delay was never set (fresh start / MinValue), enforce it now so we
            // get at least one full delay period to scan for loot before starting
            if (_state.CanStartWaveAt == DateTime.MinValue)
            {
                _state.ResetWaveDelay(_settings.MinWaveDelaySeconds.Value);
            }

            // Track how long we've been between waves — bail if stuck too long
            if (_betweenWaveStartTime == DateTime.MinValue)
                _betweenWaveStartTime = DateTime.Now;
            var betweenWaveElapsed = (DateTime.Now - _betweenWaveStartTime).TotalSeconds;
            if (betweenWaveElapsed > BetweenWaveTimeoutSeconds)
            {
                Decision = "Between-wave timeout → LootSweep";
                _phase = SimPhase.LootSweep;
                _phaseStartTime = DateTime.Now;
                _sweepNearMonolith = false;
                _lastEmptyScanAt = DateTime.MinValue;
                StatusText = $"Stuck between waves for {BetweenWaveTimeoutSeconds}s — exiting";
                return;
            }

            if (_waveStartAttempts >= MaxWaveStartAttempts)
            {
                Decision = $"Failed to start wave after {MaxWaveStartAttempts} attempts → LootSweep";
                _phase = SimPhase.LootSweep;
                _phaseStartTime = DateTime.Now;
                _sweepNearMonolith = false;
                _lastEmptyScanAt = DateTime.MinValue;
                StatusText = $"Can't start wave {_state.CurrentWave + 1} — exiting after {MaxWaveStartAttempts} failed attempts";
                return;
            }

            if (DateTime.Now >= _state.CanStartWaveAt && _state.CurrentWave < 15)
            {
                Decision = $"Wave {_state.CurrentWave}/15 → StartWave (attempt {_waveStartAttempts}/{MaxWaveStartAttempts})";
                TickStartWave(ctx);
                return;
            }

            // Waiting for wave delay (loot is clear, timer running)
            var waitRemaining = (_state.CanStartWaveAt - DateTime.Now).TotalSeconds;
            Decision = $"Loot clear — waiting ({waitRemaining:F1}s)";
            IdleNearMonolith(ctx);
            StatusText = $"Wave {_state.CurrentWave}/15 — loot clear, {waitRemaining:F1}s until next wave";
        }

        /// <summary>
        /// Find and navigate to monsters when none are in chase range.
        /// Three-tier fallback: cached distant monsters → reset exploration and explore → orbit monolith.
        /// </summary>
        private void TickExploreForMonsters(BotContext ctx)
        {
            var gc = ctx.Game;
            var playerPos = gc.Player.GridPosNum;

            // Expire old blacklist entries
            PruneBlacklist();

            // Tier 1: Known monsters exist — navigate toward the nearest non-blacklisted one
            if (ctx.Combat.CachedMonsterCount > 0)
            {
                var nearestPos = FindNearestNonBlacklisted(gc, playerPos);
                if (nearestPos.HasValue)
                {
                    _wasSearching = true;
                    var monsterDist = Vector2.Distance(playerPos, nearestPos.Value);
                    if (monsterDist > 20f && !ctx.Navigation.IsNavigating)
                        ctx.Navigation.NavigateTo(gc, nearestPos.Value);
                    StatusText = $"Wave {_state.CurrentWave}/15 — chasing nearest monster (dist: {monsterDist:F0}, {ctx.Combat.CachedMonsterCount} alive, {_blacklistedMonsters.Count} blacklisted)";
                    return;
                }
                // All cached monsters are blacklisted — fall through to explore
            }

            // Tier 2: No cached monsters — explore to find stragglers
            // (ResetSeen already called at the fighting→searching transition above)

            // Let current navigation finish before picking a new target
            if (ctx.Navigation.IsNavigating)
            {
                StatusText = $"Wave {_state.CurrentWave}/15 — searching for monsters";
                return;
            }

            if (ctx.Exploration.IsInitialized)
            {
                var target = ctx.Exploration.GetNextExplorationTarget(playerPos);
                if (target.HasValue)
                {
                    ctx.Navigation.NavigateTo(gc, target.Value);
                    StatusText = $"Wave {_state.CurrentWave}/15 — exploring for monsters";
                    return;
                }
            }

            // Tier 3: Exploration exhausted — sweep the map around the monolith
            // Simulacrum maps are small (~18K cells) — the network bubble (radius 180) covers
            // the entire map, so exploration coverage resets are useless. Instead, physically
            // patrol at varying radii to find spawned monsters.
            if (_state.MonolithPosition.HasValue)
            {
                var distToMonolith = Vector2.Distance(playerPos, _state.MonolithPosition.Value);
                if (distToMonolith > 80f)
                {
                    ctx.Navigation.NavigateTo(gc, _state.MonolithPosition.Value);
                    StatusText = $"Wave {_state.CurrentWave}/15 — returning to monolith (dist: {distToMonolith:F0})";
                    return;
                }

                if (!ctx.Navigation.IsNavigating)
                {
                    // Sweep at varying radius — cycles through the arena to find spawns
                    var angle = (float)(DateTime.Now.Ticks % 62830) / 10000f;
                    var radius = 40f + 25f * MathF.Sin(angle * 0.3f); // 15-65 radius sweep
                    var orbitTarget = _state.MonolithPosition.Value + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * radius;
                    ctx.Navigation.NavigateTo(gc, orbitTarget);
                }
                StatusText = $"Wave {_state.CurrentWave}/15 — sweeping for monsters";
                return;
            }

            StatusText = $"Wave {_state.CurrentWave}/15 — searching (no exploration targets)";
        }

        // ═══════════════════════════════════════════════════
        // Monster blacklist helpers
        // ═══════════════════════════════════════════════════

        /// <summary>
        /// Blacklist all alive hostile monsters within the given grid radius.
        /// Blacklisted monsters are ignored by TickExploreForMonsters tier 1
        /// so the bot repositions via exploration instead of chasing the same unreachable pack.
        /// </summary>
        private void BlacklistNearbyMonsters(GameController gc, Vector2 playerGrid, float radius)
        {
            var now = DateTime.Now;
            foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
            {
                if (entity.Type != EntityType.Monster || !entity.IsHostile || !entity.IsAlive)
                    continue;
                if (Vector2.Distance(entity.GridPosNum, playerGrid) <= radius)
                    _blacklistedMonsters[entity.Id] = now;
            }
        }

        /// <summary>
        /// Find the nearest alive hostile monster that isn't blacklisted.
        /// Returns null if all cached monsters are blacklisted (or none exist).
        /// </summary>
        private Vector2? FindNearestNonBlacklisted(GameController gc, Vector2 playerGrid)
        {
            float nearestDist = float.MaxValue;
            Vector2? nearestPos = null;

            foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
            {
                if (entity.Type != EntityType.Monster || !entity.IsHostile || !entity.IsAlive || !entity.IsTargetable)
                    continue;
                if (_blacklistedMonsters.ContainsKey(entity.Id))
                    continue;

                var dist = Vector2.Distance(entity.GridPosNum, playerGrid);
                if (dist < nearestDist)
                {
                    nearestDist = dist;
                    nearestPos = entity.GridPosNum;
                }
            }

            return nearestPos;
        }

        /// <summary>Remove expired blacklist entries.</summary>
        private void PruneBlacklist()
        {
            if (_blacklistedMonsters.Count == 0) return;
            var now = DateTime.Now;
            var expired = new List<long>();
            foreach (var kvp in _blacklistedMonsters)
            {
                if ((now - kvp.Value).TotalSeconds > MonsterBlacklistSeconds)
                    expired.Add(kvp.Key);
            }
            foreach (var id in expired)
                _blacklistedMonsters.Remove(id);
        }

        /// <summary>
        /// Idle near the monolith between waves.
        /// </summary>
        private void IdleNearMonolith(BotContext ctx)
        {
            if (!_state.MonolithPosition.HasValue) return;
            var gc = ctx.Game;
            var dist = Vector2.Distance(gc.Player.GridPosNum, _state.MonolithPosition.Value);

            if (dist > 30f && !ctx.Navigation.IsNavigating)
                ctx.Navigation.NavigateTo(gc, _state.MonolithPosition.Value);
            else if (dist <= 20f && ctx.Navigation.IsNavigating)
                ctx.Navigation.Stop(gc);
        }

        /// <summary>
        /// Navigate to monolith and click it to start the next wave.
        /// Tries entity label first (most reliable), falls back to WorldToScreen click.
        /// Only increments _waveStartAttempts when a click is actually sent.
        /// </summary>
        private void TickStartWave(BotContext ctx)
        {
            if (!_state.MonolithPosition.HasValue)
            {
                StatusText = "Can't start wave — monolith not found";
                return;
            }

            var gc = ctx.Game;
            var playerPos = gc.Player.GridPosNum;
            var monolithPos = _state.MonolithPosition.Value;
            var dist = Vector2.Distance(playerPos, monolithPos);

            // Navigate close first
            if (dist > ctx.Interaction.InteractRadius)
            {
                if (!ctx.Navigation.IsNavigating)
                    ctx.Navigation.NavigateTo(gc, monolithPos);
                StatusText = $"Navigating to monolith to start wave {_state.CurrentWave + 1} (dist: {dist:F0})";
                return;
            }

            ctx.Navigation.Stop(gc);

            if (!ModeHelpers.CanAct(_lastActionTime, MajorActionCooldownMs)) return;

            // Resolve monolith entity
            Entity? monolith = null;
            if (_state.MonolithId.HasValue)
            {
                monolith = gc.EntityListWrapper.OnlyValidEntities
                    .FirstOrDefault(e => e.Id == _state.MonolithId.Value);
            }
            if (monolith == null)
            {
                monolith = gc.EntityListWrapper.OnlyValidEntities
                    .FirstOrDefault(e => e.Metadata?.Contains("Objects/Afflictionator") == true);
            }

            if (monolith == null)
            {
                StatusText = "Monolith entity not found for clicking";
                return;
            }

            // Try 1: Click entity label if visible (game renders a hoverable label on the monolith)
            if (TryClickEntityLabel(gc, monolith))
            {
                _waveStartAttempts++;
                StatusText = $"Clicking monolith label to start wave {_state.CurrentWave + 1} (attempt {_waveStartAttempts})";
                return;
            }

            // Try 2: Click via WorldToScreen with bounds check
            var screenPos = gc.IngameState.Camera.WorldToScreen(monolith.BoundsCenterPosNum);
            var windowRect = gc.Window.GetWindowRectangle();

            // Screen bounds check
            if (screenPos.X < 10 || screenPos.X > windowRect.Width - 10 ||
                screenPos.Y < 10 || screenPos.Y > windowRect.Height - 10)
            {
                StatusText = $"Monolith off screen — waiting";
                return;
            }

            var absPos = new Vector2(windowRect.X + screenPos.X, windowRect.Y + screenPos.Y);
            if (BotInput.Click(absPos))
            {
                _lastActionTime = DateTime.Now;
                _waveStartAttempts++;
                StatusText = $"Clicking monolith to start wave {_state.CurrentWave + 1} (attempt {_waveStartAttempts})";
            }
        }

        /// <summary>
        /// Try to find and click the monolith's interaction label rendered by the game.
        /// These show up in the VisibleGroundItemLabels list for interactable entities.
        /// </summary>
        private bool TryClickEntityLabel(GameController gc, Entity monolith)
        {
            try
            {
                var labels = gc.IngameState.IngameUi.ItemsOnGroundLabelElement.VisibleGroundItemLabels;
                if (labels == null) return false;

                foreach (var label in labels)
                {
                    if (label.Entity?.Id != monolith.Id) continue;
                    if (label.Label == null || !label.Label.IsVisible) continue;

                    var labelRect = label.ClientRect;
                    var clickPos = new Vector2(
                        labelRect.X + labelRect.Width / 2f,
                        labelRect.Y + labelRect.Height / 2f);
                    var windowRect = gc.Window.GetWindowRectangle();
                    var absPos = new Vector2(windowRect.X + clickPos.X, windowRect.Y + clickPos.Y);

                    if (BotInput.Click(absPos))
                    {
                        _lastActionTime = DateTime.Now;
                        return true;
                    }
                    return false;
                }
            }
            catch { }
            return false;
        }

        // =================================================================
        // Between-wave stash
        // =================================================================

        private void TickBetweenWaveStash(BotContext ctx, InteractionResult interactionResult)
        {
            // If wave started while stashing, cancel and return to wave cycle
            if (_state.IsWaveActive)
            {
                if (ctx.Stash.IsBusy)
                    ctx.Stash.Cancel(ctx.Game, ctx.Navigation);
                _isStashing = false;
                _phase = SimPhase.WaveCycle;
                _phaseStartTime = DateTime.Now;
                StatusText = "Wave started — cancelling stash";
                return;
            }

            // Timeout
            if ((DateTime.Now - _phaseStartTime).TotalSeconds > 30)
            {
                if (ctx.Stash.IsBusy)
                    ctx.Stash.Cancel(ctx.Game, ctx.Navigation);
                _isStashing = false;
                _phase = SimPhase.WaveCycle;
                _phaseStartTime = DateTime.Now;
                StatusText = "Stash timeout — resuming wave cycle";
                return;
            }

            var gc = ctx.Game;

            // Step 1: Navigate to cached stash position so the entity loads into the entity list.
            // StashSystem.FindStashEntity only finds entities within network bubble range.
            if (_state.StashPosition.HasValue)
            {
                var playerPos = gc.Player.GridPosNum;
                var dist = Vector2.Distance(
                    new Vector2(playerPos.X, playerPos.Y),
                    _state.StashPosition.Value);

                if (dist > ctx.Interaction.InteractRadius)
                {
                    // Cancel StashSystem if it was started — we need to navigate first
                    if (ctx.Stash.IsBusy)
                        ctx.Stash.Cancel(gc, ctx.Navigation);
                    if (!ctx.Navigation.IsNavigating)
                        ctx.Navigation.NavigateTo(gc, _state.StashPosition.Value);
                    StatusText = $"Navigating to stash (dist: {dist:F0})";
                    return;
                }
            }

            // Step 2: Close enough — start StashSystem if not already running
            if (!ctx.Stash.IsBusy)
            {
                ctx.Navigation.Stop(gc);
                ctx.Stash.Start();
            }

            // Step 3: Tick StashSystem
            var result = ctx.Stash.Tick(gc, ctx.Navigation);

            switch (result)
            {
                case StashResult.Succeeded:
                    _isStashing = false;
                    _phase = SimPhase.WaveCycle;
                    _phaseStartTime = DateTime.Now;
                    StatusText = $"Stashed {ctx.Stash.ItemsStored} items — resuming wave cycle";
                    break;
                case StashResult.Failed:
                    // StashSystem failed (entity not found, no path, etc.)
                    // Don't immediately give up — go back to navigating to stash position
                    StatusText = $"Stash failed ({ctx.Stash.Status}) — retrying";
                    break;
                default:
                    StatusText = $"Between-wave stash: {ctx.Stash.Status}";
                    break;
            }
        }

        // =================================================================
        // Loot sweep — after wave 15, pick up remaining items then exit
        // =================================================================

        private DateTime _lastEmptyScanAt = DateTime.MinValue;
        private bool _sweepNearMonolith; // true once we've confirmed proximity to monolith
        private const float EmptyGraceSeconds = 5f;
        private const float LootSweepTimeoutSeconds = 60f;
        private const float SweepMonolithProximity = 25f; // grid distance to be "near" monolith for loot

        private void TickLootSweep(BotContext ctx, InteractionResult interactionResult)
        {
            _lootTracker.HandleResult(interactionResult, ctx);

            if ((DateTime.Now - _phaseStartTime).TotalSeconds > LootSweepTimeoutSeconds)
            {
                EnterExitMapPhase(ctx);
                StatusText = $"Loot sweep timeout — exiting ({_lootTracker.PickupCount} items)";
                return;
            }

            if (ctx.Interaction.IsBusy) return;

            var gc = ctx.Game;

            // Step 1: Navigate to monolith before scanning for loot.
            // Wave 15 rewards drop at the monolith — items won't appear in VisibleGroundItemLabels
            // unless the player is close enough. Grace timer must NOT start until we're in position.
            if (!_sweepNearMonolith && _state.MonolithPosition.HasValue)
            {
                var playerPos = new Vector2(gc.Player.GridPosNum.X, gc.Player.GridPosNum.Y);
                var distToMonolith = Vector2.Distance(playerPos, _state.MonolithPosition.Value);

                if (distToMonolith > SweepMonolithProximity)
                {
                    if (!ctx.Navigation.IsNavigating)
                        ctx.Navigation.NavigateTo(gc, _state.MonolithPosition.Value);
                    StatusText = $"Sweep: returning to monolith for drops (dist: {distToMonolith:F0})";
                    // Reset grace timer — don't count travel time as empty scan time
                    _lastEmptyScanAt = DateTime.MinValue;
                    return;
                }

                // Arrived near monolith — stop navigation, begin scanning
                if (ctx.Navigation.IsNavigating)
                    ctx.Navigation.Stop(gc);
                _sweepNearMonolith = true;
                _lastEmptyScanAt = DateTime.MinValue; // ensure grace starts fresh from arrival
            }

            // Step 2: Stash remaining items before exiting
            if (_state.StashPosition.HasValue)
            {
                var invCount = (StashSystem.GetInventorySlotItems(gc)?.Count ?? 0);
                if (invCount > 0)
                {
                    // Navigate close to stash so the entity loads into the entity list
                    var playerPos = gc.Player.GridPosNum;
                    var dist = Vector2.Distance(
                        new Vector2(playerPos.X, playerPos.Y),
                        _state.StashPosition.Value);
                    if (dist > ctx.Interaction.InteractRadius)
                    {
                        if (!ctx.Navigation.IsNavigating)
                            ctx.Navigation.NavigateTo(gc, _state.StashPosition.Value);
                        StatusText = $"Navigating to stash before exit (dist: {dist:F0})";
                        return;
                    }

                    if (!ctx.Stash.IsBusy)
                    {
                        ctx.Navigation.Stop(gc);
                        ctx.Stash.Start();
                    }
                }
                if (ctx.Stash.IsBusy)
                {
                    var stashResult = ctx.Stash.Tick(gc, ctx.Navigation);
                    if (stashResult == StashResult.Succeeded || stashResult == StashResult.Failed)
                    {
                        // After stashing, need to return to monolith for remaining drops
                        _sweepNearMonolith = false;
                    }
                    else
                    {
                        StatusText = $"Stashing before exit: {ctx.Stash.Status}";
                        return;
                    }
                }
            }

            // Step 3: Scan and pick up loot
            ctx.Loot.Scan(gc);
            var best = ctx.Loot.GetBestCandidate();
            if (best != null)
            {
                _lastEmptyScanAt = DateTime.MinValue;
                var withinRadius = best.Distance <= ctx.Interaction.InteractRadius;
                ctx.Interaction.PickupGroundItem(best.Entity, ctx.Navigation,
                    requireProximity: !withinRadius);
                _lootTracker.SetPending(best.Entity.Id, best.ItemName, best.ChaosValue);
                StatusText = $"Sweep: picking up {best.ItemName} ({_lootTracker.PickupCount} picked)";
                return;
            }

            // Step 4: Grace period — wait near monolith for items to finish dropping.
            // Timer only starts once near monolith AND scan finds nothing.
            if (_lastEmptyScanAt == DateTime.MinValue)
                _lastEmptyScanAt = DateTime.Now;

            if ((DateTime.Now - _lastEmptyScanAt).TotalSeconds >= EmptyGraceSeconds)
            {
                EnterExitMapPhase(ctx);
                StatusText = $"Sweep complete — exiting ({_lootTracker.PickupCount} items)";
                return;
            }

            StatusText = $"Sweep: waiting for drops near monolith... ({_lootTracker.PickupCount} picked)";
        }

        // =================================================================
        // Exit map
        // =================================================================

        private void EnterExitMapPhase(BotContext ctx)
        {
            _phase = SimPhase.ExitMap;
            _phaseStartTime = DateTime.Now;
            _mapCompleted = true;
            ctx.LootTracker.RecordMapComplete();

            // Cancel any in-flight systems
            if (ctx.Stash.IsBusy)
                ctx.Stash.Cancel(ctx.Game, ctx.Navigation);
            ctx.Navigation.Stop(ctx.Game);

            StatusText = "Exiting map via portal";
        }

        private void TickExitMap(BotContext ctx)
        {
            var gc = ctx.Game;

            if (gc.Area.CurrentArea.IsHideout)
                return;

            if ((DateTime.Now - _phaseStartTime).TotalSeconds > 30)
            {
                _phase = SimPhase.Done;
                StatusText = "Exit timeout — giving up";
                return;
            }

            if (!ModeHelpers.CanAct(_lastActionTime, MajorActionCooldownMs)) return;

            // Close any open panels before clicking portal
            if (gc.IngameState.IngameUi.StashElement?.IsVisible == true ||
                gc.IngameState.IngameUi.InventoryPanel?.IsVisible == true)
            {
                BotInput.PressKey(System.Windows.Forms.Keys.Escape);
                _lastActionTime = DateTime.Now;
                StatusText = "Closing panels before exit";
                return;
            }

            var portal = ModeHelpers.FindNearestPortal(gc);
            if (portal == null)
            {
                // Try cached portal position
                if (_state.PortalPosition.HasValue)
                {
                    var playerPos = gc.Player.GridPosNum;
                    var dist = Vector2.Distance(playerPos, _state.PortalPosition.Value);
                    if (dist > ctx.Interaction.InteractRadius)
                    {
                        if (!ctx.Navigation.IsNavigating)
                            ctx.Navigation.NavigateTo(gc,
                                _state.PortalPosition.Value);
                        StatusText = $"Walking to cached portal (dist: {dist:F0})";
                    }
                    else
                    {
                        StatusText = "Near cached portal — waiting for entity";
                    }
                }
                else
                {
                    StatusText = "No portal found — waiting";
                }
                return;
            }

            var playerGridPos = new Vector2(gc.Player.GridPosNum.X, gc.Player.GridPosNum.Y);
            var portalGridPos = new Vector2(portal.GridPosNum.X, portal.GridPosNum.Y);
            var portalDist = Vector2.Distance(playerGridPos, portalGridPos);

            if (portalDist > ctx.Interaction.InteractRadius)
            {
                if (!ctx.Navigation.IsNavigating)
                    ctx.Navigation.NavigateTo(gc, portalGridPos);
                StatusText = $"Walking to portal (dist: {portalDist:F0})";
                return;
            }

            ctx.Navigation.Stop(gc);
            ModeHelpers.ClickEntity(gc, portal, ref _lastActionTime);
            StatusText = "Clicking portal to exit";
        }

        // =================================================================
        // Render
        // =================================================================

        public void Render(BotContext ctx)
        {
            if (ctx.Graphics == null) return;
            var gc = ctx.Game;
            var cam = gc.IngameState.Camera;
            var g = ctx.Graphics;

            // --- HUD ---
            var hudY = 100f;
            var hudX = 20f;
            var lineH = 16f;

            g.DrawText($"Phase: {_phase}", new Vector2(hudX, hudY), SharpDX.Color.White);
            hudY += lineH;
            g.DrawText(StatusText, new Vector2(hudX, hudY), SharpDX.Color.LightGreen);
            hudY += lineH;

            g.DrawText($"Wave: {_state.CurrentWave}/15 {(_state.IsWaveActive ? "ACTIVE" : "idle")}",
                new Vector2(hudX, hudY),
                _state.IsWaveActive ? SharpDX.Color.Red : SharpDX.Color.Cyan);
            hudY += lineH;

            if (_state.DeathCount > 0)
            {
                g.DrawText($"Deaths: {_state.DeathCount}/{_settings.MaxDeaths.Value}",
                    new Vector2(hudX, hudY), SharpDX.Color.Red);
                hudY += lineH;
            }

            var runElapsed = DateTime.Now - _state.RunStartedAt;
            g.DrawText($"Runs: {_state.RunsCompleted} | This run: {runElapsed.Minutes}m{runElapsed.Seconds:D2}s",
                new Vector2(hudX, hudY), SharpDX.Color.Gold);
            hudY += lineH;

            if (_state.RunsCompleted > 0)
            {
                var avgDur = _state.AverageRunDuration;
                g.DrawText($"Avg: {avgDur.Minutes}m{avgDur.Seconds:D2}s | {_state.AverageWavesPerRun:F1} waves/run",
                    new Vector2(hudX, hudY), SharpDX.Color.Gold);
                hudY += lineH;
            }

            g.DrawText($"Loot: {_lootTracker.PickupCount} items",
                new Vector2(hudX, hudY), SharpDX.Color.Gold);
            hudY += lineH;

            if (!string.IsNullOrEmpty(Decision))
            {
                g.DrawText($"Decision: {Decision}", new Vector2(hudX, hudY), SharpDX.Color.Yellow);
                hudY += lineH;
            }

            if (ctx.Interaction.IsBusy)
            {
                g.DrawText($"Interact: {ctx.Interaction.Status}",
                    new Vector2(hudX, hudY), SharpDX.Color.Yellow);
                hudY += lineH;
            }

            // --- World drawing (only in map) ---
            if (gc.Area.CurrentArea.IsHideout || gc.Area.CurrentArea.IsTown)
                return;

            var playerZ = gc.Player.PosNum.Z;

            // Monolith
            if (_state.MonolithPosition.HasValue)
            {
                var monolithWorld = SimulacrumState.ToWorld3(_state.MonolithPosition.Value, playerZ);
                g.DrawText("MONOLITH", cam.WorldToScreen(monolithWorld), SharpDX.Color.Purple);
                g.DrawCircleInWorld(monolithWorld, 30f, SharpDX.Color.Purple, 2f);
            }

            // Portal
            if (_state.PortalPosition.HasValue)
            {
                var portalWorld = SimulacrumState.ToWorld3(_state.PortalPosition.Value, playerZ);
                g.DrawText("PORTAL", cam.WorldToScreen(portalWorld) + new Vector2(-20, -15),
                    SharpDX.Color.Aqua);
                g.DrawCircleInWorld(portalWorld, 20f, SharpDX.Color.Aqua, 1.5f);
            }

            // Stash
            if (_state.StashPosition.HasValue)
            {
                var stashWorld = SimulacrumState.ToWorld3(_state.StashPosition.Value, playerZ);
                g.DrawText("STASH", cam.WorldToScreen(stashWorld) + new Vector2(-15, -15),
                    SharpDX.Color.Gold);
            }

            // Navigation path
            if (ctx.Navigation.IsNavigating)
            {
                var path = ctx.Navigation.CurrentNavPath;
                for (int i = ctx.Navigation.CurrentWaypointIndex; i < path.Count - 1; i++)
                {
                    var from = cam.WorldToScreen(new Vector3(
                        path[i].Position.X * Systems.Pathfinding.GridToWorld, path[i].Position.Y * Systems.Pathfinding.GridToWorld, playerZ));
                    var to = cam.WorldToScreen(new Vector3(
                        path[i + 1].Position.X * Systems.Pathfinding.GridToWorld, path[i + 1].Position.Y * Systems.Pathfinding.GridToWorld, playerZ));
                    g.DrawLine(from, to, 1.5f, SharpDX.Color.CornflowerBlue);
                }
            }

            // Monster count
            g.DrawText($"Monsters: {ctx.Combat.NearbyMonsterCount}",
                new Vector2(hudX, hudY), SharpDX.Color.Gray);
            hudY += lineH;

            // Failed loot count
            if (ctx.Loot.FailedCount > 0)
            {
                g.DrawText($"Ignored items: {ctx.Loot.FailedCount}",
                    new Vector2(hudX, hudY), SharpDX.Color.OrangeRed);
                hudY += lineH;
            }

            // Draw failed/ignored items in world with reason labels
            foreach (var entry in ctx.Loot.FailedEntries.Values)
            {
                // Find the entity to get its world position
                Entity? failedEntity = null;
                foreach (var e in gc.EntityListWrapper.OnlyValidEntities)
                {
                    if (e.Id == entry.EntityId)
                    {
                        failedEntity = e;
                        break;
                    }
                }
                if (failedEntity == null) continue;

                var worldPos = failedEntity.BoundsCenterPosNum;
                var screenPos = cam.WorldToScreen(worldPos);
                if (screenPos.X < 0 || screenPos.X > gc.Window.GetWindowRectangle().Width ||
                    screenPos.Y < 0 || screenPos.Y > gc.Window.GetWindowRectangle().Height)
                    continue;

                var age = (DateTime.Now - entry.FailedAt).TotalSeconds;
                g.DrawText($"X {entry.Reason} ({age:F0}s ago)",
                    screenPos + new Vector2(5, -10), SharpDX.Color.OrangeRed);
            }
        }

    }

    public enum SimPhase
    {
        Idle,

        // Hideout phases
        InHideout,
        StashItems,
        OpenMap,
        EnterPortal,

        // Map phases
        FindMonolith,
        NavigateToMonolith,
        WaveCycle,
        BetweenWaveStash,
        LootSweep,
        ExitMap,
        Done,
    }
}
