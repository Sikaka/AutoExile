using ExileCore;
using ExileCore.PoEMemory;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using ExileCore.Shared.Nodes;
using AutoExile.Mechanics;
using AutoExile.Modes.Shared;
using AutoExile.Systems;
using System.Numerics;
using System.Windows.Forms;
using Pathfinding = AutoExile.Systems.Pathfinding;

namespace AutoExile.Modes
{
    /// <summary>
    /// Map farming mode — explore the map, fight monsters, loot items.
    /// Combines ExplorationMap (coverage-based navigation), CombatSystem, and LootSystem.
    /// Activated via F5 hotkey or mode switcher. Designed for iterative development —
    /// start with basic explore+fight+loot, add mechanic support over time.
    /// </summary>
    public class MappingMode : IBotMode
    {
        public string Name => "Mapping";

        // ── Phase machine ──
        private MappingPhase _phase = MappingPhase.Idle;
        private DateTime _phaseStartTime = DateTime.Now;

        // ── Explore state ──
        private Vector2? _navTarget;           // current nav target (grid coords)
        private int _exploreTargetsVisited;
        private int _navFailures;              // consecutive pathfind failures
        // MinCoverage is now a setting: ctx.Settings.Mapping.MinCoverage.Value

        // ── Loot state ──
        private DateTime _lastLootScan = DateTime.MinValue;
        private const float LootScanIntervalMs = 500;
        private readonly LootPickupTracker _lootTracker = new();

        // ── Clickable interactables (shrines, heist caches) ──
        private long _pendingInteractableId;
        private int _interactableClickAttempts;
        private const int MaxInteractableAttempts = 3;
        private const float InteractableDetectRadius = 120f;  // grid units — Required interactables route toward
        private const float InteractableOptionalRadius = 25f; // grid units — Optional interactables only when passing by
        private string _pendingInteractableName = "";
        private readonly HashSet<long> _failedInteractables = new();

        // ── Mechanic state ──
        private IMapMechanic? _pendingMechanic;    // Detected, navigating to it
        private bool _mechanicActive;               // Mechanic owns the tick loop

        // ── Post-mechanic settle (wait for loot drops after mechanic completes) ──
        private bool _postMechanicSettle;
        private DateTime _postMechanicSettleUntil;

        // ── Mechanic rush (beeline to Required mechanic minimap icons) ──
        private Vector2? _mechanicRushTarget;      // grid position of rush target
        private string _mechanicRushName = "";      // icon name for overlay/logging

        // ── Tile-based mechanic detection (map-wide, instant at load) ──
        private Vector2? _tileMechanicTarget;      // blob-snapped grid position
        private string _tileMechanicName = "";      // mechanic name
        private bool _tileScanProcessed;            // processed TileScanResult for this map?

        // ── Exit portal (wish zones, sub-zones with return portals) ──
        private Entity? _exitPortal;
        private DateTime _lastExitClickTime = DateTime.MinValue;
        private DateTime _zoneEnteredAt = DateTime.Now;
        private long _lastZoneHash;
        private const float MinZoneSecondsBeforeExit = 20f; // don't exit sub-zones before clearing them

        // ── Sub-zone state (wish zones, harvest groves, etc.) ──
        private bool _isInSubZone;
        private bool _expectingSubZone;       // set when TriggersSubZone mechanic activates
        private long _parentZoneHash;
        private MechanicsSnapshot? _parentMechanicsSnapshot;
        private DateTime? _zoneSettleUntil;   // block actions until terrain/entities settle after hash change
        private bool _zoneSettleExplorationDone; // exploration reinitialized after settle
        // Zone settle now driven by ctx.Settings.AreaSettleSeconds

        // ── Stats ──
        private DateTime _startTime;

        // ── Status (for overlay) ──
        public MappingPhase Phase => _phase;
        public string Status { get; private set; } = "";
        public string Decision { get; private set; } = "";
        public int ExploreTargetsVisited => _exploreTargetsVisited;
        public DateTime StartTime => _startTime;
        public Vector2? NavTarget => _navTarget;
        public bool IsPaused => _phase == MappingPhase.Paused;

        /// <summary>When true, pause (set Running=false) after the current hideout cycle completes but before opening the next map.</summary>
        public bool PauseAfterHideout { get; set; }

        // ── Priority targets (minimap icon routing) ──
        private Vector2? _priorityTarget;            // current priority nav target (grid)
        private string _priorityTargetReason = "";   // why we're going there
        private readonly HashSet<long> _visitedIconIds = new(); // minimap icon entity IDs already handled

        // ── Hideout loop state ──
        private readonly HideoutFlow _hideoutFlow = new();
        private bool _mapCompleted;
        private string _lastAreaName = "";
        private Vector2? _spawnPortalPos;          // cached from map entry (fallback for exit)

        // ── ExitMap state ──
        private DateTime _exitMapStartTime;
        private bool _portalKeyPressed;
        private const float ExitMapTimeoutSeconds = 60f;
        private const float PortalAppearTimeoutSeconds = 5f;

        public enum MappingPhase
        {
            Idle,
            InHideout,  // running HideoutFlow (stash → map device → enter)
            Exploring,
            Fighting,
            Looting,
            Paused,
            Exiting,    // navigating to and clicking exit portal (wish zones)
            ExitMap,    // opening portal → clicking it → back to hideout
            Complete,
        }

        private enum MapCompletionStatus
        {
            Incomplete,        // Keep exploring
            MechanicsDone,     // ExitAfter mechanics satisfied, exit now
            FullyExplored,     // Coverage met + no targets
            WaitingForMechanic,// Required mechanic pending
        }

        /// <summary>
        /// Reset all state and restart the mapping loop from the beginning (e.g. after Stop → Start).
        /// Equivalent to re-entering the mode while already selected.
        /// </summary>
        public void Reset(BotContext ctx) => OnEnter(ctx);

        public void OnEnter(BotContext ctx)
        {
            _startTime = DateTime.Now;
            _zoneEnteredAt = DateTime.Now;
            _lastZoneHash = ctx.Game?.IngameState?.Data?.CurrentAreaHash ?? 0;
            _exploreTargetsVisited = 0;
            _navFailures = 0;
            _navTarget = null;
            _mapCompleted = false;
            _spawnPortalPos = null;
            _portalKeyPressed = false;
            PauseAfterHideout = false;
            Decision = "";

            var gc = ctx.Game;
            _lastAreaName = gc.Area?.CurrentArea?.Name ?? "";

            // Hideout/town → start hideout flow
            if (gc.Area?.CurrentArea?.IsHideout == true || gc.Area?.CurrentArea?.IsTown == true)
            {
                _phase = MappingPhase.InHideout;
                StartMappingHideoutFlow(ctx);
                Status = "In hideout — starting map cycle";
                ctx.Log("[Mapping] OnEnter: in hideout, starting HideoutFlow");
                return;
            }

            // In map — initialize exploration
            InitMapState(ctx, gc);
        }

        /// <summary>
        /// Initialize in-map state (exploration, combat, boss rush).
        /// Called from OnEnter (mid-map F5) and from area change handler (entering map from hideout).
        /// </summary>
        private void InitMapState(BotContext ctx, GameController gc)
        {
            // NOTE: Do NOT set _lastZoneHash here. It must only be updated by the hash change
            // handler so that sub-zone transitions (wish zones with same area name) are detected.
            // Setting it here would race with the hash handler and prevent detection.

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

            ModeHelpers.EnableCombatWithKiting(ctx, ctx.Settings.Mapping.EnableKiting.Value);

            // Cache spawn position for fallback portal exit
            if (gc.Player != null)
                _spawnPortalPos = new Vector2(gc.Player.GridPosNum.X, gc.Player.GridPosNum.Y);

            // Tile-based mechanic detection — re-process on each map entry
            _tileMechanicTarget = null;
            _tileMechanicName = "";
            _tileScanProcessed = false;

            _phase = MappingPhase.Exploring;
            Status = "Started";

            ctx.Log($"Mapping — {ctx.Exploration.TotalWalkableCells} cells, " +
                    $"{ctx.Exploration.ActiveBlob?.Regions.Count ?? 0} regions");
        }

        public void OnExit()
        {
            _phase = MappingPhase.Idle;
            _navTarget = null;
            _priorityTarget = null;
            _priorityTargetReason = "";
            _visitedIconIds.Clear();
            _pendingMechanic = null;
            _mechanicActive = false;
            _mechanicRushTarget = null;
            _mechanicRushName = "";
            _tileMechanicTarget = null;
            _tileMechanicName = "";
            _tileScanProcessed = false;
            _postMechanicSettle = false;
            _pendingInteractableId = 0;
            _pendingInteractableName = "";
            _interactableClickAttempts = 0;
            _failedInteractables.Clear();
            _exitPortal = null;
            _lastZoneHash = 0;
            _isInSubZone = false;
            _expectingSubZone = false;
            _parentZoneHash = 0;
            _parentMechanicsSnapshot = null;
            _zoneSettleUntil = null;
            _zoneSettleExplorationDone = false;
            _hideoutFlow.Cancel();
            _mapCompleted = false;
            _spawnPortalPos = null;
            _lastAreaName = "";
            _portalKeyPressed = false;
            Status = "Stopped";
            Decision = "";
        }

        public void Pause(BotContext ctx)
        {
            ctx.Navigation.Stop(ctx.Game);
            _phase = MappingPhase.Paused;
            Status = "PAUSED — F5 to resume, F5 again to stop";
        }

        public void Resume()
        {
            if (_phase == MappingPhase.Paused)
            {
                _phase = MappingPhase.Exploring;
                Status = "Resumed";
            }
        }

        /// <summary>Keep portal scrolls in inventory, stash everything else.</summary>
        private static bool ShouldStashItem(ServerInventory.InventSlotItem item)
        {
            return item.Item?.Path?.Contains("CurrencyPortal") != true;
        }

        /// <summary>Get clean map name from settings (strip ★ prefix).</summary>
        private static string? GetTargetMapName(BotSettings settings)
        {
            var raw = settings.Mapping.MapName.Value;
            if (string.IsNullOrEmpty(raw)) return null;
            return raw.TrimStart('\u2605', ' ');
        }

