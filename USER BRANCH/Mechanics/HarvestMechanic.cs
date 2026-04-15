using ExileCore;
using ExileCore.PoEMemory;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.Elements;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using AutoExile.Systems;
using SharpDX;
using Pathfinding = AutoExile.Systems.Pathfinding;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;
using RectangleF = SharpDX.RectangleF;

namespace AutoExile.Mechanics
{
    /// <summary>
    /// Harvest encounter mechanic handler.
    ///
    /// Lifecycle:
    ///   Detect entrance portal in map → Navigate → Enter Sacred Grove (blob transition) →
    ///   For each crop plot pair: navigate → score both irrigator labels → click best one →
    ///   fight spawned monsters → loot → next pair → return to map via exit portal
    ///
    /// Entity structure in Sacred Grove:
    ///   - Extractor (8x): has MinimapIcon, not targetable. Navigate to these.
    ///   - Irrigator (8x): has StateMachine + Targetable. Ground label shows monsters.
    ///     Irrigators are paired (2 per crop plot). Player picks one, other becomes inactive.
    ///   - Return portal: HarvestPortalToggleableReverseReturn
    ///
    /// Irrigator ground label structure:
    ///   label[0] = button area (disperse lifeforce click target)
    ///     [0.1] = clickable button element
    ///   label[1] = monster list panel
    ///     [1.0][3] = visible monster rows
    ///       [row][0] = text element: "&lt;white&gt;{count}&lt;default&gt;{ x }MonsterName"
    ///                  TextColor encodes rarity: (127,127,127)=Normal, (200,200,200)=Magic, (184,218,242)=Rare
    ///
    /// Irrigator StateMachine states:
    ///   colour: 1=Wild, 2=Vivid, 3=Primal
    ///   in_combat: UNRELIABLE — stays 0 during encounters
    ///   current_state: stays 0 — NOT useful for tracking
    ///
    /// Extractor StateMachine states (AUTHORITATIVE encounter tracker):
    ///   current_state: 0=untouched, 2=active encounter, 4=completed
    ///   active: 0 or 1 — involved in any encounter
    ///   start_encounter: 0=not started, 1=in progress, 2=done
    ///
    /// While any Extractor has current_state==2, ALL other irrigators are locked
    /// (cannot start new encounters until monsters from active encounter are killed).
    /// </summary>
    public class HarvestMechanic : IMapMechanic
    {
        public string Name => "Harvest";
        public string Status { get; private set; } = "";
        public Vector2? AnchorGridPos { get; private set; }
        public bool IsEncounterActive => _phase is HarvestPhase.Fighting
                                            or HarvestPhase.WaitForCombat;
        public bool IsComplete => _phase is HarvestPhase.Complete
                                       or HarvestPhase.Abandoned
                                       or HarvestPhase.Failed;

        // ── Phase machine ──
        private HarvestPhase _phase = HarvestPhase.Idle;
        private DateTime _phaseStartTime = DateTime.Now;

        // ── Entity references ──
        private Entity? _entrancePortal;
        private Entity? _returnPortal;

        // ── Irrigator pair tracking ──
        private readonly List<IrrigatorPair> _pairs = new();
        private IrrigatorPair? _currentPair;
        private int _plotsCompleted;

        // ── Blob transition detection ──
        private Vector2 _preTransitionPos;
        private bool _allPlotsComplete;

        // ── Combat profile ──
        private CombatProfile? _savedCombatProfile;

        // ── Click state ──
        private DateTime _lastActionTime = DateTime.MinValue;
        private bool _buttonClicked;

        // ── Loot tracking ──
        private DateTime _lootStartTime;
        private DateTime _lastLootScan = DateTime.MinValue;
        private uint _pendingLootId;
        private string? _pendingLootName;
        private double _pendingLootValue;

        public enum HarvestPhase
        {
            Idle,
            Detected,
            Navigating,         // Walking to entrance portal
            EnterGrove,         // Clicking entrance, waiting for blob transition
            GroveSettle,        // Waiting for entities to load after transition
            FindPlot,           // Scanning for next unfinished irrigator pair
            NavigateToPlot,     // Walking to selected irrigator pair
            ScoreAndSelect,     // Reading labels, scoring, clicking best choice
            WaitForCombat,      // Waiting for in_combat=1 after clicking
            Fighting,           // Combat active, monsters alive
            LootPlot,           // Brief loot sweep after fight
            NavigateReturn,     // Walking to return portal
            ExitGrove,          // Clicking return portal, waiting for transition back
            Complete,
            Abandoned,
            Failed,
        }

        // ── Public state for overlay ──
        public HarvestPhase Phase => _phase;
        public int PlotsCompleted => _plotsCompleted;
        public int TotalPlots => _pairs.Count;
        public IrrigatorPair? CurrentPair => _currentPair;

        // ══════════════════════════════════════════════════════════════
        // Irrigator pair data
        // ══════════════════════════════════════════════════════════════

        public class IrrigatorPair
        {
            public Entity A = null!;
            public Entity B = null!;
            public Entity? ExtractorA;   // Paired Extractor for irrigator A (authoritative state)
            public Entity? ExtractorB;   // Paired Extractor for irrigator B
            public Entity? Selected;
            public bool IsFinished;
            public int ColourA;     // 1=Wild, 2=Vivid, 3=Primal
            public int ColourB;
            public float ScoreA;
            public float ScoreB;
            public string ScoreDetailA = "";
            public string ScoreDetailB = "";
        }

        // ══════════════════════════════════════════════════════════════
        // Detection
        // ══════════════════════════════════════════════════════════════

        public bool Detect(BotContext ctx)
        {
            if (_phase != HarvestPhase.Idle) return _entrancePortal != null || _returnPortal != null;

            var gc = ctx.Game;
            bool foundIrrigator = false;

            foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
            {
                if (entity.Path == null) continue;

                // Match entrance portal (in map, not yet entered grove)
                if (entity.Path == "Metadata/Terrain/Leagues/Harvest/Objects/HarvestPortalToggleableReverse"
                    && entity.IsTargetable)
                {
                    _entrancePortal = entity;
                    AnchorGridPos = new Vector2(entity.GridPosNum.X, entity.GridPosNum.Y);
                    _phase = HarvestPhase.Detected;
                    Status = $"Detected entrance at ({AnchorGridPos.Value.X:F0}, {AnchorGridPos.Value.Y:F0})";
                    return true;
                }

                // Detect irrigators (already inside grove)
                if (entity.Path == "Metadata/MiscellaneousObjects/Harvest/Irrigator" && entity.IsTargetable)
                    foundIrrigator = true;

                // Detect return portal (already inside grove)
                if (entity.Path == "Metadata/Terrain/Leagues/Harvest/Objects/HarvestPortalToggleableReverseReturn")
                    _returnPortal = entity;
            }

            // Already inside the Sacred Grove — skip entrance phases, go straight to settle
            if (foundIrrigator || _returnPortal != null)
            {
                var playerGrid = new Vector2(gc.Player.GridPosNum.X, gc.Player.GridPosNum.Y);
                AnchorGridPos = playerGrid;
                _phase = HarvestPhase.GroveSettle;
                _phaseStartTime = DateTime.Now;
                Status = "Already inside Sacred Grove — scanning irrigators";
                ctx.Log("[Harvest] Detected mid-grove (irrigators/return portal found), skipping entrance");
                return true;
            }

            return false;
        }

