using ExileCore;
using ExileCore.PoEMemory;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using SkillGem = ExileCore.PoEMemory.Components.SkillGem;
using AutoExile.Modes.Shared;
using AutoExile.Systems;
using System.IO;
using System.Linq;
using System.Numerics;

namespace AutoExile.Modes
{
    public enum LabPhase
    {
        Idle,

        // Hideout/Town
        InHideout,
        PrepareGems,
        TravelToPlaza,

        // Plaza
        InPlaza,
        SelectDifficulty,

        // Enter lab after activating
        EnterLabPortal,

        // Lab Zones
        NavigateZone,

        // Aspirant's Trial
        StagingRoom,
        FightIzaro,

        // Reward Room
        RewardRoom,
        FontPlaceGem,
        FontSelectOption,
        FontSelectResult,

        // Exit
        ExitLab,
        Done,
    }

    public class LabyrinthMode : IBotMode
    {
        public string Name => "Labyrinth";
        public LabPhase Phase => _phase;
        public string StatusText { get; private set; } = "";
        public LabyrinthState State => _state;

        // Phase state
        private LabPhase _phase = LabPhase.Idle;
        private DateTime _phaseStartTime = DateTime.Now;
        private DateTime _lastActionTime = DateTime.MinValue;
        private string _lastAreaName = "";
        private uint _lastAreaHash;

        // State tracking
        private readonly LabyrinthState _state = new();
        private readonly GemValuationService _gemValuation = new();
        private readonly LootPickupTracker _lootTracker = new();
        private readonly LabRoutingData _routing = new();
        private List<string> _preferredExits = new(); // preferred destination zone names for current room

        // Dedicated log file — cleared on each OnEnter
        private string? _logPath;
        private void LabLog(string msg)
        {
            if (_logPath == null) return;
            try
            {
                var line = $"{DateTime.Now:HH:mm:ss.fff} {msg}\n";
                File.AppendAllText(_logPath, line);
            }
            catch { }
        }

        // Loot scan timing
        private DateTime _lastLootScan = DateTime.MinValue;
        private const int LootScanIntervalMs = 500;

        // Settle timing — read from settings
        private DateTime _settleUntil = DateTime.MinValue;

        // Zone navigation
        private int _stuckCount;
        private int _lastStuckRecoveries;
        private const int MaxStuckBeforeSkip = 5;
        private Vector2? _entryPosition; // player position when entering zone — used to filter entry transition
        private readonly List<long> _entryTransitionIds = new(); // entity IDs of transitions near entry
        private const float EntryBlacklistRadius = 80f; // generous radius to catch transitions near spawn

        // Tile exit clusters — built once per zone from TileMap data
        private List<Vector2>? _exitClusters; // centroids of exit tile clusters
        private int _entryClusterIndex = -1; // which cluster is the entrance (blacklisted)
        private readonly HashSet<int> _skippedClusters = new(); // clusters we couldn't reach (stuck)
        private const float ClusterMergeRadius = 30f; // tiles within this distance are same cluster (tight to avoid merging separate exits)
        private DateTime _lastPathAttempt = DateTime.MinValue; // throttle pathfinding retries

        // Staging/arena state
        private bool _arenaTransitionClicked;

        // Door click tracking — blacklist doors that don't open after several attempts
        private readonly HashSet<long> _failedDoorIds = new();
        private readonly Dictionary<long, int> _doorClickAttempts = new();
        private long _pendingDoorId;
        private DateTime _pendingDoorClickTime;
        private const int MaxDoorClickAttempts = 4;
        private const float DoorRetryDelayMs = 1500f; // wait between door click attempts

        // Izaro fight state
        private bool _izaroWasSeen;

        // Font interaction state
        private int _fontClickAttempts;
        private const int MaxFontClickAttempts = 5;

        // Waypoint click tracking
        private bool _waypointClicked;
        private DateTime _waypointClickTime;

        // Plaza portal click tracking (prevent re-clicking)
        private bool _portalClicked;
        private DateTime _portalClickTime;

        // Difficulty UI constants
        private const int DifficultyPanelIndex = 72;
        private const int FontPanelIndex = 68;

        // Action cooldown
        private const float ActionCooldownMs = 500f;

        // ═══════════════════════════════════════════════════
        // Lifecycle
        // ═══════════════════════════════════════════════════

        public void OnEnter(BotContext ctx)
        {
            // Init dedicated log file
            try
            {
                var pluginDir = Path.GetDirectoryName(typeof(LabyrinthMode).Assembly.Location) ?? ".";
                _logPath = Path.Combine(pluginDir, "lab_debug.log");
                File.WriteAllText(_logPath, $"=== Lab mode started {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===\n");

                // Load routing data (optional — user places daily JSON in plugin folder)
                if (!_routing.IsLoaded)
                {
                    // Try difficulty-specific files first, then generic
                    var diffName = ctx.Settings.Labyrinth.Difficulty.Value?.ToLower().Replace("the ", "").Replace(" ", "_") ?? "normal";
                    var candidates = new[]
                    {
                        Path.Combine(pluginDir, $"{diffName}_lab.json"),
                        Path.Combine(pluginDir, "normal_lab.json"),
                        Path.Combine(pluginDir, "lab_routing.json"),
                    };
                    foreach (var path in candidates)
                    {
                        if (_routing.Load(path, LabLog))
                            break;
                    }
                }
            }
            catch { _logPath = null; }

            _stuckCount = 0;
            _lastStuckRecoveries = 0;
            _fontClickAttempts = 0;
            _portalClicked = false;
            _loggedPlazaEntry = false;
            _activateClicked = false;
            _arenaTransitionClicked = false;
            _izaroRetreated = false;
            _izaroExitClicked = false;
            _izaroWasSeen = false;
            _optionClicked = false;
            _resultGemClicked = false;
            _confirmClicked = false;
            _confirmClickAttempts = 0;
            _hoverIndex = -1;
            _scannedGems.Clear();
            _lootTracker.Reset();
            _settleUntil = DateTime.Now.AddSeconds(ctx.Settings.Labyrinth.SettleSeconds.Value);

            ModeHelpers.EnableDefaultCombat(ctx);

            // Always detect current location — works from hideout, town, plaza, or mid-lab
            var gc = ctx.Game;
            var areaName = gc.Area?.CurrentArea?.Name ?? "";
            _lastAreaName = areaName;
            _lastAreaHash = gc.IngameState?.Data?.CurrentAreaHash ?? 0;

            DetectCurrentLocation(ctx);
            LabLog($"OnEnter: area={areaName} phase={_phase} status={StatusText}");
        }

        public void OnExit()
        {
            _state.Reset();
            _phase = LabPhase.Idle;
        }

        // ═══════════════════════════════════════════════════
        // Main Tick
        // ═══════════════════════════════════════════════════

        public void Tick(BotContext ctx)
        {
            var gc = ctx.Game;

            // --- Area change detection ---
            var currentArea = gc.Area?.CurrentArea?.Name ?? "";
            var currentHash = gc.IngameState?.Data?.CurrentAreaHash ?? 0;
            if (!string.IsNullOrEmpty(currentArea) &&
                (currentArea != _lastAreaName || (currentHash != 0 && currentHash != _lastAreaHash)))
            {
                OnAreaChanged(ctx, currentArea, currentHash);
                _lastAreaName = currentArea;
                _lastAreaHash = currentHash;
            }

            // --- Settle delay ---
            if (DateTime.Now < _settleUntil)
                return;

            // --- State tick (when in lab) ---
            bool inHideoutOrTown = gc.Area?.CurrentArea?.IsHideout == true || gc.Area?.CurrentArea?.IsTown == true;
            if (!inHideoutOrTown)
            {
                _state.Tick(gc);
            }

            // --- Combat (always in lab zones and trials) ---
            bool combatAllowed = !inHideoutOrTown &&
                _phase != LabPhase.FontPlaceGem &&
                _phase != LabPhase.FontSelectOption &&
                _phase != LabPhase.FontSelectResult &&
                _phase != LabPhase.ExitLab;
            if (combatAllowed)
            {
                // Suppress combat positioning near locked doors — prevents dashing into walls
                bool nearLockedDoor = false;
                if (!inHideoutOrTown)
                {
                    var pPos = new Vector2(gc.Player.GridPosNum.X, gc.Player.GridPosNum.Y);
                    foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
                    {
                        if (IsLockedPuzzleDoor(entity) &&
                            Vector2.Distance(pPos, new Vector2(entity.GridPosNum.X, entity.GridPosNum.Y)) < 30f)
                        {
                            nearLockedDoor = true;
                            break;
                        }
                    }
                }
                ctx.Combat.SuppressPositioning = ctx.Interaction.IsBusy || nearLockedDoor;
                ctx.Combat.SuppressTargetedSkills = ctx.Interaction.IsBusy;
                ctx.Combat.Tick(ctx);
            }

            // --- Interaction system (always tick) ---
            var interactionResult = ctx.Interaction.Tick(gc);

            // --- Phase dispatch ---
            switch (_phase)
            {
                case LabPhase.InHideout:
                    TickInHideout(ctx);
                    break;
                case LabPhase.PrepareGems:
                    TickPrepareGems(ctx);
                    break;
                case LabPhase.TravelToPlaza:
                    TickTravelToPlaza(ctx);
                    break;
                case LabPhase.InPlaza:
                    TickInPlaza(ctx, interactionResult);
                    break;
                case LabPhase.SelectDifficulty:
                    TickSelectDifficulty(ctx);
                    break;
                case LabPhase.EnterLabPortal:
                    TickEnterLabPortal(ctx, interactionResult);
                    break;
                case LabPhase.NavigateZone:
                    TickNavigateZone(ctx, interactionResult);
                    break;
                case LabPhase.StagingRoom:
                    TickStagingRoom(ctx, interactionResult);
                    break;
                case LabPhase.FightIzaro:
                    TickFightIzaro(ctx, interactionResult);
                    break;
                case LabPhase.RewardRoom:
                    TickRewardRoom(ctx, interactionResult);
                    break;
                case LabPhase.FontPlaceGem:
                    TickFontPlaceGem(ctx);
                    break;
                case LabPhase.FontSelectOption:
                    TickFontSelectOption(ctx);
                    break;
                case LabPhase.FontSelectResult:
                    TickFontSelectResult(ctx);
                    break;
                case LabPhase.ExitLab:
                    TickExitLab(ctx, interactionResult);
                    break;
                case LabPhase.Done:
                    StatusText = "Labyrinth complete";
                    break;
                case LabPhase.Idle:
                    StatusText = "Idle";
                    break;
            }
        }

        // ═══════════════════════════════════════════════════
        // Area Change
        // ═══════════════════════════════════════════════════

