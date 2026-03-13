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
        private bool _nudgedForMonolith;

        // Loot tracking — only record on confirmed pickup
        private DateTime _lastLootScan = DateTime.MinValue;
        private const float LootScanIntervalMs = 500;
        private readonly LootPickupTracker _lootTracker = new();

        // Hideout flow
        private readonly HideoutFlow _hideoutFlow = new();

        // Between-wave stash tracking
        private bool _isStashing;

        // Patrol tracking — cycle through spawn zones during waves and between-wave loot sweeps
        private Vector2? _currentPatrolTarget;
        private DateTime _arrivedAtPatrolTarget = DateTime.MinValue;
        private const float PatrolStaleSeconds = 8f; // move on if no monsters after this long
        private int _patrolZonesVisitedWithoutMonsters; // reset when monsters found or patrol target set

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

            // Always tick state + combat when in map
            bool inMap = gc.Area?.CurrentArea != null &&
                         !gc.Area.CurrentArea.IsHideout &&
                         !gc.Area.CurrentArea.IsTown;
            if (inMap)
            {
                _state.Tick(gc, _settings.MinWaveDelaySeconds.Value);

                // Suppress combat repositioning when interaction is navigating to loot —
                // otherwise combat move commands conflict with interaction navigation
                ctx.Combat.SuppressPositioning = ctx.Interaction.IsBusy;
                ctx.Combat.Tick(gc, ctx.Settings.Build);
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
                _nudgedForMonolith = false;
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
                _nudgedForMonolith = false;
                StatusText = "Monolith found — navigating";
                return;
            }

            var gc = ctx.Game;
            var elapsed = (DateTime.Now - _phaseStartTime).TotalSeconds;

            // After 2s settle, navigate to map center to find the monolith.
            // Portal may spawn far from center — monolith is always near the middle.
            if (!_nudgedForMonolith && elapsed > 2)
            {
                _nudgedForMonolith = true;

                // Use exploration map center (weighted centroid of all walkable space)
                var mapCenter = ctx.Exploration.IsInitialized ? ctx.Exploration.GetMapCenter() : null;
                if (mapCenter.HasValue)
                {
                    ctx.Navigation.NavigateTo(gc, SimulacrumState.ToWorld(mapCenter.Value));
                    StatusText = $"Navigating to map center to find monolith";
                    ctx.Log($"FindMonolith: navigating to map center ({mapCenter.Value.X:F0}, {mapCenter.Value.Y:F0})");
                }
                else
                {
                    // Fallback: small nudge to trigger entity loading
                    var playerGrid = gc.Player.GridPosNum;
                    var nudgeTarget = new Vector2(playerGrid.X + 5, playerGrid.Y) * Systems.Pathfinding.GridToWorld;
                    ctx.Navigation.NavigateTo(gc, nudgeTarget);
                    StatusText = "Nudging to trigger entity loading...";
                }
                return;
            }

            StatusText = _nudgedForMonolith
                ? "Navigating to map center — searching for monolith..."
                : "Searching for monolith...";

            if (elapsed > 30)
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
                    SimulacrumState.ToWorld(_state.MonolithPosition.Value));
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
                ctx.Exploration.ResetSeen();
                _currentPatrolTarget = null;
                _patrolZonesVisitedWithoutMonsters = 0;
                _wasSearching = false;
                _waveStartAttempts = 0;
                _betweenWaveStartTime = DateTime.MinValue;
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
                StatusText = $"Wave {_state.CurrentWave} timed out — sweeping loot before exit";
                return;
            }

            // --- Priority 3: Wave active — fight and explore ---
            if (_state.IsWaveActive)
            {
                // Use NearbyChaseCount (within 80 grid) not NearbyMonsterCount (all cached entities)
                // Stale cached entities far beyond network bubble shouldn't prevent patrolling
                if (ctx.Combat.NearbyChaseCount > 0)
                {
                    // Found monsters after searching — reset exploration so next search
                    // re-sweeps the whole map (monsters spawn in already-visited areas)
                    if (_wasSearching)
                    {
                        ctx.Exploration.ResetSeen();
                        _wasSearching = false;
                    }

                    // Record nearby pack center for spawn zone tracking (not all cached entities)
                    _state.RecordCombatPosition(ctx.Combat.NearbyPackCenter);
                    _currentPatrolTarget = null; // reset patrol — re-evaluate after combat
                    _patrolZonesVisitedWithoutMonsters = 0;

                    // Combat system handles fighting automatically via Tick above
                    Decision = $"Wave {_state.CurrentWave} — fighting ({ctx.Combat.NearbyChaseCount} nearby, {ctx.Combat.NearbyMonsterCount} total)";
                    StatusText = $"Wave {_state.CurrentWave}/15 — fighting {ctx.Combat.NearbyChaseCount} monsters";
                }
                else
                {
                    _wasSearching = true;
                    // No reachable monsters — patrol spawn zones to find them
                    Decision = $"Wave {_state.CurrentWave} — patrolling ({ctx.Combat.NearbyMonsterCount} distant)";
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
                        IdleAtSpawnZone(ctx);
                    Decision = pickingUp ? "Between waves — picking up loot" : "Between waves — clearing loot";
                    StatusText = pickingUp ? $"Picking up loot (between waves)" : "Loot nearby — clearing before next wave";
                    return;
                }
            }

            // Priority 5: Stash items if inventory above threshold
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
                    if (!ctx.Stash.IsBusy)
                        ctx.Stash.Start();
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
                StatusText = $"Stuck between waves for {BetweenWaveTimeoutSeconds}s — exiting";
                return;
            }

            if (_waveStartAttempts >= MaxWaveStartAttempts)
            {
                Decision = $"Failed to start wave after {MaxWaveStartAttempts} attempts → LootSweep";
                _phase = SimPhase.LootSweep;
                _phaseStartTime = DateTime.Now;
                StatusText = $"Can't start wave {_state.CurrentWave + 1} — exiting after {MaxWaveStartAttempts} failed attempts";
                return;
            }

            if (DateTime.Now >= _state.CanStartWaveAt && _state.CurrentWave < 15)
            {
                _waveStartAttempts++;
                Decision = $"Wave {_state.CurrentWave}/15 → StartWave (attempt {_waveStartAttempts}/{MaxWaveStartAttempts})";
                TickStartWave(ctx);
                return;
            }

            // Waiting for wave delay (loot is clear, timer running)
            var waitRemaining = (_state.CanStartWaveAt - DateTime.Now).TotalSeconds;
            Decision = $"Loot clear — waiting ({waitRemaining:F1}s)";
            IdleAtSpawnZone(ctx);
            StatusText = $"Wave {_state.CurrentWave}/15 — loot clear, {waitRemaining:F1}s until next wave";
        }

        /// <summary>
        /// Patrol through spawn zones when wave is active but no monsters visible.
        /// Cycles through known spawn zones, spending PatrolStaleSeconds at each before
        /// moving to the next. Falls back to exploration if no spawn data yet.
        /// </summary>
        private void TickExploreForMonsters(BotContext ctx)
        {
            var gc = ctx.Game;
            var playerPos = gc.Player.GridPosNum;

            // If we have spawn zone data, patrol between them
            if (_state.HasSpawnData)
            {
                // Pick a patrol target if we don't have one
                if (!_currentPatrolTarget.HasValue)
                {
                    _currentPatrolTarget = _state.GetNextPatrolTarget(playerPos, null);
                    _arrivedAtPatrolTarget = DateTime.MinValue;
                }

                if (_currentPatrolTarget.HasValue)
                {
                    var dist = Vector2.Distance(playerPos, _currentPatrolTarget.Value);

                    if (dist > 25f)
                    {
                        // Navigate to patrol target
                        _arrivedAtPatrolTarget = DateTime.MinValue;
                        if (!ctx.Navigation.IsNavigating)
                            ctx.Navigation.NavigateTo(gc, SimulacrumState.ToWorld(_currentPatrolTarget.Value));
                        StatusText = $"Wave {_state.CurrentWave}/15 — patrolling to spawn zone (dist: {dist:F0})";
                        return;
                    }

                    // Arrived at patrol target — wait for monsters (but skip wait if none exist at all)
                    if (_arrivedAtPatrolTarget == DateTime.MinValue)
                        _arrivedAtPatrolTarget = DateTime.Now;

                    // If there are zero monsters in the entity list, don't waste time waiting
                    // at each spawn zone. Move on immediately to sweep faster.
                    var maxWait = ctx.Combat.NearbyMonsterCount > 0 ? PatrolStaleSeconds : 1f;
                    var idleTime = (DateTime.Now - _arrivedAtPatrolTarget).TotalSeconds;
                    if (idleTime < maxWait)
                    {
                        if (ctx.Navigation.IsNavigating)
                            ctx.Navigation.Stop(gc);
                        StatusText = $"Wave {_state.CurrentWave}/15 — at spawn zone, waiting ({idleTime:F0}s/{maxWait:F0}s)";
                        return;
                    }

                    // Stale — if distant monsters are visible, chase them directly
                    // instead of cycling through spawn zones that may not cover new spawn locations
                    if (ctx.Combat.NearbyMonsterCount > 0)
                    {
                        _currentPatrolTarget = null;
                        // Fall through to distant monster chase below
                    }
                    else
                    {
                        _patrolZonesVisitedWithoutMonsters++;

                        // After visiting all spawn zones once with no monsters, fall through
                        // to exploration/orbit instead of cycling the same empty zones forever
                        if (_patrolZonesVisitedWithoutMonsters >= _state.SpawnZones.Count)
                        {
                            _currentPatrolTarget = null;
                            _patrolZonesVisitedWithoutMonsters = 0;
                            // Fall through to exploration below
                        }
                        else
                        {
                            // More zones to check — move to next spawn zone
                            var lastTarget = _currentPatrolTarget;
                            _currentPatrolTarget = _state.GetNextPatrolTarget(playerPos, lastTarget);
                            _arrivedAtPatrolTarget = DateTime.MinValue;
                            StatusText = $"Wave {_state.CurrentWave}/15 — spawn zone empty, moving to next ({_patrolZonesVisitedWithoutMonsters}/{_state.SpawnZones.Count})";
                            return;
                        }
                    }
                }
            }

            // Distant monsters exist — navigate toward their center of mass
            // PackCenter tracks ALL alive monsters, not just nearby ones within chase radius.
            // This prevents the bot from idling near the monolith while monsters are alive elsewhere.
            if (ctx.Combat.NearbyMonsterCount > 0)
            {
                var packDist = Vector2.Distance(playerPos, ctx.Combat.PackCenter);
                if (packDist > 20f && !ctx.Navigation.IsNavigating)
                    ctx.Navigation.NavigateTo(gc, SimulacrumState.ToWorld(ctx.Combat.PackCenter));
                StatusText = $"Wave {_state.CurrentWave}/15 — chasing distant monsters (dist: {packDist:F0}, {ctx.Combat.NearbyMonsterCount} alive)";
                return;
            }

            // No monsters visible — actively explore to find them.
            // Never idle during an active wave; always be moving.

            // If already navigating somewhere, let it finish before picking a new target
            if (ctx.Navigation.IsNavigating)
            {
                StatusText = $"Wave {_state.CurrentWave}/15 — searching for monsters";
                return;
            }

            // Try exploration map first — it knows which areas haven't been visited
            if (ctx.Exploration.IsInitialized)
            {
                var target = ctx.Exploration.GetNextExplorationTarget(playerPos);
                if (target.HasValue)
                {
                    ctx.Navigation.NavigateTo(gc, SimulacrumState.ToWorld(target.Value));
                    StatusText = $"Wave {_state.CurrentWave}/15 — exploring for monsters";
                    return;
                }
            }

            // Exploration exhausted — orbit the monolith at medium range to cover spawns
            if (_state.MonolithPosition.HasValue)
            {
                var distToMonolith = Vector2.Distance(playerPos, _state.MonolithPosition.Value);
                // If far from monolith, return to it
                if (distToMonolith > 80f)
                {
                    ctx.Navigation.NavigateTo(gc, SimulacrumState.ToWorld(_state.MonolithPosition.Value));
                    StatusText = $"Wave {_state.CurrentWave}/15 — returning to monolith (dist: {distToMonolith:F0})";
                    return;
                }
                // If close to monolith, orbit at ~50 grid to sweep nearby area
                if (distToMonolith < 30f)
                {
                    // Pick a random direction to move outward
                    var angle = (float)(DateTime.Now.Ticks % 6283) / 1000f; // pseudo-random angle from time
                    var orbitTarget = _state.MonolithPosition.Value + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * 50f;
                    ctx.Navigation.NavigateTo(gc, SimulacrumState.ToWorld(orbitTarget));
                    StatusText = $"Wave {_state.CurrentWave}/15 — orbiting monolith for monsters";
                    return;
                }
            }

            StatusText = $"Wave {_state.CurrentWave}/15 — searching (no exploration targets)";
        }

        /// <summary>
        /// Navigate to the nearest spawn zone and idle there between waves.
        /// Positions us where monsters spawn so we're ready when the next wave starts.
        /// </summary>
        private void IdleAtSpawnZone(BotContext ctx)
        {
            var gc = ctx.Game;
            var playerPos = gc.Player.GridPosNum;
            var idlePos = _state.GetNearestSpawnZone(playerPos);
            if (!idlePos.HasValue) return;

            var dist = Vector2.Distance(playerPos, idlePos.Value);

            if (dist < 20f)
            {
                if (ctx.Navigation.IsNavigating)
                    ctx.Navigation.Stop(gc);
                return;
            }

            if (!ctx.Navigation.IsNavigating)
                ctx.Navigation.NavigateTo(gc, SimulacrumState.ToWorld(idlePos.Value));
        }

        /// <summary>
        /// Navigate to monolith and click it to start the next wave.
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
            if (dist > 18f)
            {
                if (!ctx.Navigation.IsNavigating)
                    ctx.Navigation.NavigateTo(gc, SimulacrumState.ToWorld(monolithPos));
                StatusText = $"Navigating to monolith to start wave {_state.CurrentWave + 1} (dist: {dist:F0})";
                return;
            }

            ctx.Navigation.Stop(gc);

            if (!ModeHelpers.CanAct(_lastActionTime, MajorActionCooldownMs)) return;

            // Resolve monolith entity for clicking
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

            if (monolith != null)
            {
                ModeHelpers.ClickEntity(gc, monolith, ref _lastActionTime);
                StatusText = $"Clicking monolith to start wave {_state.CurrentWave + 1}";
            }
            else
            {
                StatusText = "Monolith entity not found for clicking";
            }
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

            var result = ctx.Stash.Tick(ctx.Game, ctx.Navigation);

            switch (result)
            {
                case StashResult.Succeeded:
                    _isStashing = false;
                    _phase = SimPhase.WaveCycle;
                    _phaseStartTime = DateTime.Now;
                    StatusText = $"Stashed {ctx.Stash.ItemsStored} items — resuming wave cycle";
                    break;
                case StashResult.Failed:
                    _isStashing = false;
                    _phase = SimPhase.WaveCycle;
                    _phaseStartTime = DateTime.Now;
                    StatusText = $"Stash failed: {ctx.Stash.Status} — resuming wave cycle";
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
        private const float EmptyGraceSeconds = 5f;
        private const float LootSweepTimeoutSeconds = 60f;

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

            // Stash remaining items before exiting
            if (_state.StashPosition.HasValue)
            {
                var invCount = (StashSystem.GetInventorySlotItems(gc)?.Count ?? 0);
                if (invCount > 0 && !ctx.Stash.IsBusy)
                {
                    ctx.Stash.Start();
                }
                if (ctx.Stash.IsBusy)
                {
                    var stashResult = ctx.Stash.Tick(gc, ctx.Navigation);
                    if (stashResult == StashResult.Succeeded || stashResult == StashResult.Failed)
                    {
                        // Continue sweeping after stash
                    }
                    else
                    {
                        StatusText = $"Stashing before exit: {ctx.Stash.Status}";
                        return;
                    }
                }
            }

            // Scan and pick up loot
            ctx.Loot.Scan(gc);
            var best = ctx.Loot.GetBestCandidate();
            if (best != null)
            {
                _lastEmptyScanAt = DateTime.MinValue;
                var withinRadius = best.Distance <= ctx.Loot.LootRadius;
                ctx.Interaction.PickupGroundItem(best.Entity, ctx.Navigation,
                    requireProximity: !withinRadius);
                _lootTracker.SetPending(best.Entity.Id, best.ItemName, best.ChaosValue);
                StatusText = $"Sweep: picking up {best.ItemName} ({_lootTracker.PickupCount} picked)";
                return;
            }

            // Grace period — wait a bit before declaring done
            if (_lastEmptyScanAt == DateTime.MinValue)
                _lastEmptyScanAt = DateTime.Now;

            if ((DateTime.Now - _lastEmptyScanAt).TotalSeconds >= EmptyGraceSeconds)
            {
                EnterExitMapPhase(ctx);
                StatusText = $"Sweep complete — exiting ({_lootTracker.PickupCount} items)";
                return;
            }

            StatusText = $"Sweep: searching for loot... ({_lootTracker.PickupCount} picked)";
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
                    if (dist > 18f)
                    {
                        if (!ctx.Navigation.IsNavigating)
                            ctx.Navigation.NavigateTo(gc,
                                SimulacrumState.ToWorld(_state.PortalPosition.Value));
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

            if (portalDist > 8f)
            {
                if (!ctx.Navigation.IsNavigating)
                    ctx.Navigation.NavigateTo(gc, portalGridPos * Systems.Pathfinding.GridToWorld);
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

            g.DrawText($"Runs: {_state.RunsCompleted} | Loot: {_lootTracker.PickupCount}",
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

            // Spawn zones (patrol targets)
            for (int i = 0; i < _state.SpawnZones.Count; i++)
            {
                var zone = _state.SpawnZones[i];
                var zoneWorld = SimulacrumState.ToWorld3(zone, playerZ);
                var isCurrentTarget = _currentPatrolTarget.HasValue &&
                    Vector2.Distance(zone, _currentPatrolTarget.Value) < 30f;
                var color = isCurrentTarget ? SharpDX.Color.OrangeRed : SharpDX.Color.Orange;
                g.DrawText($"SPAWN {i + 1}", cam.WorldToScreen(zoneWorld) + new Vector2(-25, -15), color);
                g.DrawCircleInWorld(zoneWorld, 25f, color, isCurrentTarget ? 2f : 1f);
            }

            // Navigation path
            if (ctx.Navigation.IsNavigating)
            {
                var path = ctx.Navigation.CurrentNavPath;
                for (int i = ctx.Navigation.CurrentWaypointIndex; i < path.Count - 1; i++)
                {
                    var from = cam.WorldToScreen(new Vector3(
                        path[i].Position.X, path[i].Position.Y, playerZ));
                    var to = cam.WorldToScreen(new Vector3(
                        path[i + 1].Position.X, path[i + 1].Position.Y, playerZ));
                    g.DrawLine(from, to, 1.5f, SharpDX.Color.CornflowerBlue);
                }
            }

            // Monster count
            g.DrawText($"Monsters: {ctx.Combat.NearbyMonsterCount}",
                new Vector2(hudX, hudY), SharpDX.Color.Gray);
            hudY += lineH;
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