        // ══════════════════════════════════════════════════════════════
        // Tick
        // ══════════════════════════════════════════════════════════════

        public MechanicResult Tick(BotContext ctx)
        {
            var gc = ctx.Game;
            if (gc?.Player == null || !gc.InGame) return MechanicResult.InProgress;

            // Check for player death during grove phases
            if (!gc.Player.IsAlive && _phase is HarvestPhase.Fighting or HarvestPhase.ScoreAndSelect
                    or HarvestPhase.WaitForCombat or HarvestPhase.NavigateToPlot)
            {
                _phase = HarvestPhase.Failed;
                Status = "Died in Sacred Grove";
                RestoreCombatProfile(ctx);
                return MechanicResult.Failed;
            }

            switch (_phase)
            {
                case HarvestPhase.Detected:
                case HarvestPhase.Navigating:
                    return TickNavigating(ctx, gc);

                case HarvestPhase.EnterGrove:
                    return TickEnterGrove(ctx, gc);

                case HarvestPhase.GroveSettle:
                    return TickGroveSettle(ctx, gc);

                case HarvestPhase.FindPlot:
                    return TickFindPlot(ctx, gc);

                case HarvestPhase.NavigateToPlot:
                    return TickNavigateToPlot(ctx, gc);

                case HarvestPhase.ScoreAndSelect:
                    return TickScoreAndSelect(ctx, gc);

                case HarvestPhase.WaitForCombat:
                    return TickWaitForCombat(ctx, gc);

                case HarvestPhase.Fighting:
                    return TickFighting(ctx, gc);

                case HarvestPhase.LootPlot:
                    return TickLootPlot(ctx, gc);

                case HarvestPhase.NavigateReturn:
                    return TickNavigateReturn(ctx, gc);

                case HarvestPhase.ExitGrove:
                    return TickExitGrove(ctx, gc);

                case HarvestPhase.Complete:
                    return MechanicResult.Complete;
                case HarvestPhase.Abandoned:
                    return MechanicResult.Abandoned;
                case HarvestPhase.Failed:
                    return MechanicResult.Failed;

                default:
                    return MechanicResult.Idle;
            }
        }

        // ── Navigate to entrance portal ──

        private MechanicResult TickNavigating(BotContext ctx, GameController gc)
        {
            if (AnchorGridPos == null) return MechanicResult.Failed;

            var playerGrid = new Vector2(gc.Player.GridPosNum.X, gc.Player.GridPosNum.Y);
            var dist = Vector2.Distance(playerGrid, AnchorGridPos.Value);

            // Close enough to click entrance
            if (dist <= ctx.Interaction.InteractRadius)
            {
                _phase = HarvestPhase.EnterGrove;
                _phaseStartTime = DateTime.Now;
                _preTransitionPos = playerGrid;
                _lastActionTime = DateTime.MinValue;
                Status = "At entrance, clicking to enter grove";
                ctx.Log("[Harvest] Arrived at entrance portal");
                return MechanicResult.InProgress;
            }

            if (!ctx.Navigation.IsNavigating)
            {
                if (!ctx.Navigation.NavigateTo(gc, AnchorGridPos.Value))
                {
                    _phase = HarvestPhase.Failed;
                    Status = "Cannot path to entrance";
                    return MechanicResult.Failed;
                }
            }

            _phase = HarvestPhase.Navigating;
            Status = $"Navigating to entrance (dist={dist:F0})";
            return MechanicResult.InProgress;
        }

        // ── Click entrance and detect blob transition ──

        private MechanicResult TickEnterGrove(BotContext ctx, GameController gc)
        {
            var playerGrid = new Vector2(gc.Player.GridPosNum.X, gc.Player.GridPosNum.Y);

            // Detect blob transition: player position jumped significantly
            if (Vector2.Distance(playerGrid, _preTransitionPos) > 100f)
            {
                _phase = HarvestPhase.GroveSettle;
                _phaseStartTime = DateTime.Now;
                Status = "Entered grove, waiting for entities to load";
                ctx.Log("[Harvest] Blob transition detected, settling...");
                return MechanicResult.InProgress;
            }

            // Click the entrance portal
            if (BotInput.CanAct && (DateTime.Now - _lastActionTime).TotalSeconds > 2)
            {
                if (_entrancePortal != null && _entrancePortal.IsTargetable)
                {
                    if (BotInput.ClickEntity(gc, _entrancePortal))
                    {
                        _lastActionTime = DateTime.Now;
                        Status = "Clicked entrance portal...";
                        ctx.Log("[Harvest] Clicked entrance portal");
                    }
                }
            }

            if ((DateTime.Now - _phaseStartTime).TotalSeconds > 10)
            {
                _phase = HarvestPhase.Failed;
                Status = "Timeout: entrance transition not detected";
                return MechanicResult.Failed;
            }

            return MechanicResult.InProgress;
        }

        // ── Wait for grove entities to load, build irrigator pairs ──

