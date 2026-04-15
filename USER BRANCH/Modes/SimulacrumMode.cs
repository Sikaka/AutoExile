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

        /// <summary>When true, stop the bot after the current run completes instead of starting a new one.</summary>
        public bool StopAfterRun { get; set; }

        /// <summary>When true, pause (set Running=false) after the current hideout cycle completes but before opening the next map.</summary>
        public bool PauseAfterHideout { get; set; }

        // Loot tracking — only record on confirmed pickup
        private DateTime _lastLootScan = DateTime.MinValue;
        private const float LootScanIntervalMs = 500;
        private readonly LootPickupTracker _lootTracker = new();

        // Hideout flow
        private readonly HideoutFlow _hideoutFlow = new();
        public SimulacrumMode() => _hideoutFlow.OnIndexComplete = StartHideoutFlow;

        // Between-wave stash tracking
        private bool _isStashing;

        // Wave transition tracking — reset exploration seen state each wave so we re-sweep for new spawns
        private int _lastKnownWave;
        // Track IsWaveActive from the previous tick — used to detect the wave-end edge (true → false).
        private bool _lastWaveWasActive;
        // Track whether we were searching (no monsters) last tick — reset exploration when
        // transitioning from searching → combat, so the next search re-sweeps the whole map
        private bool _wasSearching;

        // Wave start retry tracking — bail if we can't start the next wave
        private int _waveStartAttempts;
        private const int MaxWaveStartAttempts = 10;
        private DateTime _betweenWaveStartTime = DateTime.MinValue;
        private const float BetweenWaveTimeoutSeconds = 120f;

        // Portal retry — bounce through the portal once before giving up on this instance
        private bool _portalRetryUsed;
        private bool _isPortalRetry;

        // Combat stuck detection — if fighting same monsters too long, move on
        private DateTime _combatEngageTime = DateTime.MinValue;
        private int _combatEngageCount;

        // Dodge tracking
        private DateTime _lastSimDodgeTime = DateTime.MinValue;
        private bool _pendingSimDodge;
        private Vector2 _pendingSimDodgeDir;
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
            _lastWaveWasActive = false;
            _wasSearching = false;
            _waveStartAttempts = 0;
            _betweenWaveStartTime = DateTime.MinValue;
            _portalRetryUsed = false;
            _isPortalRetry = false;

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
                // Already in a map — resume without resetting state
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

                // If a wave is already active (mid-encounter), jump straight into the fight
                if (_state.IsWaveActive)
                {
                    _phase = SimPhase.WaveCycle;
                    _phaseStartTime = DateTime.Now;
                    StatusText = $"Resuming — wave {_state.CurrentWave} already active";
                    ctx.Log($"[Sim] OnEnter mid-encounter: wave {_state.CurrentWave} active → WaveCycle");
                }
                else if (_state.MonolithPosition.HasValue)
                {
                    // Know where the monolith is but no wave active — navigate to it
                    _phase = SimPhase.NavigateToMonolith;
                    _phaseStartTime = DateTime.Now;
                    StatusText = "Resuming — navigating to monolith";
                    ctx.Log("[Sim] OnEnter mid-map: monolith known → NavigateToMonolith");
                }
                else
                {
                    // Haven't found the monolith yet — search for it
                    _state.Reset();
                    _phase = SimPhase.FindMonolith;
                    _phaseStartTime = DateTime.Now;
                    StatusText = "In map — finding monolith";
                    ctx.Log("[Sim] OnEnter mid-map: no monolith → FindMonolith");
                }
            }
        }

        public void OnExit()
        {
            _state.Reset();
            _phase = SimPhase.Idle;
            _isStashing = false;
        }

        /// <summary>Resume or restart the mode. If mid-map, resumes from current encounter state. If in hideout, starts a fresh run.</summary>
        public void Reset(BotContext ctx)
        {
            _mapCompleted = false;
            StopAfterRun = false;
            PauseAfterHideout = false;
            _isStashing = false;
            _hideoutFlow.Cancel();

            var gc = ctx.Game;
            if (gc.Area?.CurrentArea?.IsHideout == true || gc.Area?.CurrentArea?.IsTown == true)
            {
                _state.Reset();
                _phase = SimPhase.InHideout;
                _phaseStartTime = DateTime.Now;
                StartHideoutFlow(ctx);
                return;
            }

            // Mid-map — resume from current encounter state without resetting wave/monolith data
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

            if (_state.IsWaveActive)
            {
                _phase = SimPhase.WaveCycle;
                _phaseStartTime = DateTime.Now;
                ctx.Log($"[Sim] Reset mid-encounter: wave {_state.CurrentWave} active → WaveCycle");
            }
            else if (_state.MonolithPosition.HasValue)
            {
                _phase = SimPhase.NavigateToMonolith;
                _phaseStartTime = DateTime.Now;
                ctx.Log("[Sim] Reset mid-map: monolith known → NavigateToMonolith");
            }
            else
            {
                _state.Reset();
                _phase = SimPhase.FindMonolith;
                _phaseStartTime = DateTime.Now;
                ctx.Log("[Sim] Reset mid-map: no monolith → FindMonolith");
            }
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
                    TrySimDodge(ctx);
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
                        StartHideoutFlow(ctx);
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
                if (_isPortalRetry)
                {
                    _isPortalRetry = false;
                    _waveStartAttempts = 0;
                    _betweenWaveStartTime = DateTime.MinValue;
                    _isStashing = false;
                    _phase = SimPhase.EnterPortal;
                    _phaseStartTime = DateTime.Now;
                    _hideoutFlow.StartPortalReentry();
                    StatusText = "Portal retry — re-entering instance";
                    return;
                }
                if (_mapCompleted)
                {
                    // Map completed
                    _state.RecordRunComplete();
                    _state.Reset();
                    _mapCompleted = false;
                    _lootTracker.ResetCount();

                    if (StopAfterRun)
                    {
                        StopAfterRun = false;
                        ctx.Settings.Running.Value = false;
                        _phase = SimPhase.Idle;
                        StatusText = "Run complete — stopped (finish & stop requested)";
                    }
                    else if (PauseAfterHideout)
                    {
                        PauseAfterHideout = false;
                        ctx.Settings.Running.Value = false;
                        _phase = SimPhase.Idle;
                        StatusText = "Paused at hideout (ready to open map)";
                    }
                    else
                    {
                        _phase = SimPhase.InHideout;
                        _phaseStartTime = DateTime.Now;
                        StartHideoutFlow(ctx);
                        StatusText = "Back in hideout — starting new run";
                    }
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
                    StartHideoutFlow(ctx);
                    StatusText = "Too many deaths — starting new run";
                }
                else
                {
                    _phase = SimPhase.InHideout;
                    _phaseStartTime = DateTime.Now;
                    StartHideoutFlow(ctx);
                }
            }
            else
            {
                // Entered map — always reset exploration for simulacrum.
                // Simulacrum maps are small and fixed-shape, and new instances of the same map
                // share the area name + hash, so cached exploration state from a previous run
                // would make the bot think the map is already fully explored.
                var deathCount = _state.DeathCount;
                _state.OnAreaChanged();
                _state.DeathCount = deathCount;
                _phase = SimPhase.FindMonolith;
                _phaseStartTime = DateTime.Now;

                // Force-reinitialize exploration for this new instance
                var pfGrid = gc.IngameState?.Data?.RawPathfindingData;
                var tgtGrid = gc.IngameState?.Data?.RawTerrainTargetingData;
                if (pfGrid != null && gc.Player != null)
                {
                    var playerGrid = new Vector2(gc.Player.GridPosNum.X, gc.Player.GridPosNum.Y);
                    ctx.Exploration.Initialize(pfGrid, tgtGrid, playerGrid,
                        ctx.Settings.Build.BlinkRange.Value);
                }

                _lootTracker.ResetCount();
                StatusText = "Entered map — finding monolith";
            }
        }

        // =================================================================
        // Map phases
        // =================================================================

        private void TrySimDodge(BotContext ctx)
        {
            var threatSettings = ctx.Settings.Threat;
            if (!threatSettings.AutoDodge.Value) return;

            // Latch new urgent signal — persist across ticks so cooldown doesn't swallow the window
            if (ctx.Threat.DodgeUrgent)
            {
                _pendingSimDodge = true;
                _pendingSimDodgeDir = ctx.Threat.DodgeDirection;
            }

            // Cancel if past the dodge window
            if (_pendingSimDodge && ctx.Threat.ThreatProgress > threatSettings.DodgeMaxProgress.Value)
            {
                _pendingSimDodge = false;
                return;
            }

            if (!_pendingSimDodge) return;
            if ((DateTime.Now - _lastSimDodgeTime).TotalMilliseconds < threatSettings.DodgeCooldownMs.Value) return;

            MovementSkillInfo? skill = null;
            foreach (var ms in ctx.Navigation.MovementSkills)
            {
                if (!ms.IsReady) continue;
                if (ms.MinCastIntervalMs > 0 &&
                    (DateTime.Now - ms.LastUsedAt).TotalMilliseconds < ms.MinCastIntervalMs)
                    continue;
                skill = ms;
                break;
            }
            if (skill == null) return; // stay pending, retry next tick

            var gc = ctx.Game;
            var playerGrid = gc.Player.GridPosNum;
            var pfGrid = gc.IngameState.Data.RawFramePathfindingData;
            var windowRect = gc.Window.GetWindowRectangle();
            float dodgeDist = threatSettings.DodgeDistance.Value;

            // Sweep angles to find a walkable destination
            float[] angleDegrees = { 0f, 30f, -30f, 60f, -60f, 90f, -90f, 180f };
            Vector2? dodgeTarget = null;
            foreach (float deg in angleDegrees)
            {
                var dir = _pendingSimDodgeDir;
                if (deg != 0f)
                {
                    float rad = deg * MathF.PI / 180f;
                    float cos = MathF.Cos(rad), sin = MathF.Sin(rad);
                    dir = new Vector2(dir.X * cos - dir.Y * sin, dir.X * sin + dir.Y * cos);
                }
                var candidate = playerGrid + dir * dodgeDist;
                if (pfGrid != null && !Systems.Pathfinding.IsWalkableCell(pfGrid, (int)candidate.X, (int)candidate.Y))
                    continue;
                var sp = Systems.Pathfinding.GridToScreen(gc, candidate);
                if (sp.X <= 0 || sp.X >= windowRect.Width || sp.Y <= 0 || sp.Y >= windowRect.Height)
                    continue;
                dodgeTarget = candidate;
                break;
            }
            if (!dodgeTarget.HasValue) return;

            var screenPos = Systems.Pathfinding.GridToScreen(gc, dodgeTarget.Value);
            var absPos = new Vector2(windowRect.X + screenPos.X, windowRect.Y + screenPos.Y);
            if (BotInput.ForceCursorPressKey(absPos, skill.Key))
            {
                _lastSimDodgeTime = DateTime.Now;
                _pendingSimDodge = false;
                ctx.Log($"[Sim] Dodge! {skill.Key} for:{ctx.Threat.ThreatSkillName} prog:{ctx.Threat.ThreatProgress:F2}");
            }
        }

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

            // Wait for entity list to settle after zone load
            if (elapsed < ctx.Settings.AreaSettleSeconds.Value)
            {
                StatusText = "Searching for monolith...";
                return;
            }

            // Explore the map until the monolith entity enters the network bubble.
            if (ctx.Exploration.IsInitialized)
            {
                ctx.Exploration.Update(gc.Player.GridPosNum);
                var playerPos = gc.Player.GridPosNum;

                // If exploration is exhausted (100% seen) but monolith not found,
                // reset seen state so we re-sweep the map. Simulacrum maps are small
                // and the monolith is always present — we just need to walk past it.
                if (ctx.Exploration.ActiveBlobCoverage >= 0.99f)
                {
                    ctx.Exploration.ResetSeen();
                    ctx.Exploration.Update(gc.Player.GridPosNum);
                }

                if (!ctx.Navigation.IsNavigating)
                {
                    var target = ctx.Exploration.GetNextExplorationTarget(playerPos);
                    if (target.HasValue)
                    {
                        ctx.Navigation.NavigateTo(gc, target.Value);
                    }
                }
            }

            StatusText = "Exploring to find monolith...";

            // Use the wave timeout setting for the overall FindMonolith phase too.
            // The 60s hardcoded timeout was too short for some maps and too generous
            // for a true stuck condition — use the user-configured wave timeout instead.
            if (elapsed > _settings.WaveTimeoutMinutes.Value * 60)
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
                    if (TryPortalRetry(ctx, "no path to monolith")) return;
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
                _portalRetryUsed = false; // new wave = fresh portal retry budget
            }

            // --- Wave-end: clear stale combat cache when monolith says wave is over ---
            // CombatSystem rebuilds from entity list each tick, but we explicitly reset here
            // so the between-wave loot/navigation logic doesn't act on phantom monster entries
            // from the just-finished wave (e.g. navigating toward a dead monster's cached position).
            if (!_state.IsWaveActive && _lastWaveWasActive)
            {
                ctx.Log($"[Sim] Wave {_state.CurrentWave} complete — searching for loot");
            }
            _lastWaveWasActive = _state.IsWaveActive;

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

                        // Boss kiting: back away from Unique monsters (simulacrum bosses) when
                        // they close to melee range instead of face-tanking them.
                        if (_settings.KiteBosses.Value &&
                            ctx.Combat.BestTarget?.Rarity == MonsterRarity.Unique &&
                            ctx.Combat.BestTarget.DistancePlayer < ctx.Settings.Build.FightRange.Value * 0.6f)
                        {
                            var bossPos = ctx.Combat.BestTarget.GridPosNum;
                            var awayDir = Vector2.Normalize(playerPos - bossPos);
                            var kiteTarget = playerPos + awayDir * ctx.Settings.Build.FightRange.Value;
                            var navPath = ctx.Navigation.CurrentNavPath;
                            var currentDest = navPath.Count > 0 ? navPath[navPath.Count - 1].Position : playerPos;
                            if (!ctx.Navigation.IsNavigating || Vector2.Distance(currentDest, kiteTarget) > 5f)
                            {
                                ctx.Navigation.Stop(gc);
                                ctx.Navigation.NavigateTo(gc, kiteTarget);
                            }
                            Decision = $"Wave {_state.CurrentWave} — kiting boss ({ctx.Combat.BestTarget.RenderName})";
                            StatusText = $"Wave {_state.CurrentWave}/15 — kiting boss";
                            return;
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

            // Priority 4: Loot pickup — clear everything before stashing or starting next wave.
            // Stash check (Priority 4b) only fires when loot is clear OR a pickup just failed.
            // This way we pick up the whole pile in one sweep rather than stash after every item.
            // Exception: if a pickup fails (item unreachable/blocked), stash immediately and return
            // to clear space, then come back for the rest.

            // Priority 5: Loot must be fully cleared before starting next wave.
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

                // Also check raw WorldItem entities — catches items blocked by loot filter
                bool isFarFromMonolith = Vector2.Distance(gc.Player.GridPosNum, _state.MonolithPosition ?? Vector2.Zero) > 40f;

                // --- Priority 4a: Stash immediately if a pickup just failed ---
                // Item is unreachable/blocked. Stash what we have, then come back for the rest.
                if (_lootTracker.LastPickupFailed && _state.StashPosition.HasValue && !ctx.Interaction.IsBusy)
                {
                    var invCount = StashableItemCount(ctx);
                    if (invCount > 0)
                    {
                        _lootTracker.ResetLastFailed();
                        _isStashing = true;
                        Decision = $"Pickup failed → early stash ({invCount} items)";
                        _phase = SimPhase.BetweenWaveStash;
                        _phaseStartTime = DateTime.Now;
                        StatusText = $"Pickup failed — stashing {invCount} items, then returning";
                        return;
                    }
                    // Inventory empty anyway — just clear the flag
                    _lootTracker.ResetLastFailed();
                }

                if (hasLoot || pickingUp || isFarFromMonolith)
                {
                    if (hasLoot || isFarFromMonolith)
                    {
                        // Items exist OR we wandered off — reset wave delay
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

                    // Either picking up, clearing valid items, or walking back to monolith
                    if (!ctx.Interaction.IsBusy)
                        IdleNearMonolith(ctx);
                    
                    var reason = pickingUp ? "picking up loot"
                        : hasLoot ? "clearing loot"
                        : "returning to monolith";
                    Decision = $"Between waves — {reason}";
                    StatusText = pickingUp ? "Picking up loot (between waves)"
                        : hasLoot ? "Loot nearby — clearing before next wave"
                        : "Returning to monolith before next wave";
                    return;
                }

                // --- Priority 4b: Loot is clear — now stash if inventory is above threshold ---
                if (_state.StashPosition.HasValue && !ctx.Interaction.IsBusy)
                {
                    var invCount = StashableItemCount(ctx);
                    bool shouldStartStashing = invCount >= _settings.StashItemThreshold.Value;
                    bool shouldContinueStashing = _isStashing && invCount > 0;

                    if (shouldStartStashing || shouldContinueStashing)
                    {
                        _isStashing = true;
                        Decision = $"Loot clear → stash ({invCount} items)";
                        _phase = SimPhase.BetweenWaveStash;
                        _phaseStartTime = DateTime.Now;
                        StatusText = $"Stashing items ({invCount} in inventory)";
                        return;
                    }
                    _isStashing = false;
                }
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
                if (TryPortalRetry(ctx, $"stuck between waves for {BetweenWaveTimeoutSeconds}s")) return;
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
                if (TryPortalRetry(ctx, $"failed to start wave after {MaxWaveStartAttempts} attempts")) return;
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
                var nearestPos = FindNearestNonBlacklisted(gc, playerPos, ctx.Combat.BlacklistedEnemies);
                if (nearestPos.HasValue)
                {
                    _wasSearching = true;
                    var monsterDist = Vector2.Distance(playerPos, nearestPos.Value);
                    if (monsterDist > 20f)
                    {
                        if (ctx.Navigation.IsNavigating)
                            ctx.Navigation.UpdateDestination(gc, nearestPos.Value, driftThreshold: 15f);
                        else
                            ctx.Navigation.NavigateTo(gc, nearestPos.Value);
                    }
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
                // If map is fully explored but we are still searching, reset to sweep again
                if (ctx.Exploration.ActiveBlobCoverage >= 0.98f)
                {
                    ctx.Exploration.ResetSeen();
                    ctx.Log("[Sim] Map fully explored, resetting coverage to find stragglers");
                }

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
        private Vector2? FindNearestNonBlacklisted(GameController gc, Vector2 playerGrid, HashSet<string> enemyBlacklist)
        {
            float nearestDist = float.MaxValue;
            Vector2? nearestPos = null;

            foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
            {
                if (entity.Type != EntityType.Monster || !entity.IsHostile || !entity.IsAlive || !entity.IsTargetable)
                    continue;
                if (_blacklistedMonsters.ContainsKey(entity.Id))
                    continue;
                if (enemyBlacklist.Count > 0 && !string.IsNullOrEmpty(entity.RenderName) &&
                    enemyBlacklist.Contains(entity.RenderName)) continue;

                var dist = Vector2.Distance(entity.GridPosNum, playerGrid);
                if (dist < nearestDist)
                {
                    nearestDist = dist;
                    nearestPos = entity.GridPosNum;
                }
            }

            return nearestPos;
        }

        /// <summary>
        /// Count raw WorldItem entities on the ground within <paramref name="radius"/> grid units
        /// of <paramref name="center"/> (or the player if center is null).
        /// Includes items hidden by the loot filter — used to wait for all drops to settle
        /// before starting the next wave.
        /// </summary>
        private static int CountRawGroundItems(GameController gc, Vector2? center, float radius)
        {
            var origin = center ?? gc.Player.GridPosNum;
            int count = 0;
            foreach (var entity in gc.EntityListWrapper.ValidEntitiesByType[EntityType.WorldItem])
            {
                if (entity.DistancePlayer > radius) continue;
                if (Vector2.Distance(entity.GridPosNum, origin) > radius) continue;
                count++;
            }
            return count;
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

            // Try 2: Click entity directly using bounds-based randomization
            if (BotInput.ClickEntity(gc, monolith))
            {
                _lastActionTime = DateTime.Now;
                _waveStartAttempts++;
                StatusText = $"Clicking monolith to start wave {_state.CurrentWave + 1} (attempt {_waveStartAttempts})";
            }
            else
            {
                StatusText = $"Monolith off screen or gate blocked — waiting";
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

                    if (BotInput.ClickLabel(gc, label.ClientRect))
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
                // Between-wave stash: store only — no withdrawals. Clear any stale settings
                // left over from the hideout flow (e.g. simulacrum fragment ExtraWithdrawals).
                ctx.Stash.WithdrawTabName = null;
                ctx.Stash.WithdrawFragmentPath = null;
                ctx.Stash.WithdrawCount = 0;
                ctx.Stash.ExtraWithdrawals.Clear();
                ctx.Stash.ItemFilter = ctx.Settings.Loot.UseOmenOfAmelioration.Value
                    ? si => !(si.PosX == 11 && si.PosY == 4)
                    : null;
                ctx.Stash.Start();
            }

            // Step 3: Tick StashSystem
            var result = ctx.Stash.Tick(gc, ctx.Navigation);

            switch (result)
            {
                case StashResult.Succeeded:
                    _phase = SimPhase.WaveCycle;
                    _phaseStartTime = DateTime.Now;
                    // Clear the stashing flag once inventory is actually empty — otherwise a single
                    // pickup en route to the monolith would immediately send the bot back to stash.
                    _isStashing = StashableItemCount(ctx) > 0;
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

            // Step 2: Stash items if inventory is above threshold (same check as between-waves)
            if (_state.StashPosition.HasValue)
            {
                var invCount = StashableItemCount(ctx);
                if (invCount >= _settings.StashItemThreshold.Value)
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
                        ctx.Stash.ItemFilter = ctx.Settings.Loot.UseOmenOfAmelioration.Value
                            ? si => !(si.PosX == 11 && si.PosY == 4)
                            : null;
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

        /// <summary>
        /// On the first give-up condition, bounce through the portal and re-enter the same instance.
        /// On the second, fall through to a normal LootSweep → ExitMap → new run.
        /// </summary>
        private bool TryPortalRetry(BotContext ctx, string reason)
        {
            if (_portalRetryUsed) return false;
            _portalRetryUsed = true;
            _isPortalRetry = true;
            _phase = SimPhase.ExitMap;
            _phaseStartTime = DateTime.Now;
            // Do NOT set _mapCompleted — OnAreaChanged will do StartPortalReentry instead of a new run
            ctx.Navigation.Stop(ctx.Game);
            if (ctx.Stash.IsBusy) ctx.Stash.Cancel(ctx.Game, ctx.Navigation);
            StatusText = $"{reason} — portal retry (rejoining instance)";
            Decision = $"Portal retry: {reason}";
            return true;
        }

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

            // Monolith
            if (_state.MonolithPosition.HasValue)
            {
                var monolithWorld = Systems.Pathfinding.GridToWorld3D(gc, _state.MonolithPosition.Value);
                g.DrawText("MONOLITH", cam.WorldToScreen(monolithWorld), SharpDX.Color.Purple);
                g.DrawCircleInWorld(monolithWorld, 30f, SharpDX.Color.Purple, 2f);
            }

            // Portal
            if (_state.PortalPosition.HasValue)
            {
                var portalWorld = Systems.Pathfinding.GridToWorld3D(gc, _state.PortalPosition.Value);
                g.DrawText("PORTAL", cam.WorldToScreen(portalWorld) + new Vector2(-20, -15),
                    SharpDX.Color.Aqua);
                g.DrawCircleInWorld(portalWorld, 20f, SharpDX.Color.Aqua, 1.5f);
            }

            // Stash
            if (_state.StashPosition.HasValue)
            {
                g.DrawText("STASH", Systems.Pathfinding.GridToScreen(gc, _state.StashPosition.Value) + new Vector2(-15, -15),
                    SharpDX.Color.Gold);
            }

            // Navigation path
            if (ctx.Navigation.IsNavigating)
            {
                var path = ctx.Navigation.CurrentNavPath;
                for (int i = ctx.Navigation.CurrentWaypointIndex; i < path.Count - 1; i++)
                {
                    var from = Systems.Pathfinding.GridToScreen(gc, path[i].Position);
                    var to = Systems.Pathfinding.GridToScreen(gc, path[i + 1].Position);
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

        /// <summary>
        /// Start the hideout flow for a new simulacrum run, including omen replenishment if configured.
        /// </summary>
        private void StartHideoutFlow(BotContext ctx)
        {
            var s = ctx.Settings.Simulacrum;
            var omenTab = s.OmenTabName.Value;
            var omenPath = "AncestralOmenOnDeathPreventExpLoss";
            bool useOmen = ctx.Settings.Loot.UseOmenOfAmelioration.Value && !string.IsNullOrWhiteSpace(omenTab);

            // Build extra withdrawals: full simulacrums (with auto-detect fallback)
            var extras = new List<(string, string, int, int)>();
            var simTab = string.IsNullOrWhiteSpace(s.SimulacrumTabName.Value)
                ? (ctx.StashIndex.SimulacrumTabName ?? ctx.StashIndex.DeliriumTab?.Name ?? ctx.StashIndex.FragmentTab?.Name ?? ctx.StashIndex.CurrencyTab?.Name ?? "")
                : s.SimulacrumTabName.Value;

            int simCount = s.SimulacrumWithdrawCount.Value;
            bool needSim = simCount > 0 || (s.WithdrawFullSimulacrum.Value && StashSystem.CountInventoryItems(ctx.Game, "CurrencyAfflictionFragment") == 0);
            
            if (needSim)
            {
                int amount = simCount > 0 ? simCount : 1;
                // simTab may be empty if the stash index hasn't run yet — HideoutFlow resolves it lazily
                extras.Add((simTab, "CurrencyAfflictionFragment", amount, 0));
            }

            _hideoutFlow.Start(MapDeviceSystem.IsSimulacrum,
                dumpTabNames: BuildDumpTabList(s.DumpTabName.Value, s.DumpTabOverflow.Value),
                resourceTabName: useOmen ? omenTab : null,
                withdrawFragmentPath: useOmen ? omenPath : null,
                inventoryFragmentPath: "CurrencyAfflictionFragment",
                fragmentStock: useOmen ? 1 : 0,
                minFragments: useOmen ? 1 : 0,
                fragmentRequired: false,
                extraWithdrawals: extras.Count > 0 ? extras : null,
                deviceStorageRefillThreshold: ctx.Settings.Simulacrum.DeviceStorageRefillThreshold.Value);
        }

        private static List<string>? BuildDumpTabList(string primary, string overflow)
        {
            var tabs = new List<string>();
            if (!string.IsNullOrWhiteSpace(primary)) tabs.Add(primary.Trim());
            if (!string.IsNullOrWhiteSpace(overflow))
                foreach (var t in overflow.Split(','))
                    if (!string.IsNullOrWhiteSpace(t)) tabs.Add(t.Trim());
            return tabs.Count > 0 ? tabs : null;
        }

        /// <summary>
        /// Count inventory items that will actually be stashed (excluding the reserved omen slot if enabled).
        /// </summary>
        private static int StashableItemCount(BotContext ctx)
        {
            var items = StashSystem.GetInventorySlotItems(ctx.Game);
            if (items == null) return 0;
            if (!ctx.Settings.Loot.UseOmenOfAmelioration.Value) return items.Count;
            int count = 0;
            foreach (var si in items)
                if (!(si.PosX == 11 && si.PosY == 4)) count++;
            return count;
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
