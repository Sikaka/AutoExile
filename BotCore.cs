using ExileCore;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared;
using ImGuiNET;
using AutoExile.Mechanics;
using AutoExile.Modes;
using AutoExile.Systems;
using AutoExile.WebServer;
using System.IO;
using System.Linq;
using System.Numerics;

namespace AutoExile
{
    public class BotCore : BaseSettingsPlugin<BotSettings>
    {
        /// <summary>
        /// Static accessor for external tools (POEMCP /eval) to reach live AutoExile systems.
        /// Set in Initialise(), cleared in Dispose/OnClose.
        /// </summary>
        public static BotCore? Instance { get; private set; }

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
        private ThreatSystem _threat = new();
        private EldritchAltarHandler _altarHandler = new();
        private NinjaPriceService _ninjaPrice = new();
        private BotRecorder _recorder = new();
        private BotWebServer? _webServer;
        private DataStore? _dataStore;
        private ConfigManager? _configManager;
        private MapDatabase _mapDatabase = null!;

        // Public accessors for external tools (POEMCP /eval)
        public NavigationSystem Navigation => _navigation;
        public CombatSystem Combat => _combat;
        public LootSystem Loot => _loot;
        public InteractionSystem Interaction => _interaction;
        public ExplorationMap Exploration => _exploration;
        public LootTracker LootTrackerInstance => _lootTracker;
        public MapMechanicManager Mechanics => _mechanics;
        public ThreatSystem Threat => _threat;
        public NinjaPriceService NinjaPrice => _ninjaPrice;
        public BotRecorder Recorder => _recorder;
        public IBotMode ActiveMode => _mode;
        public BotContext Context => _ctx;
        public HeistState? HeistState => _heistMode?.State;

        // Mode references for ImGui buttons
        private DebugPathfindingMode? _debugMode;
        private FollowerMode? _followerMode;
        private BlightMode? _blightMode;
        private MappingMode? _mappingMode;
        private SimulacrumMode? _simulacrumMode;
        private HeistMode? _heistMode;

        // Area change tracking for tile map reload
        private string _lastAreaName = "";
        private long _lastAreaHash;
        private DateTime _areaChangedAt = DateTime.MinValue;
        private const float AreaSettleSeconds = 3f;

        // Cross-zone state cache (e.g., Wishes portal round-trip)
        // Keyed by area name — when returning to same-named area, restore cached state
        private readonly Dictionary<string, AreaStateCache> _areaStateCache = new();
        private const int MaxCachedAreas = 3;

        // Debug range circle — shows adjusted range values for 5 seconds
        private string _debugCircleLabel = "";
        private int _debugCircleRadius;
        private DateTime _debugCircleExpiry = DateTime.MinValue;
        private readonly Dictionary<string, int> _lastRangeValues = new();

        // Tile signature scanner (F8)
        private List<TileSignature> _tileSignatures = new();
        private string _tileSignatureArea = "";
        private Vector2 _tileSignaturePlayerPos;
        private DateTime _tileSignatureScanTime;

        // Map list population (deferred to first tick when Files are available)
        private bool _mapListPopulated;

        // Periodic config save — persists ImGui changes that would otherwise be lost on reload
        private DateTime _lastConfigSave = DateTime.Now;
        private const double ConfigSaveIntervalSec = 30.0;

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
            Instance = this;
            _recorder.SetOutputDir(Path.Combine(DirectoryFullName, "Recordings"));
            _ninjaPrice.Initialize(DirectoryFullName, msg => LogMessage($"[AutoExile] NinjaPrice: {msg}"));
            _mapDatabase = new MapDatabase(msg => LogMessage($"[AutoExile] {msg}"));
            _mapDatabase.Initialize(DirectoryFullName);

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
                Threat = _threat,
                AltarHandler = _altarHandler,
                NinjaPrice = _ninjaPrice,
                MapDatabase = _mapDatabase,
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
            _heistMode = new HeistMode();
            RegisterMode(_heistMode);

            // Register in-map mechanics
            _mechanics.Register(new UltimatumMechanic());
            _mechanics.Register(new HarvestMechanic());
            _mechanics.Register(new WishesMechanic());
            _mechanics.Register(new EssenceMechanic());
            _mechanics.Register(new RitualMechanic());

            // Populate mode dropdown and restore saved selection
            // Save value before SetListValues — it resets Value to first item
            var savedMode = Settings.ActiveMode?.Value;
            Settings.ActiveMode.SetListValues(_modes.Keys.ToList());
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

            // Load our own config (overrides ExileCore defaults)
            _configManager = new ConfigManager(msg => LogMessage($"[AutoExile] {msg}"));
            _configManager.Initialize(DirectoryFullName);
            _configManager.LoadAndApply(Settings);

            // Initialize data store
            _dataStore = new DataStore(msg => LogMessage($"[AutoExile] {msg}"));
            _dataStore.Initialize(DirectoryFullName);

            // Wire loot recording callback → data store
            _lootTracker.OnItemRecorded = (name, value, slots) =>
            {
                var area = GameController?.Area?.CurrentArea?.Name ?? "";
                _dataStore.RecordLoot(name, value, slots, area, _mode.Name);
            };

            // Wire loot skip/fail events → data store (for web UI logs tab)
            _loot.OnItemSkipped = (itemName, reason, chaosValue) =>
            {
                var area = GameController?.Area?.CurrentArea?.Name ?? "";
                var valueStr = chaosValue > 0 ? $" ({chaosValue:F0}c)" : "";
                _dataStore.RecordEvent("loot_skip", $"{itemName}{valueStr}: {reason}", area);
            };

            // Populate map name list from atlas nodes (when in-game)
            // Deferred to first tick since Files may not be ready at init time
            _mapListPopulated = false;

            // Start embedded web server
            if (Settings.WebUiEnabled.Value)
            {
                _webServer = new BotWebServer(Settings.WebUiPort.Value, Settings.WebUiNetworkAccess.Value, msg => LogMessage($"[AutoExile] {msg}"));
                _webServer.Settings = Settings;
                _webServer.DataStore = _dataStore;
                _webServer.ConfigManager = _configManager;
                _webServer.MapDatabase = _mapDatabase;
                _webServer.NinjaPrice = _ninjaPrice;
                _webServer.Start();
            }

            return base.Initialise();
        }