        private MechanicResult TickGroveSettle(BotContext ctx, GameController gc)
        {
            // Wait 2s for entities to load
            if ((DateTime.Now - _phaseStartTime).TotalSeconds < 2)
            {
                Status = "Waiting for grove entities to load...";
                return MechanicResult.InProgress;
            }

            // Scan for irrigators and extractors
            var irrigators = new List<Entity>();
            var extractors = new List<Entity>();
            foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
            {
                if (entity.Path == null) continue;
                if (entity.Path == "Metadata/MiscellaneousObjects/Harvest/Irrigator" && entity.IsTargetable)
                    irrigators.Add(entity);
                else if (entity.Path.Contains("Harvest/Extractor"))
                    extractors.Add(entity);
            }

            if (irrigators.Count == 0)
            {
                // Check if there are non-targetable irrigators (all encounters completed)
                bool hasCompletedIrrigators = false;
                foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
                {
                    if (entity.Path == "Metadata/MiscellaneousObjects/Harvest/Irrigator" && !entity.IsTargetable)
                    {
                        hasCompletedIrrigators = true;
                        break;
                    }
                }

                if (hasCompletedIrrigators)
                {
                    // All encounters done — loot sweep then return
                    ctx.Log("[Harvest] All irrigators completed, loot sweep before return");
                    _phase = HarvestPhase.LootPlot;
                    _phaseStartTime = DateTime.Now;
                    _lootStartTime = DateTime.Now;
                    _allPlotsComplete = true;
                    Status = "All encounters complete, looting before return";
                    return MechanicResult.InProgress;
                }

                // Retry for up to 10s total
                if ((DateTime.Now - _phaseStartTime).TotalSeconds > 10)
                {
                    _phase = HarvestPhase.Failed;
                    Status = "No irrigators found in grove";
                    return MechanicResult.Failed;
                }
                Status = "Waiting for irrigators...";
                return MechanicResult.InProgress;
            }

            // Find return portal
            foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
            {
                if (entity.Path == "Metadata/Terrain/Leagues/Harvest/Objects/HarvestPortalToggleableReverseReturn")
                {
                    _returnPortal = entity;
                    break;
                }
            }

            // Build irrigator pairs by proximity, associate extractors
            BuildPairs(irrigators, extractors, ctx);

            if (_pairs.Count == 0)
            {
                _phase = HarvestPhase.Failed;
                Status = "Could not pair irrigators";
                return MechanicResult.Failed;
            }

            // Enter new exploration blob
            var terrain = gc.IngameState.Data.RawPathfindingData;
            var targeting = gc.IngameState.Data.RawTerrainTargetingData;
            if (terrain != null && targeting != null)
            {
                var playerGrid = new Vector2(gc.Player.GridPosNum.X, gc.Player.GridPosNum.Y);
                ctx.Exploration.EnterNewBlob(terrain, targeting, playerGrid,
                    ctx.Settings.Build.BlinkRange.Value);
            }

            // Enable combat
            SaveAndOverrideCombatProfile(ctx);

            _phase = HarvestPhase.FindPlot;
            _phaseStartTime = DateTime.Now;
            Status = $"Grove loaded: {_pairs.Count} crop plots found";
            ctx.Log($"[Harvest] Grove settled: {irrigators.Count} irrigators, {extractors.Count} extractors, {_pairs.Count} pairs, return portal {(_returnPortal != null ? "found" : "NOT found")}");
            foreach (var pair in _pairs)
                ctx.Log($"[Harvest]   Pair: A={pair.A.Id}@({pair.A.GridPosNum.X:F0},{pair.A.GridPosNum.Y:F0}) B={pair.B.Id}@({pair.B.GridPosNum.X:F0},{pair.B.GridPosNum.Y:F0}) ExtA={pair.ExtractorA?.Id} ExtB={pair.ExtractorB?.Id} ExtStateA={GetExtractorCurrentState(pair.ExtractorA)} ExtStateB={GetExtractorCurrentState(pair.ExtractorB)}");
            return MechanicResult.InProgress;
        }

        // ── Find next unfinished irrigator pair ──

        private MechanicResult TickFindPlot(BotContext ctx, GameController gc)
        {
            var playerGrid = new Vector2(gc.Player.GridPosNum.X, gc.Player.GridPosNum.Y);

            // Update pair finished state from Extractor current_state
            foreach (var pair in _pairs)
            {
                if (pair.IsFinished) continue;
                // Extractor current_state==4 means completed
                var stateA = GetExtractorCurrentState(pair.ExtractorA);
                var stateB = GetExtractorCurrentState(pair.ExtractorB);
                pair.IsFinished = stateA == 4 || stateB == 4
                               || (!pair.A.IsTargetable && !pair.B.IsTargetable);
            }

            // If any encounter is active globally, don't try to start a new plot
            if (IsAnyEncounterActive())
            {
                Status = "Waiting for active encounter to finish...";
                // Run combat while waiting
                ctx.Combat.Tick(ctx);
                return MechanicResult.InProgress;
            }

            // Find nearest unfinished pair (by closest irrigator, not midpoint)
            IrrigatorPair? best = null;
            float bestDist = float.MaxValue;
            foreach (var pair in _pairs)
            {
                if (pair.IsFinished) continue;
                var posA = new Vector2(pair.A.GridPosNum.X, pair.A.GridPosNum.Y);
                var posB = new Vector2(pair.B.GridPosNum.X, pair.B.GridPosNum.Y);
                var dist = Math.Min(Vector2.Distance(playerGrid, posA), Vector2.Distance(playerGrid, posB));
                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = pair;
                }
            }

            if (best == null)
            {
                // All plots done — do a final loot sweep before leaving
                _phase = HarvestPhase.LootPlot;
                _phaseStartTime = DateTime.Now;
                _lootStartTime = DateTime.Now;
                _allPlotsComplete = true;
                Status = $"All {_plotsCompleted} plots complete, final loot sweep";
                ctx.Log($"[Harvest] All plots finished ({_plotsCompleted}), final loot sweep before return");
                return MechanicResult.InProgress;
            }

            _currentPair = best;
            _phase = HarvestPhase.NavigateToPlot;
            _phaseStartTime = DateTime.Now;
            _buttonClicked = false;
            Status = $"Heading to plot (dist={bestDist:F0})";
            return MechanicResult.InProgress;
        }

        // ── Navigate to selected irrigator pair ──

        private int _navRetries;

