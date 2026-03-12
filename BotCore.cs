using ExileCore;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared;
using ImGuiNET;
using AutoExile.Mechanics;
using AutoExile.Modes;
using AutoExile.Systems;
using System.Linq;
using System.Numerics;

namespace AutoExile
{
    public class BotCore : BaseSettingsPlugin<BotSettings>
    {
        private BotContext _ctx = null!;
        private IBotMode _mode = new IdleMode();
        private readonly Dictionary<string, IBotMode> _modes = new();

        // Systems
        private NavigationSystem _navigation = new();
        private InteractionSystem _interaction = new();
        private TileMap _tileMap = new();
        private CombatSystem _combat = new();
        private LootSystem _loot = new();
        private MapDeviceSystem _mapDevice = new();
        private StashSystem _stash = new();

        // Gem level-up
        private DateTime _lastGemLevelAt = DateTime.MinValue;
        private const int GemLevelCooldownMs = 10000;
        private ExplorationMap _exploration = new();
        private LootTracker _lootTracker = new();
        private MapMechanicManager _mechanics = new();

        // Mode references for ImGui buttons
        private DebugPathfindingMode? _debugMode;
        private FollowerMode? _followerMode;
        private BlightMode? _blightMode;
        private MappingMode? _mappingMode;
        private SimulacrumMode? _simulacrumMode;

        // Area change tracking for tile map reload
        private string _lastAreaName = "";

        // --- Buff scanner ---
        private bool _buffScanActive;
        private int _buffScanSlotIndex = -1; // which skill slot (0-based) we're scanning for
        private HashSet<string> _buffScanBaseline = new(); // buff names on monsters before cast
        private List<string> _buffScanResults = new(); // new buffs detected after cast
        private string _buffScanStatus = "";
        private DateTime _buffScanStartTime;
        private bool _buffScanWaitingForCast;
        private const float BuffScanTimeoutSeconds = 8f;

        public override bool Initialise()
        {
            Name = "AutoExile";

            _ctx = new BotContext
            {
                Game = GameController,
                Navigation = _navigation,
                Interaction = _interaction,
                TileMap = _tileMap,
                Combat = _combat,
                Loot = _loot,
                MapDevice = _mapDevice,
                Stash = _stash,
                Exploration = _exploration,
                LootTracker = _lootTracker,
                Mechanics = _mechanics,
                Settings = Settings,
                Log = msg => LogMessage($"[AutoExile] {msg}")
            };

            RegisterMode(new IdleMode());
            _debugMode = new DebugPathfindingMode();
            RegisterMode(_debugMode);
            _followerMode = new FollowerMode();
            RegisterMode(_followerMode);
            _blightMode = new BlightMode();
            RegisterMode(_blightMode);
            _mappingMode = new MappingMode();
            RegisterMode(_mappingMode);
            _simulacrumMode = new SimulacrumMode();
            RegisterMode(_simulacrumMode);

            // Register in-map mechanics
            _mechanics.Register(new UltimatumMechanic());

            // Populate mode dropdown and restore saved selection
            Settings.ActiveMode.SetListValues(_modes.Keys.ToList());
            var savedMode = Settings.ActiveMode?.Value;
            if (!string.IsNullOrEmpty(savedMode) && _modes.ContainsKey(savedMode))
                SetMode(savedMode);
            else
                SetMode("Idle");

            // React to dropdown changes
            Settings.ActiveMode.OnValueSelected += (name) =>
            {
                if (_modes.ContainsKey(name) && _mode.Name != name)
                    SetMode(name);
            };

            return base.Initialise();
        }