        public override Job Tick()
        {
            if (!Settings.Enable || !GameController.InGame)
                return base.Tick();

            // Web server: push status snapshot + process commands
            // Runs before all early returns so the dashboard stays live even when
            // POE is unfocused or an async action is in flight.
            TickWebServer();

            // Periodic config save — persists ImGui setting changes to config.json
            // so they survive plugin reloads (web UI changes save immediately, but
            // ImGui changes only live in memory without this).
            if ((DateTime.Now - _lastConfigSave).TotalSeconds >= ConfigSaveIntervalSec)
            {
                _lastConfigSave = DateTime.Now;
                _configManager?.Save(Settings);
            }

            // Don't do anything when POE isn't the active window
            if (!GameController.IsForeGroundCache)
                return base.Tick();

            _ctx.DeltaTime = (float)GameController.DeltaTime;
            _ctx.MinimapIcons = _knownMinimapIcons;

            // Populate map name list from atlas data (once, when Files are ready)
            if (!_mapListPopulated)
                PopulateMapList();

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

            // An async action is in flight (cursor settle, key hold).
            // Still tick combat for self-cast skills (RF, guards, etc.) and mode for state updates,
            // but skip navigation which would queue conflicting cursor movements.
            var canAct = Systems.BotInput.CanAct;

            // Reload tile map + exploration on area change
            // Use area hash (unique per instance) to detect changes — area name alone
            // can be the same for sub-zones (e.g., wish zone shares name with parent map)
            var currentArea = GameController.Area?.CurrentArea?.Name ?? "";
            var currentHash = GameController.IngameState?.Data?.CurrentAreaHash ?? 0;
            if (currentHash != _lastAreaHash && currentHash != 0)
            {
                var previousAreaName = _lastAreaName;
                LogMessage($"[BotCore] Area hash changed: {_lastAreaHash} -> {currentHash}, area='{currentArea}' (prev='{previousAreaName}')");

                // Cache current area state before switching (for round-trip zone support)
                // Use area name as cache key so returning to same-named area restores state
                if (!string.IsNullOrEmpty(previousAreaName) && _exploration.IsInitialized)
                {
                    // Only cache if we don't already have an entry for this name
                    // (prevents overwriting original map cache with wish zone state)
                    if (!_areaStateCache.ContainsKey(previousAreaName))
                    {
                        // Force-complete active mechanic before caching — if the mechanic
                        // sent us through a portal (e.g., Wishes), it should be done
                        // so it doesn't re-detect when we return
                        _mechanics.ForceCompleteActive();

                        _areaStateCache[previousAreaName] = new AreaStateCache
                        {
                            Exploration = _exploration.CreateSnapshot(),
                            Mechanics = _mechanics.CreateSnapshot(),
                            AreaHash = _lastAreaHash,
                            CachedAt = DateTime.Now,
                        };

                        // Evict oldest if cache is full
                        while (_areaStateCache.Count > MaxCachedAreas)
                        {
                            string? oldest = null;
                            var oldestTime = DateTime.MaxValue;
                            foreach (var kv in _areaStateCache)
                            {
                                if (kv.Value.CachedAt < oldestTime)
                                {
                                    oldestTime = kv.Value.CachedAt;
                                    oldest = kv.Key;
                                }
                            }
                            if (oldest != null) _areaStateCache.Remove(oldest);
                            else break;
                        }

                        _ctx.Log($"[Cache] Saved area state for '{previousAreaName}' hash={_lastAreaHash} ({_areaStateCache.Count} cached)");
                    }
                }

                _lastAreaName = currentArea;
                _lastAreaHash = currentHash;
                _areaChangedAt = DateTime.Now;
                _tileMap.Clear();
                _tileMap.Load(GameController);
                _loot.ClearFailed();
                _combat.ClearUnreachable();
                _altarHandler.Reset();
                _lootTracker.OnAreaChanged();
                ClearMinimapIcons();
                ScanMinimapIcons(forceFullLog: true);

                // NOTE: MappingMode handles hideout/town transitions internally via OnAreaChanged
                // → HideoutFlow (stash → map device → enter map). Don't SetMode("Idle") here.

                // Check if we have cached state for this area name AND matching hash
                // (returning from sub-zone back to the original map instance)
                if (_areaStateCache.TryGetValue(currentArea, out var cached) && cached.AreaHash == currentHash)
                {
                    _exploration.RestoreSnapshot(cached.Exploration);
                    _mechanics.RestoreSnapshot(cached.Mechanics);
                    _areaStateCache.Remove(currentArea);
                    _ctx.Log($"[Cache] Restored area state for '{currentArea}' hash={currentHash}");
                }
                else
                {
                    // Fresh area or different instance — reset mechanics and initialize exploration
                    // Do NOT remove cache entry — sub-zones (wish zones) share the same area name
                    // and we need the original map's cache intact for when the player returns
                    _mechanics.Reset();

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
            }

            // Retry exploration init if it was missed (terrain data not ready on area change tick)
            if (!_exploration.IsInitialized && GameController.Player != null)
            {
                var terrainData = GameController.IngameState?.Data?.RawPathfindingData;
                var targetingData = GameController.IngameState?.Data?.RawTerrainTargetingData;
                if (terrainData != null)
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

                // Periodic minimap icon scan — tiles load at ~2x network bubble as player moves
                ScanMinimapIcons();
            }

            // Sync settings → systems
            _navigation.BlinkRange = Settings.Build.BlinkRange.Value;
            _navigation.DashMinDistance = Settings.Build.DashMinDistance.Value;
            BotInput.ActionCooldownMs = Settings.ActionCooldownMs.Value;
            BotInput.WindowRect = GameController.Window.GetWindowRectangleTimeCache;

            // Sync primary movement key from skill config → NavigationSystem + CombatSystem
            var primaryMove = Settings.Build.GetPrimaryMovement();
            _navigation.MoveKey = primaryMove?.Key.Value ?? Keys.T;

            // Ensure skill bar is always up to date — NavigationSystem needs MovementSkills
            // for dash-for-speed even when combat is disabled by the active mode
            _combat.RefreshSkillBar(GameController, Settings.Build);

            // Sync movement skills (dash/blink) from CombatSystem → NavigationSystem
            _navigation.MovementSkills = _combat.MovementSkills;

            // Sync threat settings
            var threatSettings = Settings.Threat;
            _threat.Enabled = threatSettings.Enabled.Value;
            _threat.ThreatRadius = threatSettings.ThreatRadius.Value;
            _threat.DodgeTriggerDistance = threatSettings.DodgeTriggerDistance.Value;
            _threat.DodgeMinProgress = threatSettings.DodgeMinProgress.Value;
            _threat.DodgeMaxProgress = threatSettings.DodgeMaxProgress.Value;
            _threat.MonitorRares = threatSettings.MonitorRares.Value;

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

            // Recording dump hotkey — F7 (last ~10s of tick-level state)
            if (Settings.DumpRecording.PressedOnce())
            {
                _recorder.ForceDump("hotkey");
                LogMessage($"[AutoExile] {_recorder.LastDumpStatus}");
            }

            // Tile signature scanner hotkey — F8 (toggle on/off, area change clears)
            if (Settings.ScanTileSignatures.PressedOnce())
            {
                if (_tileSignatures.Count > 0)
                {
                    _tileSignatures.Clear();
                    LogMessage("[AutoExile] Tile signatures cleared");
                }
                else
                {
                    ScanTileSignatures();
                }
            }

            // Clear tile signatures on area change
            if (_tileSignatures.Count > 0 && currentArea != _tileSignatureArea)
                _tileSignatures.Clear();

            // Sync loot settings
            _loot.SkipLowValueUniques = Settings.Loot.SkipLowValueUniques.Value;
            _loot.MinUniqueChaosValue = Settings.Loot.MinUniqueChaosValue.Value;
            _loot.MinChaosPerSlot = Settings.Loot.MinChaosPerSlot.Value;
            _loot.IgnoreQuestItems = Settings.Loot.IgnoreQuestItems.Value;
            _loot.FilterClusterJewels = Settings.Loot.FilterClusterJewels.Value;
            _loot.MinClusterJewelChaosValue = Settings.Loot.MinClusterJewelChaosValue.Value;
            _loot.FilterSkillGems = Settings.Loot.FilterSkillGems.Value;
            _loot.MinGemChaosValue = Settings.Loot.MinGemChaosValue.Value;
            _loot.AlwaysLoot20QualityGems = Settings.Loot.AlwaysLoot20QualityGems.Value;
            _loot.FilterSynthesisedItems = Settings.Loot.FilterSynthesisedItems.Value;
            // Parse comma-separated whitelist into trimmed entries
            var whitelistRaw = Settings.Loot.SynthesisedWhitelist.Value ?? "";
            _loot.SynthesisedWhitelist = whitelistRaw
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(s => s.Length > 0)
                .ToList();
            // Parse must-loot uniques list
            var mustLootRaw = Settings.Loot.MustLootUniques.Value ?? "";
            _loot.MustLootUniques = new HashSet<string>(
                mustLootRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(s => s.Length > 0),
                StringComparer.OrdinalIgnoreCase);
            _loot.PriceService = _ninjaPrice;
            _lootTracker.PriceService = _ninjaPrice;

            // Sync interact radius (global setting used by all systems)
            _interaction.InteractRadius = Settings.InteractRadius.Value;
            _mapDevice.InteractRadius = Settings.InteractRadius.Value;
            _mapDevice.Interaction = _interaction;
            _stash.InteractRadius = Settings.InteractRadius.Value;

            // Sync latency + click attempts — used by systems for server-response timeouts
            // If user set ExtraLatencyMs to 0, auto-detect from game's ServerData.Latency
            var extraLatency = Settings.ExtraLatencyMs.Value;
            if (extraLatency == 0)
            {
                var serverLatency = GameController.IngameState?.ServerData?.Latency ?? 0;
                extraLatency = serverLatency > 0 ? serverLatency : 0;
            }
            float extraLatencySec = extraLatency / 1000f;
            _interaction.ExtraLatencySec = extraLatencySec;
            _interaction.MaxClickAttempts = Settings.MaxClickAttempts.Value;
            _mapDevice.ExtraLatencySec = extraLatencySec;
            _mapDevice.MaxClickAttempts = Settings.MaxClickAttempts.Value;
            _stash.ExtraLatencySec = extraLatencySec;
            _combat.ExtraLatencySec = extraLatencySec;
            _navigation.ExtraLatencyMs = extraLatency;

            // Tick ninja price service (league detection, refresh timer)
            _ninjaPrice.Tick(GameController);

            // Sync stash/map device settings
            _stash.ActionCooldownMs = Settings.Loot.StashItemCooldownMs.Value;
            _stash.ApplyIncubators = Settings.AutoApplyIncubators.Value;

            // Record tick state BEFORE early returns so recordings capture paused/loading/settle state
            _recorder.RecordTick(GameController, _mode.Name,
                (_mode as MappingMode)?.Phase.ToString()
                ?? (_mode as BlightMode)?.Phase.ToString()
                ?? (_mode as SimulacrumMode)?.Phase.ToString()
                ?? (_mode as HeistMode)?.Phase.ToString()
                ?? (_mode as FollowerMode)?.State.ToString()
                ?? "",
                (_mode as MappingMode)?.Decision
                ?? (_mode as SimulacrumMode)?.Decision
                ?? (_mode as HeistMode)?.Decision
                ?? (_mode as FollowerMode)?.Decision
                ?? "",
                (_mode as MappingMode)?.Status ?? "",
                _navigation, _interaction, _loot);

            // Only run full mode logic when running
            if (!Settings.Running)
                return base.Tick();

            // Global interrupts — handle before mode gets control
            if (!HandleInterrupts())
                return base.Tick();

            // Area change settle — entity list and game state aren't reliable for
            // a few seconds after zone transition. Skip mode logic to prevent
            // stale entity reads (e.g., mechanic re-detection from old zone data).
            if ((DateTime.Now - _areaChangedAt).TotalSeconds < AreaSettleSeconds)
                return base.Tick();

            // Sync follower settings
            if (_followerMode != null)
            {
                _followerMode.LeaderName = Settings.Follower.LeaderName.Value;
                _followerMode.FollowDistance = Settings.Follower.FollowDistance.Value;
                _followerMode.StopDistance = Settings.Follower.StopDistance.Value;
                _followerMode.FollowThroughTransitions = Settings.Follower.FollowThroughTransitions.Value;
                _followerMode.EnableCombat = Settings.Follower.EnableCombat.Value;
                _followerMode.EnableLoot = Settings.Follower.EnableLoot.Value;
                _followerMode.LootNearLeaderOnly = Settings.Follower.LootNearLeaderOnly.Value;
            }

            // Let the active mode decide what to do (may set up navigation paths)
            _mode.Tick(_ctx);

            // Navigation ticks AFTER mode — mode sets up/updates paths, then nav executes movement.
            // This prevents stale walk commands: the walk command always targets the current path,
            // not a path that's about to be replaced.
            // Only tick nav when no async action is in flight (cursor settle / key hold).
            if (canAct)
                _navigation.Tick(GameController);

            // Auto level gems (global, runs across all modes)
            TickGemLevelUp();

            return base.Tick();
        }

        public override void Render()
        {
            if (!Settings.Enable || !GameController.InGame)
                return;

            UpdateDebugRangeCircle();

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

            // Ritual shop overlay — always render when shop is open, regardless of mode
            RitualMechanic.RenderShopOverlay(_ctx, Graphics, GameController);

            _ctx.Graphics = null;

            // Incubator debug overlay — shows when stash/inventory is open and setting enabled
            if (Settings.DebugIncubatorOverlay.Value &&
                (GameController.IngameState.IngameUi.StashElement?.IsVisible == true ||
                 GameController.IngameState.IngameUi.InventoryPanel?.IsVisible == true))
            {
                _stash.RenderDebugIncubators(Graphics, GameController);
            }

            // Tile signature overlay (F8)
            RenderTileSignatures();

            // Boss position overlay (always visible when data exists for current map)
            RenderBossMarker();

            // Debug range circle overlay
            if (DateTime.Now < _debugCircleExpiry && _debugCircleRadius > 0 && GameController.Player != null)
            {
                var playerPos = GameController.Player.PosNum;
                var worldRadius = _debugCircleRadius * Systems.Pathfinding.GridToWorld;
                Graphics.DrawCircleInWorld(
                    new System.Numerics.Vector3(playerPos.X, playerPos.Y, playerPos.Z),
                    (float)worldRadius, SharpDX.Color.Yellow, 2f);

                var camera = GameController.IngameState.Camera;
                var labelScreen = camera.WorldToScreen(playerPos);
                Graphics.DrawText(_debugCircleLabel,
                    new System.Numerics.Vector2(labelScreen.X - 40, labelScreen.Y - 60),
                    SharpDX.Color.Yellow);
            }
        }

        private void UpdateDebugRangeCircle()
        {
            var b = Settings.Build;
            var l = Settings.Loot;

            CheckRange("Blink Range", b.BlinkRange.Value);
            CheckRange("Dash Min Distance", b.DashMinDistance.Value);
            CheckRange("Fight Range", b.FightRange.Value);
            CheckRange("Combat Range", b.CombatRange.Value);
            CheckRange("Interact Radius", Settings.InteractRadius.Value);

            var f = Settings.Follower;
            CheckRange("Follow Distance", f.FollowDistance.Value);
            CheckRange("Stop Distance", f.StopDistance.Value);

            // Per-skill MaxTargetRange
            int i = 1;
            foreach (var slot in b.AllSkillSlots)
            {
                CheckRange($"Skill {i} Range", slot.MaxTargetRange.Value);
                i++;
            }
        }

        private void CheckRange(string label, int currentValue)
        {
            if (_lastRangeValues.TryGetValue(label, out var prev) && prev != currentValue)
            {
                _debugCircleLabel = $"{label}: {currentValue}";
                _debugCircleRadius = currentValue;
                _debugCircleExpiry = DateTime.Now.AddSeconds(5);
            }
            _lastRangeValues[label] = currentValue;
        }

        public override void DrawSettings()
        {
            base.DrawSettings();

            // Web UI link
            if (_webServer != null && _webServer.IsRunning)
            {
                ImGui.Separator();
                ImGui.TextColored(new Vector4(0.42f, 0.55f, 1f, 1f), $"Web Dashboard: {_webServer.Url}");
                if (ImGui.SmallButton("Copy URL"))
                    ImGui.SetClipboardText(_webServer.Url);
            }

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

                // Threat detection testing
                ImGui.Separator();
                ImGui.Text("=== Threat Detection ===");

                ImGui.Text($"Status: {_threat.LastAction}");
                ImGui.Text($"Casts detected: {_threat.CastsDetected} | Dodges triggered: {_threat.DodgesTriggered}");

                if (_threat.TrackedMonsters.Count > 0)
                {
                    ImGui.BeginChild("ThreatMonsters", new Vector2(0, 120), ImGuiChildFlags.Border);
                    foreach (var kv in _threat.TrackedMonsters)
                    {
                        var mt = kv.Value;
                        var castInfo = mt.HasCast
                            ? $" CASTING {mt.SkillName} stg={mt.AnimationStage} prog={mt.AnimationProgress:F2} in={mt.TimeRemainingMs:F0}ms dest=({mt.CastDestination.X:F0},{mt.CastDestination.Y:F0}){(mt.DodgeSignaled ? " [DODGED]" : "")}"
                            : $" {mt.AnimationName}";
                        var color = mt.HasCast
                            ? (mt.DodgeSignaled ? new Vector4(1, 0.5f, 0, 1) : new Vector4(1, 1, 0, 1))
                            : new Vector4(0.6f, 0.6f, 0.6f, 1);
                        ImGui.TextColored(color, $"#{mt.EntityId} d={Vector2.Distance(mt.GridPos, new Vector2(GameController.Player.GridPosNum.X, GameController.Player.GridPosNum.Y)):F0}{castInfo}");
                    }
                    ImGui.EndChild();
                }
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
                if (simState.RunsCompleted > 0)
                {
                    var avgDur = simState.AverageRunDuration;
                    ImGui.Text($"Avg: {avgDur.Minutes}m{avgDur.Seconds:D2}s/run | {simState.AverageWavesPerRun:F1} waves/run");
                }
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
                    if (ImGui.Button("Enable Combat (Melee)"))
                        _combat.SetProfile(new Systems.CombatProfile { Enabled = true, Positioning = Systems.CombatPositioning.Melee });
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
        // Web Server
        // =================================================================

        private long _lastTerrainHash;
        private DateTime _lastTerrainRefresh = DateTime.MinValue;
        private const double TerrainRefreshIntervalSec = 3.0;

        private void TickWebServer()
        {
            if (_webServer == null || !_webServer.IsRunning) return;

            // Refresh terrain data on area change or periodically (for exploration updates)
            var currentHash = GameController.IngameState?.Data?.CurrentAreaHash ?? 0;
            var needsRefresh = currentHash != _lastTerrainHash
                || (DateTime.Now - _lastTerrainRefresh).TotalSeconds >= TerrainRefreshIntervalSec;

            if (needsRefresh && currentHash != 0)
            {
                var pfGrid = GameController.IngameState?.Data?.RawPathfindingData;
                var tgtGrid = GameController.IngameState?.Data?.RawTerrainTargetingData;
                var terrain = MapRenderer.BuildTerrainData(pfGrid, tgtGrid, _exploration);
                if (terrain != null)
                {
                    _webServer.UpdateTerrain(terrain, currentHash);
                    _lastTerrainHash = currentHash;
                    _lastTerrainRefresh = DateTime.Now;
                }
            }

            // Process commands from web UI
            while (_webServer.TryDequeueCommand(out var cmd))
            {
                switch (cmd.Action)
                {
                    case "start":
                        Settings.Running.Value = true;
                        if (!_lootTracker.IsActive) _lootTracker.StartSession();
                        break;
                    case "stop":
                        Settings.Running.Value = false;
                        if (_lootTracker.IsActive) _lootTracker.StopSession();
                        break;
                    case "setMode":
                        if (!string.IsNullOrEmpty(cmd.Value) && _modes.ContainsKey(cmd.Value))
                            SetMode(cmd.Value);
                        break;
                }
            }

            // Build status snapshot for web UI — wrapped in try/catch so a single
            // bad entity or stale pointer doesn't kill all dashboard updates.
            try
            {
                var phase = "";
                var decision = "";
                var status = "";

                if (_mode is MappingMode map)
                { phase = map.Phase.ToString(); decision = map.Decision; status = map.Status; }
                else if (_mode is SimulacrumMode sim)
                { phase = sim.Phase.ToString(); decision = sim.Decision; status = sim.StatusText; }
                else if (_mode is BlightMode blight)
                { phase = blight.Phase.ToString(); status = blight.StatusText; }
                else if (_mode is HeistMode heist)
                { phase = heist.Phase.ToString(); decision = heist.Decision; }
                else if (_mode is FollowerMode follower)
                { phase = follower.State.ToString(); decision = follower.Decision; status = follower.StatusText; }

                // Player position for map overlay
                var playerGridPos = GameController.Player?.GridPosNum ?? Vector2.Zero;
                var playerGrid = new Vector2(playerGridPos.X, playerGridPos.Y);

                // Detect skill bar from game
                List<DetectedSkillSlot>? detectedSkills = null;
                try
                {
                    var barIds = GameController.IngameState?.ServerData?.SkillBarIds;
                    var actor = GameController.Player?.GetComponent<ExileCore.PoEMemory.Components.Actor>();
                    if (barIds != null && actor?.ActorSkills != null)
                    {
                        var idToSkill = new Dictionary<int, ExileCore.PoEMemory.MemoryObjects.ActorSkill>();
                        foreach (var skill in actor.ActorSkills)
                        {
                            var id = (int)skill.Id;
                            if (!idToSkill.ContainsKey(id))
                                idToSkill[id] = skill;
                        }

                        string[] posKeys = { "LMB", "RMB", "MButton", "Q", "W", "E", "R", "T" };
                        detectedSkills = new();

                        var limit = Math.Min(barIds.Count, posKeys.Length);
                        for (int i = 3; i < limit; i++)
                        {
                            var skillId = barIds[i];
                            if (skillId == 0) continue;
                            if (!idToSkill.TryGetValue(skillId, out var actorSkill)) continue;
                            var skillName = actorSkill.Name ?? "";
                            if (string.IsNullOrEmpty(skillName)) continue;

                            detectedSkills.Add(new DetectedSkillSlot
                            {
                                SlotIndex = i,
                                Key = posKeys[i],
                                SkillName = skillName,
                                InternalName = actorSkill.InternalName ?? "",
                                IsSpell = actorSkill?.IsSpell ?? false,
                                IsAttack = actorSkill?.IsAttack ?? false,
                                IsVaalSkill = actorSkill?.IsVaalSkill ?? false,
                                IsInstant = actorSkill?.IsInstant ?? false,
                                IsCry = actorSkill?.IsCry ?? false,
                                IsChanneling = actorSkill?.IsChanneling ?? false,
                                IsTotem = actorSkill?.IsTotem ?? false,
                                IsTrap = actorSkill?.IsTrap ?? false,
                                IsMine = actorSkill?.IsMine ?? false,
                                SoulsPerUse = actorSkill?.SoulsPerUse ?? 0,
                                DeployedCount = actorSkill?.DeployedObjects?.Count ?? 0,
                            });
                        }
                    }
                }
                catch { }

                // Read vitals directly from player — CombatSystem only updates when combat is ticking
                var life = GameController.Player?.GetComponent<ExileCore.PoEMemory.Components.Life>();
                var hpPct = Sanitize(life?.HPPercentage ?? 0f);
                var esPct = Sanitize(life?.ESPercentage ?? 0f);
                var manaPct = Sanitize(life?.MPPercentage ?? 0f);

                // Collect entities/nav safely — these iterate game state that can go stale
                List<MapEntity>? entities = null;
                try { entities = MapRenderer.CollectEntities(GameController, playerGrid); } catch { }
                List<float[]>? navPath = null;
                try { navPath = MapRenderer.CollectNavPath(_navigation); } catch { }

                _webServer.UpdateStatus(new BotStatusSnapshot
                {
                    Running = Settings.Running.Value,
                    InGame = GameController.InGame,
                    Mode = _mode?.Name ?? "Unknown",
                    Phase = phase,
                    Decision = decision,
                    Status = status,
                    Area = GameController.Area?.CurrentArea?.Name ?? "",
                    HpPercent = hpPct,
                    EsPercent = esPct,
                    ManaPercent = manaPct,
                    InCombat = _combat.InCombat,
                    NearbyMonsters = _combat.NearbyMonsterCount,
                    CombatTarget = _combat.BestTarget?.RenderName,
                    IsNavigating = _navigation.IsNavigating,
                    WaypointIndex = _navigation.CurrentWaypointIndex,
                    WaypointTotal = _navigation.CurrentNavPath?.Count ?? 0,
                    ExplorationCoverage = Sanitize(_exploration.IsInitialized && _exploration.ActiveBlob != null
                        ? _exploration.ActiveBlob.Coverage : 0f),
                    ExplorationRegions = _exploration.IsInitialized && _exploration.ActiveBlob != null
                        ? _exploration.ActiveBlob.Regions.Count : 0,
                    LootCandidates = _loot.Candidates.Count,
                    SessionChaos = Sanitize((float)_lootTracker.TotalChaosValue),
                    ChaosPerHour = Sanitize((float)_lootTracker.ChaosPerHour),
                    ItemsLooted = _lootTracker.TotalItemsLooted,
                    MapsCompleted = _lootTracker.MapsCompleted,
                    SessionDuration = _lootTracker.SessionDuration.TotalSeconds > 0
                        ? _lootTracker.SessionDuration.ToString(@"hh\:mm\:ss") : "",
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),

                    // Map overlay
                    PlayerGridX = Sanitize(playerGrid.X),
                    PlayerGridY = Sanitize(playerGrid.Y),
                    AreaHash = currentHash,
                    Entities = entities,
                    NavPath = navPath,
                    SkillBar = detectedSkills,
                });
            }
            catch (Exception ex)
            {
                // Log the crash so we can diagnose — don't let it kill future updates
                LogMessage($"[AutoExile] Web snapshot error: {ex.Message}");
            }
        }

        /// <summary>Clamp NaN/Infinity to 0 — System.Text.Json can't serialize them.</summary>
        private static float Sanitize(float v) => float.IsFinite(v) ? v : 0f;

        /// <summary>Called by ExileCore when plugin is being unloaded.</summary>
        public override void OnClose()
        {
            _webServer?.Stop();
            _webServer = null;
            Instance = null;
            base.OnClose();
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
        // Minimap Icon Scanner
        // =================================================================

        // Known minimap icon entities — keyed by entity ID to avoid re-logging
        private readonly Dictionary<long, MinimapIconEntry> _knownMinimapIcons = new();
        private DateTime _lastMinimapIconScan = DateTime.MinValue;
        private const int MinimapIconScanIntervalMs = 2000;

        /// <summary>Discovered minimap icon positions, accessible by modes for navigation.</summary>
        public IReadOnlyDictionary<long, MinimapIconEntry> KnownMinimapIcons => _knownMinimapIcons;

        /// <summary>
        /// Periodic scan of TileEntities for minimap icons. Runs every 2s during mapping.
        /// Tiles load at ~2x network bubble range (~360-400 grid units) as the player moves,
        /// so periodic scanning discovers mechanics well before the entity list does.
        /// New discoveries are logged to console and appended to the persistent dump file.
        /// </summary>
        private void ScanMinimapIcons(bool forceFullLog = false)
        {
            var gc = GameController;
            if (gc?.Player == null) return;

            if (!forceFullLog && (DateTime.Now - _lastMinimapIconScan).TotalMilliseconds < MinimapIconScanIntervalMs)
                return;
            _lastMinimapIconScan = DateTime.Now;

            var tileEntities = gc.IngameState?.Data?.TileEntities;
            if (tileEntities == null) return;

            int newCount = 0;
            foreach (var entity in tileEntities)
            {
                if (entity?.Path == null) continue;
                if (_knownMinimapIcons.ContainsKey(entity.Id)) continue;
                try
                {
                    var mic = entity.GetComponent<ExileCore.PoEMemory.Components.MinimapIcon>();
                    if (mic?.Name == null) continue;

                    var entry = new MinimapIconEntry
                    {
                        EntityId = entity.Id,
                        IconName = mic.Name,
                        Path = entity.Path,
                        GridPos = entity.GridPosNum,
                        EntityType = entity.Type.ToString(),
                    };
                    _knownMinimapIcons[entity.Id] = entry;
                    newCount++;

                    if (!forceFullLog)
                        LogMessage($"[MinimapIcons] New: {mic.Name} — {entity.Path} @ ({entry.GridPos.X:F0},{entry.GridPos.Y:F0})");
                }
                catch { }
            }

            if (forceFullLog || newCount > 0)
                DumpMinimapIconsToFile(gc, forceFullLog);
        }

        /// <summary>
        /// Clear known icons on area change so the next scan starts fresh.
        /// </summary>
        private void ClearMinimapIcons()
        {
            _knownMinimapIcons.Clear();
            _lastMinimapIconScan = DateTime.MinValue;
        }

        private void DumpMinimapIconsToFile(GameController gc, bool logSummary)
        {
            var areaName = gc.Area?.CurrentArea?.Name ?? "Unknown";

            if (logSummary)
            {
                var iconCounts = _knownMinimapIcons.Values
                    .GroupBy(e => e.IconName)
                    .OrderByDescending(g => g.Count());
                LogMessage($"[MinimapIcons] {areaName}: {_knownMinimapIcons.Count} icons, {iconCounts.Count()} types");
                foreach (var g in iconCounts)
                    LogMessage($"  {g.Key} x{g.Count()} — {g.First().Path}");
            }

            try
            {
                var outputDir = Path.Combine(DirectoryFullName, "Dumps");
                Directory.CreateDirectory(outputDir);
                var outputPath = Path.Combine(outputDir, "MinimapIcons.jsonl");
                var entry = new
                {
                    timestamp = DateTime.Now.ToString("o"),
                    area = areaName,
                    totalIcons = _knownMinimapIcons.Count,
                    icons = _knownMinimapIcons.Values.Select(i => new
                    {
                        icon = i.IconName,
                        path = i.Path,
                        gridX = (int)i.GridPos.X,
                        gridY = (int)i.GridPos.Y,
                        type = i.EntityType,
                    }).ToList(),
                };
                var json = System.Text.Json.JsonSerializer.Serialize(entry,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = false });
                var path = outputPath;
                Task.Run(() => { try { File.AppendAllText(path, json + "\n"); } catch { } });
            }
            catch { }
        }

        public class MinimapIconEntry
        {
            public long EntityId;
            public string IconName = "";
            public string Path = "";
            public Vector2 GridPos;
            public string EntityType = "";
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
                    Path = entity.Path ?? "",
                    EntityType = entity.Type.ToString(),
                    GridPos = gridPos,
                    DistanceToPlayer = dist,
                    Category = category,
                    IsAlive = entity.IsAlive,
                    IsTargetable = entity.IsTargetable,
                    IsHostile = entity.IsHostile,
                    Rarity = entity.Type == ExileCore.Shared.Enums.EntityType.Monster
                        ? entity.Rarity.ToString() : null,
                    ShortName = ExtractShortName(entity.Metadata ?? entity.Path ?? ""),
                    RenderName = entity.Type == ExileCore.Shared.Enums.EntityType.Player
                        ? (entity.GetComponent<ExileCore.PoEMemory.Components.Player>()?.PlayerName ?? entity.RenderName ?? "")
                        : (entity.RenderName ?? ""),
                };

                // Capture MinimapIcon name
                try
                {
                    var mic = entity.GetComponent<ExileCore.PoEMemory.Components.MinimapIcon>();
                    if (mic?.Name != null)
                        ent.MinimapIconName = mic.Name;
                }
                catch { }

                // Capture StateMachine states for entities that have them (pump, monolith, etc.)
                if (entity.TryGetComponent<ExileCore.PoEMemory.Components.StateMachine>(out var sm) && sm.States != null)
                {
                    var states = new Dictionary<string, long>();
                    try
                    {
                        foreach (var s in sm.States)
                        {
                            if (!string.IsNullOrEmpty(s.Name))
                                states[s.Name] = s.Value;
                        }
                    }
                    catch { }
                    if (states.Count > 0)
                        ent.States = states;
                }

                snapshot.Entities.Add(ent);
            }