        private MechanicResult TickNavigateToPlot(BotContext ctx, GameController gc)
        {
            if (_currentPair == null) return MechanicResult.Failed;

            var playerGrid = new Vector2(gc.Player.GridPosNum.X, gc.Player.GridPosNum.Y);
            // Navigate to nearest irrigator directly (midpoint may be in a wall)
            var targetA = new Vector2(_currentPair.A.GridPosNum.X, _currentPair.A.GridPosNum.Y);
            var targetB = new Vector2(_currentPair.B.GridPosNum.X, _currentPair.B.GridPosNum.Y);
            var distA = Vector2.Distance(playerGrid, targetA);
            var distB = Vector2.Distance(playerGrid, targetB);
            var navTarget = distA <= distB ? targetA : targetB;
            var dist = Math.Min(distA, distB);

            // Close enough for labels to be visible
            if (dist <= 30f)
            {
                _phase = HarvestPhase.ScoreAndSelect;
                _phaseStartTime = DateTime.Now;
                _buttonClicked = false;
                _navRetries = 0;
                Status = "At plot, scoring crops...";
                return MechanicResult.InProgress;
            }

            // Run combat while navigating — suppress positioning so it doesn't fight navigation
            ctx.Combat.SuppressPositioning = true;
            ctx.Combat.Tick(ctx);
            ctx.Combat.SuppressPositioning = false;

            if (!ctx.Navigation.IsNavigating)
            {
                ctx.Log($"[Harvest] NavigateTo grid=({navTarget.X:F0},{navTarget.Y:F0}) playerGrid=({playerGrid.X:F0},{playerGrid.Y:F0}) dist={dist:F0}");
                if (!ctx.Navigation.NavigateTo(gc, navTarget))
                {
                    ctx.Log($"[Harvest] NavigateTo FAILED. FramePF null={gc.IngameState.Data.RawFramePathfindingData == null}");
                    _navRetries++;
                    // Try the other irrigator on second attempt
                    if (_navRetries == 1)
                    {
                        var altTarget = navTarget == targetA ? targetB : targetA;
                        if (ctx.Navigation.NavigateTo(gc, altTarget))
                        {
                            Status = "Retrying path to other irrigator...";
                            return MechanicResult.InProgress;
                        }
                    }
                    if (_navRetries >= 3)
                    {
                        _currentPair.IsFinished = true;
                        _phase = HarvestPhase.FindPlot;
                        _phaseStartTime = DateTime.Now;
                        _navRetries = 0;
                        Status = "Cannot path to plot after retries, skipping";
                        ctx.Log($"[Harvest] Gave up pathing to irrigator pair after {_navRetries} retries");
                        return MechanicResult.InProgress;
                    }
                    Status = $"Path failed, retry {_navRetries}/3...";
                    return MechanicResult.InProgress;
                }
            }

            if ((DateTime.Now - _phaseStartTime).TotalSeconds > 30)
            {
                _currentPair.IsFinished = true;
                _phase = HarvestPhase.FindPlot;
                _navRetries = 0;
                Status = "Timeout navigating to plot";
                return MechanicResult.InProgress;
            }

            Status = $"Navigating to plot (dist={dist:F0})";
            return MechanicResult.InProgress;
        }

        // ── Score both irrigators and click the best one ──

        private MechanicResult TickScoreAndSelect(BotContext ctx, GameController gc)
        {
            if (_currentPair == null) return MechanicResult.Failed;

            var settings = ctx.Settings.Mechanics.Harvest;

            // Already clicked, waiting for confirmation
            if (_buttonClicked)
            {
                // Check if selection was accepted via Extractor state:
                // Extractor current_state transitions from 0 → 2 (active encounter)
                var extractor = (_currentPair.Selected == _currentPair.A)
                    ? _currentPair.ExtractorA : _currentPair.ExtractorB;
                var extState = GetExtractorCurrentState(extractor);
                if (extState >= 2 || !_currentPair.Selected!.IsTargetable)
                {
                    _phase = HarvestPhase.WaitForCombat;
                    _phaseStartTime = DateTime.Now;
                    Status = "Crop selected, waiting for combat...";
                    ctx.Log($"[Harvest] Selection confirmed (extractor {extractor?.Id} current_state={extState})");
                    return MechanicResult.InProgress;
                }

                // Retry click after 3s
                if ((DateTime.Now - _lastActionTime).TotalSeconds > 3)
                {
                    _buttonClicked = false;
                    ctx.Log("[Harvest] Selection not confirmed, retrying click");
                }

                if ((DateTime.Now - _phaseStartTime).TotalSeconds > 15)
                {
                    _currentPair.IsFinished = true;
                    _phase = HarvestPhase.FindPlot;
                    Status = "Timeout: crop selection never confirmed";
                    return MechanicResult.InProgress;
                }

                Status = "Waiting for selection to confirm...";
                return MechanicResult.InProgress;
            }

            // Find ground labels for both irrigators
            var labelA = FindIrrigatorLabel(gc, _currentPair.A);
            var labelB = FindIrrigatorLabel(gc, _currentPair.B);

            if (labelA == null && labelB == null)
            {
                // Labels not visible yet — nudge closer
                if ((DateTime.Now - _phaseStartTime).TotalSeconds > 10)
                {
                    _currentPair.IsFinished = true;
                    _phase = HarvestPhase.FindPlot;
                    Status = "Timeout: labels not visible";
                    return MechanicResult.InProgress;
                }

                // Navigate closer to irrigator A
                var posA = new Vector2(_currentPair.A.GridPosNum.X, _currentPair.A.GridPosNum.Y);
                if (!ctx.Navigation.IsNavigating)
                    ctx.Navigation.NavigateTo(gc, posA);

                Status = "Moving closer to read labels...";
                return MechanicResult.InProgress;
            }

            // Score both irrigators
            string detailA = "N/A", detailB = "N/A";
            float scoreA = labelA != null ? ScoreIrrigatorLabel(labelA, settings, out detailA) : -1;
            float scoreB = labelB != null ? ScoreIrrigatorLabel(labelB, settings, out detailB) : -1;

            if (scoreA < 0 && scoreB < 0)
            {
                // Can't read either label
                _currentPair.IsFinished = true;
                _phase = HarvestPhase.FindPlot;
                Status = "Cannot parse either irrigator label";
                return MechanicResult.InProgress;
            }

            // Store scores for overlay
            _currentPair.ScoreA = scoreA;
            _currentPair.ScoreB = scoreB;
            _currentPair.ScoreDetailA = detailA;
            _currentPair.ScoreDetailB = detailB;

            // Apply per-type multiplier (Wild/Vivid/Primal)
            _currentPair.ColourA = (int)GetEntityState(_currentPair.A, "colour");
            _currentPair.ColourB = (int)GetEntityState(_currentPair.B, "colour");
            if (scoreA > 0) scoreA *= GetTypeMultiplier(_currentPair.ColourA, settings);
            if (scoreB > 0) scoreB *= GetTypeMultiplier(_currentPair.ColourB, settings);

            // Apply preferred colour bonus on top
            var preferredColour = GetPreferredColourValue(settings.PreferredColour.Value);
            if (preferredColour > 0)
            {
                if (_currentPair.ColourA == preferredColour && scoreA > 0)
                    scoreA *= settings.ColourPreferenceBonus.Value;
                if (_currentPair.ColourB == preferredColour && scoreB > 0)
                    scoreB *= settings.ColourPreferenceBonus.Value;
            }

            // Pick the winner
            Entity winner;
            Element? winnerLabel;
            if (scoreA >= scoreB)
            {
                winner = _currentPair.A;
                winnerLabel = labelA;
            }
            else
            {
                winner = _currentPair.B;
                winnerLabel = labelB;
            }
            _currentPair.Selected = winner;

            // Click the button on the winner's label
            if (!BotInput.CanAct) return MechanicResult.InProgress;

            var button = GetDisperseButton(winnerLabel!);
            if (button == null)
            {
                Status = "Disperse button not found on label";
                if ((DateTime.Now - _phaseStartTime).TotalSeconds > 10)
                {
                    _currentPair.IsFinished = true;
                    _phase = HarvestPhase.FindPlot;
                }
                return MechanicResult.InProgress;
            }

            ClickElement(gc, button);
            _buttonClicked = true;
            _lastActionTime = DateTime.Now;

            var colourName = GetColourName((int)GetEntityState(winner, "colour"));
            Status = $"Selected {colourName} (score={Math.Max(scoreA, scoreB):F0} vs {Math.Min(scoreA, scoreB):F0})";
            ctx.Log($"[Harvest] Selected irrigator {winner.Id} ({colourName}), scoreA={scoreA:F0} scoreB={scoreB:F0}");
            return MechanicResult.InProgress;
        }