        public override Job Tick()
        {
            if (!Settings.Enable || !GameController.InGame)
                return base.Tick();

            // Don't do anything when POE isn't the active window
            if (!GameController.IsForeGroundCache)
                return base.Tick();

            _ctx.DeltaTime = (float)GameController.DeltaTime;

            // Toggle running with hotkey — always check, even during async actions
            if (Settings.ToggleRunning.PressedOnce())
            {
                Settings.Running.Value = !Settings.Running.Value;
                if (Settings.Running.Value)
                {
                    if (!_lootTracker.IsActive)
                        _lootTracker.StartSession();
                    // Reset wave timer so pause duration doesn't count toward timeout
                    _simulacrumMode?.State.ResetWaveTimer();
                }
                else if (_lootTracker.IsActive)
                    _lootTracker.StopSession();
            }

            // An async action is in flight (cursor settle, key hold) — don't interfere
            if (!Systems.BotInput.CanAct)
                return base.Tick();

            // Reload tile map + exploration on area change
            var currentArea = GameController.Area?.CurrentArea?.Name ?? "";
            if (currentArea != _lastAreaName && !string.IsNullOrEmpty(currentArea))
            {
                _lastAreaName = currentArea;
                _tileMap.Clear();
                _tileMap.Load(GameController);
                _loot.ClearFailed();
                _mechanics.Reset();

                // Stop MappingMode if we landed in hideout/town (e.g. death respawn)
                if (_mode == _mappingMode && _mappingMode != null)
                {
                    var area = GameController.Area?.CurrentArea;
                    if (area != null && (area.IsHideout || area.IsTown))
                    {
                        _ctx.Log("Area changed to hideout/town — stopping MappingMode");
                        SetMode("Idle");
                    }
                }

                // Initialize exploration from full walkability grid
                var terrainData = GameController.IngameState?.Data?.RawPathfindingData;
                var targetingData = GameController.IngameState?.Data?.RawTerrainTargetingData;
                if (terrainData != null && GameController.Player != null)
                {
                    var playerGrid = new Vector2(
                        GameController.Player.GridPosNum.X,
                        GameController.Player.GridPosNum.Y);
                    _exploration.Initialize(terrainData, targetingData, playerGrid,
                        Settings.Build.BlinkRange.Value);
                }
            }

            // Update exploration coverage each tick
            if (_exploration.IsInitialized && GameController.Player != null)
            {
                var playerGrid = new Vector2(
                    GameController.Player.GridPosNum.X,
                    GameController.Player.GridPosNum.Y);
                _exploration.Update(playerGrid);

                // Scan for area transition entities and record them
                ScanAreaTransitions();
            }

            // Sync settings → systems
            _navigation.BlinkRange = Settings.Build.BlinkRange.Value;
            BotInput.ActionCooldownMs = Settings.ActionCooldownMs.Value;

            // Sync primary movement key from skill config → NavigationSystem + CombatSystem
            var primaryMove = Settings.Build.GetPrimaryMovement();
            _navigation.MoveKey = primaryMove?.Key.Value ?? Keys.T;

            // Sync movement skills (dash/blink) from CombatSystem → NavigationSystem
            _navigation.MovementSkills = _combat.MovementSkills;

            // Mapping mode hotkey — F5 cycles: start → pause → resume → pause ...
            // Double-tap from paused switches back to previous mode
            if (Settings.TestMapExplore.PressedOnce())
            {
                if (_mode == _mappingMode && _mappingMode != null)
                {
                    if (_mappingMode.IsPaused)
                    {
                        // Paused → resume
                        _mappingMode.Resume();
                        LogMessage("[AutoExile] Mapping resumed");
                    }
                    else
                    {
                        // Running → pause
                        _mappingMode.Pause(_ctx);
                        LogMessage("[AutoExile] Mapping paused (overlay preserved)");
                    }
                }
                else
                {
                    // Not in mapping mode → switch to it
                    SetMode("Mapping");
                    LogMessage("[AutoExile] Mapping mode activated");
                }
            }

            // Game state dump hotkey — F6
            if (Settings.DumpGameState.PressedOnce())
                TriggerGameStateDump();

            // Sync loot settings
            _loot.SkipLowValueUniques = Settings.Loot.SkipLowValueUniques.Value;
            _loot.MinUniqueChaosValue = Settings.Loot.MinUniqueChaosValue.Value;
            _loot.MinChaosPerSlot = Settings.Loot.MinChaosPerSlot.Value;
            _loot.LootRadius = Settings.Loot.LootRadius.Value;
            _loot.IgnoreQuestItems = Settings.Loot.IgnoreQuestItems.Value;

            // Sync stash settings — use active mode's cooldown
            _stash.ActionCooldownMs = _mode == _simulacrumMode
                ? Settings.Simulacrum.StashItemCooldownMs.Value
                : Settings.Blight.StashItemCooldownMs.Value;
            _stash.ApplyIncubators = Settings.AutoApplyIncubators.Value;

            // Navigation always ticks when running (it executes movement)
            if (Settings.Running)
                _navigation.Tick(GameController);

            // Only run full mode logic when running
            if (!Settings.Running)
                return base.Tick();

            // Global interrupts — handle before mode gets control
            if (!HandleInterrupts())
                return base.Tick();

            // Sync follower settings
            if (_followerMode != null)
            {
                _followerMode.LeaderName = Settings.Follower.LeaderName.Value;
                _followerMode.FollowDistance = Settings.Follower.FollowDistance.Value;
                _followerMode.StopDistance = Settings.Follower.StopDistance.Value;
                _followerMode.FollowThroughTransitions = Settings.Follower.FollowThroughTransitions.Value;
            }

            // Let the active mode decide what to do
            _mode.Tick(_ctx);

            // Auto level gems (global, runs across all modes)
            TickGemLevelUp();

            return base.Tick();
        }

        public override void Render()
        {
            if (!Settings.Enable || !GameController.InGame)
                return;

            // Status overlay
            var running = Settings.Running.Value;
            var color = running ? SharpDX.Color.LimeGreen : SharpDX.Color.Yellow;
            var status = running ? $"BOT: {_mode.Name}" : $"BOT: PAUSED ({_mode.Name})";
            Graphics.DrawText(status, new Vector2(100, 80), color);

            // Loot tracker overlay (top-right area)
            var winWidth = GameController.Window.GetWindowRectangle().Width;
            _lootTracker.Render(Graphics, new Vector2(winWidth - 250, 80));

            // Pass graphics to context for mode rendering
            _ctx.Graphics = Graphics;
            _mode.Render(_ctx);
            _ctx.Graphics = null;
        }