        /// <summary>Start HideoutFlow with mapping-specific settings.</summary>
        private void StartMappingHideoutFlow(BotContext ctx)
        {
            var mapName = GetTargetMapName(ctx.Settings);
            var minTier = ctx.Settings.Mapping.MinMapTier.Value;
            var modFilter = BuildMapModFilter(ctx.Settings.Mapping.MapModFilter);
            var scarabs = ctx.Settings.Mapping.ActiveScarabs()
                .Select(s => (s.StashTab.Value, s.PathSubstring.Value))
                .ToList();

            var autoRestock = ctx.Settings.Mapping.AutoRestock.Value;

            // Map restock path: use specific name if set, otherwise any map ("Maps/") filtered by tier.
            // PoE1 specific-map paths: Metadata/Items/Maps/MapWorlds{NameNoSpaces}
            // Tier-only (Mirage) maps: just need "Maps/" + tier filter via MapKey component.
            string? mapRestockPath = null;
            int mapRestockMinTier = 0;
            if (autoRestock)
            {
                if (!string.IsNullOrEmpty(mapName))
                    mapRestockPath = "MapWorlds" + mapName.Replace(" ", "").Replace("'", "").Replace("-", "");
                else
                {
                    mapRestockPath = "Maps/";
                    mapRestockMinTier = minTier;
                }
            }

            // Portal scroll withdrawal: pull 5 from stash if fewer than 1 in inventory.
            List<(string, string, int, int)>? portalWithdrawals = null;
            var portalScrollTab = ctx.Settings.Mapping.PortalScrollTabName.Value;
            int portalScrollsInInv = StashSystem.CountInventoryItems(ctx.Game, "CurrencyPortal");
            if (portalScrollsInInv < 1)
            {
                portalWithdrawals = new List<(string, string, int, int)>
                {
                    (portalScrollTab, "CurrencyPortal", 5, 0)
                };
            }

            _hideoutFlow.Start(modFilter, ShouldStashItem,
                targetMapName: mapName, minMapTier: minTier,
                scarabSlots: scarabs.Count > 0 ? scarabs : null,
                autoRestock: autoRestock,
                mapRestockPath: mapRestockPath,
                mapRestockMinTier: mapRestockMinTier,
                extraWithdrawals: portalWithdrawals,
                deviceStorageRefillThreshold: ctx.Settings.Mapping.DeviceStorageRefillThreshold.Value);

            // Pass scarab paths to MapDeviceSystem for insertion
            ctx.MapDevice.ScarabPaths.Clear();
            foreach (var (_, path) in scarabs)
                ctx.MapDevice.ScarabPaths.Add(path);

            // Tell MapDeviceSystem what to look for in inventory if map was restocked
            ctx.MapDevice.InventoryMapPath = mapRestockPath;
            ctx.MapDevice.InventoryMapMinTier = mapRestockMinTier;
        }

        /// <summary>
        /// Build a map element filter that combines IsStandardMap with the mod filter config.
        /// </summary>
        private static Func<Element, bool> BuildMapModFilter(BotSettings.MapModFilter config)
        {
            if (config.ModStates.Count == 0)
                return MapDeviceSystem.IsStandardMap;

            var required = config.ModStates
                .Where(kv => kv.Value == 1)
                .Select(kv => kv.Key)
                .ToList();
            var blocked = config.ModStates
                .Where(kv => kv.Value == -1)
                .Select(kv => kv.Key)
                .ToList();

            return item =>
            {
                if (!MapDeviceSystem.IsStandardMap(item)) return false;

                var entity = item.Entity;
                if (entity == null) return true;
                if (!entity.TryGetComponent<ExileCore.PoEMemory.Components.Mods>(out var mods)) return true;
                var itemMods = mods?.ItemMods;
                if (itemMods == null) return true;

                // Any blocked mod → skip
                if (blocked.Count > 0 && itemMods.Any(m =>
                    blocked.Any(b => m.RawName.Contains(b, StringComparison.OrdinalIgnoreCase))))
                    return false;

                // All required mods must be present
                if (required.Count > 0 && !required.All(r =>
                    itemMods.Any(m => m.RawName.Contains(r, StringComparison.OrdinalIgnoreCase))))
                    return false;

                return true;
            };
        }