        // ── Wait for combat to start ──

        private MechanicResult TickWaitForCombat(BotContext ctx, GameController gc)
        {
            if (_currentPair?.Selected == null) return MechanicResult.Failed;

            // Check Extractor current_state==2 (active encounter) as primary indicator
            var extractor = (_currentPair.Selected == _currentPair.A)
                ? _currentPair.ExtractorA : _currentPair.ExtractorB;
            var extState = GetExtractorCurrentState(extractor);
            if (extState == 2)
            {
                _phase = HarvestPhase.Fighting;
                _phaseStartTime = DateTime.Now;
                Status = "Fighting harvest monsters";
                ctx.Log($"[Harvest] Combat started (extractor {extractor?.Id} current_state=2)");
                return MechanicResult.InProgress;
            }

            // Run combat while waiting (monsters may already be spawning)
            ctx.Combat.Tick(ctx);

            if ((DateTime.Now - _phaseStartTime).TotalSeconds > 5)
            {
                // Maybe combat already started and ended, or state didn't update
                // Check for nearby hostile monsters as fallback
                var playerGrid = new Vector2(gc.Player.GridPosNum.X, gc.Player.GridPosNum.Y);
                bool hasMonsters = false;
                foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
                {
                    if (entity.Type != EntityType.Monster) continue;
                    if (!entity.IsHostile || !entity.IsAlive) continue;
                    var mGrid = new Vector2(entity.GridPosNum.X, entity.GridPosNum.Y);
                    if (Vector2.Distance(playerGrid, mGrid) < 60)
                    {
                        hasMonsters = true;
                        break;
                    }
                }

                if (hasMonsters)
                {
                    _phase = HarvestPhase.Fighting;
                    _phaseStartTime = DateTime.Now;
                    Status = "Fighting (detected nearby monsters)";
                    return MechanicResult.InProgress;
                }
            }

            if ((DateTime.Now - _phaseStartTime).TotalSeconds > 15)
            {
                // Combat never started — mark pair done and move on
                _currentPair.IsFinished = true;
                _plotsCompleted++;
                _phase = HarvestPhase.FindPlot;
                Status = "Combat did not start, moving to next plot";
                ctx.Log("[Harvest] WaitForCombat timeout, skipping to next plot");
                return MechanicResult.InProgress;
            }

            Status = "Waiting for monsters to spawn...";
            return MechanicResult.InProgress;
        }

        // ── Fighting: combat system handles skills ──

        private MechanicResult TickFighting(BotContext ctx, GameController gc)
        {
            // Suppress combat positioning when leash will pull us back,
            // otherwise combat movement fights navigation for cursor control
            bool leashing = false;
            if (_currentPair?.Selected != null)
            {
                var irrigatorPos = new Vector2(_currentPair.Selected.GridPosNum.X, _currentPair.Selected.GridPosNum.Y);
                var playerGrid = new Vector2(gc.Player.GridPosNum.X, gc.Player.GridPosNum.Y);
                leashing = Vector2.Distance(playerGrid, irrigatorPos) > 60;
            }
            ctx.Combat.SuppressPositioning = leashing;
            ctx.Combat.Tick(ctx);
            ctx.Combat.SuppressPositioning = false;

            if (_currentPair?.Selected == null) return MechanicResult.Failed;

            // Check if combat ended via Extractor state (current_state transitions 2 → 4)
            var extractor = (_currentPair.Selected == _currentPair.A)
                ? _currentPair.ExtractorA : _currentPair.ExtractorB;
            var extState = GetExtractorCurrentState(extractor);

            if (extState == 4 && (DateTime.Now - _phaseStartTime).TotalSeconds > 3)
            {
                // Extractor says encounter complete — verify no harvest monsters remain
                var playerGrid = new Vector2(gc.Player.GridPosNum.X, gc.Player.GridPosNum.Y);
                bool hasHarvestMonsters = false;
                foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
                {
                    if (entity.Path == null || !entity.Path.Contains("LeagueHarvest")) continue;
                    if (entity.Type != EntityType.Monster) continue;
                    if (!entity.IsAlive) continue;
                    var mGrid = new Vector2(entity.GridPosNum.X, entity.GridPosNum.Y);
                    if (Vector2.Distance(playerGrid, mGrid) < 120)
                    {
                        hasHarvestMonsters = true;
                        break;
                    }
                }

                if (!hasHarvestMonsters)
                {
                    _currentPair.IsFinished = true;
                    _plotsCompleted++;
                    _phase = HarvestPhase.LootPlot;
                    _phaseStartTime = DateTime.Now;
                    _lootStartTime = DateTime.Now;
                    Status = $"Plot {_plotsCompleted}/{_pairs.Count} cleared, looting...";
                    ctx.Log($"[Harvest] Plot fight complete ({_plotsCompleted}/{_pairs.Count}), extractor {extractor?.Id} current_state=4");
                    return MechanicResult.InProgress;
                }
            }
            // Also check: if extractor still shows active (2), but no monsters nearby for 10s+,
            // fall back to monster scan (in case extractor state is stale)
            else if (extState == 2 && (DateTime.Now - _phaseStartTime).TotalSeconds > 10)
            {
                var playerGrid = new Vector2(gc.Player.GridPosNum.X, gc.Player.GridPosNum.Y);
                bool hasMonsters = false;
                foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
                {
                    if (entity.Path == null || !entity.Path.Contains("LeagueHarvest")) continue;
                    if (entity.Type != EntityType.Monster) continue;
                    if (!entity.IsAlive) continue;
                    var mGrid = new Vector2(entity.GridPosNum.X, entity.GridPosNum.Y);
                    if (Vector2.Distance(playerGrid, mGrid) < 120)
                    {
                        hasMonsters = true;
                        break;
                    }
                }

                if (!hasMonsters)
                {
                    // Monsters dead but extractor hasn't updated yet — proceed anyway
                    _currentPair.IsFinished = true;
                    _plotsCompleted++;
                    _phase = HarvestPhase.LootPlot;
                    _phaseStartTime = DateTime.Now;
                    _lootStartTime = DateTime.Now;
                    Status = $"Plot {_plotsCompleted}/{_pairs.Count} cleared (no monsters), looting...";
                    ctx.Log($"[Harvest] Plot fight complete by monster scan ({_plotsCompleted}/{_pairs.Count}), extractor still shows state={extState}");
                    return MechanicResult.InProgress;
                }
            }

            // Leash to irrigator area
            if (_currentPair.Selected != null)
            {
                var irrigatorPos = new Vector2(_currentPair.Selected.GridPosNum.X, _currentPair.Selected.GridPosNum.Y);
                var playerGrid = new Vector2(gc.Player.GridPosNum.X, gc.Player.GridPosNum.Y);
                var dist = Vector2.Distance(playerGrid, irrigatorPos);
                if (dist > 60)
                {
                    ctx.Navigation.NavigateTo(gc, irrigatorPos);
                }
            }

            if ((DateTime.Now - _phaseStartTime).TotalSeconds > 120)
            {
                _currentPair.IsFinished = true;
                _plotsCompleted++;
                _phase = HarvestPhase.LootPlot;
                _lootStartTime = DateTime.Now;
                Status = "Fight timeout, moving on";
                return MechanicResult.InProgress;
            }

            Status = $"Fighting plot {_plotsCompleted + 1}/{_pairs.Count}";
            return MechanicResult.InProgress;
        }