        public override void DrawSettings()
        {
            base.DrawSettings();

            ImGui.Separator();
            ImGui.Text($"Mode: {_mode.Name}");
            ImGui.Text($"Running: {Settings.Running.Value}");

            if (_mode == _debugMode && _debugMode != null)
            {
                ImGui.Separator();
                ImGui.Text("=== Debug Pathfinding ===");

                if (ImGui.Button("Set Target (save current pos)"))
                    _debugMode.SetTarget(_ctx);
                ImGui.SameLine();
                if (ImGui.Button("Navigate"))
                    _debugMode.Navigate(_ctx);
                ImGui.SameLine();
                if (ImGui.Button("Stop"))
                    _debugMode.StopNavigation(_ctx);

                // Nav stats
                if (_navigation.IsNavigating)
                {
                    ImGui.Text($"Waypoint: {_navigation.CurrentWaypointIndex + 1}/{_navigation.CurrentNavPath.Count}");
                    if (_navigation.BlinkCount > 0)
                        ImGui.Text($"Blinks in path: {_navigation.BlinkCount}");
                }
                ImGui.Text($"Last pathfind: {_navigation.LastPathfindMs}ms");

                // Tile search
                ImGui.Separator();
                ImGui.Text($"=== Tile Navigation ({(_tileMap.IsLoaded ? _tileMap.LoadedArea : "not loaded")}, {_tileMap.TileCount} tiles) ===");

                var tileSearch = _debugMode.TileSearchText;
                if (ImGui.InputText("Tile search", ref tileSearch, 256))
                    _debugMode.TileSearchText = tileSearch;

                if (ImGui.Button("Search Tiles"))
                    _debugMode.SearchTiles(_ctx);
                ImGui.SameLine();
                if (ImGui.Button("Navigate to Tile"))
                    _debugMode.NavigateToTile(_ctx);

                var results = _debugMode.TileSearchResults;
                if (results.Count > 0)
                {
                    ImGui.BeginChild("TileResults", new Vector2(0, 150), ImGuiChildFlags.Border);
                    var shown = 0;
                    foreach (var (key, positions) in results)
                    {
                        if (shown >= 20) break;
                        ImGui.Text($"{key} ({positions.Count} pos)");
                        shown++;
                    }
                    if (results.Count > 20)
                        ImGui.Text($"... and {results.Count - 20} more");
                    ImGui.EndChild();
                }

                if (ImGui.Button("Reload TileMap"))
                {
                    _tileMap.Clear();
                    _tileMap.Load(GameController);
                }

                // Interaction testing
                ImGui.Separator();
                ImGui.Text("=== Interaction Testing ===");

                if (ImGui.Button("Click Nearest Chest"))
                    _debugMode.InteractNearest(_ctx, "Chest");
                ImGui.SameLine();
                if (ImGui.Button("Click Nearest Shrine"))
                    _debugMode.InteractNearest(_ctx, "Shrine");
                ImGui.SameLine();
                if (ImGui.Button("Click Nearest Any"))
                    _debugMode.InteractNearest(_ctx, "");

                if (ImGui.Button("Pickup Nearest Item"))
                    _debugMode.PickupNearestItem(_ctx);
                ImGui.SameLine();
                if (ImGui.Button("Cancel Interaction"))
                    _debugMode.CancelInteraction(_ctx);

                if (_interaction.IsBusy)
                    ImGui.Text($"Interaction: {_interaction.Status}");

                // Loot testing
                ImGui.Separator();
                ImGui.Text("=== Loot System ===");
                ImGui.Text($"Ninja bridge: {_loot.NinjaBridgeStatus}");

                if (ImGui.Button("Scan Loot"))
                    _debugMode.ScanLoot(_ctx);
                ImGui.SameLine();
                if (ImGui.Button("Pickup Next"))
                    _debugMode.PickupNextLoot(_ctx);
                ImGui.SameLine();
                if (ImGui.Button("Loot All"))
                    _debugMode.LootAll(_ctx);

                var candidates = _loot.Candidates;
                if (candidates.Count > 0)
                {
                    ImGui.BeginChild("LootCandidates", new Vector2(0, 150), ImGuiChildFlags.Border);
                    foreach (var c in candidates)
                    {
                        var priceStr = c.ChaosValue > 0
                            ? $" [{c.ChaosValue:F0}c, {c.InventorySlots}slot, {c.ChaosPerSlot:F1}c/s]"
                            : $" [{c.InventorySlots}slot]";
                        ImGui.Text($"{c.ItemName}{priceStr} (dist={c.Distance:F0})");
                    }
                    ImGui.EndChild();
                }

                if (!string.IsNullOrEmpty(_loot.LastSkipReason))
                    ImGui.TextColored(new Vector4(1, 0.5f, 0, 1), _loot.LastSkipReason);

                // Map device testing
                ImGui.Separator();
                ImGui.Text("=== Map Device ===");

                if (ImGui.Button("Open Blight Map"))
                    _debugMode.StartBlightMap(_ctx);
                ImGui.SameLine();
                if (ImGui.Button("Open Standard Map"))
                    _debugMode.StartStandardMap(_ctx);
                ImGui.SameLine();
                if (ImGui.Button("Cancel##mapdevice"))
                    _debugMode.CancelMapDevice(_ctx);

                if (_mapDevice.IsBusy)
                    ImGui.Text($"MapDevice: {_mapDevice.Phase} — {_mapDevice.Status}");
            }

            if (_mode == _followerMode && _followerMode != null)
            {
                ImGui.Separator();
                ImGui.Text("=== Follower Mode ===");

                if (_navigation.IsNavigating)
                {
                    ImGui.Text($"Waypoint: {_navigation.CurrentWaypointIndex + 1}/{_navigation.CurrentNavPath.Count}");
                    ImGui.Text($"Last pathfind: {_navigation.LastPathfindMs}ms");
                }
            }

            if (_mode == _blightMode && _blightMode != null)
            {
                ImGui.Separator();
                ImGui.Text("=== Blight Mode ===");
                ImGui.Text($"Phase: {_blightMode.Phase}");
                ImGui.Text($"Status: {_blightMode.StatusText}");

                var state = _blightMode.State;
                ImGui.Text($"Pump: {(state.PumpPosition.HasValue ? $"({state.PumpPosition.Value.X:F0}, {state.PumpPosition.Value.Y:F0})" : "not found")}");
                ImGui.Text($"Encounter: active={state.IsEncounterActive} done={state.IsEncounterDone} timer={state.IsTimerDone}");
                ImGui.Text($"Countdown: {state.CountdownText}");
                ImGui.Text($"Chests: {state.ChestPositions.Count} | Towers: {state.KnownTowerEntityIds.Count}");
                ImGui.Text($"Monsters: {state.AliveMonsterCount} {(state.PumpUnderAttack ? "PUMP DANGER!" : "")}");
                ImGui.Text($"Lanes: {state.LaneDebug}");
                ImGui.Text(state.FoundationDebug);

                // Show cached foundation details (first 10)
                int shown = 0;
                foreach (var cf in state.CachedFoundations.Values)
                {
                    if (shown >= 10) break;
                    ImGui.TextColored(
                        cf.IsBuilt ? new Vector4(0.5f, 0.5f, 0.5f, 1) : new Vector4(0, 1, 0, 1),
                        $"  F#{cf.EntityId}: built={cf.IsBuilt} vis={cf.IsVisible}");
                    shown++;
                }

                var towerStatus = _blightMode.TowerActionStatus;
                if (!string.IsNullOrEmpty(towerStatus))
                    ImGui.Text($"Tower Action: {towerStatus}");
            }

            // Mapping mode status
            if (_mode == _mappingMode && _mappingMode != null)
            {
                ImGui.Separator();
                ImGui.Text("=== Mapping Mode ===");
                ImGui.Text($"Phase: {_mappingMode.Phase}");
                ImGui.Text(_mappingMode.Status);
                ImGui.Text($"Decision: {_mappingMode.Decision}");
                ImGui.Text($"Targets visited: {_mappingMode.ExploreTargetsVisited}");
                var elapsed = (DateTime.Now - _mappingMode.StartTime).TotalSeconds;
                ImGui.Text($"Elapsed: {elapsed:F0}s");
            }

            // Simulacrum mode status
            if (_mode == _simulacrumMode && _simulacrumMode != null)
            {
                ImGui.Separator();
                ImGui.Text("=== Simulacrum Mode ===");
                ImGui.Text($"Phase: {_simulacrumMode.Phase}");
                ImGui.Text($"Status: {_simulacrumMode.StatusText}");
                ImGui.Text($"Decision: {_simulacrumMode.Decision}");

                var simState = _simulacrumMode.State;
                ImGui.Text($"Wave: {simState.CurrentWave}/15 {(simState.IsWaveActive ? "ACTIVE" : "idle")}");
                ImGui.Text($"Monolith: {(simState.MonolithPosition.HasValue ? $"({simState.MonolithPosition.Value.X:F0}, {simState.MonolithPosition.Value.Y:F0})" : "not found")}");
                ImGui.Text($"Stash: {(simState.StashPosition.HasValue ? "found" : "not found")} | Portal: {(simState.PortalPosition.HasValue ? "found" : "not found")}");
                ImGui.Text($"Deaths: {simState.DeathCount}/{Settings.Simulacrum.MaxDeaths.Value} | Runs: {simState.RunsCompleted}");
            }

            // Game state dump
            ImGui.Separator();
            ImGui.Text("=== Game State Dump ===");
            if (ImGui.Button("Dump (F6)"))
                TriggerGameStateDump();
            if (!string.IsNullOrEmpty(_dumpStatus))
            {
                ImGui.SameLine();
                ImGui.Text(_dumpStatus);
            }

            // Combat system status (always visible)
            ImGui.Separator();
            ImGui.Text("=== Combat System ===");
            ImGui.Text($"InCombat: {_combat.InCombat} | Monsters: {_combat.NearbyMonsterCount} | Target: {_combat.BestTarget?.RenderName ?? "none"}");
            ImGui.Text($"HP: {_combat.HpPercent:P0} ES: {_combat.EsPercent:P0} Mana: {_combat.ManaPercent:P0}");
            ImGui.Text($"Action: {_combat.LastAction} | Skill: {_combat.LastSkillAction}");
            ImGui.Text($"Profile: {(_combat.Profile.Enabled ? _combat.Profile.Positioning.ToString() : "disabled")}");
            if (_combat.NearestCorpse.HasValue)
                ImGui.Text($"Nearest corpse: ({_combat.NearestCorpse.Value.X:F0},{_combat.NearestCorpse.Value.Y:F0})");

            // Skill slot config display with scan buttons
            int slotIdx = 0;
            foreach (var cfg in Settings.Build.AllSkillSlots)
            {
                slotIdx++;
                if (cfg.Key.Value == Keys.None) continue;
                var crossStr = cfg.CanCrossTerrain.Value ? " [crosses terrain]" : "";
                var buffStr = !string.IsNullOrEmpty(cfg.BuffDebuffName.Value) ? $" buff=\"{cfg.BuffDebuffName.Value}\"" : "";
                ImGui.Text($"  Slot{slotIdx} [{cfg.Key.Value}]: {cfg.Role.Value} pri={cfg.Priority.Value}{crossStr}{buffStr}");
                ImGui.SameLine();
                if (ImGui.SmallButton($"Scan##{slotIdx}"))
                    StartBuffScan(slotIdx - 1);
            }

            // Buff scanner UI
            DrawBuffScannerUI();

            // Quick combat test button (sets profile to Aggressive + enabled)
            if (_mode == _debugMode)
            {
                if (!_combat.Profile.Enabled)
                {
                    if (ImGui.Button("Enable Combat (Aggressive)"))
                        _combat.SetProfile(new Systems.CombatProfile { Enabled = true, Positioning = Systems.CombatPositioning.Aggressive });
                    ImGui.SameLine();
                    if (ImGui.Button("Enable Combat (Orbit)"))
                        _combat.SetProfile(new Systems.CombatProfile { Enabled = true, Positioning = Systems.CombatPositioning.Orbit });
                    ImGui.SameLine();
                    if (ImGui.Button("Enable Combat (Ranged)"))
                        _combat.SetProfile(new Systems.CombatProfile { Enabled = true, Positioning = Systems.CombatPositioning.Ranged });
                }
                else
                {
                    if (ImGui.Button("Disable Combat"))
                        _combat.SetProfile(Systems.CombatProfile.Default);
                }
            }

            // Exploration map status (always visible)
            ImGui.Separator();
            ImGui.Text("=== Exploration ===");
            if (_exploration.IsInitialized)
            {
                var blob = _exploration.ActiveBlob;
                ImGui.Text($"Blobs: {_exploration.TotalBlobCount} | Active: {_exploration.ActiveBlobIndex}");
                ImGui.Text($"Total walkable cells: {_exploration.TotalWalkableCells}");
                if (blob != null)
                {
                    ImGui.Text($"Coverage: {blob.Coverage:P1} ({blob.SeenCells.Count}/{blob.WalkableCells.Count})");
                    ImGui.Text($"Regions: {blob.Regions.Count}");

                    // Show top unexplored regions
                    int regionShown = 0;
                    foreach (var region in blob.Regions.OrderBy(r => r.ExploredRatio))
                    {
                        if (regionShown >= 5) break;
                        if (region.ExploredRatio >= 0.8f) continue;
                        ImGui.TextColored(
                            new Vector4(1f, 1f - region.ExploredRatio, 0, 1),
                            $"  R{region.Index}: {region.ExploredRatio:P0} ({region.CellCount} cells) @ ({region.Center.X:F0},{region.Center.Y:F0})");
                        regionShown++;
                    }
                }

                ImGui.Text($"Transitions: {_exploration.KnownTransitions.Count}");
                foreach (var t in _exploration.KnownTransitions)
                    ImGui.Text($"  {t.Name} @ ({t.GridPos.X:F0},{t.GridPos.Y:F0}) blob:{t.SourceBlobIndex}→{t.DestBlobIndex}");

                ImGui.Text($"Last: {_exploration.LastAction}");

                if (ImGui.Button("Reinitialize Exploration"))
                {
                    var terrainData = GameController.IngameState?.Data?.RawPathfindingData;
                    var targetingData = GameController.IngameState?.Data?.RawTerrainTargetingData;
                    if (terrainData != null && GameController.Player != null)
                    {
                        var pg = new Vector2(
                            GameController.Player.GridPosNum.X,
                            GameController.Player.GridPosNum.Y);
                        _exploration.Initialize(terrainData, targetingData, pg,
                            Settings.Build.BlinkRange.Value);
                    }
                }
            }
            else
            {
                ImGui.Text("Not initialized (waiting for area load)");
            }

            ImGui.Separator();
        }