        public void Tick(BotContext ctx)
        {
            if (_phase == MappingPhase.Idle || _phase == MappingPhase.Paused) return;

            var gc = ctx.Game;
            if (gc?.Player == null || !gc.InGame || !gc.Player.IsAlive) return;

            // ── Area change detection (hideout ↔ map transitions) ──
            var currentArea = gc.Area?.CurrentArea?.Name ?? "";
            if (!string.IsNullOrEmpty(currentArea) && currentArea != _lastAreaName)
            {
                Decision = $"AREA NAME CHANGED: '{_lastAreaName}'->'{currentArea}' subZone={_isInSubZone}";
                OnAreaChanged(ctx, currentArea);
                _lastAreaName = currentArea;
            }

            // ── InHideout phase — tick HideoutFlow ──
            if (_phase == MappingPhase.InHideout)
            {
                // InteractionSystem must be ticked for HideoutFlow's portal clicking to work
                ctx.Interaction.Tick(gc);
                var signal = _hideoutFlow.Tick(ctx);
                Status = _hideoutFlow.Status;
                if (signal == HideoutSignal.PortalTimeout)
                {
                    // No portal to re-enter — start fresh map
                    StartMappingHideoutFlow(ctx);
                    Status = "No portal found — starting new map";
                }
                return;
            }

            // Detect zone changes (sub-zone transitions like wish zones)
            // Use area hash — area name can be identical between parent map and sub-zone
            var currentHash = gc.IngameState?.Data?.CurrentAreaHash ?? 0;

            if (currentHash != 0 && currentHash != _lastZoneHash)
            {
                if (_lastZoneHash == 0)
                {
                    // First time seeing a hash — initialize tracking without settlement
                    ctx.Log($"[Mapping] Hash initialized: {currentHash} (was 0)");
                }
                else
                {
                    Decision = $"HASH CHANGED: {_lastZoneHash}->{currentHash} subZone={_isInSubZone} expecting={_expectingSubZone}";
                    ctx.Log($"[Mapping] Zone changed: hash {_lastZoneHash} -> {currentHash}, phase={_phase}, subZone={_isInSubZone}, expecting={_expectingSubZone}");

                    // Cancel all in-flight systems — stale state from old zone
                    ModeHelpers.CancelAllSystems(ctx);
                    ctx.Navigation.Stop(gc);

                    if (_isInSubZone && currentHash == _parentZoneHash)
                    {
                        // ── Returning to parent map from sub-zone ──
                        ctx.Log("[Mapping] Returning to parent map from sub-zone");
                        _isInSubZone = false;
                        _expectingSubZone = false;
                        _parentZoneHash = 0;

                        if (_parentMechanicsSnapshot != null)
                        {
                            ctx.Mechanics.RestoreSnapshot(_parentMechanicsSnapshot);
                            _parentMechanicsSnapshot = null;
                            ctx.Log("[Mapping] Restored parent map mechanics state");
                        }

                        _mechanicActive = false;
                        _pendingMechanic = null;
                    }
                    else if (_expectingSubZone)
                    {
                        // ── Entering sub-zone (mechanic flagged TriggersSubZone) ──
                        // NOTE: BotCore may have already called _mechanics.Reset() before we run.
                        // Snapshot may be empty — that's OK, we just need the sub-zone flag.
                        ctx.Log($"[Mapping] Entering sub-zone, snapshotting mechanics (active={ctx.Mechanics.ActiveMechanic?.Name ?? "null"})");

                        _parentMechanicsSnapshot = ctx.Mechanics.CreateSnapshot();
                        _parentZoneHash = _lastZoneHash;
                        _isInSubZone = true;
                        _expectingSubZone = false;

                        if (_mechanicActive && ctx.Mechanics.ActiveMechanic != null)
                            ctx.Mechanics.ForceCompleteActive();
                        ctx.Mechanics.Reset();
                        ctx.Mechanics.SuppressMechanic("Wishes");

                        _mechanicActive = false;
                        _pendingMechanic = null;
                    }
                    else if (_isInSubZone)
                    {
                        // ── Late hash change within sub-zone ──
                        // The area hash loads AFTER entities — mechanic completion handler already
                        // set up the sub-zone, but BotCore saw the hash change first and called
                        // _mechanics.Reset() (clearing our Wishes suppression). Re-suppress it.
                        // The generic zone-local reset below will restart the settle timer,
                        // which re-initializes exploration with the now-stable terrain data.
                        ctx.Log($"[Mapping] Late hash change in sub-zone — re-suppressing Wishes (parent={_parentZoneHash})");
                        ctx.Mechanics.SuppressMechanic("Wishes");

                        _mechanicActive = false;
                        _pendingMechanic = null;
                    }

                    // Reset zone-local state for the new zone
                    _zoneEnteredAt = DateTime.Now;
                    _exitPortal = null;
                    _navTarget = null;
                    _mechanicRushTarget = null;
                    _mechanicRushName = "";
                    _tileMechanicTarget = null;
                    _tileMechanicName = "";
                    _tileScanProcessed = false;
                    _exploreTargetsVisited = 0;
                    _navFailures = 0;
                    _visitedIconIds.Clear();

                    // Start settle timer — block actions until terrain/entities load
                    var settleTime = ctx.Settings.AreaSettleSeconds.Value;
                    _zoneSettleUntil = DateTime.Now.AddSeconds(settleTime);
                    _zoneSettleExplorationDone = false;
                    _phase = MappingPhase.Exploring;
                    Status = "Zone transition — settling...";

                    ctx.Log($"[Mapping] Zone settle started ({settleTime}s)");
                }
                _lastZoneHash = currentHash;
            }

            // ── Zone settle gate ──
            // After a hash-based zone change (sub-zone entry/exit), block all actions
            // for a few seconds so terrain data, entity list, and server state stabilize.
            if (_zoneSettleUntil.HasValue)
            {
                var settleRemaining = (_zoneSettleUntil.Value - DateTime.Now).TotalSeconds;
                if (settleRemaining > 0)
                {
                    var curHash = gc.IngameState?.Data?.CurrentAreaHash ?? 0;
                    Status = $"Zone settling ({settleRemaining:F1}s) subZone={_isInSubZone} hash={curHash} parent={_parentZoneHash}";
                    return; // Block all actions
                }

                // Settle complete — reinitialize exploration with now-stable terrain data
                _zoneSettleUntil = null;
                if (!_zoneSettleExplorationDone)
                {
                    ReinitExplorationForNewZone(ctx, gc);
                    _zoneSettleExplorationDone = true;
                }

                _phase = MappingPhase.Exploring;
                ModeHelpers.EnableCombatWithKiting(ctx, ctx.Settings.Mapping.EnableKiting.Value);
                var settleCoverage = ctx.Exploration.ActiveBlobCoverage;
                var settleHash = gc.IngameState?.Data?.CurrentAreaHash ?? 0;
                var wishesSuppressed = ctx.Mechanics.CompletionCounts.ContainsKey("Wishes");
                ctx.Log($"[Mapping] Zone settle complete — subZone={_isInSubZone} phase={_phase} coverage={settleCoverage:P1} hash={settleHash} parentHash={_parentZoneHash} wishesSuppressed={wishesSuppressed} icons={ctx.MinimapIcons.Count}");
            }

            var playerGrid = new Vector2(gc.Player.GridPosNum.X, gc.Player.GridPosNum.Y);

            // ── ExitMap phase — portal key → click portal → hideout ──
            // Placed after hash change + settle gate so zone transitions can interrupt it
            if (_phase == MappingPhase.ExitMap)
            {
                TickExitMap(ctx, gc);
                return;
            }

            // ── Combat (always runs — fires skills while exploring) ──
            // Navigation always owns movement. CombatSystem scans threats, fires skills,
            // manages flasks, but never repositions. Dense packs and rares trigger detours below.
            if (!_mechanicActive)
            {
                ctx.Combat.SuppressPositioning = true;  // Always — we own movement
                ctx.Combat.SuppressTargetedSkills = ctx.Interaction.IsBusy;
                ctx.Combat.Tick(ctx);
            }

            // ── Mechanic Rush (beeline to Required mechanic minimap icons) ──
            // Don't rush while interaction system is busy (loot pickup, interactable click) —
            // rush navigation would override the interaction's navigation every tick.
            var _isRushing = !_mechanicActive && !ctx.Interaction.IsBusy
                && TryMechanicRush(ctx, playerGrid);

            // Resume navigation when density drops below threshold (detour over)
            if (!_mechanicActive && ctx.Navigation.IsPaused)
            {
                if (!ctx.Combat.InCombat ||
                    ctx.Combat.NearbyMonsterCount < ctx.Settings.Mapping.MinPackDensity.Value ||
                    ctx.Interaction.IsBusy)
                {
                    ctx.Navigation.Resume(gc);
                }
            }

            // ── Mechanics (in-map encounters like Ultimatum) ──
            if (_mechanicActive)
            {
                var result = ctx.Mechanics.TickActive(ctx);
                if (result == MechanicResult.InProgress)
                {
                    // Mechanic owns the loop — don't loot or explore
                    Status = $"Mechanic: {ctx.Mechanics.ActiveMechanic?.Status ?? "working"}";
                    return;
                }

                // Mechanic finished (complete/abandoned/failed)
                var wasExpectingSubZone = _expectingSubZone;
                if (result != MechanicResult.Complete)
                    _expectingSubZone = false;
                _mechanicActive = false;
                _pendingMechanic = null;
                Decision = $"Mechanic result: {result}";

                // Sub-zone entry: wish zones don't change area hash or name.
                // The mechanic returns Complete when it detects SekhemaPortal (targetable).
                // That means we're already inside the wish zone.
                if (result == MechanicResult.Complete && wasExpectingSubZone)
                {
                    // CRITICAL: Stop navigation immediately. The mechanic was navigating to/clicking
                    // the DjinnPortal. If nav keeps ticking, the player walks through the SekhemaPortal
                    // (exit) during settle and gets sent back to the parent map.
                    ctx.Navigation.Stop(gc);
                    ModeHelpers.CancelAllSystems(ctx);

                    ctx.Log($"[Mapping] Mechanic completed with sub-zone expected — entering sub-zone mode (hash={gc.IngameState?.Data?.CurrentAreaHash}, lastHash={_lastZoneHash})");
                    _parentMechanicsSnapshot = ctx.Mechanics.CreateSnapshot();
                    _parentZoneHash = _lastZoneHash;
                    _isInSubZone = true;
                    _expectingSubZone = false;

                    ctx.Mechanics.Reset();
                    ctx.Mechanics.SuppressMechanic("Wishes");

                    // Settle + reinitialize for sub-zone
                    _zoneEnteredAt = DateTime.Now;
                    _exitPortal = null;
                    _navTarget = null;
                    _exploreTargetsVisited = 0;
                    _navFailures = 0;
                    _visitedIconIds.Clear();
                    var wishSettleTime = ctx.Settings.AreaSettleSeconds.Value;
                    _zoneSettleUntil = DateTime.Now.AddSeconds(wishSettleTime);
                    _zoneSettleExplorationDone = false;
                    _phase = MappingPhase.Exploring;
                    var entryHash = gc.IngameState?.Data?.CurrentAreaHash ?? 0;
                    Status = $"Entered wish zone — settling... hash={entryHash} parent={_parentZoneHash}";
                    ctx.Log($"[Mapping] Wish zone settle started ({wishSettleTime}s) hash={entryHash} parent={_parentZoneHash}");
                    return;
                }

                // Start post-mechanic settle period for loot pickup
                if (result == MechanicResult.Complete)
                {
                    var settleSeconds = ctx.Settings.Mechanics.PostMechanicSettleSeconds.Value;
                    if (settleSeconds > 0)
                    {
                        _postMechanicSettle = true;
                        _postMechanicSettleUntil = DateTime.Now.AddSeconds(settleSeconds);
                        ctx.Log($"[Mapping] Post-mechanic settle: {settleSeconds}s for loot pickup");
                    }
                    else if (!_isInSubZone && EvaluateCompletion(ctx, playerGrid) == MapCompletionStatus.MechanicsDone)
                    {
                        ctx.Log("[Mapping] All ExitAfter mechanics complete — exiting map");
                        EnterExitMap(ctx, gc);
                        return;
                    }
                }
                // Fall through to normal loot/explore
            }

            // ── Post-mechanic settle: loot nearby items, then check map exit ──
            if (_postMechanicSettle)
            {
                var settleRemaining = (_postMechanicSettleUntil - DateTime.Now).TotalSeconds;
                if (settleRemaining > 0)
                {
                    // Scan and pick up loot during settle period
                    if ((DateTime.Now - _lastLootScan).TotalMilliseconds >= LootScanIntervalMs)
                    {
                        ctx.Loot.Scan(gc);
                        _lastLootScan = DateTime.Now;
                    }
                    if (ctx.Interaction.IsBusy)
                    {
                        var interResult = ctx.Interaction.Tick(gc);
                        var hadPending = _lootTracker.HasPending;
                        _lootTracker.HandleResult(interResult, ctx);
                        if (hadPending && interResult == InteractionResult.Succeeded)
                        {
                            ctx.Loot.Scan(gc);
                            _lastLootScan = DateTime.Now;
                        }

                        // If loot pickup failed due to portal/blocking obstruction, trigger Z-key toggle
                        if (hadPending && interResult == InteractionResult.Failed)
                        {
                            var failReason = ctx.Interaction.LastFailReason.ToLower();
                            if (failReason.Contains("portal") || failReason.Contains("block") || failReason.Contains("obstruct"))
                            {
                                if (ctx.Loot.StartLabelToggle(gc))
                                {
                                    ctx.Log($"[Mapping] Loot blocked by {failReason} — toggling labels to unstick");
                                }
                            }
                        }
                    }
                    else if (ctx.Loot.TogglePhase != LootSystem.LabelTogglePhase.Idle)
                    {
                        ctx.Loot.TickLabelToggle(gc);
                    }
                    else if (ctx.Loot.HasLootNearby)
                    {
                        var (_, candidate) = ctx.Loot.PickupNext(ctx.Interaction, ctx.Navigation);
                        if (candidate != null && ctx.Interaction.IsBusy)
                            _lootTracker.SetPending(candidate.Entity.Id, candidate.ItemName, candidate.ChaosValue);
                    }
                    else if (ctx.Loot.ShouldToggleLabels(gc))
                    {
                        ctx.Loot.StartLabelToggle(gc);
                        ctx.Log("[Loot] Post-mechanic: starting label toggle unstick");
                    }
                    Status = ctx.Loot.TogglePhase != LootSystem.LabelTogglePhase.Idle
                        ? $"Post-mechanic: {ctx.Loot.ToggleStatus} ({settleRemaining:F1}s remaining)"
                        : $"Post-mechanic loot settle ({settleRemaining:F1}s remaining)";
                    return;
                }

                // Settle expired
                _postMechanicSettle = false;
                ctx.Log("[Mapping] Post-mechanic settle complete");
                if (!_isInSubZone && EvaluateCompletion(ctx, playerGrid) == MapCompletionStatus.MechanicsDone)
                {
                    ctx.Log("[Mapping] All ExitAfter mechanics complete — exiting map");
                    EnterExitMap(ctx, gc);
                    return;
                }
            }

            // Skip mechanic start/detection during rush — we're beelining to the target
            if (!_isRushing)
            {
                // Check for pending mechanic to start (navigate to it)
                if (_pendingMechanic != null && !_pendingMechanic.IsComplete)
                {
                    // Let the mechanic handle its own navigation
                    ctx.Mechanics.SetActive(_pendingMechanic);
                    _mechanicActive = true;
                    if (_pendingMechanic.TriggersSubZone)
                    {
                        _expectingSubZone = true;
                        ctx.Log($"[Mapping] Set _expectingSubZone=true for {_pendingMechanic.Name}");
                    }
                    Status = $"Starting mechanic: {_pendingMechanic.Name}";
                    return;
                }

                // ── Mechanic detection (periodic scan for in-map encounters) ──
                // Runs before loot so in-progress encounters get immediate priority
                var detectedMechanic = ctx.Mechanics.DetectAndPrioritize(ctx);
                if (detectedMechanic != null && _pendingMechanic == null && !_mechanicActive)
                {
                    _pendingMechanic = detectedMechanic;
                    Decision = $"Found mechanic: {detectedMechanic.Name}";
                    // Start immediately on next iteration (top of tick)
                    return;
                }
            }

            // ── Looting (between combat and exploration) ──
            if ((DateTime.Now - _lastLootScan).TotalMilliseconds >= LootScanIntervalMs)
            {
                ctx.Loot.Scan(gc);
                _lastLootScan = DateTime.Now;
            }

            // ── Tick interaction system if busy (loot pickups, shrine clicks, etc.) ──
            if (ctx.Interaction.IsBusy)
            {
                var result = ctx.Interaction.Tick(gc);

                // Handle loot pickup results
                var hadPending = _lootTracker.HasPending;
                _lootTracker.HandleResult(result, ctx);
                if (hadPending && result == InteractionResult.Succeeded)
                {
                    ctx.Loot.Scan(gc);
                    _lastLootScan = DateTime.Now;
                }

                // If loot pickup failed due to portal/blocking obstruction, trigger Z-key toggle
                // to unstick labels and try again with better visibility
                if (hadPending && result == InteractionResult.Failed)
                {
                    var failReason = ctx.Interaction.LastFailReason.ToLower();
                    if (failReason.Contains("portal") || failReason.Contains("block") || failReason.Contains("obstruct"))
                    {
                        if (ctx.Loot.StartLabelToggle(gc))
                        {
                            ctx.Log($"[Mapping] Loot blocked by {failReason} — toggling labels to unstick");
                        }
                    }
                }

                // Handle interactable (shrine/cache) results
                if (result != InteractionResult.InProgress && result != InteractionResult.None
                    && _pendingInteractableId != 0)
                {
                    var prev = FindEntityById(gc, _pendingInteractableId);
                    if (prev != null && prev.IsTargetable && !IsInteractableDone(prev))
                    {
                        _interactableClickAttempts++;
                        if (_interactableClickAttempts >= MaxInteractableAttempts)
                        {
                            _failedInteractables.Add(_pendingInteractableId);
                            ctx.Log($"[Interactable] Blacklisted after {MaxInteractableAttempts} attempts");
                        }
                    }
                    else
                    {
                        _interactableClickAttempts = 0;
                    }
                    _pendingInteractableId = 0;
                }

                if (ctx.Interaction.IsBusy)
                {
                    if (_lootTracker.HasPending)
                    {
                        _phase = MappingPhase.Looting;
                        Status = $"Looting: {_lootTracker.PendingItemName}";
                    }
                    else if (_pendingInteractableId != 0)
                    {
                        Status = $"Clicking: {_pendingInteractableName} ({ctx.Interaction.Status})";
                    }
                    return;
                }
            }

            // ── Label toggle unstick (runs mid-sequence, blocks other actions) ──
            if (ctx.Loot.TogglePhase != LootSystem.LabelTogglePhase.Idle)
            {
                if (ctx.Loot.TickLabelToggle(gc))
                {
                    Status = $"Label toggle: {ctx.Loot.ToggleStatus}";
                    return;
                }
                // Toggle finished — re-scan immediately
                ctx.Loot.Scan(gc);
                _lastLootScan = DateTime.Now;
            }

            // ── Looting (pick up nearby items) ──
            if (ctx.Loot.HasLootNearby && !ctx.Interaction.IsBusy)
            {
                var (wasInRadius, candidate) = ctx.Loot.PickupNext(ctx.Interaction, ctx.Navigation);
                if (candidate != null && ctx.Interaction.IsBusy)
                {
                    _lootTracker.SetPending(candidate.Entity.Id, candidate.ItemName, candidate.ChaosValue);
                    _phase = MappingPhase.Looting;
                    Status = $"Looting: {candidate.ItemName}";
                    return;
                }
            }

            // ── Label toggle: no candidates but items exist nearby → toggle labels ──
            if (!ctx.Loot.HasLootNearby && !ctx.Interaction.IsBusy &&
                ctx.Loot.ShouldToggleLabels(gc))
            {
                ctx.Loot.StartLabelToggle(gc);
                ctx.Log("[Loot] Starting label toggle unstick");
                Status = "Label toggle: unsticking stacked labels";
                return;
            }

            // ── Clickable interactables (shrines, heist caches — click as we pass by) ──
            if (!ctx.Interaction.IsBusy)
            {
                var target = FindNearbyInteractable(gc, playerGrid, ctx.Settings.Mechanics.Interactables);
                if (target != null)
                {
                    if (ctx.Interaction.InteractWithEntity(target, ctx.Navigation))
                    {
                        _pendingInteractableId = target.Id;
                        _pendingInteractableName = target.RenderName ?? target.Path;
                        _interactableClickAttempts = 0;
                        Status = $"Clicking: {_pendingInteractableName}";
                        ctx.Log($"[Interactable] Clicking {target.Path} dist={target.DistancePlayer:F0}");
                        return;
                    }
                }
            }

            // ── Eldritch Altars (opportunistic — click as we pass by) ──
            if (!ctx.Interaction.IsBusy)
            {
                var altarResult = ctx.AltarHandler.Tick(ctx);
                if (altarResult == AltarTickResult.Busy)
                {
                    if (ctx.Navigation.IsNavigating)
                        ctx.Navigation.Pause();
                    Status = $"Altar: {ctx.AltarHandler.Status}";
                    return;
                }
            }

            // Rushing — loot/interactables handled above, skip exploration and combat detours
            if (_isRushing)
                return;

            // ── Density-gated combat detour ──
            // Only pause exploration for dense packs or rare/unique targets.
            // Otherwise skills fire passively while walking.
            if (ctx.Combat.InCombat)
            {
                int minDensity = ctx.Settings.Mapping.MinPackDensity.Value;
                bool denseEnough = ctx.Combat.NearbyMonsterCount >= minDensity;

                // Check for rare/unique detour
                bool rareDetour = false;
                if (!denseEnough && ctx.Settings.Mapping.DetourForRares.Value && ctx.Combat.BestTarget != null)
                {
                    var rarity = ctx.Combat.BestTarget.Rarity;
                    if (rarity == MonsterRarity.Rare || rarity == MonsterRarity.Unique)
                    {
                        var distToTarget = Vector2.Distance(playerGrid, ctx.Combat.BestTarget.GridPosNum);
                        if (distToTarget <= ctx.Settings.Mapping.MaxDetourDistance.Value)
                            rareDetour = true;
                    }
                }

                if (denseEnough || rareDetour)
                {
                    // Pause nav, pathfind to density cluster
                    if (ctx.Navigation.IsNavigating)
                        ctx.Navigation.Pause();

                    var combatTarget = ctx.Combat.DenseClusterCenter;
                    var distToCluster = Vector2.Distance(playerGrid, combatTarget);

                    if (distToCluster > 10f &&
                        (!ctx.Navigation.IsNavigating || _navTarget == null ||
                         Vector2.Distance(_navTarget.Value, combatTarget) > 20f))
                    {
                        ctx.Navigation.Stop(gc);
                        if (ctx.Navigation.NavigateTo(gc, combatTarget))
                        {
                            _navTarget = combatTarget;
                        }
                    }

                    _phase = MappingPhase.Fighting;
                    var reason = rareDetour ? "rare/unique detour" : $"dense pack ({ctx.Combat.NearbyMonsterCount})";
                    Decision = $"Fighting: {reason} @ ({combatTarget.X:F0},{combatTarget.Y:F0})";
                    Status = $"Fighting: {ctx.Combat.NearbyMonsterCount} monsters ({ctx.Combat.LastSkillAction})";
                    return;
                }
                // else: below density threshold, no rare target — skills fire passively while walking
            }

            // ── Exit portal handling (wish zones / sub-zones) ──
            if (_phase == MappingPhase.Exiting)
            {
                TickExitPortal(ctx, gc);
                return;
            }

            // ── Exploration ──
            if (_phase == MappingPhase.Complete)
                return; // Don't overwrite Complete state

            if (_phase == MappingPhase.Looting || _phase == MappingPhase.Fighting)
            {
                Decision = _phase == MappingPhase.Fighting
                    ? "Combat cleared, resuming exploration"
                    : "Loot cleared, resuming exploration";
            }

            _phase = ctx.Combat.InCombat ? MappingPhase.Fighting : MappingPhase.Exploring;
            var coverage = ctx.Exploration.ActiveBlobCoverage;
            var combatInfo = ctx.Combat.InCombat
                ? $" | Fighting: {ctx.Combat.NearbyMonsterCount} ({ctx.Combat.LastSkillAction})"
                : "";
            Status = $"Coverage: {coverage:P1}{combatInfo}";

            // Stuck abandonment
            if (ctx.Navigation.IsNavigating && !ctx.Navigation.IsPaused &&
                ctx.Navigation.StuckRecoveries >= 5 && _navTarget.HasValue)
            {
                ctx.Log($"Stuck {ctx.Navigation.StuckRecoveries}x, abandoning target");
                ctx.Exploration.MarkRegionFailed(_navTarget.Value);
                ctx.Navigation.Stop(gc);
                _navTarget = null;
                Decision = $"Abandoned stuck target ({ctx.Exploration.FailedRegions.Count} failed regions)";
            }

            // Pick next target when idle (not navigating, or paused nav just resumed)
            if (!ctx.Navigation.IsNavigating)
            {
                // Priority 1: Required mechanics/interactables from minimap icons
                var priority = GetPriorityTarget(ctx, playerGrid);
                if (priority.HasValue)
                {
                    NavigateToTarget(ctx, gc, playerGrid, priority.Value);
                    Decision = $"Priority: {_priorityTargetReason}";
                }
                // Priority 2: Normal exploration — use unified completion check
                else
                {
                    HandleExplorationCompletion(ctx, gc, playerGrid, coverage);
                }
            }
            else
            {
                // Nav is active — check if a Required target appeared that should override current exploration
                var priority = GetPriorityTarget(ctx, playerGrid);
                if (priority.HasValue && _priorityTargetReason.Contains("Required") &&
                    _mechanicRushTarget == null) // mechanic rush handles Required mechanics already
                {
                    // Only redirect if current target is generic exploration (not already a priority)
                    if (_navTarget.HasValue && _priorityTarget.HasValue &&
                        Vector2.Distance(_navTarget.Value, _priorityTarget.Value) > 30)
                    {
                        ctx.Navigation.Stop(gc);
                        NavigateToTarget(ctx, gc, playerGrid, priority.Value);
                        Decision = $"Redirected: {_priorityTargetReason}";
                    }
                }
            }
        }