        // ── Brief loot sweep ──

        private MechanicResult TickLootPlot(BotContext ctx, GameController gc)
        {
            var settings = ctx.Settings.Mechanics.Harvest;
            var elapsed = (DateTime.Now - _lootStartTime).TotalSeconds;

            // Run combat in case stragglers appear
            ctx.Combat.Tick(ctx);

            // Scan for loot periodically
            if ((DateTime.Now - _lastLootScan).TotalMilliseconds >= 500)
            {
                ctx.Loot.Scan(gc);
                _lastLootScan = DateTime.Now;
            }

            // Tick interaction system for pickup results
            if (ctx.Interaction.IsBusy)
            {
                var result = ctx.Interaction.Tick(gc);
                if (result == InteractionResult.Succeeded && _pendingLootName != null)
                {
                    ctx.Log($"[Harvest] Picked up: {_pendingLootName} ({_pendingLootValue:F0}c)");
                    _pendingLootName = null;
                }
                else if (result == InteractionResult.Failed && _pendingLootId > 0)
                {
                    ctx.Loot.MarkFailed(_pendingLootId);
                    _pendingLootName = null;
                }
                Status = $"Looting plot ({elapsed:F1}s)";
                return MechanicResult.InProgress;
            }

            // Pick up nearby items
            if (ctx.Loot.HasLootNearby)
            {
                var (wasInRadius, candidate) = ctx.Loot.PickupNext(ctx.Interaction, ctx.Navigation);
                if (candidate != null && ctx.Interaction.IsBusy)
                {
                    _pendingLootId = candidate.Entity.Id;
                    _pendingLootName = candidate.ItemName;
                    _pendingLootValue = candidate.ChaosValue;
                    Status = $"Picking up: {candidate.ItemName}";
                    // Reset loot timer when we find items
                    _lootStartTime = DateTime.Now;
                    return MechanicResult.InProgress;
                }
            }

            // Done looting after timeout with no items to pick up
            if (elapsed >= settings.LootSweepSeconds.Value)
            {
                if (_allPlotsComplete)
                {
                    _phase = HarvestPhase.NavigateReturn;
                    _phaseStartTime = DateTime.Now;
                    Status = "Loot sweep done, returning to map";
                    ctx.Log("[Harvest] Final loot sweep complete, heading to return portal");
                }
                else
                {
                    _phase = HarvestPhase.FindPlot;
                    _phaseStartTime = DateTime.Now;
                }
                return MechanicResult.InProgress;
            }

            Status = $"Looting plot ({elapsed:F1}s / {settings.LootSweepSeconds.Value}s)";
            return MechanicResult.InProgress;
        }

        // ── Navigate to return portal ──

        private MechanicResult TickNavigateReturn(BotContext ctx, GameController gc)
        {
            if (_returnPortal == null)
            {
                // Try to find it
                foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
                {
                    if (entity.Path == "Metadata/Terrain/Leagues/Harvest/Objects/HarvestPortalToggleableReverseReturn")
                    {
                        _returnPortal = entity;
                        break;
                    }
                }

                if (_returnPortal == null)
                {
                    if ((DateTime.Now - _phaseStartTime).TotalSeconds > 30)
                    {
                        _phase = HarvestPhase.Failed;
                        Status = "Return portal not found";
                        RestoreCombatProfile(ctx);
                        return MechanicResult.Failed;
                    }
                    Status = "Searching for return portal...";
                    return MechanicResult.InProgress;
                }
            }

            var playerGrid = new Vector2(gc.Player.GridPosNum.X, gc.Player.GridPosNum.Y);
            var portalGrid = new Vector2(_returnPortal.GridPosNum.X, _returnPortal.GridPosNum.Y);
            var dist = Vector2.Distance(playerGrid, portalGrid);

            if (dist <= ctx.Interaction.InteractRadius)
            {
                _phase = HarvestPhase.ExitGrove;
                _phaseStartTime = DateTime.Now;
                _preTransitionPos = playerGrid;
                _lastActionTime = DateTime.MinValue;
                Status = "At return portal, clicking to exit";
                ctx.Log("[Harvest] At return portal, exiting grove");
                return MechanicResult.InProgress;
            }

            // Run combat while walking back — suppress positioning so it doesn't fight navigation
            ctx.Combat.SuppressPositioning = true;
            ctx.Combat.Tick(ctx);
            ctx.Combat.SuppressPositioning = false;

            if (!ctx.Navigation.IsNavigating)
            {
                if (!ctx.Navigation.NavigateTo(gc, portalGrid))
                {
                    _phase = HarvestPhase.Failed;
                    Status = "Cannot path to return portal";
                    RestoreCombatProfile(ctx);
                    return MechanicResult.Failed;
                }
            }

            if ((DateTime.Now - _phaseStartTime).TotalSeconds > 60)
            {
                _phase = HarvestPhase.Failed;
                Status = "Timeout navigating to return portal";
                RestoreCombatProfile(ctx);
                return MechanicResult.Failed;
            }

            Status = $"Walking to return portal (dist={dist:F0})";
            return MechanicResult.InProgress;
        }

        // ── Click return portal and detect transition back ──