        // =================================================================
        // Exploration — area transition scanning
        // =================================================================

        private void ScanAreaTransitions()
        {
            if (!_exploration.IsInitialized) return;

            foreach (var entity in GameController.EntityListWrapper.OnlyValidEntities)
            {
                if (entity.Type != ExileCore.Shared.Enums.EntityType.AreaTransition) continue;
                var gridPos = new Vector2(entity.GridPosNum.X, entity.GridPosNum.Y);
                _exploration.RecordTransition(gridPos, entity.RenderName ?? entity.Path ?? "");
            }
        }

        // =================================================================
        // Game State Dump
        // =================================================================

        private string _dumpStatus = "";

        private void TriggerGameStateDump()
        {
            var gc = GameController;
            var pfGrid = gc.IngameState?.Data?.RawPathfindingData;
            var tgtGrid = gc.IngameState?.Data?.RawTerrainTargetingData;
            var heightGrid = gc.IngameState?.Data?.RawTerrainHeightData;

            if (pfGrid == null || gc.Player == null)
            {
                _dumpStatus = "Dump failed: no terrain data or player";
                LogMessage($"[AutoExile] {_dumpStatus}");
                return;
            }

            var playerGrid = new Vector2(gc.Player.GridPosNum.X, gc.Player.GridPosNum.Y);
            var areaName = gc.Area?.CurrentArea?.Name ?? "Unknown";
            var outputDir = Path.Combine(DirectoryFullName, "Dumps");

            // Capture live game state on the main thread (entity refs aren't safe on thread pool)
            var snapshot = BuildGameStateSnapshot(gc, playerGrid);

            _dumpStatus = "Dumping...";
            LogMessage("[AutoExile] Starting game state dump...");

            // Run on thread pool to avoid blocking the tick loop (pathfinding can be slow)
            Task.Run(() =>
            {
                var result = GameStateDump.Dump(
                    pfGrid, tgtGrid, heightGrid,
                    playerGrid, Settings.Build.BlinkRange.Value,
                    _exploration, _navigation, snapshot,
                    areaName, outputDir);

                _dumpStatus = result;
                LogMessage($"[AutoExile] {result}");
            });
        }