            // Scan TileEntities for map-wide minimap icons (beyond network bubble)
            var existingIds = new HashSet<long>(snapshot.Entities.Select(e => e.Id));
            var tileEntities = gc.IngameState?.Data?.TileEntities;
            if (tileEntities != null)
            {
                foreach (var entity in tileEntities)
                {
                    if (entity?.Path == null) continue;
                    if (existingIds.Contains(entity.Id)) continue; // already captured above
                    try
                    {
                        var mic = entity.GetComponent<ExileCore.PoEMemory.Components.MinimapIcon>();
                        if (mic?.Name == null) continue;

                        snapshot.Entities.Add(new EntitySnapshot
                        {
                            Id = entity.Id,
                            Path = entity.Path,
                            EntityType = entity.Type.ToString(),
                            GridPos = entity.GridPosNum,
                            DistanceToPlayer = Vector2.Distance(entity.GridPosNum, playerGrid),
                            Category = CategorizeEntity(entity),
                            ShortName = ExtractShortName(entity.Path),
                            MinimapIconName = mic.Name,
                        });
                    }
                    catch { }
                }
            }

            // Combat state
            snapshot.Combat = new CombatSnapshot
            {
                InCombat = _combat.InCombat,
                NearbyMonsterCount = _combat.NearbyMonsterCount,
                CachedMonsterCount = _combat.CachedMonsterCount,
                PackCenter = _combat.PackCenter,
                DenseClusterCenter = _combat.DenseClusterCenter,
                NearestMonsterPos = _combat.NearestMonsterPos,
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
                snapshot.Mode.Extra["highestWave"] = sim.State.HighestWaveThisRun;
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
                var bs = blight.State;
                snapshot.Mode.Extra["pumpEntityId"] = bs.PumpEntityId;
                snapshot.Mode.Extra["pumpPos"] = bs.PumpPosition.HasValue
                    ? new[] { bs.PumpPosition.Value.X, bs.PumpPosition.Value.Y }
                    : (object)Array.Empty<float>();
                snapshot.Mode.Extra["isEncounterActive"] = bs.IsEncounterActive;
                snapshot.Mode.Extra["isEncounterDone"] = bs.IsEncounterDone;
                snapshot.Mode.Extra["isTimerDone"] = bs.IsTimerDone;
                snapshot.Mode.Extra["encounterSucceeded"] = bs.EncounterSucceeded;
                snapshot.Mode.Extra["pumpUnderAttack"] = bs.PumpUnderAttack;
                snapshot.Mode.Extra["aliveMonsterCount"] = bs.AliveMonsterCount;
                snapshot.Mode.Extra["chestCount"] = bs.ChestPositions.Count;
                snapshot.Mode.Extra["portalPos"] = bs.PortalPosition.HasValue
                    ? new[] { bs.PortalPosition.Value.X, bs.PortalPosition.Value.Y }
                    : (object)Array.Empty<float>();
                snapshot.Mode.Extra["deathCount"] = bs.DeathCount;
            }
            else if (_mode is FollowerMode follower)
            {
                snapshot.Mode.Phase = follower.State.ToString();
                snapshot.Mode.Status = follower.StatusText;
                snapshot.Mode.Decision = follower.Decision;
                snapshot.Mode.Extra["leaderName"] = follower.LeaderName;
                snapshot.Mode.Extra["followDistance"] = follower.FollowDistance;
                snapshot.Mode.Extra["stopDistance"] = follower.StopDistance;
                snapshot.Mode.Extra["lastLeaderPos"] = follower.LastLeaderGridPos.HasValue
                    ? new[] { follower.LastLeaderGridPos.Value.X, follower.LastLeaderGridPos.Value.Y }
                    : (object)Array.Empty<float>();
                snapshot.Mode.Extra["transitionTarget"] = follower.TransitionTargetGridPos.HasValue
                    ? new[] { follower.TransitionTargetGridPos.Value.X, follower.TransitionTargetGridPos.Value.Y }
                    : (object)Array.Empty<float>();
                snapshot.Mode.Extra["combatEnabled"] = follower.EnableCombat;
                snapshot.Mode.Extra["lootEnabled"] = follower.EnableLoot;
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
                LootRadius = _interaction.InteractRadius,
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

            if (type == ExileCore.Shared.Enums.EntityType.Player)
                return EntityCategory.Player;
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

                // Use the dedicated "Level Up All Gems" button if visible.
                // Property exists at runtime but not in bundled ExileCore DLL — use dynamic.
                try
                {
                    dynamic dynPanel = panel;
                    var levelAllBtn = (ExileCore.PoEMemory.Element)dynPanel.LevelUpAllGemsButton;
                    if (levelAllBtn?.IsVisible == true)
                    {
                        var rect = levelAllBtn.GetClientRect();
                        var absPos = new Vector2(windowRect.X + rect.Center.X, windowRect.Y + rect.Center.Y);
                        BotInput.Click(absPos);
                        _lastGemLevelAt = DateTime.Now;
                        return;
                    }
                }
                catch { /* Property not available in this ExileCore version — fall through to per-gem */ }

                // Click the level-up "+" button for the first levelable gem.
                // Layout: [0]=dismiss(X) [1]=level(+) [2]=bar(hidden) [3]=text
                // The "+" button is the second visible small square child.
                // Skip gems where the button is greyed out (insufficient stats) —
                // detected via the button's highlight state (IsHighlighted).
                foreach (var gemEl in gems)
                {
                    if (gemEl?.IsVisible != true) continue;

                    // Check if this gem can actually be leveled by examining child button state.
                    // When stats are insufficient, the level-up row still appears but the
                    // "+" button's highlight is disabled — use this as a proxy for "enabled".
                    dynamic? levelButton = null;
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
                                levelButton = child;
                                break;
                            }
                        }
                    }

                    if (levelButton == null) continue;

                    // Try to check IsEnabled if the property exists on this Element type.
                    // ExileCore Element may expose it — if not, fall through and click anyway.
                    try
                    {
                        bool enabled = levelButton.IsEnabled;
                        if (!enabled) continue;
                    }
                    catch { /* Property doesn't exist on this type — skip check */ }

                    SharpDX.RectangleF rect = levelButton.GetClientRect();
                    var absPos = new Vector2(windowRect.X + rect.Center.X, windowRect.Y + rect.Center.Y);
                    BotInput.Click(absPos);
                    _lastGemLevelAt = DateTime.Now;
                    return;
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
                if (!_wasDead && _heistMode != null)
                    _heistMode.State.DeathCount++;
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
            _configManager?.Save(Settings);
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

        // =================================================================
        // Map List Population
        // =================================================================

        private void PopulateMapList()
        {
            try
            {
                var nodes = GameController.Files?.AtlasNodes?.EntriesList;
                if (nodes == null || nodes.Count == 0) return;

                // Build map name list from normal atlas nodes (0-109)
                // Mark supported maps (have boss tile data) with ★ prefix
                var mapNames = new List<string>();
                var limit = Math.Min(nodes.Count, 110);
                for (int i = 0; i < limit; i++)
                {
                    var name = nodes[i].Area?.Name;
                    if (string.IsNullOrEmpty(name)) continue;
                    var prefix = _mapDatabase.IsSupported(name) ? "\u2605 " : "";
                    mapNames.Add($"{prefix}{name}");
                }
                mapNames.Sort((a, b) =>
                    a.TrimStart('\u2605', ' ').CompareTo(b.TrimStart('\u2605', ' ')));

                // Save value before SetListValues — it resets Value to first item
                var savedMapName = Settings.Mapping.MapName.Value;
                Settings.Mapping.MapName.SetListValues(mapNames);

                // Restore saved selection
                if (!string.IsNullOrEmpty(savedMapName))
                {
                    if (mapNames.Contains(savedMapName))
                    {
                        Settings.Mapping.MapName.Value = savedMapName;
                    }
                    else
                    {
                        // Try to match without the ★ prefix (prefix may have changed)
                        var plain = savedMapName.TrimStart('\u2605', ' ');
                        var match = mapNames.FirstOrDefault(m => m.TrimStart('\u2605', ' ') == plain);
                        if (match != null)
                            Settings.Mapping.MapName.Value = match;
                    }
                }

                _mapListPopulated = true;
                LogMessage($"[AutoExile] Map list populated: {mapNames.Count} maps ({_mapDatabase.SupportedMaps.Count()} supported)");
            }
            catch (Exception ex)
            {
                LogMessage($"[AutoExile] Map list population failed: {ex.Message}");
            }
        }

        // =================================================================
        // Tile Signature Scanner (F8)
        // =================================================================

        private void ScanTileSignatures()
        {
            var gc = GameController;
            if (gc?.Player == null || !_tileMap.IsLoaded)
            {
                LogMessage("[AutoExile] Tile scan failed: no player or tile map not loaded");
                return;
            }

            var playerGrid = new Vector2(gc.Player.GridPosNum.X, gc.Player.GridPosNum.Y);
            var areaName = gc.Area?.CurrentArea?.Name ?? "Unknown";
            const float scanRadius = 100f;

            var signatures = new List<TileSignature>();

            foreach (var key in _tileMap.GetAllKeys())
            {
                var allPositions = _tileMap.GetPositions(key);
                if (allPositions == null) continue;

                int total = allPositions.Count;
                if (total >= 20) continue; // Skip common tiles

                var nearPositions = new List<Vector2>();
                foreach (var pos in allPositions)
                {
                    if (Vector2.Distance(playerGrid, pos) <= scanRadius)
                        nearPositions.Add(pos);
                }

                if (nearPositions.Count == 0) continue;

                float concentration = (float)nearPositions.Count / total;
                SignatureTier? tier = null;

                if (total == 1)
                    tier = SignatureTier.Unique;
                else if (total <= 3)
                    tier = SignatureTier.VeryRare;
                else if (total <= 9 && concentration >= 0.5f)
                    tier = SignatureTier.Rare;
                else if (total <= 19 && concentration >= 0.8f)
                    tier = SignatureTier.Clustered;

                if (tier == null) continue;

                float score = (1f / total) * concentration * nearPositions.Count;

                signatures.Add(new TileSignature
                {
                    Key = key,
                    Tier = tier.Value,
                    TotalCount = total,
                    NearCount = nearPositions.Count,
                    Concentration = concentration,
                    Score = score,
                    NearPositions = nearPositions,
                });
            }

            // Sort by tier (Unique first) then score descending
            signatures.Sort((a, b) =>
            {
                int tierCmp = a.Tier.CompareTo(b.Tier);
                return tierCmp != 0 ? tierCmp : b.Score.CompareTo(a.Score);
            });

            _tileSignatures = signatures;
            _tileSignatureArea = areaName;
            _tileSignaturePlayerPos = playerGrid;
            _tileSignatureScanTime = DateTime.Now;

            LogMessage($"[AutoExile] Tile scan: {signatures.Count} signatures found in {areaName}");
            WriteTileSignatureLog();

            // Save Unique + VeryRare signatures as boss tiles in the map database
            var bossTiles = signatures
                .Where(s => s.Tier == SignatureTier.Unique || s.Tier == SignatureTier.VeryRare)
                .Select(s => s.Key)
                .ToList();
            if (bossTiles.Count > 0)
            {
                _mapDatabase.SaveBossTiles(areaName, bossTiles);
                // Refresh the map list so ★ markers update
                _mapListPopulated = false;
            }
        }

        private void WriteTileSignatureLog()
        {
            if (_tileSignatures.Count == 0) return;

            var outputDir = Path.Combine(DirectoryFullName, "Dumps");
            Directory.CreateDirectory(outputDir);
            var logPath = Path.Combine(outputDir, "TileSignatures.log");

            var lines = new List<string>();
            lines.Add($"=== {_tileSignatureScanTime:yyyy-MM-dd HH:mm:ss} | {_tileSignatureArea} | Player: ({_tileSignaturePlayerPos.X:F0},{_tileSignaturePlayerPos.Y:F0}) | {_tileSignatures.Count} signatures ===");

            foreach (var sig in _tileSignatures)
            {
                var positions = string.Join("; ", sig.NearPositions.Select(p => $"({p.X:F0},{p.Y:F0})"));
                lines.Add($"  [{sig.Tier}] {sig.Key} | total={sig.TotalCount} near={sig.NearCount} conc={sig.Concentration:P0} score={sig.Score:F3} | {positions}");
            }

            lines.Add("");
            File.AppendAllLines(logPath, lines);
            LogMessage($"[AutoExile] Tile signatures written to {logPath}");
        }

        private void RenderBossMarker()
        {
            var gc = GameController;
            if (gc?.Player == null) return;

            var areaName = gc.Area?.CurrentArea?.Name ?? "";
            if (string.IsNullOrEmpty(areaName)) return;

            var bossTiles = _mapDatabase.GetBossTiles(areaName);
            var rushEnabled = Settings.Mapping.RushBoss.Value;

            // Show status text regardless
            var statusY = 130f;
            if (rushEnabled)
            {
                if (bossTiles == null || bossTiles.Count == 0)
                {
                    Graphics.DrawText($"Boss Rush: ON — no tile data for '{areaName}'",
                        new Vector2(100, statusY), SharpDX.Color.OrangeRed);
                    return;
                }

                // Try to find boss position from tile map
                if (!_tileMap.IsLoaded)
                {
                    Graphics.DrawText("Boss Rush: ON — tile map not loaded",
                        new Vector2(100, statusY), SharpDX.Color.Yellow);
                    return;
                }

                Vector2? bossPos = null;
                string matchedKey = "";
                var playerGrid = new Vector2(gc.Player.GridPosNum.X, gc.Player.GridPosNum.Y);

                foreach (var key in bossTiles)
                {
                    var positions = _tileMap.GetPositions(key);
                    if (positions != null && positions.Count > 0)
                    {
                        // Tile center offset
                        bossPos = positions
                            .OrderBy(p => Vector2.Distance(playerGrid, p))
                            .First() + new Vector2(11.5f, 11.5f);
                        matchedKey = key;
                        break;
                    }
                }

                if (bossPos == null)
                {
                    var keyList = string.Join(", ", bossTiles.Select(k =>
                    {
                        var slash = k.LastIndexOf('/');
                        return slash >= 0 ? k[(slash + 1)..] : k;
                    }));
                    Graphics.DrawText($"Boss Rush: ON — tile keys not found in this instance [{keyList}]",
                        new Vector2(100, statusY), SharpDX.Color.OrangeRed);
                    return;
                }

                // Draw status text
                var dist = Vector2.Distance(playerGrid, bossPos.Value);
                Graphics.DrawText($"Boss Rush: ON — ({bossPos.Value.X:F0}, {bossPos.Value.Y:F0}) dist={dist:F0}",
                    new Vector2(100, statusY), SharpDX.Color.Gold);

                // Draw world marker
                var camera = gc.IngameState.Camera;
                var playerZ = gc.Player.PosNum.Z;
                var bossWorld = new System.Numerics.Vector3(
                    bossPos.Value.X * (float)Systems.Pathfinding.GridToWorld,
                    bossPos.Value.Y * (float)Systems.Pathfinding.GridToWorld,
                    playerZ);
                Graphics.DrawCircleInWorld(bossWorld, 60f, SharpDX.Color.Gold, 3f);
                Graphics.DrawCircleInWorld(bossWorld, 30f, SharpDX.Color.Gold, 2f);
                var bossScreen = camera.WorldToScreen(bossWorld);
                if (bossScreen.X > -200 && bossScreen.X < 2400)
                {
                    Graphics.DrawText($"BOSS ({dist:F0})", bossScreen + new Vector2(-25, -30), SharpDX.Color.Gold);
                }
            }
            else if (bossTiles != null && bossTiles.Count > 0)
            {
                // Not rushing but data exists — show subtle indicator
                Graphics.DrawText($"Boss data available for {areaName} (rush disabled)",
                    new Vector2(100, statusY), SharpDX.Color.DarkGray);
            }
        }

        private void RenderTileSignatures()
        {
            if (_tileSignatures.Count == 0) return;

            var gc = GameController;
            if (gc?.Player == null) return;

            var camera = gc.IngameState.Camera;

            // HUD summary
            Graphics.DrawText(
                $"Tile Signatures: {_tileSignatures.Count} in {_tileSignatureArea} (F8 to clear)",
                new Vector2(100, 110), SharpDX.Color.Gold);

            // World overlay for each signature
            foreach (var sig in _tileSignatures)
            {
                var color = sig.Tier switch
                {
                    SignatureTier.Unique => SharpDX.Color.Gold,
                    SignatureTier.VeryRare => SharpDX.Color.OrangeRed,
                    SignatureTier.Rare => SharpDX.Color.Cyan,
                    SignatureTier.Clustered => SharpDX.Color.LimeGreen,
                    _ => SharpDX.Color.White,
                };

                foreach (var gridPos in sig.NearPositions)
                {
                    // Tile center = gridPos + half tile size (23/2 ≈ 11.5)
                    var centerGrid = gridPos + new Vector2(11.5f, 11.5f);
                    var worldPos = new System.Numerics.Vector3(
                        centerGrid.X * (float)Systems.Pathfinding.GridToWorld,
                        centerGrid.Y * (float)Systems.Pathfinding.GridToWorld,
                        gc.Player.PosNum.Z);

                    // Circle
                    Graphics.DrawCircleInWorld(worldPos, 80f, color, 2f);

                    // Label
                    var screenPos = camera.WorldToScreen(worldPos);
                    if (screenPos.X > 0 && screenPos.Y > 0)
                    {
                        // Short key: last path segment
                        var shortKey = sig.Key;
                        var lastSlash = shortKey.LastIndexOf('/');
                        if (lastSlash >= 0 && lastSlash < shortKey.Length - 1)
                            shortKey = shortKey[(lastSlash + 1)..];

                        var label = $"[{sig.Tier}] {shortKey} ({sig.TotalCount})";
                        Graphics.DrawText(label, new Vector2(screenPos.X - 60, screenPos.Y - 20), color);
                    }
                }
            }
        }
    }

    internal enum SignatureTier { Unique, VeryRare, Rare, Clustered }

    internal class TileSignature
    {
        public string Key = "";
        public SignatureTier Tier;
        public int TotalCount;
        public int NearCount;
        public float Concentration;
        public float Score;
        public List<Vector2> NearPositions = new();
    }

    /// <summary>Cached exploration + mechanics state for a map area, used for round-trip zone support.</summary>
    internal class AreaStateCache
    {
        public ExplorationSnapshot Exploration = null!;
        public MechanicsSnapshot Mechanics = null!;
        public long AreaHash;
        public DateTime CachedAt;
    }
}