        private MechanicResult TickExitGrove(BotContext ctx, GameController gc)
        {
            var playerGrid = new Vector2(gc.Player.GridPosNum.X, gc.Player.GridPosNum.Y);

            // Detect blob transition back to map
            if (Vector2.Distance(playerGrid, _preTransitionPos) > 100f)
            {
                // Re-enter original blob
                var terrain = gc.IngameState.Data.RawPathfindingData;
                var targeting = gc.IngameState.Data.RawTerrainTargetingData;
                if (terrain != null && targeting != null)
                {
                    ctx.Exploration.EnterNewBlob(terrain, targeting, playerGrid,
                        ctx.Settings.Build.BlinkRange.Value);
                }

                RestoreCombatProfile(ctx);
                _phase = HarvestPhase.Complete;
                Status = $"Complete — {_plotsCompleted} plots cleared";
                ctx.Log($"[Harvest] Returned to map, {_plotsCompleted} plots completed");
                return MechanicResult.Complete;
            }

            // Click the return portal
            if (BotInput.CanAct && (DateTime.Now - _lastActionTime).TotalSeconds > 2)
            {
                if (_returnPortal != null && _returnPortal.IsTargetable)
                {
                    if (BotInput.ClickEntity(gc, _returnPortal))
                    {
                        _lastActionTime = DateTime.Now;
                        Status = "Clicked return portal...";
                    }
                }
            }

            if ((DateTime.Now - _phaseStartTime).TotalSeconds > 10)
            {
                _phase = HarvestPhase.Failed;
                Status = "Timeout: exit transition not detected";
                RestoreCombatProfile(ctx);
                return MechanicResult.Failed;
            }

            return MechanicResult.InProgress;
        }

        // ══════════════════════════════════════════════════════════════
        // Label parsing and scoring
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// Find the ground label for an irrigator entity.
        /// </summary>
        private Element? FindIrrigatorLabel(GameController gc, Entity irrigator)
        {
            foreach (var label in gc.IngameState.IngameUi.ItemsOnGroundLabelsVisible)
            {
                if (label.ItemOnGround != null &&
                    label.ItemOnGround.Id == irrigator.Id &&
                    label.Label?.IsVisible == true)
                {
                    return label.Label;
                }
            }
            return null;
        }

        /// <summary>
        /// Get the disperse lifeforce button element from an irrigator label.
        /// Button is at label[0][1] (the clickable area).
        /// </summary>
        private Element? GetDisperseButton(Element label)
        {
            if (label.ChildCount < 1) return null;
            var btnArea = label.GetChildAtIndex(0);
            if (btnArea == null) return null;
            // The button area itself is the click target (78x78 px region)
            return btnArea;
        }

        /// <summary>
        /// Score an irrigator's monster list from its ground label.
        /// Returns total score = sum(count * rarityWeight).
        /// Rarity determined by TextColor on the monster name element.
        /// </summary>
        private float ScoreIrrigatorLabel(Element label, BotSettings.HarvestMechanicSettings settings, out string detail)
        {
            detail = "";
            float totalScore = 0;

            // Navigate to monster panel: label[1][0][3]
            if (label.ChildCount < 2) return -1;
            var panel = label.GetChildAtIndex(1)?.GetChildAtIndex(0);
            if (panel == null || panel.ChildCount < 4) return -1;
            var monsterPanel = panel.GetChildAtIndex(3);
            if (monsterPanel == null || !monsterPanel.IsVisible) return -1;

            var parts = new List<string>();

            for (int i = 0; i < monsterPanel.ChildCount; i++)
            {
                var row = monsterPanel.GetChildAtIndex(i);
                if (row == null || !row.IsVisible) continue;

                var inner = row.GetChildAtIndex(0);
                if (inner == null) continue;

                // Get the deepest text element (inner[0] if it exists, otherwise inner itself)
                Element textElement;
                if (inner.ChildCount > 0)
                {
                    var deep = inner.GetChildAtIndex(0);
                    textElement = deep ?? inner;
                }
                else
                {
                    textElement = inner;
                }

                var text = textElement.Text;
                if (string.IsNullOrEmpty(text) || text.Length < 5) continue;

                // Parse count from "<white>{count}<default>{ x }MonsterName"
                int count = ParseMonsterCount(text);
                if (count <= 0) continue;

                // Parse monster name
                var name = ParseMonsterName(text);

                // Determine rarity from TextColor
                var tc = textElement.TextColor;
                var rarity = ClassifyRarity(tc.R, tc.G, tc.B);
                int weight = rarity switch
                {
                    MonsterRarity.Normal => settings.NormalWeight.Value,
                    MonsterRarity.Magic => settings.MagicWeight.Value,
                    MonsterRarity.Rare => settings.RareWeight.Value,
                    _ => settings.NormalWeight.Value,
                };

                float rowScore = count * weight;
                totalScore += rowScore;
                parts.Add($"{count}x {name}({rarity:G})={rowScore:F0}");
            }

            detail = string.Join(", ", parts);
            return totalScore;
        }

        /// <summary>
        /// Parse monster count from text format: &lt;white&gt;{count}&lt;default&gt;{ x }Name
        /// </summary>
        private static int ParseMonsterCount(string text)
        {
            var braceStart = text.IndexOf('{');
            var braceEnd = text.IndexOf('}');
            if (braceStart < 0 || braceEnd <= braceStart) return 0;

            var countStr = text.Substring(braceStart + 1, braceEnd - braceStart - 1);
            return int.TryParse(countStr, out var count) ? count : 0;
        }

        /// <summary>
        /// Parse monster name from text, stripping tags.
        /// </summary>
        private static string ParseMonsterName(string text)
        {
            // Find "{ x }" separator, name follows
            var xIdx = text.IndexOf("{ x }", StringComparison.Ordinal);
            if (xIdx >= 0)
                return text.Substring(xIdx + 5).Trim();

            // Fallback: strip all tags
            return System.Text.RegularExpressions.Regex.Replace(text, @"<[^>]+>|\{[^}]+\}", "").Trim();
        }

        private enum MonsterRarity { Normal, Magic, Rare }

        /// <summary>
        /// Classify monster rarity from TextColor RGB values.
        /// (127,127,127) = Normal, (200,200,200) = Magic, (184,218,242) = Rare
        /// </summary>
        private static MonsterRarity ClassifyRarity(int r, int g, int b)
        {
            // Rare: blue-tinted (R~184, G~218, B~242)
            if (b > 220 && g > 200 && r < 200)
                return MonsterRarity.Rare;

            // Magic: bright grey (R~200, G~200, B~200)
            if (r > 170 && g > 170 && b > 170 && r == g && g == b)
                return MonsterRarity.Magic;

            // Also catch magic when values are close but not exactly equal
            if (r > 170 && g > 170 && b > 170 && Math.Abs(r - g) < 10 && Math.Abs(g - b) < 10)
                return MonsterRarity.Magic;

            // Default: Normal
            return MonsterRarity.Normal;
        }