        private GameStateSnapshot BuildGameStateSnapshot(GameController gc, Vector2 playerGrid)
        {
            var snapshot = new GameStateSnapshot();

            // Capture entities — only those within 2x network bubble (includes stale cached ones)
            foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
            {
                var gridPos = entity.GridPosNum;
                var dist = Vector2.Distance(gridPos, playerGrid);
                if (dist > Pathfinding.NetworkBubbleRadius * 2) continue;

                var category = CategorizeEntity(entity);

                var ent = new EntitySnapshot
                {
                    Id = entity.Id,
                    Metadata = entity.Metadata ?? "",
                    GridPos = gridPos,
                    DistanceToPlayer = dist,
                    Category = category,
                    IsAlive = entity.IsAlive,
                    IsTargetable = entity.IsTargetable,
                    IsHostile = entity.IsHostile,
                    Rarity = entity.Type == ExileCore.Shared.Enums.EntityType.Monster
                        ? entity.Rarity.ToString() : null,
                    ShortName = ExtractShortName(entity.Metadata ?? entity.Path ?? ""),
                };
                snapshot.Entities.Add(ent);
            }

            // Combat state
            snapshot.Combat = new CombatSnapshot
            {
                InCombat = _combat.InCombat,
                NearbyMonsterCount = _combat.NearbyMonsterCount,
                NearbyChaseCount = _combat.NearbyChaseCount,
                PackCenter = _combat.PackCenter,
                NearbyPackCenter = _combat.NearbyPackCenter,
                LastAction = _combat.LastAction,
                LastSkillAction = _combat.LastSkillAction,
                BestTargetId = _combat.BestTarget?.Id,
                WantsToMove = _combat.WantsToMove,
            };

            // Active mode state
            snapshot.Mode = new ModeSnapshot
            {
                Name = _mode.Name,
            };
            if (_mode is SimulacrumMode sim)
            {
                snapshot.Mode.Phase = sim.Phase.ToString();
                snapshot.Mode.Status = sim.StatusText;
                snapshot.Mode.Decision = sim.Decision;
                snapshot.Mode.Extra["wave"] = sim.State.CurrentWave;
                snapshot.Mode.Extra["isWaveActive"] = sim.State.IsWaveActive;
                snapshot.Mode.Extra["deaths"] = sim.State.DeathCount;
                snapshot.Mode.Extra["monolithPos"] = sim.State.MonolithPosition.HasValue
                    ? new[] { sim.State.MonolithPosition.Value.X, sim.State.MonolithPosition.Value.Y }
                    : (object)Array.Empty<float>();
                snapshot.Mode.Extra["spawnZones"] = sim.State.SpawnZones
                    .Select(z => new[] { z.X, z.Y }).ToArray();
            }
            else if (_mode is MappingMode map)
            {
                snapshot.Mode.Phase = map.Phase.ToString();
                snapshot.Mode.Status = map.Status;
                snapshot.Mode.Decision = map.Decision;
            }
            else if (_mode is BlightMode blight)
            {
                snapshot.Mode.Phase = blight.Phase.ToString();
                snapshot.Mode.Status = blight.StatusText;
            }

            // Interaction state
            snapshot.Interaction = new InteractionSnapshot
            {
                IsBusy = _interaction.IsBusy,
                Status = _interaction.Status,
            };

            // Loot state — force a fresh scan so dump always has current data
            _loot.Scan(gc);
            var visibleLabels = 0;
            try
            {
                var labels = gc.IngameState?.IngameUi?.ItemsOnGroundLabelElement?.VisibleGroundItemLabels;
                if (labels != null)
                    visibleLabels = labels.Count();
            }
            catch { }

            snapshot.Loot = new LootSnapshot
            {
                HasLootNearby = _loot.HasLootNearby,
                CandidateCount = _loot.Candidates.Count,
                FailedCount = _loot.FailedCount,
                LastSkipReason = _loot.LastSkipReason,
                NinjaBridgeStatus = _loot.NinjaBridgeStatus,
                LootRadius = _loot.LootRadius,
                VisibleGroundLabelCount = visibleLabels,
                Candidates = _loot.Candidates.Select(c => new LootCandidateSnapshot
                {
                    EntityId = c.Entity.Id,
                    ItemName = c.ItemName,
                    Distance = c.Distance,
                    ChaosValue = c.ChaosValue,
                    InventorySlots = c.InventorySlots,
                    ChaosPerSlot = c.ChaosPerSlot,
                    GridPos = c.Entity.GridPosNum,
                }).ToList(),
            };

            return snapshot;
        }