        private void OnAreaChanged(BotContext ctx, string newArea, uint newHash)
        {
            ModeHelpers.CancelAllSystems(ctx);
            _state.OnAreaChanged();
            _stuckCount = 0;
            _exitClusters = null;
            _entryClusterIndex = -1;
            _skippedClusters.Clear();
            _failedDoorIds.Clear();
            _doorClickAttempts.Clear();
            _pendingDoorId = 0;
            _entryPosition = null;
            _entryTransitionIds.Clear();
            _izaroRetreated = false;
            _izaroExitClicked = false;
            _izaroWasSeen = false;
            _settleUntil = DateTime.Now.AddSeconds(ctx.Settings.Labyrinth.SettleSeconds.Value);

            var gc = ctx.Game;
            bool isHideout = gc.Area.CurrentArea.IsHideout;
            bool isTown = gc.Area.CurrentArea.IsTown;

            if (isHideout || isTown)
            {
                // Returned to hideout/town — check if run completed or died
                if (_phase >= LabPhase.NavigateZone && _phase <= LabPhase.ExitLab)
                {
                    // Was in lab — either completed or died
                    if (_phase == LabPhase.ExitLab)
                    {
                        _state.RecordRunComplete();
                        ctx.Log($"Lab run {_state.RunsCompleted} complete. Profit: {_state.TotalProfit:F0}c");
                    }
                    else
                    {
                        _state.DeathCount++;
                        ctx.Log($"Died in lab (death {_state.DeathCount})");
                    }
                }

                // Check stop conditions
                var settings = ctx.Settings.Labyrinth;
                if (settings.MaxRuns.Value > 0 && _state.RunsCompleted >= settings.MaxRuns.Value)
                {
                    _phase = LabPhase.Done;
                    StatusText = $"Done — completed {_state.RunsCompleted} runs";
                    return;
                }
                if (_state.DeathCount >= settings.MaxDeaths.Value && _phase != LabPhase.Done)
                {
                    _state.Reset();
                    ctx.Log("Too many deaths — starting fresh run");
                }

                _phase = LabPhase.InHideout;
                _phaseStartTime = DateTime.Now;
                StatusText = isHideout ? "In hideout" : "In town";
            }
            else if (newArea == "Aspirants' Plaza")
            {
                _phase = LabPhase.InPlaza;
                _phaseStartTime = DateTime.Now;
                _loggedPlazaEntry = false;
                StatusText = "In Aspirants' Plaza";
                LabLog($"Area changed → InPlaza");
            }
            else if (newArea == "Aspirant's Trial")
            {
                // Could be staging, arena, or reward — will detect via entities after settle
                _phaseStartTime = DateTime.Now;
                StatusText = "Aspirant's Trial — detecting zone type...";
                _phase = LabPhase.StagingRoom; // default, will be corrected

                if (gc.Player != null)
                    _entryPosition = new Vector2(gc.Player.GridPosNum.X, gc.Player.GridPosNum.Y);
            }
            else
            {
                // Lab zone
                _state.ZoneCount++;
                _phase = LabPhase.NavigateZone;
                _phaseStartTime = DateTime.Now;
                StatusText = $"Lab zone: {newArea}";

                // Compute preferred exits from routing data
                if (_routing.IsLoaded)
                {
                    _preferredExits = _routing.GetPreferredExits(newArea, _state.IzaroEncounterCount);
                    if (_preferredExits.Count > 0)
                        LabLog($"Routing: preferred exits from '{newArea}': {string.Join(", ", _preferredExits)}");
                }

                // Record entry position to blacklist the entrance transition
                if (gc.Player != null)
                    _entryPosition = new Vector2(gc.Player.GridPosNum.X, gc.Player.GridPosNum.Y);

                // Initialize exploration for this zone
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

        /// <summary>
        /// Check if an entity is a locked puzzle door (Door_Closed, Door_Counter, etc.)
        /// These are under Puzzle_Parts/Door_* and require switches/levers to open.
        /// </summary>
        private static bool IsLockedPuzzleDoor(Entity entity)
        {
            return entity.IsTargetable &&
                   entity.Path?.Contains("Puzzle_Parts/Door_", StringComparison.Ordinal) == true;
        }

        /// <summary>
        /// Detect current location when entering the mode mid-run.
        /// </summary>
        private void DetectCurrentLocation(BotContext ctx)
        {
            var gc = ctx.Game;
            var areaName = gc.Area?.CurrentArea?.Name ?? "";
            bool isHideout = gc.Area?.CurrentArea?.IsHideout == true;
            bool isTown = gc.Area?.CurrentArea?.IsTown == true;

            if (isHideout || isTown)
            {
                _phase = LabPhase.InHideout;
                StatusText = $"In {(isHideout ? "hideout" : "town")} — settling";
            }
            else if (areaName == "Aspirants' Plaza")
            {
                // Check if lab transition is already open (post-activation)
                bool hasTransition = false;
                foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
                {
                    if (entity.Path?.Contains("LabyrinthAreaTransition", StringComparison.Ordinal) == true
                        && entity.IsTargetable)
                    {
                        hasTransition = true;
                        break;
                    }
                }

                if (hasTransition)
                {
                    _phase = LabPhase.EnterLabPortal;
                    StatusText = "Lab transition open — entering";
                }
                else
                {
                    _phase = LabPhase.InPlaza;
                    StatusText = "In Aspirants' Plaza";
                }
            }
            else if (areaName == "Aspirant's Trial")
            {
                // Check if font UI is already open (bot restarted mid-interaction)
                var fontPanel = GetFontPanel(gc);
                if (fontPanel != null && fontPanel.IsVisible)
                {
                    var resultArea = fontPanel.GetChildAtIndex(4);
                    if (resultArea != null && resultArea.IsVisible)
                    {
                        _phase = LabPhase.FontSelectResult;
                        _resultGemClicked = false;
                        _hoverIndex = -1;
                        _scannedGems.Clear();
                        StatusText = "Font result screen — selecting gem";
                        LabLog("DetectLocation: font result screen open → FontSelectResult");
                    }
                    else
                    {
                        _phase = LabPhase.FontSelectOption;
                        _optionClicked = false;
                        StatusText = "Font options screen — selecting option";
                        LabLog("DetectLocation: font options open → FontSelectOption");
                    }
                }
                else
                {
                    // Scan entities to determine which sub-zone
                    if (gc.Player != null)
                        _entryPosition = new Vector2(gc.Player.GridPosNum.X, gc.Player.GridPosNum.Y);
                    _state.Tick(gc);
                    if (_state.HasFont)
                    {
                        _phase = LabPhase.RewardRoom;
                        StatusText = "In reward room";
                    }
                    else if (_state.IsIzaroPresent)
                    {
                        _phase = LabPhase.FightIzaro;
                        StatusText = "Fighting Izaro";
                    }
                    else if (_state.HasIzaroDoor)
                    {
                        _phase = LabPhase.StagingRoom;
                        StatusText = "In staging room";
                    }
                    else
                    {
                        _phase = LabPhase.StagingRoom;
                        StatusText = "In Aspirant's Trial (unknown sub-zone)";
                    }
                }
            }
            else
            {
                // Lab zone
                _phase = LabPhase.NavigateZone;
                StatusText = $"In lab zone: {areaName}";
                if (gc.Player != null)
                    _entryPosition = new Vector2(gc.Player.GridPosNum.X, gc.Player.GridPosNum.Y);
                var pfGrid = gc.IngameState?.Data?.RawPathfindingData;
                var tgtGrid = gc.IngameState?.Data?.RawTerrainTargetingData;
                if (pfGrid != null && gc.Player != null)
                {
                    var playerGrid = new Vector2(gc.Player.GridPosNum.X, gc.Player.GridPosNum.Y);
                    ctx.Exploration.Initialize(pfGrid, tgtGrid, playerGrid,
                        ctx.Settings.Build.BlinkRange.Value);
                }
            }
            _phaseStartTime = DateTime.Now;
        }

        // ═══════════════════════════════════════════════════
        // Phase: InHideout
        // ═══════════════════════════════════════════════════

        private void TickInHideout(BotContext ctx)
        {
            // Don't stash — inventory gems are the transformation payload.
            // Go straight to gem evaluation.
            LabLog($"InHideout → PrepareGems (area: {ctx.Game.Area?.CurrentArea?.Name})");
            _phase = LabPhase.PrepareGems;
            _phaseStartTime = DateTime.Now;
            StatusText = "Preparing gems...";
        }

        // ═══════════════════════════════════════════════════
        // Phase: PrepareGems
        // ═══════════════════════════════════════════════════

        private void TickPrepareGems(BotContext ctx)
        {
            var gc = ctx.Game;
            var settings = ctx.Settings.Labyrinth;

            // Wait for ninja prices to load before evaluating gems
            if (!ctx.NinjaPrice.IsLoaded)
            {
                StatusText = $"Waiting for price data... ({ctx.NinjaPrice.Status})";
                return;
            }

            // Build colour map from game data if not done yet
            _gemValuation.BuildColourMap(gc);

            // Rebuild gem valuation index
            _gemValuation.RebuildIndex(ctx.NinjaPrice);

            // Read inventory for skill gems
            var slotItems = StashSystem.GetInventorySlotItems(gc);
            if (slotItems == null || slotItems.Count == 0)
            {
                StatusText = $"No inventory items (slotItems={(slotItems == null ? "null" : "empty")})";
                LabLog($"PrepareGems: {StatusText}");
                _phase = LabPhase.Done;
                return;
            }

            var inventoryGems = new List<(string Name, double ChaosValue, int Quality, int Level)>();
            foreach (var si in slotItems)
            {
                var item = si.Item;
                if (item == null) continue;
                var baseItemType = gc.Files.BaseItemTypes.Translate(item.Path);
                var className = baseItemType?.ClassName;
                if (className is not ("Active Skill Gem" or "Support Skill Gem")) continue;

                var gemName = baseItemType?.BaseName ?? "";
                if (string.IsNullOrEmpty(gemName)) continue;

                int quality = 0;
                if (item.TryGetComponent<ExileCore.PoEMemory.Components.Quality>(out var q))
                    quality = q.ItemQuality;
                int level = 1;
                if (item.TryGetComponent<SkillGem>(out var sg))
                    level = sg.Level;

                var price = ctx.NinjaPrice.GetPrice(gc, item);
                inventoryGems.Add((gemName, price.MaxChaosValue, quality, level));
                LabLog($"Inventory gem: {gemName} lvl={level} q={quality} = {price.MaxChaosValue:F1}c");
            }

            if (inventoryGems.Count == 0)
            {
                StatusText = $"No skill gems in inventory ({slotItems.Count} items total)";
                LabLog($"PrepareGems: {StatusText}");
                _phase = LabPhase.Done;
                return;
            }

            var keepThreshold = (double)settings.KeepThreshold.Value;
            LabLog($"Found {inventoryGems.Count} gems, min profit: {settings.MinExpectedProfit.Value}c, keep above: {keepThreshold}c");

            GemValuationService.GemEvaluation? best = null;
            try
            {
                // Log all evaluations for debugging
                foreach (var (name, value, quality, level) in inventoryGems)
                {
                    var baseName = GemValuationService.GetBaseGemName(name);
                    var eval = _gemValuation.Evaluate(baseName, value, quality >= 20);
                    var kept = keepThreshold > 0 && value >= keepThreshold ? " [KEEP]" : "";
                    LabLog($"  {baseName} (lvl{level} q{quality}): val={value:F1}c EV={eval.BestEV:F1}c profit={eval.ExpectedProfit:F1}c ({eval.BestStrategy}, {eval.SameTypeVariants}type/{eval.SameColourVariants}clr){kept}");
                }
                best = _gemValuation.SelectBestGem(
                    inventoryGems.Select(g => (g.Name, g.ChaosValue, g.Quality)),
                    settings.MinExpectedProfit.Value, keepThreshold);
            }
            catch (Exception ex)
            {
                LabLog($"PrepareGems EXCEPTION: {ex.Message}");
                StatusText = $"Gem evaluation error: {ex.Message}";
                _phase = LabPhase.Done;
                return;
            }

            if (best == null)
            {
                StatusText = $"No gems meet profit threshold ({settings.MinExpectedProfit.Value}c)";
                LabLog($"PrepareGems: {StatusText}");
                _phase = LabPhase.Done;
                return;
            }

            _state.SelectedGemName = best.GemName;
            _state.SelectedGemValue = best.CurrentValue;
            // Store the input gem's level/quality so reward gems are priced at matching tier
            var matchedGem = inventoryGems.FirstOrDefault(g =>
                GemValuationService.GetBaseGemName(g.Name) == best.GemName);
            _state.SelectedGemLevel = matchedGem.Level > 0 ? matchedGem.Level : 1;
            _state.SelectedGemQuality = matchedGem.Quality;
            StatusText = $"Selected: {best.GemName} (lvl{_state.SelectedGemLevel} q{_state.SelectedGemQuality}, EV: {best.BestEV:F0}c, profit: {best.ExpectedProfit:F0}c via {best.BestStrategy})";
            ctx.Log(StatusText);

            // Proceed to travel
            _phase = LabPhase.TravelToPlaza;
            _phaseStartTime = DateTime.Now;
        }

        // ═══════════════════════════════════════════════════
        // Phase: TravelToPlaza
        // ═══════════════════════════════════════════════════

        private void TickTravelToPlaza(BotContext ctx)
        {
            var gc = ctx.Game;

            // Find waypoint entity
            Entity? waypoint = null;
            foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
            {
                if (entity.Type == EntityType.Waypoint && entity.IsTargetable)
                {
                    waypoint = entity;
                    break;
                }
            }

            if (waypoint == null)
            {
                StatusText = "No waypoint found";
                return;
            }

            // Check if WorldMap is already open
            var worldMap = gc.IngameState.IngameUi.WorldMap;
            if (worldMap != null && worldMap.IsVisible)
            {
                _waypointClicked = false; // reset for next time
                ClickAspirantTrialWaypoint(ctx, gc);
                return;
            }

            // After clicking waypoint, wait for the map to open before clicking again
            if (_waypointClicked)
            {
                if ((DateTime.Now - _waypointClickTime).TotalSeconds < 3.0)
                {
                    StatusText = "Waiting for waypoint map to open...";
                    return;
                }
                // Map didn't open after 3s — retry
                _waypointClicked = false;
                LabLog("TravelToPlaza: waypoint map didn't open after 3s, retrying");
            }

            // Walk to and click the waypoint
            if (!ctx.Interaction.IsBusy)
            {
                ctx.Interaction.InteractWithEntity(waypoint, ctx.Navigation);
                _waypointClicked = true;
                _waypointClickTime = DateTime.Now;
                StatusText = $"Clicking waypoint (dist: {waypoint.DistancePlayer:F0})...";
            }
            else
            {
                StatusText = "Walking to waypoint...";
            }
        }

        private void ClickAspirantTrialWaypoint(BotContext ctx, GameController gc)
        {
            if (!ModeHelpers.CanAct(_lastActionTime, ActionCooldownMs)) return;

            // Walk the WorldMap UI tree to find the Aspirant's Trial button
            // From research: the waypoint nodes are at various indices in the tree
            // We look for a node with text containing "Aspirant" or by PathFromRoot
            var worldMap = gc.IngameState.IngameUi.WorldMap;
            if (worldMap == null) return;

            // Search through visible tab content for the Aspirant's Trial node
            // The exact path may vary — try to find it by iterating visible elements
            // PathFromRoot from research: (OpenLeftPanel/WorldMap)37->2->0->1->0->0->1->1->2->0->2->6
            // Try to navigate this path
            try
            {
                Element node = worldMap;
                var indices = new[] { 2, 0, 1, 0, 0, 1, 1, 2, 0, 2, 6 };
                foreach (var idx in indices)
                {
                    if (node == null || idx >= node.ChildCount) break;
                    node = node.GetChildAtIndex(idx);
                }

                if (node != null && node.IsVisible)
                {
                    var rect = node.GetClientRect();
                    var windowRect = gc.Window.GetWindowRectangle();
                    var clickPos = BotInput.RandomizeWithinRect(rect);
                    var absPos = new Vector2(windowRect.X + clickPos.X, windowRect.Y + clickPos.Y);

                    if (BotInput.Click(absPos))
                    {
                        _lastActionTime = DateTime.Now;
                        StatusText = "Clicked Aspirant's Trial waypoint";
                    }
                    return;
                }
            }
            catch { }

            StatusText = "Could not find Aspirant's Trial waypoint node";
        }

        // ═══════════════════════════════════════════════════
        // Phase: InPlaza
        // ═══════════════════════════════════════════════════

        private bool _loggedPlazaEntry;

        private void TickInPlaza(BotContext ctx, InteractionResult interactionResult)
        {
            var gc = ctx.Game;

            if (!_loggedPlazaEntry)
            {
                LabLog($"InPlaza tick — searching for LabyrinthAirlockPortal");
                _loggedPlazaEntry = true;
            }

            // Close any open panels first
            if (gc.IngameState.IngameUi.WorldMap?.IsVisible == true)
            {
                if (ModeHelpers.CanAct(_lastActionTime, ActionCooldownMs))
                {
                    BotInput.PressKey(System.Windows.Forms.Keys.Escape);
                    _lastActionTime = DateTime.Now;
                    StatusText = "Closing world map...";
                }
                return;
            }

            // Check if difficulty panel is already open
            var diffPanel = GetDifficultyPanel(gc);
            if (diffPanel != null && diffPanel.IsVisible)
            {
                _phase = LabPhase.SelectDifficulty;
                _phaseStartTime = DateTime.Now;
                ctx.Interaction.Cancel(gc);
                StatusText = "Difficulty panel open";
                LabLog("InPlaza: difficulty panel detected → SelectDifficulty");
                return;
            }

            // Find the difficulty select device (NOT the airlock portal — that's the return to town)
            Entity? diffDevice = null;
            foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
            {
                if (entity.Path?.Contains("LabyrinthDifficultySelect", StringComparison.Ordinal) == true
                    && entity.IsTargetable)
                {
                    diffDevice = entity;
                    break;
                }
            }

            if (diffDevice == null)
            {
                StatusText = "Difficulty device not found — exploring";
                // Device might be out of range — explore to find it
                if (!ctx.Navigation.IsNavigating && ctx.Exploration.IsInitialized)
                {
                    var playerPos = new Vector2(gc.Player.GridPosNum.X, gc.Player.GridPosNum.Y);
                    ctx.Exploration.Update(playerPos);
                    var target = ctx.Exploration.GetNextExplorationTarget(playerPos);
                    if (target.HasValue)
                        ctx.Navigation.NavigateTo(gc, target.Value);
                }
                else if (!ctx.Exploration.IsInitialized)
                {
                    // Initialize exploration for the plaza
                    var pfGrid = gc.IngameState?.Data?.RawPathfindingData;
                    var tgtGrid = gc.IngameState?.Data?.RawTerrainTargetingData;
                    if (pfGrid != null && gc.Player != null)
                    {
                        var playerGrid = new Vector2(gc.Player.GridPosNum.X, gc.Player.GridPosNum.Y);
                        ctx.Exploration.Initialize(pfGrid, tgtGrid, playerGrid,
                            ctx.Settings.Build.BlinkRange.Value);
                    }
                }
                if ((DateTime.Now - _phaseStartTime).TotalSeconds > 60)
                {
                    _phase = LabPhase.Done;
                    StatusText = "Timeout finding difficulty device";
                    LabLog("InPlaza: timeout finding LabyrinthDifficultySelect");
                }
                return;
            }

            // Navigate to and click the device
            if (!ctx.Interaction.IsBusy)
            {
                LabLog($"InPlaza: clicking LabyrinthDifficultySelect (dist={diffDevice.DistancePlayer:F0})");
                ctx.Interaction.InteractWithEntity(diffDevice, ctx.Navigation);
                StatusText = $"Clicking difficulty device (dist: {diffDevice.DistancePlayer:F0})...";
            }
            else
            {
                // Check if difficulty panel appeared while interacting
                diffPanel = GetDifficultyPanel(gc);
                if (diffPanel != null && diffPanel.IsVisible)
                {
                    _phase = LabPhase.SelectDifficulty;
                    _phaseStartTime = DateTime.Now;
                    ctx.Interaction.Cancel(gc);
                    StatusText = "Difficulty selection open";
                    return;
                }

                StatusText = "Interacting with lab entrance...";
                if (interactionResult == InteractionResult.Failed)
                {
                    StatusText = "Failed to interact with lab entrance";
                }
            }
        }

        // ═══════════════════════════════════════════════════
        // Phase: SelectDifficulty
        // ═══════════════════════════════════════════════════

        private bool _activateClicked; // true after clicking activate, waiting for panel to close

        private void TickSelectDifficulty(BotContext ctx)
        {
            var gc = ctx.Game;
            if (!ModeHelpers.CanAct(_lastActionTime, ActionCooldownMs)) return;

            var diffPanel = GetDifficultyPanel(gc);

            // Panel closed after activate → portal should be open, find and enter it
            if (diffPanel == null || !diffPanel.IsVisible)
            {
                if (_activateClicked)
                {
                    LabLog("SelectDifficulty: panel closed → entering portal");
                    _phase = LabPhase.EnterLabPortal;
                    _phaseStartTime = DateTime.Now;
                    _activateClicked = false;
                    StatusText = "Panel closed — entering portal";
                    return;
                }
                StatusText = "Difficulty panel not visible";
                if ((DateTime.Now - _phaseStartTime).TotalSeconds > 15)
                {
                    _phase = LabPhase.InPlaza;
                    _phaseStartTime = DateTime.Now;
                    LabLog("SelectDifficulty: panel timeout → InPlaza");
                }
                return;
            }

            var windowRect = gc.Window.GetWindowRectangle();
            var activateBtn = diffPanel.GetChildFromIndices(3, 0);

            // Check if activate button is enabled (IsActive == true means option is selected)
            bool activateEnabled = activateBtn?.IsActive == true;

            if (activateEnabled)
            {
                // Option is selected — click activate
                var btnRect = activateBtn!.GetClientRect();
                var btnClick = BotInput.RandomizeWithinRect(btnRect);
                var btnAbs = new Vector2(windowRect.X + btnClick.X, windowRect.Y + btnClick.Y);

                if (BotInput.Click(btnAbs))
                {
                    _lastActionTime = DateTime.Now;
                    _activateClicked = true;
                    StatusText = "Clicked activate — waiting for panel to close...";
                    LabLog("SelectDifficulty: clicked activate");
                }
                return;
            }

            // Activate not enabled — need to click a difficulty option
            var settings = ctx.Settings.Labyrinth;
            var targetDifficulty = settings.Difficulty.Value;

            var optionList = diffPanel.GetChildFromIndices(2, 1);
            if (optionList == null)
            {
                StatusText = "Could not find difficulty option list";
                return;
            }

            Element? targetOption = null;
            for (int i = 0; i < optionList.ChildCount; i++)
            {
                var opt = optionList.GetChildAtIndex(i);
                if (opt == null || !opt.IsVisible) continue;
                var nameEl = opt.GetChildAtIndex(0);
                if (nameEl?.Text == targetDifficulty)
                {
                    targetOption = opt;
                    break;
                }
            }

            if (targetOption == null)
            {
                StatusText = $"Difficulty '{targetDifficulty}' not found in UI";
                return;
            }

            var optRect = targetOption.GetClientRect();
            var optClick = BotInput.RandomizeWithinRect(optRect);
            var optAbs = new Vector2(windowRect.X + optClick.X, windowRect.Y + optClick.Y);

            if (BotInput.Click(optAbs))
            {
                _lastActionTime = DateTime.Now;
                StatusText = $"Clicked {targetDifficulty} — checking activate...";
                LabLog($"SelectDifficulty: clicked {targetDifficulty}");
            }
        }

        // ═══════════════════════════════════════════════════
        // Phase: EnterLabPortal (after difficulty activated)
        // ═══════════════════════════════════════════════════

        private void TickEnterLabPortal(BotContext ctx, InteractionResult interactionResult)
        {
            var gc = ctx.Game;

            // Find the lab entrance transition (spawns after activating difficulty)
            Entity? portal = null;
            foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
            {
                if (entity.Path?.Contains("LabyrinthAreaTransition", StringComparison.Ordinal) == true
                    && entity.IsTargetable)
                {
                    portal = entity;
                    break;
                }
            }

            if (portal == null)
            {
                StatusText = "Waiting for portal to appear...";
                if ((DateTime.Now - _phaseStartTime).TotalSeconds > 15)
                {
                    LabLog("EnterLabPortal: portal not found, timeout");
                    _phase = LabPhase.InPlaza;
                    _phaseStartTime = DateTime.Now;
                }
                return;
            }

            if (!ctx.Interaction.IsBusy)
            {
                LabLog($"EnterLabPortal: clicking portal (dist={portal.DistancePlayer:F0})");
                ctx.Interaction.InteractWithEntity(portal, ctx.Navigation);
                StatusText = $"Clicking lab portal (dist: {portal.DistancePlayer:F0})...";
            }
            else
            {
                StatusText = "Entering lab...";
            }
        }

        private static bool HasTreasureKeys(GameController gc)
        {
            var slotItems = StashSystem.GetInventorySlotItems(gc);
            if (slotItems == null) return false;
            foreach (var si in slotItems)
            {
                var item = si.Item;
                if (item?.Path == null) continue;
                // Keys: Metadata/Items/Labyrinth/BronzeKey, SilverKey, GoldKey, etc.
                if (item.Path.Contains("Items/Labyrinth/", StringComparison.Ordinal) &&
                    item.Path.Contains("Key", StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        private Element? GetDifficultyPanel(GameController gc)
        {
            try
            {
                var panel = gc.IngameState.IngameUi.LabyrinthSelectPanel;
                return panel?.IsVisible == true ? panel : null;
            }
            catch { return null; }
        }

        // ═══════════════════════════════════════════════════
        // Phase: NavigateZone
        // ═══════════════════════════════════════════════════

        private void TickNavigateZone(BotContext ctx, InteractionResult interactionResult)
        {
            var gc = ctx.Game;
            var settings = ctx.Settings.Labyrinth;

            // Zone timeout
            if ((DateTime.Now - _phaseStartTime).TotalSeconds > settings.ZoneTimeoutSeconds.Value)
            {
                StatusText = "Zone timeout — aborting";
                _phase = LabPhase.ExitLab;
                _phaseStartTime = DateTime.Now;
                return;
            }

            // Loot scanning — pick up keys and other items
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
                    StatusText = $"Picking up: {candidate.ItemName}";
                    return;
                }
            }

            // Don't interrupt active interaction
            if (ctx.Interaction.IsBusy)
            {
                StatusText = "Interacting...";
                return;
            }

            // Proactively click doors and switches that are nearby AND reachable
            // Only click if within interact range — let navigation handle routing to them
            {
                var playerPos = new Vector2(gc.Player.GridPosNum.X, gc.Player.GridPosNum.Y);
                var interactRange = ctx.Settings.InteractRadius.Value;

                Entity? nearestInteractable = null;
                float nearestDist = float.MaxValue;
                string interactType = "";

                foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
                {
                    if (!entity.IsTargetable) continue;
                    var path = entity.Path;
                    if (path == null) continue;

                    bool isSwitch = path.Contains("Switch_", StringComparison.Ordinal)
                        && path.Contains("Puzzle_Parts/", StringComparison.Ordinal);
                    // Only click regular doors (EntityType.Door) — NOT Puzzle_Parts/Door_Closed
                    // which require levers and can't be clicked directly
                    bool isDoor = entity.Type == EntityType.Door;
                    if (!isSwitch && !isDoor) continue;

                    var dist = Vector2.Distance(playerPos,
                        new Vector2(entity.GridPosNum.X, entity.GridPosNum.Y));

                    // Only click things within interact range (we can actually reach them)
                    // Skip doors we already tried and failed to open
                    if (isDoor && _failedDoorIds.Contains(entity.Id)) continue;

                    if (dist < interactRange && dist < nearestDist)
                    {
                        nearestDist = dist;
                        nearestInteractable = entity;
                        interactType = isSwitch ? "switch" : "door";
                    }
                }

                if (nearestInteractable != null)
                {
                    if (interactType == "door")
                    {
                        // Check cooldown — don't spam-click doors
                        if ((DateTime.Now - _pendingDoorClickTime).TotalMilliseconds < DoorRetryDelayMs)
                        {
                            StatusText = "Waiting to retry door...";
                            // Don't return — let navigation continue while waiting
                        }
                        else
                        {
                            _doorClickAttempts.TryGetValue(nearestInteractable.Id, out var attempts);
                            _doorClickAttempts[nearestInteractable.Id] = attempts + 1;

                            if (attempts + 1 >= MaxDoorClickAttempts)
                            {
                                _failedDoorIds.Add(nearestInteractable.Id);
                                LabLog($"Door {nearestInteractable.Id} blacklisted after {MaxDoorClickAttempts} attempts");
                            }
                            else
                            {
                                LabLog($"Clicking door attempt {attempts + 1}/{MaxDoorClickAttempts} (dist={nearestDist:F0})");
                                ctx.Interaction.InteractWithEntity(nearestInteractable, requireProximity: false);
                                _pendingDoorClickTime = DateTime.Now;
                                StatusText = $"Opening door ({attempts + 1}/{MaxDoorClickAttempts})";
                                return;
                            }
                        }
                    }
                    else
                    {
                        // Switches — click immediately
                        LabLog($"Clicking switch (dist={nearestDist:F0})");
                        ctx.Interaction.InteractWithEntity(nearestInteractable, requireProximity: false);
                        StatusText = $"Clicking switch (dist: {nearestDist:F0})";
                        return;
                    }
                }
            }

            // Proactive locked door avoidance: if navigating and a locked puzzle door is near
            // our remaining path waypoints, divert to an accessible switch before reaching it
            if (ctx.Navigation.IsNavigating)
            {
                var playerPos3 = new Vector2(gc.Player.GridPosNum.X, gc.Player.GridPosNum.Y);
                bool lockedDoorAhead = false;
                var navPath = ctx.Navigation.CurrentNavPath;
                var wpIndex = ctx.Navigation.CurrentWaypointIndex;
                foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
                {
                    if (!IsLockedPuzzleDoor(entity)) continue;
                    var doorPos = new Vector2(entity.GridPosNum.X, entity.GridPosNum.Y);

                    // Check if any remaining waypoint passes within 15 grid units of this door
                    for (int wi = wpIndex; wi < navPath.Count; wi++)
                    {
                        if (Vector2.Distance(navPath[wi].Position, doorPos) < 15f)
                        {
                            lockedDoorAhead = true;
                            break;
                        }
                    }
                    if (lockedDoorAhead) break;
                }

                if (lockedDoorAhead)
                {
                    // Collect locked door positions to check if switches are behind them
                    var lockedDoors = new List<Vector2>();
                    foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
                    {
                        if (IsLockedPuzzleDoor(entity))
                            lockedDoors.Add(new Vector2(entity.GridPosNum.X, entity.GridPosNum.Y));
                    }

                    // Find nearest switch that's NOT behind a locked door
                    Entity? divertSwitch = null;
                    float divertDist = float.MaxValue;
                    foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
                    {
                        if (!entity.IsTargetable) continue;
                        if (entity.Path?.Contains("Switch_", StringComparison.Ordinal) != true) continue;
                        var switchPos = new Vector2(entity.GridPosNum.X, entity.GridPosNum.Y);
                        var dist = Vector2.Distance(playerPos3, switchPos);

                        // Check if any locked door is between us and this switch
                        bool blocked = false;
                        foreach (var doorPos in lockedDoors)
                        {
                            var doorDist = Vector2.Distance(playerPos3, doorPos);
                            // Door is between us and switch if: door is closer than switch,
                            // and door is roughly in the same direction as switch
                            if (doorDist < dist)
                            {
                                var toSwitch = switchPos - playerPos3;
                                var toDoor = doorPos - playerPos3;
                                if (toSwitch.LengthSquared() > 0 && toDoor.LengthSquared() > 0)
                                {
                                    var dot = Vector2.Dot(
                                        Vector2.Normalize(toSwitch),
                                        Vector2.Normalize(toDoor));
                                    // dot > 0.7 means within ~45 degrees — same direction
                                    if (dot > 0.7f)
                                    {
                                        blocked = true;
                                        break;
                                    }
                                }
                            }
                        }

                        if (blocked) continue;
                        if (dist < divertDist)
                        {
                            divertDist = dist;
                            divertSwitch = entity;
                        }
                    }

                    if (divertSwitch != null && !ctx.Interaction.IsBusy)
                    {
                        LabLog($"Locked door ahead — diverting to unblocked switch (dist={divertDist:F0})");
                        ctx.Navigation.Stop(gc);
                        ctx.Interaction.InteractWithEntity(divertSwitch, ctx.Navigation);
                        StatusText = $"Locked door ahead — going to switch (dist: {divertDist:F0})";
                        return;
                    }
                    else if (divertSwitch == null)
                    {
                        LabLog("Locked door ahead but no unblocked switches visible");
                    }
                }
            }

            // Update exploration every tick (even while navigating)
            {
                var pp = new Vector2(gc.Player.GridPosNum.X, gc.Player.GridPosNum.Y);
                if (ctx.Exploration.IsInitialized)
                    ctx.Exploration.Update(pp);
            }

            // On first ticks in a zone, capture any transition near entry as "entry transition"
            if (_entryPosition.HasValue && (DateTime.Now - _phaseStartTime).TotalSeconds < 10)
            {
                foreach (var (id, pos) in _state.ExitTransitions)
                {
                    if (!_entryTransitionIds.Contains(id) &&
                        Vector2.Distance(pos, _entryPosition.Value) < EntryBlacklistRadius)
                    {
                        _entryTransitionIds.Add(id);
                        LabLog($"Marked entry transition id={id} at ({pos.X:F0},{pos.Y:F0})");
                    }
                }
            }

            // Check for exit transitions, excluding entry ones by ID and position
            var forwardExits = _state.ExitTransitions
                .Where(e => !_entryTransitionIds.Contains(e.Id) &&
                    (!_entryPosition.HasValue || Vector2.Distance(e.Position, _entryPosition.Value) > EntryBlacklistRadius))
                .ToList();

            if (forwardExits.Count > 0)
            {
                var playerPos = new Vector2(gc.Player.GridPosNum.X, gc.Player.GridPosNum.Y);
                var currentZone = gc.Area?.CurrentArea?.Name ?? "";

                // If routing data is loaded, prefer the exit leading to shortest path
                (long Id, Vector2 Position) nearest = default;
                bool foundPreferred = false;

                if (_routing.IsLoaded && forwardExits.Count > 1)
                {
                    _preferredExits = _routing.GetPreferredExits(currentZone, _state.IzaroEncounterCount);

                    // Check each exit entity's RenderName against preferred destinations
                    foreach (var exit in forwardExits)
                    {
                        foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
                        {
                            if (entity.Id != exit.Id || !entity.IsTargetable) continue;
                            var renderName = entity.RenderName ?? "";
                            if (_preferredExits.Count > 0 &&
                                renderName.Equals(_preferredExits[0], StringComparison.OrdinalIgnoreCase))
                            {
                                nearest = exit;
                                foundPreferred = true;
                                LabLog($"Routing: preferred exit '{renderName}' found (shortest path to trial)");
                                break;
                            }
                        }
                        if (foundPreferred) break;
                    }
                }

                // Fall back to nearest exit if no routing preference or can't read names yet
                if (!foundPreferred)
                {
                    nearest = forwardExits
                        .OrderBy(e => Vector2.Distance(playerPos, e.Position))
                        .First();
                }

                var dist = Vector2.Distance(playerPos, nearest.Position);

                // Find the actual entity to click
                Entity? exitEntity = null;
                foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
                {
                    if (entity.Id == nearest.Id && entity.IsTargetable)
                    {
                        exitEntity = entity;
                        break;
                    }
                }

                if (exitEntity != null)
                {
                    if (dist < 20f && !ctx.Interaction.IsBusy)
                    {
                        ctx.Interaction.InteractWithEntity(exitEntity, requireProximity: false);
                        StatusText = $"Clicking exit (dist: {dist:F0})";
                    }
                    else if (!ctx.Navigation.IsNavigating || ctx.Navigation.Destination != nearest.Position)
                    {
                        ctx.Navigation.NavigateTo(gc, nearest.Position);
                        StatusText = $"Navigating to exit (dist: {dist:F0})";
                    }
                    else
                    {
                        StatusText = $"Walking to exit (dist: {dist:F0})";
                    }
                    return;
                }
            }

            // Try tile-based exit detection via TileMap
            // Navigate toward nearest non-entry exit cluster
            var tileExit = FindExitViaTileMap(ctx);
            if (tileExit.HasValue)
            {
                var playerPos2 = new Vector2(gc.Player.GridPosNum.X, gc.Player.GridPosNum.Y);
                var tileDist = Vector2.Distance(playerPos2, tileExit.Value);

                if (!ctx.Navigation.IsNavigating)
                {
                    // Throttle pathfinding attempts — don't retry every tick if it fails
                    if ((DateTime.Now - _lastPathAttempt).TotalSeconds >= 3.0)
                    {
                        _lastPathAttempt = DateTime.Now;
                        ctx.Navigation.NavigateTo(gc, tileExit.Value);
                        if (ctx.Navigation.IsNavigating)
                        {
                            StatusText = $"Navigating to exit cluster (dist: {tileDist:F0})";
                            return;
                        }
                        else
                        {
                            LabLog($"Pathfinding to cluster ({tileExit.Value.X:F0},{tileExit.Value.Y:F0}) failed — path blocked?");
                            // Fall through to exploration which might find an alternate route
                        }
                    }
                    else
                    {
                        StatusText = $"Exit cluster blocked (dist: {tileDist:F0}) — exploring...";
                        // Fall through to exploration
                    }
                }
                else
                {
                    StatusText = $"Walking to exit cluster (dist: {tileDist:F0})";
                    return;
                }
            }

            // Explore to find exit — only start new nav when not already moving
            if (!ctx.Navigation.IsNavigating)
            {
                var playerPos = new Vector2(gc.Player.GridPosNum.X, gc.Player.GridPosNum.Y);

                // Ensure exploration is initialized
                if (!ctx.Exploration.IsInitialized)
                {
                    var pfGrid = gc.IngameState?.Data?.RawPathfindingData;
                    var tgtGrid = gc.IngameState?.Data?.RawTerrainTargetingData;
                    if (pfGrid != null)
                    {
                        ctx.Exploration.Initialize(pfGrid, tgtGrid, playerPos,
                            ctx.Settings.Build.BlinkRange.Value);
                        LabLog("NavigateZone: initialized exploration");
                    }
                    return;
                }

                ctx.Exploration.Update(playerPos);

                var target = ctx.Exploration.GetNextExplorationTarget(playerPos);
                if (target.HasValue)
                {
                    ctx.Navigation.NavigateTo(gc, target.Value);
                    StatusText = $"Exploring for exit ({ctx.Exploration.ActiveBlobCoverage * 100:F0}%)";
                }
                else
                {
                    // No exploration targets — look for doors/switches NOT blocked by locked doors
                    var lockedDoorPositions = new List<Vector2>();
                    foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
                    {
                        if (IsLockedPuzzleDoor(entity))
                            lockedDoorPositions.Add(new Vector2(entity.GridPosNum.X, entity.GridPosNum.Y));
                    }

                    Entity? nearestTarget = null;
                    float nearestTargetDist = float.MaxValue;
                    string nearestType = "";
                    int switchCount = 0, doorCount = 0, failedCount = 0;
                    foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
                    {
                        if (!entity.IsTargetable) continue;
                        var ePath = entity.Path;
                        if (ePath == null) continue;
                        bool isDoor = entity.Type == EntityType.Door;
                        bool isSwitch = ePath.Contains("Switch_", StringComparison.Ordinal);
                        if (!isDoor && !isSwitch) continue;

                        if (isDoor) doorCount++;
                        if (isSwitch) switchCount++;
                        if (isDoor && _failedDoorIds.Contains(entity.Id)) { failedCount++; continue; }

                        var entityPos = new Vector2(entity.GridPosNum.X, entity.GridPosNum.Y);
                        var dist = Vector2.Distance(playerPos, entityPos);

                        // Check if blocked by a locked door
                        bool blocked = false;
                        foreach (var doorPos in lockedDoorPositions)
                        {
                            var doorDist = Vector2.Distance(playerPos, doorPos);
                            if (doorDist < dist)
                            {
                                var toTarget = entityPos - playerPos;
                                var toDoor = doorPos - playerPos;
                                if (toTarget.LengthSquared() > 0 && toDoor.LengthSquared() > 0 &&
                                    Vector2.Dot(Vector2.Normalize(toTarget), Vector2.Normalize(toDoor)) > 0.7f)
                                {
                                    blocked = true;
                                    break;
                                }
                            }
                        }
                        if (blocked) continue;

                        if (dist < nearestTargetDist)
                        {
                            nearestTargetDist = dist;
                            nearestTarget = entity;
                            nearestType = isSwitch ? "switch" : "door";
                        }
                    }

                    if (nearestTarget != null)
                    {
                        LabLog($"No explore targets — navigating to {nearestType} at dist={nearestTargetDist:F0} (doors={doorCount} switches={switchCount} failed={failedCount})");
                        ctx.Interaction.InteractWithEntity(nearestTarget, ctx.Navigation);
                        StatusText = $"Going to {nearestType} (dist: {nearestTargetDist:F0})";
                        return;
                    }

                    LabLog($"No explore targets, no reachable interactables (doors={doorCount} switches={switchCount} failed={failedCount} coverage={ctx.Exploration.ActiveBlobCoverage * 100:F0}%)");
                    _stuckCount++;
                    if (_stuckCount > MaxStuckBeforeSkip)
                    {
                        StatusText = "Stuck — aborting zone";
                        _phase = LabPhase.ExitLab;
                        _phaseStartTime = DateTime.Now;
                    }
                    else
                    {
                        StatusText = "No exploration targets — waiting...";
                    }
                }
            }
            else
            {
                StatusText = $"Exploring ({ctx.Exploration.ActiveBlobCoverage * 100:F0}%)";
            }

            // Handle navigation stuck — detect via recovery count increasing
            var currentRecoveries = ctx.Navigation.StuckRecoveries;
            if (currentRecoveries > _lastStuckRecoveries)
            {
                _stuckCount += currentRecoveries - _lastStuckRecoveries;
                var stuckPos = new Vector2(gc.Player.GridPosNum.X, gc.Player.GridPosNum.Y);

                // Log what's near us when stuck
                int nearDoors = 0, nearSwitches = 0, nearLockedDoors = 0;
                foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
                {
                    if (!entity.IsTargetable) continue;
                    var p = entity.Path;
                    if (p == null) continue;
                    var d = Vector2.Distance(stuckPos, new Vector2(entity.GridPosNum.X, entity.GridPosNum.Y));
                    if (d > 50f) continue;
                    if (entity.Type == EntityType.Door) nearDoors++;
                    if (p.Contains("Puzzle_Parts/Door_")) nearLockedDoors++;
                    if (p.Contains("Switch_")) nearSwitches++;
                }
                LabLog($"STUCK recovery #{_stuckCount} at ({stuckPos.X:F0},{stuckPos.Y:F0}) — nearby: doors={nearDoors} locked={nearLockedDoors} switches={nearSwitches} failedDoors={_failedDoorIds.Count}");

                // Collect locked door positions
                var stuckLockedDoors = new List<Vector2>();
                foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
                {
                    if (IsLockedPuzzleDoor(entity))
                        stuckLockedDoors.Add(new Vector2(entity.GridPosNum.X, entity.GridPosNum.Y));
                }

                // When stuck, try to find switches not blocked by locked doors
                Entity? nearestSwitch = null;
                float bestSwitchDist = float.MaxValue;
                foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
                {
                    if (!entity.IsTargetable) continue;
                    if (entity.Path?.Contains("Switch_", StringComparison.Ordinal) != true) continue;
                    var switchPos = new Vector2(entity.GridPosNum.X, entity.GridPosNum.Y);
                    var dist = Vector2.Distance(stuckPos, switchPos);

                    // Skip switches behind locked doors
                    bool blocked = false;
                    foreach (var doorPos in stuckLockedDoors)
                    {
                        var doorDist = Vector2.Distance(stuckPos, doorPos);
                        if (doorDist < dist)
                        {
                            var toSwitch = switchPos - stuckPos;
                            var toDoor = doorPos - stuckPos;
                            if (toSwitch.LengthSquared() > 0 && toDoor.LengthSquared() > 0 &&
                                Vector2.Dot(Vector2.Normalize(toSwitch), Vector2.Normalize(toDoor)) > 0.7f)
                            {
                                blocked = true;
                                break;
                            }
                        }
                    }
                    if (blocked) continue;

                    if (dist < bestSwitchDist)
                    {
                        bestSwitchDist = dist;
                        nearestSwitch = entity;
                    }
                }

                if (nearestSwitch != null && !ctx.Interaction.IsBusy)
                {
                    LabLog($"Stuck — navigating to switch (dist={bestSwitchDist:F0})");
                    ctx.Navigation.Stop(gc);
                    ctx.Interaction.InteractWithEntity(nearestSwitch, ctx.Navigation);
                    StatusText = $"Stuck — going to switch (dist: {bestSwitchDist:F0})";
                    _stuckCount = 0; // reset after taking action
                }
                else if (_exitClusters != null && _stuckCount >= 3)
                {
                    // No switch reachable — skip this exit cluster, try another
                    for (int ci = 0; ci < _exitClusters.Count; ci++)
                    {
                        if (ci == _entryClusterIndex || _skippedClusters.Contains(ci)) continue;
                        if (Vector2.Distance(stuckPos, _exitClusters[ci]) < 150f)
                        {
                            _skippedClusters.Add(ci);
                            _stuckCount = 0;
                            ctx.Navigation.Stop(gc);
                            LabLog($"Skipping cluster {ci} at ({_exitClusters[ci].X:F0},{_exitClusters[ci].Y:F0}) — stuck, trying next");
                            StatusText = "Skipping blocked exit — trying another";
                            break;
                        }
                    }
                }
            }
            _lastStuckRecoveries = currentRecoveries;
        }

        /// <summary>
        /// Build exit clusters from TileMap data. Groups nearby exit tiles into clusters
        /// and identifies the entry cluster (nearest to spawn position).
        /// </summary>
        private void BuildExitClusters(BotContext ctx)
        {
            if (_exitClusters != null) return; // already built for this zone

            var exitNames = new[] { "entry", "exit", "exitup", "entranceup" };
            var allTiles = new List<Vector2>();

            foreach (var name in exitNames)
            {
                var results = ctx.TileMap.SearchTiles(name);
                foreach (var (key, positions) in results)
                    allTiles.AddRange(positions);
            }

            if (allTiles.Count == 0)
            {
                _exitClusters = new();
                return;
            }

            // Cluster tiles by proximity
            var clusters = new List<List<Vector2>>();
            var assigned = new bool[allTiles.Count];

            for (int i = 0; i < allTiles.Count; i++)
            {
                if (assigned[i]) continue;
                var cluster = new List<Vector2> { allTiles[i] };
                assigned[i] = true;

                // Find all tiles close to any tile in this cluster
                bool grew = true;
                while (grew)
                {
                    grew = false;
                    for (int j = 0; j < allTiles.Count; j++)
                    {
                        if (assigned[j]) continue;
                        foreach (var existing in cluster)
                        {
                            if (Vector2.Distance(allTiles[j], existing) < ClusterMergeRadius)
                            {
                                cluster.Add(allTiles[j]);
                                assigned[j] = true;
                                grew = true;
                                break;
                            }
                        }
                    }
                }
                clusters.Add(cluster);
            }

            // Compute centroids
            _exitClusters = clusters
                .Select(c => new Vector2(c.Average(p => p.X), c.Average(p => p.Y)))
                .ToList();

            // Identify entry cluster (nearest to entry position)
            _entryClusterIndex = -1;
            if (_entryPosition.HasValue && _exitClusters.Count > 0)
            {
                float bestDist = float.MaxValue;
                for (int i = 0; i < _exitClusters.Count; i++)
                {
                    var dist = Vector2.Distance(_exitClusters[i], _entryPosition.Value);
                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        _entryClusterIndex = i;
                    }
                }
            }

            if (_exitClusters.Count > 4)
                LabLog($"WARNING: {_exitClusters.Count} clusters found (expected 2-4), merge radius may be too tight");

            LabLog($"Built {_exitClusters.Count} exit clusters from {allTiles.Count} tiles, entry cluster={_entryClusterIndex}");
            for (int i = 0; i < _exitClusters.Count; i++)
            {
                var c = _exitClusters[i];
                var label = i == _entryClusterIndex ? " [ENTRY]" : "";
                LabLog($"  Cluster {i}: ({c.X:F0},{c.Y:F0}){label}");
            }
        }

        /// <summary>
        /// Find nearest non-entry exit cluster centroid.
        /// </summary>
        private Vector2? FindExitViaTileMap(BotContext ctx)
        {
            BuildExitClusters(ctx);
            if (_exitClusters == null || _exitClusters.Count == 0) return null;

            var playerPos = new Vector2(ctx.Game.Player.GridPosNum.X, ctx.Game.Player.GridPosNum.Y);
            Vector2? nearest = null;
            float nearestDist = float.MaxValue;

            for (int i = 0; i < _exitClusters.Count; i++)
            {
                if (i == _entryClusterIndex) continue; // skip entrance
                if (_skippedClusters.Contains(i)) continue; // skip unreachable
                var dist = Vector2.Distance(playerPos, _exitClusters[i]);
                if (dist < nearestDist)
                {
                    nearestDist = dist;
                    nearest = _exitClusters[i];
                }
            }

            return nearest;
        }

        // ═══════════════════════════════════════════════════
        // Phase: StagingRoom
        // ═══════════════════════════════════════════════════

        private void TickStagingRoom(BotContext ctx, InteractionResult interactionResult)
        {
            var gc = ctx.Game;

            // After settle, re-detect zone type
            if (_state.HasFont)
            {
                _phase = LabPhase.RewardRoom;
                _phaseStartTime = DateTime.Now;
                StatusText = "Detected reward room";
                return;
            }
            if (_state.IsIzaroPresent)
            {
                _phase = LabPhase.FightIzaro;
                _phaseStartTime = DateTime.Now;
                _izaroRetreated = false;
                _izaroExitClicked = false;
                _izaroWasSeen = false;
                StatusText = "Detected Izaro — fighting";
                return;
            }

            // Check if we're in the arena (has elevator, no door) — need to walk in to trigger Izaro
            bool hasElevator = false;
            foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
            {
                if (entity.Path?.Contains("Elevator_Izaro", StringComparison.Ordinal) == true)
                {
                    hasElevator = true;
                    break;
                }
            }
            if (hasElevator && !_state.HasIzaroDoor)
            {
                _phase = LabPhase.FightIzaro;
                _phaseStartTime = DateTime.Now;
                _izaroRetreated = false;
                _izaroExitClicked = false;
                _izaroWasSeen = false;
                StatusText = "In arena — walking to trigger Izaro";
                LabLog("StagingRoom: detected arena (elevator, no door) → FightIzaro");
                return;
            }

            // Look for Izaro-specific entities only
            Entity? izaroDoor = null;
            Entity? arenaTransition = null;
            foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
            {
                if (entity.Path?.Contains("LabyrinthIzaroDoor", StringComparison.Ordinal) == true)
                    izaroDoor = entity;
                if (entity.Path?.Contains("LabyrinthIzaroArenaTransition", StringComparison.Ordinal) == true)
                    arenaTransition = entity;
            }

            // If arena transition is targetable, click it (door already opened)
            if (arenaTransition != null && arenaTransition.IsTargetable)
            {
                if (!ctx.Interaction.IsBusy && !_arenaTransitionClicked)
                {
                    LabLog("StagingRoom: clicking arena transition");
                    ctx.Interaction.InteractWithEntity(arenaTransition, ctx.Navigation);
                    _arenaTransitionClicked = true;
                    StatusText = "Clicking arena transition...";
                }
                else if (_arenaTransitionClicked && !ctx.Interaction.IsBusy)
                {
                    // Interaction completed — we've teleported into the arena
                    LabLog("StagingRoom: arena transition complete → FightIzaro");
                    _state.OnAreaChanged();
                    _phase = LabPhase.FightIzaro;
                    _phaseStartTime = DateTime.Now;
                    _izaroRetreated = false;
                    _izaroExitClicked = false;
                    _izaroWasSeen = false;
                    _arenaTransitionClicked = false;
                    _entryPosition = new Vector2(gc.Player.GridPosNum.X, gc.Player.GridPosNum.Y);
                }
                else
                {
                    StatusText = "Entering arena...";
                }
                return;
            }

            // If door is targetable, click it to open (reveals arena transition)
            if (izaroDoor != null && izaroDoor.IsTargetable)
            {
                if (!ctx.Interaction.IsBusy)
                {
                    LabLog("StagingRoom: clicking Izaro door");
                    ctx.Interaction.InteractWithEntity(izaroDoor, ctx.Navigation);
                    StatusText = "Clicking Izaro door...";
                }
                else
                {
                    StatusText = "Opening door...";
                }
                return;
            }

            // Door exists but not targetable, transition not yet targetable — wait
            if (izaroDoor != null || arenaTransition != null)
            {
                StatusText = "Waiting for door/transition state change...";
                return;
            }

            // Phase timeout
            if ((DateTime.Now - _phaseStartTime).TotalSeconds > 30)
            {
                StatusText = "Staging room timeout";
                _phase = LabPhase.ExitLab;
                _phaseStartTime = DateTime.Now;
            }
        }

        // ═══════════════════════════════════════════════════
        // Phase: FightIzaro
        // ═══════════════════════════════════════════════════

        private bool _izaroRetreated;
        private bool _izaroExitClicked;

        private void TickFightIzaro(BotContext ctx, InteractionResult interactionResult)
        {
            var gc = ctx.Game;
            var settings = ctx.Settings.Labyrinth;

            // Izaro timeout
            if ((DateTime.Now - _phaseStartTime).TotalSeconds > settings.IzaroTimeoutSeconds.Value)
            {
                StatusText = "Izaro timeout — aborting";
                _phase = LabPhase.ExitLab;
                _phaseStartTime = DateTime.Now;
                return;
            }

            // Izaro has retreated — loot then exit
            if (_izaroRetreated)
            {
                // Loot nearby items first (keys, etc.)
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
                        StatusText = $"Looting: {candidate.ItemName}";
                        return;
                    }
                }

                if (ctx.Interaction.IsBusy)
                {
                    StatusText = $"Izaro retreated ({_state.IzaroEncounterCount}/3) — using exit...";
                    return;
                }

                // Check if we already clicked the exit and it completed (no area change for same-area transitions)
                if (_izaroExitClicked && !ctx.Interaction.IsBusy)
                {
                    // Interaction completed — we've transitioned. Re-detect zone type.
                    LabLog("FightIzaro: exit interaction complete — re-detecting zone");
                    _state.OnAreaChanged();
                    _state.Tick(gc);
                    _izaroExitClicked = false;
                    _izaroRetreated = false;

                    if (_state.HasFont)
                    {
                        _phase = LabPhase.RewardRoom;
                        _phaseStartTime = DateTime.Now;
                        _entryPosition = new Vector2(gc.Player.GridPosNum.X, gc.Player.GridPosNum.Y);
                        LabLog("FightIzaro: detected reward room after exit");
                        StatusText = "Entered reward room";
                    }
                    else
                    {
                        // Back to staging room or another zone
                        _phase = LabPhase.StagingRoom;
                        _phaseStartTime = DateTime.Now;
                        _entryPosition = new Vector2(gc.Player.GridPosNum.X, gc.Player.GridPosNum.Y);
                        LabLog("FightIzaro: back to staging after exit");
                        StatusText = "Exited arena";
                    }
                    return;
                }

                // Find and click the forward exit (not "Stairs")
                if (!_izaroExitClicked)
                {
                    Entity? forwardExit = null;
                    foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
                    {
                        if (entity.Type != EntityType.AreaTransition || !entity.IsTargetable) continue;
                        var renderName = entity.RenderName ?? "";
                        if (renderName == "Stairs") continue;
                        if (_entryTransitionIds.Contains(entity.Id)) continue;
                        if (_entryPosition.HasValue)
                        {
                            var entityPos = new Vector2(entity.GridPosNum.X, entity.GridPosNum.Y);
                            if (Vector2.Distance(entityPos, _entryPosition.Value) < EntryBlacklistRadius) continue;
                        }
                        forwardExit = entity;
                        break;
                    }

                    if (forwardExit != null)
                    {
                        LabLog($"FightIzaro: clicking forward exit '{forwardExit.RenderName}'");
                        ctx.Interaction.InteractWithEntity(forwardExit, ctx.Navigation);
                        _izaroExitClicked = true;
                        StatusText = $"Izaro retreated ({_state.IzaroEncounterCount}/3) — exiting via {forwardExit.RenderName}";
                    }
                    else
                    {
                        StatusText = $"Izaro retreated ({_state.IzaroEncounterCount}/3) — looking for exit...";
                    }
                }
                return;
            }

            // Detect Izaro retreat: was seen THIS fight, now gone
            if (!_state.IsIzaroPresent && _izaroWasSeen)
            {
                _state.IzaroEncounterCount++;
                _izaroRetreated = true;
                _izaroWasSeen = false;
                LabLog($"Izaro retreated (encounter {_state.IzaroEncounterCount}/3)");
                return;
            }

            // Izaro not present and not yet seen this fight — walk deeper to trigger spawn
            if (!_state.IsIzaroPresent && !_izaroWasSeen)
            {
                // Navigate toward the Izaro elevator (center of arena)
                Entity? elevator = null;
                foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
                {
                    if (entity.Path?.Contains("Elevator_Izaro", StringComparison.Ordinal) == true)
                    {
                        elevator = entity;
                        break;
                    }
                }

                if (elevator != null && !ctx.Navigation.IsNavigating)
                {
                    var elevatorPos = new Vector2(elevator.GridPosNum.X, elevator.GridPosNum.Y);
                    ctx.Navigation.NavigateTo(gc, elevatorPos);
                    StatusText = "Walking into arena to trigger Izaro...";
                }
                else if (!ctx.Navigation.IsNavigating)
                {
                    // No elevator found — walk toward the far end (away from entry)
                    if (_entryPosition.HasValue)
                    {
                        var playerPos = new Vector2(gc.Player.GridPosNum.X, gc.Player.GridPosNum.Y);
                        var awayDir = Vector2.Normalize(playerPos - _entryPosition.Value);
                        var target = playerPos + awayDir * 60f;
                        ctx.Navigation.NavigateTo(gc, target);
                        StatusText = "Walking deeper into arena...";
                    }
                }
                else
                {
                    StatusText = "Approaching arena center...";
                }
                return;
            }

            if (_state.IsIzaroPresent)
            {
                _izaroWasSeen = true;
                StatusText = $"Fighting Izaro ({_state.IzaroEncounterCount + 1}/3)";
            }
            else
            {
                StatusText = "Waiting for Izaro...";
            }
        }

        // ═══════════════════════════════════════════════════
        // Phase: RewardRoom
        // ═══════════════════════════════════════════════════

        private void TickRewardRoom(BotContext ctx, InteractionResult interactionResult)
        {
            var gc = ctx.Game;
            var settings = ctx.Settings.Labyrinth;

            // Optional: open reward chests if we have treasure keys
            if (settings.OpenRewardChests.Value && HasTreasureKeys(gc))
            {
                Entity? nearestChest = null;
                float nearestDist = float.MaxValue;
                foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
                {
                    if (entity.Path?.Contains("Chests/Labyrinth/Izaro/", StringComparison.Ordinal) != true)
                        continue;
                    if (!entity.IsTargetable) continue;

                    var dist = entity.DistancePlayer;
                    if (dist < nearestDist)
                    {
                        nearestDist = dist;
                        nearestChest = entity;
                    }
                }

                if (nearestChest != null && !ctx.Interaction.IsBusy)
                {
                    ctx.Interaction.InteractWithEntity(nearestChest, ctx.Navigation);
                    StatusText = $"Opening chest (dist: {nearestDist:F0})";
                    return;
                }
                else if (nearestChest != null && ctx.Interaction.IsBusy)
                {
                    StatusText = "Opening chest...";
                    return;
                }
            }

            // Check if font UI is already open (from previous click or interaction)
            var fontPanel = GetFontPanel(gc);
            if (fontPanel != null && fontPanel.IsVisible)
            {
                _phase = LabPhase.FontPlaceGem;
                _phaseStartTime = DateTime.Now;
                _fontClickAttempts = 0;
                ctx.Interaction.Cancel(gc);
                LabLog("RewardRoom: font UI detected → FontPlaceGem");
                StatusText = "Font UI open — placing gem";
                return;
            }

            // Navigate to and click the Divine Font
            Entity? fontEntity = null;
            foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
            {
                if (entity.Path?.Contains("LabyrinthBlessingBench", StringComparison.Ordinal) == true
                    && entity.IsTargetable)
                {
                    fontEntity = entity;
                    break;
                }
            }

            if (fontEntity != null && !ctx.Interaction.IsBusy)
            {
                // Don't re-click too fast — give the UI time to appear after a click
                if (ModeHelpers.CanAct(_lastActionTime, 2000f))
                {
                    LabLog($"RewardRoom: clicking font (dist={fontEntity.DistancePlayer:F0})");
                    ctx.Interaction.InteractWithEntity(fontEntity, ctx.Navigation);
                    _lastActionTime = DateTime.Now;
                    StatusText = $"Clicking Divine Font (dist: {fontEntity.DistancePlayer:F0})...";
                }
                else
                {
                    StatusText = "Waiting for font UI to open...";
                }
            }
            else if (ctx.Interaction.IsBusy)
            {
                StatusText = "Interacting with font...";
            }
            else
            {
                StatusText = "Searching for Divine Font...";
            }

            // Phase timeout
            if ((DateTime.Now - _phaseStartTime).TotalSeconds > 60)
            {
                StatusText = "Reward room timeout — exiting";
                LabLog("RewardRoom: timeout");
                _phase = LabPhase.ExitLab;
                _phaseStartTime = DateTime.Now;
            }
        }

        // ═══════════════════════════════════════════════════
        // Phase: FontPlaceGem
        // ═══════════════════════════════════════════════════

        private void TickFontPlaceGem(BotContext ctx)
        {
            var gc = ctx.Game;
            if (!ModeHelpers.CanAct(_lastActionTime, ActionCooldownMs)) return;

            var fontPanel = GetFontPanel(gc);
            if (fontPanel == null || !fontPanel.IsVisible)
            {
                StatusText = "Font panel closed — returning to reward room";
                _phase = LabPhase.RewardRoom;
                _phaseStartTime = DateTime.Now;
                return;
            }

            // If no gem selected yet (bot restarted mid-run), evaluate inventory now
            if (string.IsNullOrEmpty(_state.SelectedGemName))
            {
                _gemValuation.BuildColourMap(gc);
                _gemValuation.RebuildIndex(ctx.NinjaPrice);
                var settings2 = ctx.Settings.Labyrinth;
                var keepThreshold = (double)settings2.KeepThreshold.Value;

                var slotItems2 = StashSystem.GetInventorySlotItems(gc);
                if (slotItems2 != null)
                {
                    var candidates = new List<(string Name, double ChaosValue, int Quality, int Level)>();
                    foreach (var si in slotItems2)
                    {
                        var item = si.Item;
                        if (item == null) continue;
                        var bit = gc.Files.BaseItemTypes.Translate(item.Path);
                        if (bit?.ClassName is not ("Active Skill Gem" or "Support Skill Gem")) continue;
                        var gemName = bit.BaseName ?? "";
                        if (string.IsNullOrEmpty(gemName)) continue;

                        int quality = 0;
                        if (item.TryGetComponent<ExileCore.PoEMemory.Components.Quality>(out var q))
                            quality = q.ItemQuality;
                        int level = 1;
                        if (item.TryGetComponent<SkillGem>(out var sg))
                            level = sg.Level;

                        var price = ctx.NinjaPrice.GetPrice(gc, item);
                        candidates.Add((gemName, price.MaxChaosValue, quality, level));
                        LabLog($"FontPlaceGem eval: {gemName} lvl={level} q={quality} val={price.MaxChaosValue:F1}c keep={keepThreshold}c");
                    }

                    var best = _gemValuation.SelectBestGem(
                        candidates.Select(g => (g.Name, g.ChaosValue, g.Quality)),
                        0, keepThreshold);
                    if (best != null)
                    {
                        _state.SelectedGemName = best.GemName;
                        _state.SelectedGemValue = best.CurrentValue;
                        var matched = candidates.FirstOrDefault(g =>
                            GemValuationService.GetBaseGemName(g.Name) == best.GemName);
                        _state.SelectedGemLevel = matched.Level > 0 ? matched.Level : 1;
                        _state.SelectedGemQuality = matched.Quality;
                        LabLog($"FontPlaceGem: auto-selected '{best.GemName}' lvl{_state.SelectedGemLevel} q{_state.SelectedGemQuality} (EV={best.BestEV:F1}c profit={best.ExpectedProfit:F1}c)");
                    }
                }
            }

            // Find the selected gem in inventory and Ctrl+click it into the font
            var slotItems = StashSystem.GetInventorySlotItems(gc);
            if (slotItems == null)
            {
                StatusText = "Cannot read inventory";
                return;
            }

            ServerInventory.InventSlotItem? targetItem = null;
            foreach (var si in slotItems)
            {
                var item = si.Item;
                if (item == null) continue;
                var bit = gc.Files.BaseItemTypes.Translate(item.Path);
                var className = bit?.ClassName;
                if (className is not ("Active Skill Gem" or "Support Skill Gem")) continue;

                var baseName = GemValuationService.GetBaseGemName(bit?.BaseName ?? "");
                if (baseName == _state.SelectedGemName)
                {
                    targetItem = si;
                    break;
                }
            }

            if (targetItem == null)
            {
                StatusText = "No suitable gem found in inventory";
                LabLog($"FontPlaceGem: gem '{_state.SelectedGemName}' not found → ExitLab");
                _phase = LabPhase.ExitLab;
                _phaseStartTime = DateTime.Now;
                return;
            }

            // Ctrl+click the gem into the font
            var rect = targetItem.GetClientRect();
            var windowRect = gc.Window.GetWindowRectangle();
            var clickPos = BotInput.RandomizeWithinRect(rect);
            var absPos = new Vector2(windowRect.X + clickPos.X, windowRect.Y + clickPos.Y);

            if (BotInput.CtrlClick(absPos))
            {
                _lastActionTime = DateTime.Now;
                _fontClickAttempts++;
                LabLog($"FontPlaceGem: Ctrl+clicked gem '{_state.SelectedGemName}' into font");
                StatusText = "Placed gem in font — selecting option...";

                // Move to option selection after a short delay
                _phase = LabPhase.FontSelectOption;
                _phaseStartTime = DateTime.Now;
            }
        }

        // ═══════════════════════════════════════════════════
        // Phase: FontSelectOption
        // ═══════════════════════════════════════════════════

        private bool _optionClicked; // true after clicking option, waiting to click craft

        private void TickFontSelectOption(BotContext ctx)
        {
            var gc = ctx.Game;
            if (!ModeHelpers.CanAct(_lastActionTime, ActionCooldownMs)) return;

            var fontPanel = GetFontPanel(gc);
            if (fontPanel == null || !fontPanel.IsVisible)
            {
                StatusText = "Font panel closed";
                _phase = LabPhase.RewardRoom;
                _phaseStartTime = DateTime.Now;
                _optionClicked = false;
                return;
            }

            var windowRect = gc.Window.GetWindowRectangle();

            // Step 2: After clicking option, click craft button
            if (_optionClicked)
            {
                // Check if result area appeared (craft already processed)
                var resultCheck = fontPanel.GetChildAtIndex(4);
                if (resultCheck != null && resultCheck.IsVisible)
                {
                    LabLog("FontSelectOption: result area visible → FontSelectResult");
                    _phase = LabPhase.FontSelectResult;
                    _phaseStartTime = DateTime.Now;
                    _optionClicked = false;
                    _resultGemClicked = false;
                    _hoverIndex = -1;
                    _scannedGems.Clear();
                    return;
                }

                var craftBtn = fontPanel.GetChildFromIndices(0, 3, 0);
                if (craftBtn != null && craftBtn.IsVisible)
                {
                    var btnRect = craftBtn.GetClientRect();
                    var btnClick = BotInput.RandomizeWithinRect(btnRect);
                    var btnAbs = new Vector2(windowRect.X + btnClick.X, windowRect.Y + btnClick.Y);
                    if (BotInput.Click(btnAbs))
                    {
                        _lastActionTime = DateTime.Now;
                        LabLog("FontSelectOption: clicked craft");
                        StatusText = "Clicked craft — waiting for results...";
                    }
                }
                return;
            }

            // Step 1: Read options at UI[68][0][2][2][i]
            var optionsParent = fontPanel.GetChildFromIndices(0, 2, 2);
            if (optionsParent == null || !optionsParent.IsVisible)
            {
                // Options not visible — may already be in result phase
                var resultArea = fontPanel.GetChildAtIndex(4);
                if (resultArea != null && resultArea.IsVisible)
                {
                    _phase = LabPhase.FontSelectResult;
                    _phaseStartTime = DateTime.Now;
                    StatusText = "Already in result selection";
                    return;
                }

                StatusText = "Waiting for options to appear...";
                if ((DateTime.Now - _phaseStartTime).TotalSeconds > 10)
                {
                    _phase = LabPhase.ExitLab;
                    StatusText = "Options timeout";
                }
                return;
            }

            var settings = ctx.Settings.Labyrinth;

            // Find the best option
            Element? bestOption = null;
            string bestDescription = "";
            for (int i = 0; i < optionsParent.ChildCount; i++)
            {
                var opt = optionsParent.GetChildAtIndex(i);
                if (opt == null || !opt.IsVisible) continue;

                var textEl = opt.GetChildAtIndex(1);
                var text = textEl?.Text ?? "";

                // Prefer "same type" (Transfigured Gem of the same type)
                if (text.Contains("same type", StringComparison.OrdinalIgnoreCase) && settings.PreferSameType.Value)
                {
                    bestOption = opt;
                    bestDescription = "same type";
                    break; // This is always best when available
                }

                // "same colour" transform
                if (text.Contains("same colour", StringComparison.OrdinalIgnoreCase) ||
                    text.Contains("Transfigured Gem", StringComparison.OrdinalIgnoreCase))
                {
                    if (bestOption == null || bestDescription != "same type")
                    {
                        bestOption = opt;
                        bestDescription = "same colour";
                    }
                }
            }

            if (bestOption == null)
            {
                StatusText = "No suitable transformation option found";
                _phase = LabPhase.ExitLab;
                _phaseStartTime = DateTime.Now;
                return;
            }

            // Click the option
            var optRect = bestOption.GetClientRect();
            var optClick = BotInput.RandomizeWithinRect(optRect);
            var optAbs = new Vector2(windowRect.X + optClick.X, windowRect.Y + optClick.Y);

            if (BotInput.Click(optAbs))
            {
                _lastActionTime = DateTime.Now;
                StatusText = $"Selected option: {bestDescription} — clicking craft next";
                LabLog($"FontSelectOption: clicked '{bestDescription}'");
                _optionClicked = true;
            }
        }

        private void ClickCraftButton(GameController gc)
        {
            var fontPanel = GetFontPanel(gc);
            if (fontPanel == null) return;

            // Craft button at [0][3][0]
            var craftBtn = fontPanel.GetChildFromIndices(0, 3, 0);
            if (craftBtn == null || !craftBtn.IsVisible) return;

            var windowRect = gc.Window.GetWindowRectangle();
            var btnRect = craftBtn.GetClientRect();
            var btnClick = BotInput.RandomizeWithinRect(btnRect);
            var btnAbs = new Vector2(windowRect.X + btnClick.X, windowRect.Y + btnClick.Y);

            BotInput.Click(btnAbs);
        }

        // ═══════════════════════════════════════════════════
        // Phase: FontSelectResult
        // ═══════════════════════════════════════════════════

        private bool _resultGemClicked;
        private bool _confirmClicked;
        private DateTime _confirmClickTime;
        private int _confirmClickAttempts;

        // Gem hover scanning state
        private int _hoverIndex = -1; // -1 = not started, 0+ = hovering gem at index
        private DateTime _hoverStartTime;
        private readonly Dictionary<int, (string Name, double Value)> _scannedGems = new();
        private const float HoverReadDelayMs = 400f; // wait after hovering before reading tooltip

        private void TickFontSelectResult(BotContext ctx)
        {
            var gc = ctx.Game;
            if (!ModeHelpers.CanAct(_lastActionTime, ActionCooldownMs)) return;

            var fontPanel = GetFontPanel(gc);
            if (fontPanel == null || !fontPanel.IsVisible)
            {
                StatusText = "Font panel closed — gem collected";
                LabLog("FontSelectResult: panel closed → ExitLab");
                _state.GemsTransformed++;
                _phase = LabPhase.ExitLab;
                _phaseStartTime = DateTime.Now;
                _resultGemClicked = false;
                _hoverIndex = -1;
                _scannedGems.Clear();
                return;
            }

            var windowRect = gc.Window.GetWindowRectangle();

            // Step 3: After confirm, Ctrl+click the gem from font back to inventory
            // Must check BEFORE resultArea visibility — result area hides after confirm
            if (_confirmClicked)
            {
                // The transformed gem is at LabyrinthDivineFontPanel[0][3][3][1]
                // Verify it's actually an item by checking it has an Entity
                var gemSlot = fontPanel.GetChildFromIndices(0, 3, 3, 1);
                if (gemSlot?.Entity == null || string.IsNullOrEmpty(gemSlot.Entity.Path))
                    gemSlot = null; // not an item — UI might have changed

                if (gemSlot != null)
                {
                    // Gem still in font — Ctrl+click to retrieve
                    var slotRect = gemSlot.GetClientRect();
                    var slotClick = BotInput.RandomizeWithinRect(slotRect);
                    var slotAbs = new Vector2(windowRect.X + slotClick.X, windowRect.Y + slotClick.Y);
                    if (BotInput.CtrlClick(slotAbs))
                    {
                        _lastActionTime = DateTime.Now;
                        _confirmClickAttempts++;
                        LabLog($"FontSelectResult: Ctrl+click retrieval attempt {_confirmClickAttempts}");
                        StatusText = $"Retrieving gem (attempt {_confirmClickAttempts})...";
                    }
                }
                else
                {
                    // Gem is gone from font — successfully retrieved! Close panel and exit.
                    LabLog("FontSelectResult: gem retrieved successfully → closing font → ExitLab");
                    _state.GemsTransformed++;
                    _phase = LabPhase.ExitLab;
                    _phaseStartTime = DateTime.Now;
                    _confirmClicked = false;
                    BotInput.PressKey(System.Windows.Forms.Keys.Escape);
                    return;
                }

                // Timeout — if we've been trying for 10s, give up and close
                if ((DateTime.Now - _confirmClickTime).TotalSeconds > 10)
                {
                    LabLog("FontSelectResult: gem retrieval timeout → closing font");
                    _state.GemsTransformed++;
                    _phase = LabPhase.ExitLab;
                    _phaseStartTime = DateTime.Now;
                    _confirmClicked = false;
                    BotInput.PressKey(System.Windows.Forms.Keys.Escape);
                }
                return;
            }

            // Check if result area is visible (needed for steps 1 & 2)
            var resultArea = fontPanel.GetChildAtIndex(4);
            if (resultArea == null || !resultArea.IsVisible)
            {
                StatusText = "Waiting for result gems...";
                if ((DateTime.Now - _phaseStartTime).TotalSeconds > 15)
                {
                    StatusText = "Result timeout";
                    _phase = LabPhase.ExitLab;
                    _phaseStartTime = DateTime.Now;
                }
                return;
            }

            // Step 2: After clicking a gem, check if confirm is enabled and click it
            if (_resultGemClicked)
            {
                // LabyrinthDivineFontPanel[4][0][1][0] — confirm button, IsActive when gem selected
                var confirmBtn = fontPanel.GetChildFromIndices(4, 0, 1, 0);
                if (confirmBtn != null && confirmBtn.IsActive)
                {
                    var btnRect = confirmBtn.GetClientRect();
                    var btnClick = BotInput.RandomizeWithinRect(btnRect);
                    var btnAbs = new Vector2(windowRect.X + btnClick.X, windowRect.Y + btnClick.Y);
                    if (BotInput.Click(btnAbs))
                    {
                        _lastActionTime = DateTime.Now;
                        _confirmClicked = true;
                        _confirmClickTime = DateTime.Now;
                        _confirmClickAttempts = 0;
                        LabLog("FontSelectResult: clicked confirm");
                        StatusText = "Confirmed — retrieving gem...";
                    }
                }
                else
                {
                    StatusText = "Waiting for confirm to enable...";
                }
                return;
            }

            // Step 1: Hover over each gem to populate tooltips, then pick the best
            var gemsParent = fontPanel.GetChildFromIndices(4, 0, 0, 0);
            if (gemsParent == null)
            {
                LabLog("FontSelectResult: gem container not found");
                StatusText = "Cannot find gem results";
                if ((DateTime.Now - _phaseStartTime).TotalSeconds > 10)
                {
                    _phase = LabPhase.ExitLab;
                    _phaseStartTime = DateTime.Now;
                }
                return;
            }

            // Count visible gems
            int visibleGems = 0;
            for (int i = 0; i < gemsParent.ChildCount; i++)
            {
                var el = gemsParent.GetChildAtIndex(i);
                if (el != null && el.IsVisible) visibleGems++;
            }

            // Phase A: Hover over gems one at a time to populate tooltips
            if (_hoverIndex < visibleGems)
            {
                // Start hovering the next gem
                if (_hoverIndex < 0)
                {
                    _hoverIndex = 0;
                    _hoverStartTime = DateTime.Now;
                    LabLog($"FontSelectResult: scanning {visibleGems} gems via hover");
                }

                // Find the gem element at current hover index
                int visIdx = 0;
                Element? currentGem = null;
                for (int i = 0; i < gemsParent.ChildCount; i++)
                {
                    var el = gemsParent.GetChildAtIndex(i);
                    if (el == null || !el.IsVisible) continue;
                    if (visIdx == _hoverIndex) { currentGem = el; break; }
                    visIdx++;
                }

                if (currentGem == null)
                {
                    _hoverIndex = visibleGems; // done
                }
                else
                {
                    // Move mouse over this gem
                    var gemRect = currentGem.GetClientRect();
                    var hoverPos = new Vector2(
                        windowRect.X + gemRect.X + gemRect.Width / 2,
                        windowRect.Y + gemRect.Y + gemRect.Height / 2);
                    ExileCore.Input.SetCursorPos(hoverPos);

                    // Wait for tooltip to populate
                    if ((DateTime.Now - _hoverStartTime).TotalMilliseconds < HoverReadDelayMs)
                    {
                        StatusText = $"Scanning gem {_hoverIndex + 1}/{visibleGems}...";
                        return;
                    }

                    // Try reading the tooltip now
                    string gemName = "";
                    try
                    {
                        var tooltip = currentGem.Tooltip;
                        if (tooltip != null)
                        {
                            var nameEl = tooltip.GetChildAtIndex(0)?.GetChildAtIndex(0);
                            gemName = nameEl?.Text ?? "";
                        }

                        // Also try UIHover tooltip as fallback
                        if (string.IsNullOrEmpty(gemName))
                        {
                            var uiHover = gc.IngameState.UIHover;
                            if (uiHover?.Tooltip != null)
                            {
                                var nameEl = uiHover.Tooltip.GetChildAtIndex(0)?.GetChildAtIndex(0);
                                gemName = nameEl?.Text ?? "";
                            }
                        }
                    }
                    catch { }

                    if (!string.IsNullOrEmpty(gemName))
                    {
                        var value = _gemValuation.PriceTransfiguredGem(gemName, ctx.NinjaPrice, _state.SelectedGemLevel, _state.SelectedGemQuality);
                        _scannedGems[_hoverIndex] = (gemName, value);
                        LabLog($"  Gem [{_hoverIndex}]: {gemName} (lvl{_state.SelectedGemLevel} q{_state.SelectedGemQuality}) = {value:F0}c");
                    }
                    else
                    {
                        LabLog($"  Gem [{_hoverIndex}]: tooltip not readable");
                    }

                    // Move to next gem
                    _hoverIndex++;
                    _hoverStartTime = DateTime.Now;
                }
                return;
            }

            // Phase B: All gems scanned — pick the best and click it
            if (_scannedGems.Count > 0)
            {
                var best = _scannedGems.OrderByDescending(kv => kv.Value.Value).First();
                int bestIdx = best.Key;
                var bestName = best.Value.Name;
                var bestValue = best.Value.Value;

                // Find the element at bestIdx
                int idx = 0;
                Element? bestGem = null;
                for (int i = 0; i < gemsParent.ChildCount; i++)
                {
                    var el = gemsParent.GetChildAtIndex(i);
                    if (el == null || !el.IsVisible) continue;
                    if (idx == bestIdx) { bestGem = el; break; }
                    idx++;
                }

                if (bestGem != null)
                {
                    var gemRect = bestGem.GetClientRect();
                    var gemClick = BotInput.RandomizeWithinRect(gemRect);
                    var gemAbs = new Vector2(windowRect.X + gemClick.X, windowRect.Y + gemClick.Y);
                    if (BotInput.Click(gemAbs))
                    {
                        _lastActionTime = DateTime.Now;
                        _resultGemClicked = true;
                        var profit = bestValue - _state.SelectedGemValue;
                        _state.TotalProfit += profit;
                        StatusText = $"Selected: {bestName} ({bestValue:F0}c) — waiting for confirm";
                        LabLog($"FontSelectResult: clicked best gem '{bestName}' = {bestValue:F0}c (profit: {profit:F0}c)");
                    }
                }
            }
            else
            {
                // No gems were readable — click first visible as fallback
                LabLog("FontSelectResult: no gems readable after hover scan, clicking first");
                for (int i = 0; i < gemsParent.ChildCount; i++)
                {
                    var el = gemsParent.GetChildAtIndex(i);
                    if (el == null || !el.IsVisible) continue;
                    var gemRect = el.GetClientRect();
                    var gemClick = BotInput.RandomizeWithinRect(gemRect);
                    var gemAbs = new Vector2(windowRect.X + gemClick.X, windowRect.Y + gemClick.Y);
                    if (BotInput.Click(gemAbs))
                    {
                        _lastActionTime = DateTime.Now;
                        _resultGemClicked = true;
                    }
                    break;
                }
            }
        }

        private void ClickConfirmButton(GameController gc)
        {
            var fontPanel = GetFontPanel(gc);
            if (fontPanel == null) return;

            // Confirm button at [4][0][1][0]
            var confirmBtn = fontPanel.GetChildFromIndices(4, 0, 1, 0);
            if (confirmBtn == null || !confirmBtn.IsVisible) return;

            var windowRect = gc.Window.GetWindowRectangle();
            var btnRect = confirmBtn.GetClientRect();
            var btnClick = BotInput.RandomizeWithinRect(btnRect);
            var btnAbs = new Vector2(windowRect.X + btnClick.X, windowRect.Y + btnClick.Y);

            BotInput.Click(btnAbs);
        }

        private Element? GetFontPanel(GameController gc)
        {
            try
            {
                var panel = gc.IngameState.IngameUi.LabyrinthDivineFontPanel;
                return panel?.IsVisible == true ? panel : null;
            }
            catch { return null; }
        }

        // ═══════════════════════════════════════════════════
        // Phase: ExitLab
        // ═══════════════════════════════════════════════════

        private void TickExitLab(BotContext ctx, InteractionResult interactionResult)
        {
            var gc = ctx.Game;

            // Close any open panels
            var fontPanel = GetFontPanel(gc);
            if (fontPanel != null && fontPanel.IsVisible)
            {
                if (ModeHelpers.CanAct(_lastActionTime, ActionCooldownMs))
                {
                    BotInput.PressKey(System.Windows.Forms.Keys.Escape);
                    _lastActionTime = DateTime.Now;
                    StatusText = "Closing font panel...";
                }
                return;
            }

            // Look for return portal
            Entity? returnPortal = null;
            foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
            {
                if (entity.Path?.Contains("LabyrinthReturnPortal", StringComparison.Ordinal) == true
                    && entity.IsTargetable)
                {
                    returnPortal = entity;
                    break;
                }
            }

            if (returnPortal != null && !ctx.Interaction.IsBusy)
            {
                ctx.Interaction.InteractWithEntity(returnPortal, ctx.Navigation);
                StatusText = "Clicking return portal...";
                return;
            }

            // No return portal — try town portal (emergency exit)
            if (!ctx.Interaction.IsBusy && returnPortal == null)
            {
                // Look for any town portal
                var townPortal = ModeHelpers.FindNearestPortal(gc);
                if (townPortal != null)
                {
                    ctx.Interaction.InteractWithEntity(townPortal, ctx.Navigation);
                    StatusText = "Using town portal...";
                    return;
                }

                StatusText = "No exit found — waiting...";
            }

            // Phase timeout
            if ((DateTime.Now - _phaseStartTime).TotalSeconds > 30)
            {
                StatusText = "Exit timeout";
                _phase = LabPhase.Done;
            }
        }

        // ═══════════════════════════════════════════════════
        // Render
        // ═══════════════════════════════════════════════════

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

            g.DrawText($"Izaro: {_state.IzaroEncounterCount}/3 | Zone: {_state.ZoneCount}",
                new Vector2(hudX, hudY), SharpDX.Color.Cyan);
            hudY += lineH;

            if (_state.DeathCount > 0)
            {
                g.DrawText($"Deaths: {_state.DeathCount}/{ctx.Settings.Labyrinth.MaxDeaths.Value}",
                    new Vector2(hudX, hudY), SharpDX.Color.Red);
                hudY += lineH;
            }

            g.DrawText($"Runs: {_state.RunsCompleted} | Gems: {_state.GemsTransformed} | Profit: {_state.TotalProfit:F0}c",
                new Vector2(hudX, hudY), SharpDX.Color.Gold);
            hudY += lineH;

            if (!string.IsNullOrEmpty(_state.SelectedGemName))
            {
                g.DrawText($"Gem: {_state.SelectedGemName} ({_state.SelectedGemValue:F0}c)",
                    new Vector2(hudX, hudY), SharpDX.Color.Orange);
                hudY += lineH;
            }

            if (ctx.Interaction.IsBusy)
            {
                g.DrawText($"Interact: {ctx.Interaction.Status}",
                    new Vector2(hudX, hudY), SharpDX.Color.Yellow);
                hudY += lineH;
            }

            if (ctx.Navigation.IsNavigating)
            {
                g.DrawText($"Nav: wp {ctx.Navigation.CurrentWaypointIndex}/{ctx.Navigation.CurrentNavPath?.Count ?? 0}",
                    new Vector2(hudX, hudY), SharpDX.Color.CornflowerBlue);
                hudY += lineH;
            }

            // Routing info
            if (_routing.IsLoaded && _preferredExits.Count > 0)
            {
                g.DrawText($"Route: {string.Join(" → ", _preferredExits)}",
                    new Vector2(hudX, hudY), SharpDX.Color.Orange);
                hudY += lineH;
            }
            else if (_routing.IsLoaded)
            {
                g.DrawText("Route: no preferred exit", new Vector2(hudX, hudY), SharpDX.Color.Gray);
                hudY += lineH;
            }

            // Entity status
            var entityInfo = new List<string>();
            if (_state.HasFont) entityInfo.Add("Font");
            if (_state.HasIzaroDoor) entityInfo.Add("Door");
            if (_state.IsIzaroPresent) entityInfo.Add("IZARO");
            if (_state.HasReturnPortal) entityInfo.Add("Portal");
            if (_state.ExitTransitions.Count > 0) entityInfo.Add($"Exits:{_state.ExitTransitions.Count}");
            if (_state.ChestCount > 0) entityInfo.Add($"Chests:{_state.ChestCount}");
            if (entityInfo.Count > 0)
            {
                g.DrawText($"Entities: {string.Join(" | ", entityInfo)}",
                    new Vector2(hudX, hudY), SharpDX.Color.Gray);
                hudY += lineH;
            }

            // --- World markers ---
            if (gc.Area?.CurrentArea?.IsHideout == true || gc.Area?.CurrentArea?.IsTown == true)
                return;

            // Divine Font
            if (_state.FontPosition.HasValue)
            {
                var pos = Pathfinding.GridToWorld3D(gc, _state.FontPosition.Value);
                g.DrawText("FONT", cam.WorldToScreen(pos) + new Vector2(-15, -20), SharpDX.Color.Purple);
                g.DrawCircleInWorld(pos, 25f, SharpDX.Color.Purple, 2f);
            }

            // Return portal
            if (_state.ReturnPortalPosition.HasValue)
            {
                var pos = Pathfinding.GridToWorld3D(gc, _state.ReturnPortalPosition.Value);
                g.DrawText("EXIT", cam.WorldToScreen(pos) + new Vector2(-12, -15), SharpDX.Color.Aqua);
            }

            // Izaro
            if (_state.IzaroPosition.HasValue)
            {
                var pos = Pathfinding.GridToWorld3D(gc, _state.IzaroPosition.Value);
                g.DrawText("IZARO", cam.WorldToScreen(pos) + new Vector2(-18, -25), SharpDX.Color.Red);
                g.DrawCircleInWorld(pos, 30f, SharpDX.Color.Red, 2f);
            }

            // Izaro door
            if (_state.IzaroDoorPosition.HasValue)
            {
                var pos = Pathfinding.GridToWorld3D(gc, _state.IzaroDoorPosition.Value);
                g.DrawText("DOOR", cam.WorldToScreen(pos) + new Vector2(-15, -15), SharpDX.Color.Yellow);
            }

            // Stash
            if (_state.StashPosition.HasValue)
            {
                var pos = Pathfinding.GridToWorld3D(gc, _state.StashPosition.Value);
                g.DrawText("STASH", cam.WorldToScreen(pos) + new Vector2(-15, -15), SharpDX.Color.Gold);
            }

            // Exit transitions (entity-based) — label with routing info
            foreach (var (id, exitPos) in _state.ExitTransitions)
            {
                bool isEntry = _entryPosition.HasValue && Vector2.Distance(exitPos, _entryPosition.Value) < EntryBlacklistRadius;
                var pos = Pathfinding.GridToWorld3D(gc, exitPos);
                var screenPos = cam.WorldToScreen(pos);

                if (isEntry)
                {
                    g.DrawText("ENTRY", screenPos + new Vector2(-12, -15), SharpDX.Color.DarkGray);
                }
                else
                {
                    // Try to get the transition's destination name
                    string destName = "";
                    foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
                    {
                        if (entity.Id == id) { destName = entity.RenderName ?? ""; break; }
                    }

                    bool isPreferred = _preferredExits.Count > 0 &&
                        !string.IsNullOrEmpty(destName) &&
                        destName.Equals(_preferredExits[0], StringComparison.OrdinalIgnoreCase);

                    var color = isPreferred ? SharpDX.Color.Gold : SharpDX.Color.LimeGreen;
                    var label = isPreferred ? $"★ {destName}" : (string.IsNullOrEmpty(destName) ? "EXIT" : destName);
                    g.DrawText(label, screenPos + new Vector2(-30, -15), color);
                }
            }

            // Navigation path
            if (ctx.Navigation.IsNavigating && ctx.Navigation.CurrentNavPath != null)
            {
                var path = ctx.Navigation.CurrentNavPath;
                for (int i = ctx.Navigation.CurrentWaypointIndex; i < path.Count - 1; i++)
                {
                    var from = Pathfinding.GridToScreen(gc, path[i].Position);
                    var to = Pathfinding.GridToScreen(gc, path[i + 1].Position);
                    g.DrawLine(from, to, 1.5f, SharpDX.Color.CornflowerBlue);
                }
            }
        }
    }
}