        private void NavigateToTarget(BotContext ctx, GameController gc, Vector2 playerGrid, Vector2 targetGrid)
        {
            if (ctx.Navigation.NavigateTo(gc, targetGrid))
            {
                _navTarget = targetGrid;
                _exploreTargetsVisited++;
                _navFailures = 0;
                Decision = $"Exploring #{_exploreTargetsVisited} @ ({targetGrid.X:F0},{targetGrid.Y:F0})";
            }
            else
            {
                ctx.Exploration.MarkRegionFailed(targetGrid);
                _navFailures++;
                Decision = $"Pathfind failed to ({targetGrid.X:F0},{targetGrid.Y:F0})";
            }
        }

        private void NavigateToNextExploreTarget(BotContext ctx, GameController gc, Vector2 playerGrid)
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                var target = ctx.Exploration.GetNextExplorationTarget(playerGrid);
                if (!target.HasValue)
                {
                    var coverage = ctx.Exploration.ActiveBlobCoverage;
                    var blob = ctx.Exploration.ActiveBlob;
                    ctx.Log($"[Mapping] No explore target: coverage={coverage:P1} regions={blob?.Regions.Count ?? 0} failed={ctx.Exploration.FailedRegions.Count} cells={blob?.SeenCells.Count ?? 0}/{blob?.WalkableCells.Count ?? 0} attempt={attempt}");

                    // Only consider exit after meaningful exploration (>10% coverage).
                    // Prevents immediately exiting wish zones / sub-zones before exploring them.
                    if (coverage > 0.10f)
                        HandleMapExit(ctx, gc, playerGrid, coverage, "no-targets");
                    // else: fresh zone, no targets yet — wait for exploration to populate
                    return;
                }

                if (ctx.Navigation.NavigateTo(gc, target.Value))
                {
                    _navTarget = target.Value;
                    _exploreTargetsVisited++;
                    _navFailures = 0;
                    Decision = $"Exploring #{_exploreTargetsVisited} @ ({target.Value.X:F0},{target.Value.Y:F0})";
                    return;
                }

                ctx.Exploration.MarkRegionFailed(target.Value);
                _navFailures++;
                ctx.Log($"Pathfind failed to ({target.Value.X:F0},{target.Value.Y:F0}), marking unreachable");
            }