        private static EntityCategory CategorizeEntity(Entity entity)
        {
            var type = entity.Type;
            var path = entity.Path ?? "";

            if (type == ExileCore.Shared.Enums.EntityType.Monster)
                return EntityCategory.Monster;
            if (type == ExileCore.Shared.Enums.EntityType.Chest)
                return EntityCategory.Chest;
            if (type == ExileCore.Shared.Enums.EntityType.AreaTransition)
                return EntityCategory.AreaTransition;
            if (type == ExileCore.Shared.Enums.EntityType.TownPortal || type == ExileCore.Shared.Enums.EntityType.Portal)
                return EntityCategory.Portal;
            if (type == ExileCore.Shared.Enums.EntityType.Stash)
                return EntityCategory.Stash;
            if (path.Contains("Afflictionator"))
                return EntityCategory.Monolith;
            if (path.Contains("MiscellaneousObjects/Stash"))
                return EntityCategory.Stash;
            return EntityCategory.Other;
        }

        private static string ExtractShortName(string metadata)
        {
            if (string.IsNullOrEmpty(metadata)) return "";
            var lastSlash = metadata.LastIndexOf('/');
            return lastSlash >= 0 && lastSlash < metadata.Length - 1
                ? metadata[(lastSlash + 1)..]
                : metadata;
        }