        // ══════════════════════════════════════════════════════════════
        // Irrigator pairing
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// Build irrigator pairs by proximity. Two irrigators within 80 grid units
        /// of each other are considered a pair (they share a crop plot).
        /// Observed spacing: 46-69 grid units between paired irrigators.
        /// </summary>
        private void BuildPairs(List<Entity> irrigators, List<Entity> extractors, BotContext ctx)
        {
            _pairs.Clear();
            var used = new HashSet<uint>();

            // Sort by grid position for deterministic pairing
            irrigators.Sort((a, b) =>
            {
                var diff = a.GridPosNum.X.CompareTo(b.GridPosNum.X);
                return diff != 0 ? diff : a.GridPosNum.Y.CompareTo(b.GridPosNum.Y);
            });

            for (int i = 0; i < irrigators.Count; i++)
            {
                if (used.Contains(irrigators[i].Id)) continue;

                float nearestDist = float.MaxValue;
                int nearestIdx = -1;

                var posI = new Vector2(irrigators[i].GridPosNum.X, irrigators[i].GridPosNum.Y);

                for (int j = i + 1; j < irrigators.Count; j++)
                {
                    if (used.Contains(irrigators[j].Id)) continue;

                    var posJ = new Vector2(irrigators[j].GridPosNum.X, irrigators[j].GridPosNum.Y);
                    var dist = Vector2.Distance(posI, posJ);
                    if (dist < nearestDist && dist < 80)
                    {
                        nearestDist = dist;
                        nearestIdx = j;
                    }
                }

                if (nearestIdx >= 0)
                {
                    var pair = new IrrigatorPair
                    {
                        A = irrigators[i],
                        B = irrigators[nearestIdx],
                        ColourA = (int)GetEntityState(irrigators[i], "colour"),
                        ColourB = (int)GetEntityState(irrigators[nearestIdx], "colour"),
                    };

                    // Associate nearest extractors with each irrigator
                    pair.ExtractorA = FindNearestExtractor(irrigators[i], extractors);
                    pair.ExtractorB = FindNearestExtractor(irrigators[nearestIdx], extractors);

                    _pairs.Add(pair);
                    used.Add(irrigators[i].Id);
                    used.Add(irrigators[nearestIdx].Id);
                    ctx.Log($"[Harvest] Paired irrigator {irrigators[i].Id} ({GetColourName(pair.ColourA)}) with {irrigators[nearestIdx].Id} ({GetColourName(pair.ColourB)}), dist={nearestDist:F0}, extractors={pair.ExtractorA?.Id ?? 0}/{pair.ExtractorB?.Id ?? 0}");
                }
            }
        }

        private static Entity? FindNearestExtractor(Entity irrigator, List<Entity> extractors)
        {
            var irrPos = new Vector2(irrigator.GridPosNum.X, irrigator.GridPosNum.Y);
            Entity? nearest = null;
            float nearestDist = float.MaxValue;
            foreach (var ext in extractors)
            {
                var extPos = new Vector2(ext.GridPosNum.X, ext.GridPosNum.Y);
                var dist = Vector2.Distance(irrPos, extPos);
                if (dist < nearestDist)
                {
                    nearestDist = dist;
                    nearest = ext;
                }
            }
            return nearest;
        }

        // ══════════════════════════════════════════════════════════════
        // Helpers
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// Check if any extractor across all pairs has an active encounter (current_state==2).
        /// While active, all other irrigators are locked.
        /// </summary>
        private bool IsAnyEncounterActive()
        {
            foreach (var pair in _pairs)
            {
                if (GetExtractorCurrentState(pair.ExtractorA) == 2) return true;
                if (GetExtractorCurrentState(pair.ExtractorB) == 2) return true;
            }
            return false;
        }

        /// <summary>
        /// Get the Extractor's current_state: 0=untouched, 2=active, 4=completed.
        /// </summary>
        private static int GetExtractorCurrentState(Entity? extractor)
        {
            if (extractor == null) return 0;
            return (int)GetEntityState(extractor, "current_state");
        }

        private static long GetEntityState(Entity entity, string stateName)
        {
            if (!entity.TryGetComponent<StateMachine>(out var sm)) return 0;
            var state = sm.States.FirstOrDefault(s => s.Name == stateName);
            return state?.Value ?? 0;
        }

        private static Vector2 GetPairMidpoint(IrrigatorPair pair)
        {
            var posA = new Vector2(pair.A.GridPosNum.X, pair.A.GridPosNum.Y);
            var posB = new Vector2(pair.B.GridPosNum.X, pair.B.GridPosNum.Y);
            return (posA + posB) * 0.5f;
        }

        private static int GetPreferredColourValue(string name)
        {
            return name switch
            {
                "Wild" => 1,
                "Vivid" => 2,
                "Primal" => 3,
                _ => 0, // "Any" = no preference
            };
        }

        private static float GetTypeMultiplier(int colour, BotSettings.HarvestMechanicSettings settings)
        {
            return colour switch
            {
                1 => settings.WildMultiplier.Value,
                2 => settings.VividMultiplier.Value,
                3 => settings.PrimalMultiplier.Value,
                _ => 1.0f,
            };
        }

        private static string GetColourName(int colour)
        {
            return colour switch
            {
                1 => "Wild",
                2 => "Vivid",
                3 => "Primal",
                _ => $"Unknown({colour})",
            };
        }

        private void ClickElement(GameController gc, Element element)
        {
            var rect = element.GetClientRectCache;
            var clickPos = new Vector2(rect.Center.X, rect.Center.Y);
            var windowRect = gc.Window.GetWindowRectangleTimeCache;
            var absPos = clickPos + new Vector2(windowRect.X, windowRect.Y);
            BotInput.Click(absPos);
        }

        private void SaveAndOverrideCombatProfile(BotContext ctx)
        {
            if (_savedCombatProfile != null) return;

            _savedCombatProfile = new CombatProfile
            {
                Enabled = ctx.Combat.Profile.Enabled,
                Positioning = ctx.Combat.Profile.Positioning,
            };

            ctx.Combat.SetProfile(new CombatProfile
            {
                Enabled = true,
                Positioning = CombatPositioning.Aggressive,
            });
        }

        private void RestoreCombatProfile(BotContext ctx)
        {
            if (_savedCombatProfile != null)
            {
                ctx.Combat.SetProfile(_savedCombatProfile);
                _savedCombatProfile = null;
            }
        }

        // ══════════════════════════════════════════════════════════════
        // Reset
        // ══════════════════════════════════════════════════════════════

        public void Reset()
        {
            _phase = HarvestPhase.Idle;
            _phaseStartTime = DateTime.Now;
            _entrancePortal = null;
            _returnPortal = null;
            _pairs.Clear();
            _currentPair = null;
            _plotsCompleted = 0;
            _buttonClicked = false;
            _navRetries = 0;
            _allPlotsComplete = false;
            _savedCombatProfile = null;
            AnchorGridPos = null;
            Status = "";
        }
    }
}