            Decision = $"5 consecutive pathfind failures ({ctx.Exploration.FailedRegions.Count} total unreachable)";
        }

        /// <summary>
        /// Handle exploration when nav is idle — use EvaluateCompletion to decide next action.
        /// </summary>
        private void HandleExplorationCompletion(BotContext ctx, GameController gc, Vector2 playerGrid, float coverage)
        {
            var status = EvaluateCompletion(ctx, playerGrid);
            switch (status)
            {
                case MapCompletionStatus.MechanicsDone:
                    ctx.Log("[Mapping] All ExitAfter mechanics complete — exiting map");
                    EnterExitMap(ctx, gc);
                    break;

                case MapCompletionStatus.FullyExplored:
                    HandleMapExit(ctx, gc, playerGrid, coverage, "explore-complete");
                    break;

                case MapCompletionStatus.WaitingForMechanic:
                    // Required mechanic detected but not done — keep exploring nearby
                    NavigateToNextExploreTarget(ctx, gc, playerGrid);
                    break;

                case MapCompletionStatus.Incomplete:
                    NavigateToNextExploreTarget(ctx, gc, playerGrid);
                    break;
            }
        }

        /// <summary>
        /// Common exit logic: try exit portal (sub-zones) or portal scroll (parent map).
        /// </summary>
        private void HandleMapExit(BotContext ctx, GameController gc, Vector2 playerGrid, float coverage, string caller)
        {
            var zoneTime = (DateTime.Now - _zoneEnteredAt).TotalSeconds;
            ctx.Log($"[Mapping] HandleMapExit ({caller}): coverage={coverage:P1} zoneTime={zoneTime:F1}s subZone={_isInSubZone}");

            var exitPortal = FindExitPortal(ctx, gc, ctx.Combat, caller);
            if (exitPortal != null)
            {
                _exitPortal = exitPortal;
                _phase = MappingPhase.Exiting;
                var elapsed = (DateTime.Now - _startTime).TotalSeconds;
                Status = $"Explored {coverage:P1} in {elapsed:F0}s — using exit portal";
                Decision = $"EXIT: {caller} coverage={coverage:P1} zoneTime={zoneTime:F0}s subZone={_isInSubZone}";
                ctx.Log($"[Mapping] Found exit portal — returning to parent map");
            }
            else if (!_isInSubZone)
            {
                ctx.Log($"[Mapping] Map complete ({caller}) — exiting via portal scroll");
                EnterExitMap(ctx, gc);
            }
            else
            {
                _phase = MappingPhase.Complete;
                var elapsed = (DateTime.Now - _startTime).TotalSeconds;
                Status = $"COMPLETE — {coverage:P1} coverage in {elapsed:F0}s";
                Decision = $"Sub-zone complete ({ctx.Exploration.FailedRegions.Count} unreachable)";
            }
        }

        // =================================================================
        // Area Change Handling (hideout ↔ map transitions)
        // =================================================================

        private void OnAreaChanged(BotContext ctx, string newArea)
        {
            var gc = ctx.Game;

            // Cancel all in-flight systems on any area change
            ModeHelpers.CancelAllSystems(ctx);
            _hideoutFlow.Cancel();

            if (gc.Area.CurrentArea.IsHideout || gc.Area.CurrentArea.IsTown)
            {
                // Arrived in hideout
                ctx.Mechanics.Reset();
                _pendingMechanic = null;
                _mechanicActive = false;
                _postMechanicSettle = false;

                if (_mapCompleted)
                {
                    // Map done — check for pause or start new cycle
                    _mapCompleted = false;
                    if (PauseAfterHideout)
                    {
                        PauseAfterHideout = false;
                        ctx.Settings.Running.Value = false;
                        _phase = MappingPhase.Idle;
                        Status = "Paused at hideout (ready to open map)";
                        ctx.Log("[Mapping] Map completed, paused at hideout");
                    }
                    else
                    {
                        _phase = MappingPhase.InHideout;
                        StartMappingHideoutFlow(ctx);
                        Status = "Back in hideout — starting new map";
                        ctx.Log("[Mapping] Map completed, starting new hideout flow");
                    }
                }
                else
                {
                    // Died or unexpected return — try to re-enter map
                    _phase = MappingPhase.InHideout;
                    _hideoutFlow.StartPortalReentry();
                    Status = "Back in hideout — re-entering map";
                    ctx.Log("[Mapping] Returned to hideout unexpectedly — attempting portal re-entry");
                }
            }
            else
            {
                // Entered map — initialize exploration and combat
                ctx.Log($"[Mapping] Entered map: {newArea}");
                _zoneEnteredAt = DateTime.Now;
                _portalKeyPressed = false;
                _failedInteractables.Clear();
                _visitedIconIds.Clear();
                InitMapState(ctx, gc);
            }
        }

        // =================================================================
        // Exit Map (portal key → click portal → hideout)
        // =================================================================

        private void EnterExitMap(BotContext ctx, GameController gc)
        {
            // In sub-zones (wish zones), portal scrolls don't work — use SekhemaPortal instead
            if (_isInSubZone)
            {
                var exitPortal = FindExitPortal(ctx, gc, ctx.Combat, "sub-zone-exit");
                if (exitPortal != null)
                {
                    _exitPortal = exitPortal;
                    _phase = MappingPhase.Exiting;
                    Status = "Sub-zone complete — using exit portal";
                    Decision = "Exiting sub-zone via SekhemaPortal";
                    ctx.Log("[Mapping] Sub-zone complete — exiting via SekhemaPortal");
                }
                else
                {
                    // No exit portal found yet — keep exploring, it will be found later
                    ctx.Log("[Mapping] Sub-zone complete but no exit portal found — continuing exploration");
                }
                return;
            }

            // Last-resort check — if there's a targetable SekhemaPortal (actual exit portal,
            // not minimap icon), we're in a wish zone — don't press portal key
            if (_isInSubZone || HasTargetableSekhemaPortal(gc))
            {
                ctx.Log("[Mapping] EnterExitMap: SekhemaPortal detected — redirecting to exit portal");
                _isInSubZone = true; // Fix the flag retroactively
                var exitPortal = FindExitPortal(ctx, gc, ctx.Combat, "exit-map-guard");
                if (exitPortal != null)
                {
                    _exitPortal = exitPortal;
                    _phase = MappingPhase.Exiting;
                    Status = "Sub-zone complete — using exit portal";
                    Decision = "Exiting sub-zone via SekhemaPortal";
                }
                else
                {
                    ctx.Log("[Mapping] SekhemaPortal exists but FindExitPortal blocked (combat/timing) — waiting");
                }
                return;
            }

            ctx.Navigation.Stop(gc);
            _phase = MappingPhase.ExitMap;
            _exitMapStartTime = DateTime.Now;
            _portalKeyPressed = false;
            _mapCompleted = true;
            var elapsed = (DateTime.Now - _startTime).TotalSeconds;
            Status = $"Map complete ({elapsed:F0}s) — opening portal";
            Decision = "Exiting map";
            ctx.Log($"[Mapping] EnterExitMap: elapsed={elapsed:F0}s coverage={ctx.Exploration.ActiveBlobCoverage:P1}");
        }

        private void TickExitMap(BotContext ctx, GameController gc)
        {
            var elapsed = (DateTime.Now - _exitMapStartTime).TotalSeconds;

            // Overall timeout
            if (elapsed > ExitMapTimeoutSeconds)
            {
                Status = "Exit map timed out";
                _phase = MappingPhase.Complete;
                ctx.Log("[Mapping] ExitMap timed out");
                return;
            }

            // Step 1: Press portal key
            if (!_portalKeyPressed)
            {
                if (BotInput.CanAct)
                {
                    BotInput.PressKey(ctx.Settings.Mapping.PortalKey.Value);
                    _portalKeyPressed = true;
                    Status = "Pressed portal key — waiting for portal";
                    ctx.Log("[Mapping] Pressed portal key");
                }
                return;
            }

            // Step 2: Wait for TownPortal entity to appear
            var portal = ModeHelpers.FindNearestPortal(gc);
            if (portal == null)
            {
                if (elapsed > PortalAppearTimeoutSeconds)
                {
                    // No portal appeared — fallback to spawn position
                    if (_spawnPortalPos.HasValue)
                    {
                        ctx.Log("[Mapping] No portal scroll — walking to spawn portal");
                        ctx.Navigation.NavigateTo(gc, _spawnPortalPos.Value);
                        Status = "No portal scroll — walking to spawn point";
                        // Keep checking for portals while walking
                    }
                    else
                    {
                        Status = "No portal and no spawn position — stuck";
                    }
                }
                else
                {
                    Status = $"Waiting for portal ({elapsed:F1}s)";
                }
                return;
            }

            // Step 3: Click the portal via InteractionSystem
            if (!ctx.Interaction.IsBusy)
            {
                ctx.Interaction.InteractWithEntity(portal, ctx.Navigation, requireProximity: true);
                Status = "Clicking portal to exit map";
            }
            else
            {
                var result = ctx.Interaction.Tick(gc);
                Status = $"Exiting: {ctx.Interaction.Status}";
                if (result == InteractionResult.Failed)
                {
                    // Retry
                    ctx.Interaction.InteractWithEntity(portal, ctx.Navigation, requireProximity: true);
                }
            }
        }

        // =================================================================
        // Priority Target Selection (minimap icon routing)
        // =================================================================

        /// <summary>
        /// Known minimap icon names → interactable setting mapping.
        /// Icons not listed here are ignored (unknown/unmapped).
        /// </summary>
        private static ListNode? GetInteractableSetting(string iconName, BotSettings.InteractableSettings settings)
        {
            return iconName switch
            {
                "Shrine" => settings.Shrines,
                "Strongbox" => settings.Strongboxes,
                "HeistSumgglersCache" => settings.HeistCaches,  // GGG typo in icon name
                "CraftingUnlockObject" => settings.CraftingRecipes,
                _ => null,
            };
        }

        /// <summary>
        /// Known minimap icon names → mechanic mode setting mapping.
        /// Returns the MechanicMode ListNode for mechanics that have one.
        /// </summary>
        private static ListNode? GetMechanicSetting(string iconName, BotSettings settings)
        {
            return iconName switch
            {
                "UltimatumAltar" => settings.Mechanics.Ultimatum.Mode,
                "RitualRune" or "RitualRuneFinished" => settings.Mechanics.Ritual.Mode,
                "HarvestPortal" => settings.Mechanics.Harvest.Mode,
                "Mirage" => settings.Mechanics.Wishes.Mode,
                "BlightCore" => null, // Blight has its own mode (BlightMode), not a map mechanic
                _ => null,
            };
        }

        /// <summary>Map minimap icon name to mechanic name (as used by MapMechanicManager).</summary>
        private static string? IconToMechanicName(string iconName)
        {
            return iconName switch
            {
                "UltimatumAltar" => "Ultimatum",
                "RitualRune" or "RitualRuneFinished" => "Ritual",
                "HarvestPortal" => "Harvest",
                "Mirage" => "Wishes",
                _ => null,
            };
        }

        // =================================================================
        // Mechanic Rush (tile-based map-wide detection + minimap icon fallback)
        // =================================================================

        /// <summary>
        /// Process tile scan results to find Required mechanic targets.
        /// Called lazily — needs both TileScan and Exploration initialized.
        /// </summary>
        private void ProcessTileScan(BotContext ctx)
        {
            if (_tileScanProcessed) return;
            if (ctx.TileScan == null || !ctx.Exploration.IsInitialized) return;

            var blob = ctx.Exploration.ActiveBlob;
            if (blob == null) return;

            _tileScanProcessed = true;
            _tileMechanicTarget = null;
            _tileMechanicName = "";

            // Run blob-relative landmark scan (filters tiles to active blob, finds anomalies)
            TileScanner.ScanBlobLandmarks(ctx.TileScan, ctx.TileMap, blob.WalkableCells);

            ctx.Log($"[TileScan] Blob landmarks: {ctx.TileScan.Landmarks.Count} found");
            foreach (var lm in ctx.TileScan.Landmarks)
            {
                ctx.Log($"[TileScan]   [{lm.Type}] {lm.DetailName}: {lm.TileCount}/{lm.TotalInBlob} tiles @ ({lm.CentroidGridPos.X:F0},{lm.CentroidGridPos.Y:F0})");
            }

            // Find rush target from landmarks: first Required mechanic landmark
            foreach (var landmark in ctx.TileScan.Landmarks)
            {
                if (landmark.Type != LandmarkType.Mechanic) continue;

                // Map detail name to mechanic name
                var mechName = landmark.DetailName switch
                {
                    "ultimatum_altar" => "Ultimatum",
                    "abyssfeature" => "Abyss",
                    _ => null,
                };
                if (mechName == null) continue;

                var mode = GetMechanicModeByName(mechName, ctx.Settings);
                if (mode == "Skip") continue;
                if (ctx.Mechanics.GetCompletionCount(mechName) > 0) continue;

                ctx.Log($"[TileScan] Mechanic landmark: {mechName} at ({landmark.CentroidGridPos.X:F0},{landmark.CentroidGridPos.Y:F0})");

                if (mode == "Required" && _tileMechanicTarget == null)
                {
                    _tileMechanicTarget = landmark.CentroidGridPos;
                    _tileMechanicName = mechName;
                }
            }

            // Also check map-wide DetectedMechanics for path-prefix mechanics (Harvest)
            // that might not have detail name landmarks
            foreach (var (mechName, info) in ctx.TileScan.DetectedMechanics)
            {
                if (_tileMechanicTarget != null) break; // already have a target
                var mode = GetMechanicModeByName(mechName, ctx.Settings);
                if (mode != "Required") continue;
                if (ctx.Mechanics.GetCompletionCount(mechName) > 0) continue;

                // Snap centroid to blob — Harvest tiles may be in a disconnected area
                var snapped = ctx.Exploration.SnapToActiveBlob(info.CentroidGridPos);
                if (snapped.HasValue)
                {
                    _tileMechanicTarget = snapped.Value;
                    _tileMechanicName = mechName;
                    ctx.Log($"[TileScan] Path-prefix mechanic: {mechName} snapped to ({snapped.Value.X:F0},{snapped.Value.Y:F0})");
                }
                else
                {
                    ctx.Log($"[TileScan] Path-prefix mechanic: {mechName} unreachable in active blob");
                }
            }
        }

        /// <summary>
        /// Get mechanic mode setting value by mechanic name (not icon name).
        /// </summary>
        private static string GetMechanicModeByName(string mechName, BotSettings settings)
        {
            return mechName switch
            {
                "Ultimatum" => settings.Mechanics.Ultimatum.Mode.Value,
                "Harvest" => settings.Mechanics.Harvest.Mode.Value,
                "Wishes" => settings.Mechanics.Wishes.Mode.Value,
                "Essence" => settings.Mechanics.Essence.Mode.Value,
                "Ritual" => settings.Mechanics.Ritual.Mode.Value,
                _ => "Skip",
            };
        }

        /// <summary>
        /// Rush to Required mechanics. Two-phase approach:
        /// 1. Tile-based (map-wide, instant): from TileScanResult at map load
        /// 2. Minimap-icon fallback: for mechanics not detectable via tiles (Ritual, Wishes, Essence)
        /// Returns true if rushing (caller should return from Tick).
        /// </summary>
        private bool TryMechanicRush(BotContext ctx, Vector2 playerGrid)
        {
            // Don't rush in sub-zones or when a mechanic is already active/pending
            if (_isInSubZone || _mechanicActive || _pendingMechanic != null)
            {
                _mechanicRushTarget = null;
                return false;
            }

            // ── Phase 1: Tile-based target (map-wide, known from load) ──
            ProcessTileScan(ctx);

            if (_tileMechanicTarget.HasValue)
            {
                // Clear if completed
                if (ctx.Mechanics.GetCompletionCount(_tileMechanicName) > 0)
                {
                    ctx.Log($"[MechanicRush] Tile target {_tileMechanicName} completed — clearing");
                    _tileMechanicTarget = null;
                    _tileMechanicName = "";
                    // Re-scan for next Required mechanic
                    _tileScanProcessed = false;
                    ProcessTileScan(ctx);
                }
            }

            if (_tileMechanicTarget.HasValue)
            {
                var dist = Vector2.Distance(playerGrid, _tileMechanicTarget.Value);
                if (dist < 40)
                {
                    // Close enough — entity detection will handle it
                    _tileMechanicTarget = null;
                }
                else
                {
                    return RushToTarget(ctx, playerGrid, _tileMechanicTarget.Value, _tileMechanicName, "tile");
                }
            }

            // ── Phase 2: Minimap icon fallback (for non-tile-detectable mechanics) ──
            Vector2? bestTarget = null;
            float bestDist = float.MaxValue;
            string bestName = "";

            foreach (var (id, icon) in ctx.MinimapIcons)
            {
                if (_visitedIconIds.Contains(id)) continue;

                var mechSetting = GetMechanicSetting(icon.IconName, ctx.Settings);
                if (mechSetting == null || mechSetting.Value != "Required") continue;

                var mechName = IconToMechanicName(icon.IconName);
                if (mechName != null && ctx.Mechanics.GetCompletionCount(mechName) > 0) continue;

                // Skip if this mechanic is already handled by tile detection
                if (mechName != null && ctx.TileScan?.DetectedMechanics.ContainsKey(mechName) == true) continue;

                var dist = Vector2.Distance(playerGrid, icon.GridPos);
                if (dist < 40) continue;

                if (dist < bestDist)
                {
                    bestTarget = icon.GridPos;
                    bestDist = dist;
                    bestName = icon.IconName;
                }
            }

            if (bestTarget.HasValue)
                return RushToTarget(ctx, playerGrid, bestTarget.Value, bestName, "icon");

            _mechanicRushTarget = null;
            return false;
        }

        /// <summary>Navigate to a rush target. Shared by tile-based and icon-based rush.</summary>
        private bool RushToTarget(BotContext ctx, Vector2 playerGrid, Vector2 target, string name, string source)
        {
            _mechanicRushTarget = target;
            _mechanicRushName = name;

            var dist = Vector2.Distance(playerGrid, target);

            if (!ctx.Navigation.IsNavigating ||
                (_navTarget.HasValue && Vector2.Distance(_navTarget.Value, target) > 30))
            {
                ctx.Navigation.Stop(ctx.Game);
                if (ctx.Navigation.NavigateTo(ctx.Game, target))
                {
                    _navTarget = target;
                    ctx.Log($"[MechanicRush] Rushing to {name} [{source}] at ({target.X:F0},{target.Y:F0}) dist={dist:F0}");
                }
                else
                {
                    // Diagnostic: why did pathfinding fail?
                    var pfGrid = ctx.Game.IngameState?.Data?.RawPathfindingData;
                    int pfValue = 0;
                    if (pfGrid != null)
                    {
                        int tx = (int)target.X, ty = (int)target.Y;
                        if (ty >= 0 && ty < pfGrid.Length && tx >= 0 && tx < pfGrid[ty].Length)
                            pfValue = pfGrid[ty][tx];
                    }
                    var inBlob = ctx.Exploration.ActiveBlob?.WalkableCells.Contains(
                        new Systems.Vector2i((int)target.X, (int)target.Y)) == true;
                    ctx.Log($"[MechanicRush] FAILED path to {name} [{source}] at ({target.X:F0},{target.Y:F0}) dist={dist:F0} pf={pfValue} inBlob={inBlob} player=({playerGrid.X:F0},{playerGrid.Y:F0})");
                    _mechanicRushTarget = null;
                    return false;
                }
            }

            Decision = $"Mechanic rush: {name} [{source}] ({dist:F0}g away)";
            Status = $"Rushing to {name} — {dist:F0}g";
            return true;
        }

        /// <summary>
        /// Find the highest-priority unvisited minimap icon target.
        /// Required targets first, then optional. Skips icons in already-explored areas.
        /// </summary>
        private Vector2? GetPriorityTarget(BotContext ctx, Vector2 playerGrid)
        {
            _priorityTarget = null;
            _priorityTargetReason = "";

            // Sub-zones (wish zones): minimap icons from parent map may persist in the entity
            // list as stale/cached entries. Don't route to them — explore the sub-zone normally.
            if (_isInSubZone) return null;

            if (ctx.MinimapIcons.Count == 0) return null;

            var seen = ctx.Exploration.ActiveBlob?.SeenCells;
            Vector2? bestRequired = null;
            float bestRequiredDist = float.MaxValue;
            string bestRequiredReason = "";

            Vector2? bestOptional = null;
            float bestOptionalDist = float.MaxValue;
            string bestOptionalReason = "";

            foreach (var (id, icon) in ctx.MinimapIcons)
            {
                if (_visitedIconIds.Contains(id)) continue;

                // Check if we've already explored this area (within ~10 grid cells)
                if (seen != null && seen.Contains(new Vector2i((int)icon.GridPos.X, (int)icon.GridPos.Y)))
                {
                    _visitedIconIds.Add(id);
                    continue;
                }

                var dist = Vector2.Distance(playerGrid, icon.GridPos);

                // Check interactable settings
                var interactSetting = GetInteractableSetting(icon.IconName, ctx.Settings.Mechanics.Interactables);
                if (interactSetting != null)
                {
                    if (interactSetting.Value == "Ignore") continue;

                    // When close enough, verify the actual entity is still claimable.
                    // TileEntity icons persist after shrines are claimed / chests are opened.
                    if (dist < InteractableOptionalRadius && IsInteractableAlreadyClaimed(ctx.Game, icon))
                    {
                        _visitedIconIds.Add(id);
                        continue;
                    }

                    if (interactSetting.Value == "Required" && dist < bestRequiredDist)
                    {
                        bestRequired = icon.GridPos;
                        bestRequiredDist = dist;
                        bestRequiredReason = $"{icon.IconName} (Required) @ d={dist:F0}";
                    }
                    else if (interactSetting.Value == "Optional" && dist < bestOptionalDist)
                    {
                        bestOptional = icon.GridPos;
                        bestOptionalDist = dist;
                        bestOptionalReason = $"{icon.IconName} (Optional) @ d={dist:F0}";
                    }
                    continue;
                }

                // Check mechanic settings
                var mechSetting = GetMechanicSetting(icon.IconName, ctx.Settings);
                if (mechSetting != null)
                {
                    var mode = mechSetting.Value;
                    if (mode == "Skip") continue;
                    if (mode == "Required" && dist < bestRequiredDist)
                    {
                        bestRequired = icon.GridPos;
                        bestRequiredDist = dist;
                        bestRequiredReason = $"{icon.IconName} (Required) @ d={dist:F0}";
                    }
                    else if (mode == "Optional" && dist < bestOptionalDist)
                    {
                        bestOptional = icon.GridPos;
                        bestOptionalDist = dist;
                        bestOptionalReason = $"{icon.IconName} (Optional) @ d={dist:F0}";
                    }
                }
            }

            // Required first, then optional
            if (bestRequired.HasValue)
            {
                _priorityTarget = bestRequired;
                _priorityTargetReason = bestRequiredReason;
                return bestRequired;
            }
            if (bestOptional.HasValue)
            {
                _priorityTarget = bestOptional;
                _priorityTargetReason = bestOptionalReason;
                return bestOptional;
            }
            return null;
        }

        // =================================================================
        // Map Completion Evaluation
        // =================================================================

        /// <summary>
        /// Unified completion check — evaluates all conditions to determine if the map is done.
        /// Replaces scattered inline checks at mechanic completion, coverage threshold, and no-targets paths.
        /// </summary>
        private MapCompletionStatus EvaluateCompletion(BotContext ctx, Vector2 playerGrid)
        {
            // 0. Tile-absence intelligence: if ExitAfter mechanics are tile-detectable
            //    and absent from tile scan, the map lacks them → treat as complete
            //    Only applies inside maps — hideout/town tile scans don't have map mechanics
            var isInMap = ctx.Game.Area?.CurrentArea != null &&
                          !ctx.Game.Area.CurrentArea.IsHideout &&
                          !ctx.Game.Area.CurrentArea.IsTown;
            if (isInMap && _tileScanProcessed && ctx.TileScan != null &&
                AreExitMechanicsAbsentFromTiles(ctx))
            {
                return MapCompletionStatus.MechanicsDone;
            }

            // 1. ExitAfter mechanics — highest priority exit trigger
            if (ctx.Mechanics.AreExitMechanicsComplete(ctx.Settings) &&
                !ctx.Mechanics.HasPendingMechanics())
                return MapCompletionStatus.MechanicsDone;

            // 2. Required mechanics still pending (detected but not done)
            if (ctx.Mechanics.HasPendingMechanics())
                return MapCompletionStatus.WaitingForMechanic;

            // 3. Coverage check
            var coverage = ctx.Exploration.ActiveBlobCoverage;
            if (coverage < ctx.Settings.Mapping.MinCoverage.Value)
                return MapCompletionStatus.Incomplete;

            // 5. Any unexplored regions left?
            var nextTarget = ctx.Exploration.GetNextExplorationTarget(playerGrid);
            if (nextTarget.HasValue)
                return MapCompletionStatus.Incomplete;

            return MapCompletionStatus.FullyExplored;
        }

        /// <summary>
        /// Check if all ExitAfter mechanics are tile-detectable AND absent from the tile scan.
        /// If so, this map definitely doesn't have them — no need to explore further.
        /// Returns false if any ExitAfter mechanic is either not tile-detectable or is present in tiles.
        /// </summary>
        private bool AreExitMechanicsAbsentFromTiles(BotContext ctx)
        {
            bool anyExitAfter = false;
            var tileScan = ctx.TileScan;
            if (tileScan == null) return false;

            foreach (var mechName in new[] { "Ultimatum", "Harvest", "Wishes", "Essence", "Ritual" })
            {
                var exitAfter = mechName switch
                {
                    "Ultimatum" => ctx.Settings.Mechanics.Ultimatum.ExitAfter.Value,
                    "Harvest" => ctx.Settings.Mechanics.Harvest.ExitAfter.Value,
                    "Wishes" => ctx.Settings.Mechanics.Wishes.ExitAfter.Value,
                    "Essence" => ctx.Settings.Mechanics.Essence.ExitAfter.Value,
                    "Ritual" => ctx.Settings.Mechanics.Ritual.ExitAfter.Value,
                    _ => false,
                };
                if (!exitAfter) continue;
                anyExitAfter = true;

                // If this mechanic is NOT tile-detectable, we can't confirm absence
                if (!TileScanner.TileDetectableMechanics.Contains(mechName))
                    return false;

                // If it IS tile-detectable and IS present → not absent
                if (tileScan.DetectedMechanics.ContainsKey(mechName))
                    return false;
            }

            // Only return true if there were ExitAfter mechanics and all were absent
            return anyExitAfter;
        }

        /// <summary>
        /// Find the nearest clickable interactable within detection radius, filtered by settings.
        /// Optional interactables use a short radius (click when passing by).
        /// Required interactables use the full detect radius (route toward).
        /// </summary>
        private ExileCore.PoEMemory.MemoryObjects.Entity? FindNearbyInteractable(
            GameController gc, Vector2 playerGrid, BotSettings.InteractableSettings settings)
        {
            ExileCore.PoEMemory.MemoryObjects.Entity? best = null;
            float bestDist = float.MaxValue;

            float RadiusFor(ListNode setting) =>
                settings.IsRequired(setting) ? InteractableDetectRadius : InteractableOptionalRadius;

            // Shrines
            if (settings.IsEnabled(settings.Shrines))
            {
                var radius = RadiusFor(settings.Shrines);
                foreach (var entity in gc.EntityListWrapper.ValidEntitiesByType[EntityType.Shrine])
                {
                    if (!entity.IsTargetable) continue;
                    if (_failedInteractables.Contains(entity.Id)) continue;
                    if (!entity.TryGetComponent<Shrine>(out var shrine) || !shrine.IsAvailable) continue;

                    var dist = Vector2.Distance(playerGrid, new Vector2(entity.GridPosNum.X, entity.GridPosNum.Y));
                    if (dist < bestDist && dist <= radius)
                    {
                        bestDist = dist;
                        best = entity;
                    }
                }
            }

            // Chests: strongboxes, heist caches, djinn caches
            foreach (var entity in gc.EntityListWrapper.ValidEntitiesByType[EntityType.Chest])
            {
                if (!entity.IsTargetable) continue;
                if (_failedInteractables.Contains(entity.Id)) continue;
                if (!entity.TryGetComponent<Chest>(out var chest) || chest.IsOpened) continue;

                // Determine which setting applies and check enabled
                ListNode? chestSetting = null;
                if (chest.IsStrongbox)
                    chestSetting = settings.Strongboxes;
                else if (entity.Path.Contains("LeagueHeist/HeistSmuggler"))
                    chestSetting = settings.HeistCaches;
                else if (entity.Path.Contains("Chests/Faridun/"))
                    chestSetting = settings.DjinnCaches;

                // Must be a known type and enabled
                if (chestSetting == null || !settings.IsEnabled(chestSetting))
                    continue;

                var radius = RadiusFor(chestSetting);
                var dist = Vector2.Distance(playerGrid, new Vector2(entity.GridPosNum.X, entity.GridPosNum.Y));
                if (dist < bestDist && dist <= radius)
                {
                    bestDist = dist;
                    best = entity;
                }
            }

            // IngameIcon interactables (crafting recipes, memory tears)
            bool wantRecipes = settings.IsEnabled(settings.CraftingRecipes);
            bool wantMemoryTears = settings.IsEnabled(settings.MemoryTears);
            if (wantRecipes || wantMemoryTears)
            {
                foreach (var entity in gc.EntityListWrapper.ValidEntitiesByType[EntityType.IngameIcon])
                {
                    if (!entity.IsTargetable) continue;
                    if (_failedInteractables.Contains(entity.Id)) continue;

                    ListNode? iconSetting = null;
                    if (wantRecipes && entity.Path.Contains("CraftingUnlocks/RecipeUnlock"))
                        iconSetting = settings.CraftingRecipes;
                    else if (wantMemoryTears && entity.Path.Contains("MapAtlasMemory/"))
                        iconSetting = settings.MemoryTears;

                    if (iconSetting == null) continue;

                    var radius = RadiusFor(iconSetting);
                    var dist = Vector2.Distance(playerGrid, new Vector2(entity.GridPosNum.X, entity.GridPosNum.Y));
                    if (dist < bestDist && dist <= radius)
                    {
                        bestDist = dist;
                        best = entity;
                    }
                }
            }

            return best;
        }

        /// <summary>
        /// Check if a minimap icon's interactable entity is already claimed/opened.
        /// Searches nearby entities matching the icon's path. Only reliable when close
        /// (entities in network bubble range).
        /// </summary>
        private static bool IsInteractableAlreadyClaimed(GameController gc, BotCore.MinimapIconEntry icon)
        {
            foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
            {
                if (entity.Path != icon.Path) continue;
                var entityGrid = new Vector2(entity.GridPosNum.X, entity.GridPosNum.Y);
                if (Vector2.Distance(entityGrid, icon.GridPos) > 15) continue; // same location

                if (!entity.IsTargetable) return true;
                if (entity.TryGetComponent<Shrine>(out var shrine) && !shrine.IsAvailable) return true;
                if (entity.TryGetComponent<Chest>(out var chest) && chest.IsOpened) return true;
                return false; // found matching entity, it's still claimable
            }
            return false; // entity not loaded yet — don't mark as claimed
        }

        /// <summary>
        /// Check if an interactable has been successfully activated (shrine claimed or chest opened).
        /// </summary>
        private static bool IsInteractableDone(ExileCore.PoEMemory.MemoryObjects.Entity entity)
        {
            if (entity.TryGetComponent<Shrine>(out var shrine))
                return !shrine.IsAvailable;
            if (entity.TryGetComponent<Chest>(out var chest))
                return chest.IsOpened;
            return !entity.IsTargetable;
        }

        /// <summary>
        /// Find an entity by ID from the valid entity list.
        /// </summary>
        private static ExileCore.PoEMemory.MemoryObjects.Entity? FindEntityById(GameController gc, long entityId)
        {
            foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
            {
                if (entity.Id == entityId) return entity;
            }
            return null;
        }

        /// <summary>
        /// Check if a targetable SekhemaPortal entity exists (the actual exit portal in wish zones).
        /// Minimap icon entities also contain "SekhemaPortal" in their path but are NOT targetable.
        /// Only the real portal entity in the wish zone is targetable.
        /// </summary>
        private static bool HasTargetableSekhemaPortal(GameController gc)
        {
            foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
            {
                if (entity.Path != null && entity.Path.Contains("SekhemaPortal") && entity.IsTargetable)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Reinitialize exploration for a new zone (sub-zone entry).
        /// Wish zones have the same area name but different terrain — must rebuild the grid.
        /// </summary>
        private void ReinitExplorationForNewZone(BotContext ctx, GameController gc)
        {
            var pfGrid = gc.IngameState?.Data?.RawPathfindingData;
            var tgtGrid = gc.IngameState?.Data?.RawTerrainTargetingData;
            if (pfGrid != null && gc.Player != null)
            {
                var playerGrid = new Vector2(gc.Player.GridPosNum.X, gc.Player.GridPosNum.Y);
                ctx.Exploration.EnterNewBlob(pfGrid, tgtGrid, playerGrid,
                    ctx.Settings.Build.BlinkRange.Value);
                ctx.Log($"[Mapping] Reinitialized exploration for sub-zone: {ctx.Exploration.TotalWalkableCells} cells");
            }
        }

        /// <summary>
        /// Find a return/exit portal in the current zone (e.g., SekhemaPortal in wish zones).
        /// Requires minimum time in zone AND no nearby combat before allowing exit.
        /// </summary>
        private Entity? FindExitPortal(BotContext ctx, GameController gc, CombatSystem combat, string caller = "unknown")
        {
            var elapsed = (DateTime.Now - _zoneEnteredAt).TotalSeconds;
            var coverage = ctx.Exploration.ActiveBlobCoverage;

            // Don't exit sub-zones too quickly — need time to fight and loot
            if (elapsed < MinZoneSecondsBeforeExit)
            {
                ctx.Log($"[ExitPortal] ({caller}) Blocked: zone time {elapsed:F1}s < {MinZoneSecondsBeforeExit}s, coverage={coverage:P1}");
                return null;
            }

            // Don't exit while monsters are still nearby
            if (combat.NearbyMonsterCount > 0)
            {
                ctx.Log($"[ExitPortal] ({caller}) Blocked: {combat.NearbyMonsterCount} monsters nearby, zoneTime={elapsed:F1}s");
                return null;
            }

            foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
            {
                if (entity.Path == null) continue;
                if (!entity.IsTargetable) continue;
                if (entity.Path.Contains("SekhemaPortal"))
                {
                    ctx.Log($"[ExitPortal] ({caller}) FOUND SekhemaPortal id={entity.Id} dist={entity.DistancePlayer:F0} zoneTime={elapsed:F1}s coverage={coverage:P1}");
                    return entity;
                }
            }
            return null;
        }

        /// <summary>
        /// Navigate to and click the exit portal to return to the parent map.
        /// </summary>
        private void TickExitPortal(BotContext ctx, GameController gc)
        {
            // Refresh entity reference
            if (_exitPortal != null)
            {
                var refreshed = FindEntityById(gc, _exitPortal.Id);
                if (refreshed != null) _exitPortal = refreshed;
            }

            if (_exitPortal == null || !_exitPortal.IsTargetable)
            {
                // Portal gone — area change should have fired
                Status = "Exit portal gone — waiting for area change";
                return;
            }

            var playerGrid = new Vector2(gc.Player.GridPosNum.X, gc.Player.GridPosNum.Y);
            var portalGrid = new Vector2(_exitPortal.GridPosNum.X, _exitPortal.GridPosNum.Y);
            var dist = Vector2.Distance(playerGrid, portalGrid);

            if (dist > ctx.Interaction.InteractRadius)
            {
                // Navigate to portal
                ctx.Navigation.NavigateTo(gc, portalGrid);
                Status = $"Exiting — walking to portal ({dist:F0}g)";
                return;
            }

            // Close enough — click it
            if (!BotInput.CanAct) return;
            if ((DateTime.Now - _lastExitClickTime).TotalMilliseconds < 500) return;

            if (BotInput.ClickEntity(gc, _exitPortal))
            {
                _lastExitClickTime = DateTime.Now;
                Status = "Exiting — clicked portal";
            }
        }

        public void Render(BotContext ctx)
        {
            if (_phase == MappingPhase.Idle) return;

            var g = ctx.Graphics;
            var gc = ctx.Game;
            if (g == null || gc?.Player == null || !gc.InGame) return;

            var cam = gc.IngameState.Camera;
            var playerGrid = gc.Player.GridPosNum;
            var blob = ctx.Exploration.ActiveBlob;

            // ═══ HUD Panel ═══
            var hudX = 20f;
            var hudY = 200f;
            var lineH = 18f;

            var phaseColor = _phase switch
            {
                MappingPhase.InHideout => SharpDX.Color.CornflowerBlue,
                MappingPhase.Fighting => SharpDX.Color.Red,
                MappingPhase.Looting => SharpDX.Color.LimeGreen,
                MappingPhase.Exploring => SharpDX.Color.Cyan,
                MappingPhase.Complete => SharpDX.Color.LimeGreen,
                MappingPhase.Exiting => SharpDX.Color.Magenta,
                MappingPhase.ExitMap => SharpDX.Color.Orange,
                MappingPhase.Paused => SharpDX.Color.Yellow,
                _ => SharpDX.Color.White,
            };

            var elapsed = (DateTime.Now - _startTime).TotalSeconds;
            var mins = (int)(elapsed / 60);
            var secs = (int)(elapsed % 60);
            g.DrawText($"MAPPING  {_phase}  {mins}:{secs:D2}", new Vector2(hudX, hudY), phaseColor);
            hudY += lineH;

            // Coverage bar
            if (blob != null)
            {
                var coverage = blob.Coverage;
                var barWidth = 200f;
                var barHeight = 12f;

                g.DrawBox(new SharpDX.RectangleF(hudX, hudY, barWidth, barHeight),
                    new SharpDX.Color(40, 40, 40, 200));
                var fillWidth = barWidth * Math.Min(coverage, 1f);
                var minCov = ctx.Settings.Mapping.MinCoverage.Value;
                var fillColor = coverage >= minCov
                    ? new SharpDX.Color(0, 200, 0, 200)
                    : new SharpDX.Color(200, 200, 0, 200);
                g.DrawBox(new SharpDX.RectangleF(hudX, hudY, fillWidth, barHeight), fillColor);
                var targetX = hudX + barWidth * minCov;
                g.DrawLine(new Vector2(targetX, hudY), new Vector2(targetX, hudY + barHeight), 2f, SharpDX.Color.White);
                g.DrawText($"{coverage:P0}",
                    new Vector2(hudX + barWidth + 8, hudY - 1), SharpDX.Color.White);
                hudY += barHeight + 4;
            }

            // Status + decision (one line each, only when meaningful)
            if (!string.IsNullOrEmpty(Status))
            {
                g.DrawText(Status, new Vector2(hudX, hudY), SharpDX.Color.White);
                hudY += lineH;
            }

            // Combat (inline)
            if (ctx.Combat.InCombat)
            {
                g.DrawText($"Combat: {ctx.Combat.NearbyMonsterCount} nearby  {ctx.Combat.LastSkillAction}",
                    new Vector2(hudX, hudY), SharpDX.Color.Red);
                hudY += lineH;
            }

            // Loot
            if (ctx.Loot.LootableCount > 0)
            {
                g.DrawText($"Loot: {ctx.Loot.LootableCount} items", new Vector2(hudX, hudY), SharpDX.Color.LimeGreen);
                hudY += lineH;
            }


            // Priority target
            if (_priorityTarget.HasValue)
            {
                g.DrawText($"Priority: {_priorityTargetReason}", new Vector2(hudX, hudY), SharpDX.Color.Yellow);
                hudY += lineH;
            }

            // Mechanics (compact: active + completed counts)
            if (ctx.Mechanics.ActiveMechanic != null)
            {
                g.DrawText($"Mechanic: {ctx.Mechanics.ActiveMechanic.Name} — {ctx.Mechanics.ActiveMechanic.Status}",
                    new Vector2(hudX, hudY), SharpDX.Color.Orange);
                hudY += lineH;
            }
            var completedCount = ctx.Mechanics.CompletionCounts.Values.Sum();
            if (completedCount > 0)
            {
                var mechList = string.Join(", ", ctx.Mechanics.CompletionCounts
                    .Select(kv => kv.Value > 1 ? $"{kv.Key} x{kv.Value}" : kv.Key));
                g.DrawText($"Done: {mechList}", new Vector2(hudX, hudY), SharpDX.Color.DarkGreen);
                hudY += lineH;
            }

            // Strategy state — show Required mechanic progress with tile intelligence
            var stratParts = new List<string>();
            foreach (var mechName in new[] { "Ultimatum", "Ritual", "Harvest", "Wishes" })
            {
                var modeSetting = mechName switch
                {
                    "Ultimatum" => ctx.Settings.Mechanics.Ultimatum.Mode,
                    "Ritual" => ctx.Settings.Mechanics.Ritual.Mode,
                    "Harvest" => ctx.Settings.Mechanics.Harvest.Mode,
                    "Wishes" => ctx.Settings.Mechanics.Wishes.Mode,
                    _ => null,
                };
                if (modeSetting?.Value != "Required") continue;

                if (ctx.Mechanics.GetCompletionCount(mechName) > 0)
                    stratParts.Add($"\u2714 {mechName}"); // ✔ done
                else if (_tileMechanicTarget.HasValue && _tileMechanicName == mechName)
                    stratParts.Add($"\u2192 {mechName} [tile]"); // → rushing via tile
                else if (_mechanicRushTarget.HasValue && (IconToMechanicName(_mechanicRushName) == mechName || _mechanicRushName == mechName))
                    stratParts.Add($"\u2192 {mechName}"); // → rushing via icon
                else if (ctx.TileScan?.DetectedMechanics.ContainsKey(mechName) == true)
                    stratParts.Add($"! {mechName} (unreachable)"); // detected but can't path
                else if (_phase != MappingPhase.InHideout && _tileScanProcessed &&
                         TileScanner.TileDetectableMechanics.Contains(mechName))
                    stratParts.Add($"\u2717 {mechName} (absent)"); // ✗ not in this map
                else
                    stratParts.Add($"? {mechName}"); // unknown or in hideout
            }
            if (stratParts.Count > 0)
            {
                g.DrawText($"Strategy: {string.Join("  ", stratParts)}", new Vector2(hudX, hudY), SharpDX.Color.Yellow);
                hudY += lineH;
            }

            // Mechanic rush target
            if (_mechanicRushTarget.HasValue)
            {
                var rushDist = Vector2.Distance(playerGrid, _mechanicRushTarget.Value);
                g.DrawText($"Rush: {_mechanicRushName} ({rushDist:F0}g)", new Vector2(hudX, hudY), SharpDX.Color.Orange);
                hudY += lineH;
            }

            // ═══ World Overlays ═══

            // Nav target marker
            if (_navTarget.HasValue && ctx.Navigation.IsNavigating)
            {
                var targetWorld = Pathfinding.GridToWorld3D(gc, _navTarget.Value);
                var targetScreen = cam.WorldToScreen(targetWorld);
                if (targetScreen.X > -200 && targetScreen.X < 2400)
                {
                    var markerColor = _priorityTarget.HasValue ? SharpDX.Color.Yellow : SharpDX.Color.Cyan;
                    var markerLabel = _priorityTarget.HasValue ? "PRIORITY" : "NAV";
                    g.DrawCircleInWorld(targetWorld, 30f, markerColor, 2f);
                    g.DrawText(markerLabel, targetScreen + new Vector2(-20, -25), markerColor);
                }
            }

            // Navigation path
            if (ctx.Navigation.IsNavigating)
            {
                var path = ctx.Navigation.CurrentNavPath;
                for (int i = ctx.Navigation.CurrentWaypointIndex; i < path.Count - 1; i++)
                {
                    var from = Pathfinding.GridToScreen(gc, path[i].Position);
                    var to = Pathfinding.GridToScreen(gc, path[i + 1].Position);
                    if (from.X < -200 || from.X > 2400 || to.X < -200 || to.X > 2400) continue;

                    var isBlink = path[i + 1].Action == WaypointAction.Blink;
                    var pathColor = isBlink ? SharpDX.Color.Magenta : SharpDX.Color.Orange;
                    g.DrawLine(from, to, isBlink ? 3f : 2f, pathColor);
                }
            }

            // Mechanic rush target world marker
            if (_mechanicRushTarget.HasValue)
            {
                var rushWorld = Pathfinding.GridToWorld3D(gc, _mechanicRushTarget.Value);
                g.DrawCircleInWorld(rushWorld, 50f, SharpDX.Color.Orange, 3f);
                var rushScreen = cam.WorldToScreen(rushWorld);
                if (rushScreen.X > -200 && rushScreen.X < 2400)
                    g.DrawText(_mechanicRushName, rushScreen + new Vector2(-25, -25), SharpDX.Color.Orange);
            }

            // Active mechanic overlay
            if (ctx.Mechanics.ActiveMechanic != null)
                ctx.Mechanics.ActiveMechanic.Render(ctx);
        }
    }
}