        private void TickGemLevelUp()
        {
            if (!Settings.AutoLevelGems.Value) return;
            if (!BotInput.CanAct) return;
            if ((DateTime.Now - _lastGemLevelAt).TotalMilliseconds < GemLevelCooldownMs) return;

            try
            {
                var panel = GameController.IngameState.IngameUi.GemLvlUpPanel;
                if (panel == null || !panel.IsVisible) return;

                // GemsToLvlUp returns the list of gems ready to level.
                // Each GemLevelUpElement is a UI row — click it to level that gem.
                // The panel may also have a "Level All" child — try clicking it first.
                var gems = panel.GemsToLvlUp;
                if (gems == null || gems.Count == 0) return;

                var windowRect = GameController.Window.GetWindowRectangle();

                // Try to find a "Level All" button by checking panel children.
                // It's typically the first child before the individual gem rows.
                // If it has more children than gems, the extra one is likely "Level All".
                if (panel.ChildCount > gems.Count)
                {
                    for (int i = 0; i < panel.ChildCount; i++)
                    {
                        var child = panel.GetChildAtIndex(i);
                        if (child?.IsVisible != true) continue;
                        var text = child.Text;
                        if (text != null && text.IndexOf("level", StringComparison.OrdinalIgnoreCase) >= 0
                            && text.IndexOf("all", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            var rect = child.GetClientRect();
                            var absPos = new Vector2(windowRect.X + rect.Center.X, windowRect.Y + rect.Center.Y);
                            BotInput.Click(absPos);
                            _lastGemLevelAt = DateTime.Now;
                            return;
                        }
                    }
                }

                // Click the level-up "+" button for the first gem
                // Layout: [0]=dismiss(X) [1]=level(+) [2]=bar(hidden) [3]=text
                // The "+" button is the second visible small square child
                var gemEl = gems[0];
                if (gemEl?.IsVisible == true)
                {
                    SharpDX.RectangleF rect = gemEl.GetClientRect();
                    int smallSquareCount = 0;
                    for (int i = 0; i < gemEl.ChildCount; i++)
                    {
                        var child = gemEl.GetChildAtIndex(i);
                        if (child?.IsVisible != true) continue;
                        var cr = child.GetClientRect();
                        if (cr.Width > 5 && cr.Width < 60 && cr.Height > 5 && cr.Height < 60)
                        {
                            smallSquareCount++;
                            if (smallSquareCount == 2) // Second square = "+" button
                            {
                                rect = cr;
                                break;
                            }
                        }
                    }

                    var absPos = new Vector2(windowRect.X + rect.Center.X, windowRect.Y + rect.Center.Y);
                    BotInput.Click(absPos);
                    _lastGemLevelAt = DateTime.Now;
                }
            }
            catch { }
        }

        // Death tracking for revive
        private bool _wasDead;
        private DateTime _lastReviveClickAt = DateTime.MinValue;

        private bool HandleInterrupts()
        {
            var gc = GameController;

            if (gc.IsLoading)
                return false;

            if (!gc.Player.IsAlive)
            {
                // Track death for mode re-entry logic
                if (!_wasDead && _blightMode != null)
                    _blightMode.State.DeathCount++;
                if (!_wasDead && _simulacrumMode != null)
                    _simulacrumMode.State.DeathCount++;
                _wasDead = true;

                // Click resurrect button
                if (BotInput.CanAct && (DateTime.Now - _lastReviveClickAt).TotalMilliseconds > 1000)
                {
                    try
                    {
                        var revivePanel = gc.IngameState.IngameUi.ResurrectPanel;
                        if (revivePanel?.IsVisible == true)
                        {
                            var atCheckpoint = revivePanel.ResurrectAtCheckpoint;
                            if (atCheckpoint?.IsVisible == true)
                            {
                                var rect = atCheckpoint.GetClientRect();
                                var center = new Vector2(rect.Center.X, rect.Center.Y);
                                var windowRect = gc.Window.GetWindowRectangle();
                                BotInput.Click(new Vector2(windowRect.X + center.X, windowRect.Y + center.Y));
                                _lastReviveClickAt = DateTime.Now;
                            }
                        }
                    }
                    catch { }
                }
                return false;
            }

            _wasDead = false;
            return true;
        }

        public override void EntityAdded(Entity entity)
        {
            if (_blightMode != null && _mode == _blightMode)
                _blightMode.OnEntityAdded(entity);
        }

        public override void EntityRemoved(Entity entity)
        {
            if (_blightMode != null && _mode == _blightMode && GameController?.Player != null)
            {
                var playerPos = GameController.Player.GridPosNum;
                _blightMode.OnEntityRemoved(entity, playerPos);
            }
        }

        public void RegisterMode(IBotMode mode)
        {
            _modes[mode.Name] = mode;
        }

        public void SetMode(string name)
        {
            if (!_modes.TryGetValue(name, out var newMode))
            {
                _ctx.Log($"Unknown mode: {name}");
                return;
            }

            if (newMode == _mode)
                return;

            _ctx.Log($"Switching mode: {_mode.Name} -> {newMode.Name}");
            _mode.OnExit();
            _mode = newMode;
            _mode.OnEnter(_ctx);

            // Persist to settings so it survives reloads
            if (Settings.ActiveMode != null)
                Settings.ActiveMode.Value = name;
        }

        // =================================================================
        // Buff Scanner
        // =================================================================

        private void StartBuffScan(int slotIndex)
        {
            var gc = GameController;
            if (gc?.Player == null || !gc.InGame)
            {
                _buffScanStatus = "Not in game";
                return;
            }

            _buffScanSlotIndex = slotIndex;
            _buffScanResults.Clear();
            _buffScanStatus = "Snapshotting nearby monster buffs...";

            // Snapshot all buff names on nearby alive hostile monsters
            _buffScanBaseline.Clear();
            foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
            {
                if (entity.Type != ExileCore.Shared.Enums.EntityType.Monster) continue;
                if (!entity.IsHostile || !entity.IsAlive) continue;
                if (Vector2.Distance(entity.GridPosNum, gc.Player.GridPosNum) > 80) continue;

                try
                {
                    var buffs = entity.Buffs;
                    if (buffs == null) continue;
                    foreach (var buff in buffs)
                    {
                        if (!string.IsNullOrEmpty(buff.Name))
                            _buffScanBaseline.Add(buff.Name);
                    }
                }
                catch { }
            }

            _buffScanActive = true;
            _buffScanWaitingForCast = true;
            _buffScanStartTime = DateTime.Now;
            _buffScanStatus = $"Baseline: {_buffScanBaseline.Count} buff names. Cast your skill on nearby monsters now!";
            LogMessage($"[AutoExile] Buff scan started for slot {slotIndex + 1} — {_buffScanBaseline.Count} baseline buffs");
        }

        private void TickBuffScan()
        {
            if (!_buffScanActive) return;

            var gc = GameController;
            if (gc?.Player == null)
            {
                _buffScanActive = false;
                _buffScanStatus = "Lost game state";
                return;
            }

            // Timeout
            if ((DateTime.Now - _buffScanStartTime).TotalSeconds > BuffScanTimeoutSeconds)
            {
                _buffScanActive = false;
                _buffScanWaitingForCast = false;
                if (_buffScanResults.Count == 0)
                    _buffScanStatus = "Timed out — no new buffs detected. Cast the skill on enemies and try again.";
                return;
            }

            // Continuously scan for new buff names not in baseline
            var newBuffs = new HashSet<string>();
            foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
            {
                if (entity.Type != ExileCore.Shared.Enums.EntityType.Monster) continue;
                if (!entity.IsHostile || !entity.IsAlive) continue;
                if (Vector2.Distance(entity.GridPosNum, gc.Player.GridPosNum) > 80) continue;

                try
                {
                    var buffs = entity.Buffs;
                    if (buffs == null) continue;
                    foreach (var buff in buffs)
                    {
                        if (string.IsNullOrEmpty(buff.Name)) continue;
                        if (!_buffScanBaseline.Contains(buff.Name))
                            newBuffs.Add(buff.Name);
                    }
                }
                catch { }
            }

            if (newBuffs.Count > 0)
            {
                _buffScanResults = newBuffs.OrderBy(n => n).ToList();
                _buffScanWaitingForCast = false;
                _buffScanActive = false;
                _buffScanStatus = $"Found {_buffScanResults.Count} new buff(s). Pick one:";
                LogMessage($"[AutoExile] Buff scan found: {string.Join(", ", _buffScanResults)}");
            }
        }

        private void DrawBuffScannerUI()
        {
            // Tick the scanner each frame during Render
            TickBuffScan();

            if (_buffScanSlotIndex < 0) return;

            // Show status
            if (!string.IsNullOrEmpty(_buffScanStatus))
            {
                var color = _buffScanWaitingForCast
                    ? new Vector4(1, 1, 0, 1)    // yellow while waiting
                    : _buffScanResults.Count > 0
                        ? new Vector4(0, 1, 0, 1)  // green with results
                        : new Vector4(1, 0.5f, 0, 1); // orange otherwise
                ImGui.TextColored(color, _buffScanStatus);
            }

            // Show results as clickable buttons
            if (_buffScanResults.Count > 0)
            {
                var slots = Settings.Build.AllSkillSlots.ToArray();
                if (_buffScanSlotIndex < slots.Length)
                {
                    var targetSlot = slots[_buffScanSlotIndex];
                    foreach (var buffName in _buffScanResults)
                    {
                        if (ImGui.Button(buffName))
                        {
                            targetSlot.BuffDebuffName.Value = buffName;
                            _buffScanStatus = $"Set Slot{_buffScanSlotIndex + 1} buff name to \"{buffName}\"";
                            _buffScanResults.Clear();
                            LogMessage($"[AutoExile] Set slot {_buffScanSlotIndex + 1} BuffDebuffName = \"{buffName}\"");
                        }
                        ImGui.SameLine();
                    }
                    ImGui.NewLine();
                }

                if (ImGui.SmallButton("Cancel##buffscan"))
                {
                    _buffScanResults.Clear();
                    _buffScanSlotIndex = -1;
                    _buffScanStatus = "";
                }
            }
            else if (_buffScanActive)
            {
                if (ImGui.SmallButton("Cancel Scan"))
                {
                    _buffScanActive = false;
                    _buffScanStatus = "Cancelled";
                }
            }
        }
    }
}
