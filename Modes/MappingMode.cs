using ExileCore;
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
        private float _minCoverage = 0.70f;

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
        private const float ZoneSettleSeconds = 3f;

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

        // ── Boss rush state ──
        private Vector2? _bossTarget;          // grid position of boss room tile
        private bool _bossRushComplete;        // boss rush done, switch to normal explore
        private bool _bossTargetResolved;      // true after we've attempted to look up boss tiles
        private string _bossRushDebug = "";    // debug info for overlay

        // ── Boss kill verification ──
        private bool _bossKillVerified;              // boss confirmed dead
        private List<Vector2>? _bossKillCheckPositions; // boss tile grid positions to check
        private DateTime? _bossAreaArrivalTime;      // when player entered boss radius
        private const float BossAreaDwellSeconds = 3f; // wait for entities to load

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
            RushingBoss, // navigating to boss room, fighting on the way but not stopping
            Exploring,
            Fighting,
            Looting,
            Paused,
            Exiting,    // navigating to and clicking exit portal (wish zones)
            ExitMap,    // opening portal → clicking it → back to hideout
            Complete,
        }

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
            Decision = "";

            var gc = ctx.Game;
            _lastAreaName = gc.Area?.CurrentArea?.Name ?? "";

            // Hideout/town → start hideout flow
            if (gc.Area.CurrentArea.IsHideout || gc.Area.CurrentArea.IsTown)
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

            ModeHelpers.EnableDefaultCombat(ctx);

            // Cache spawn position for fallback portal exit
            if (gc.Player != null)
                _spawnPortalPos = new Vector2(gc.Player.GridPosNum.X, gc.Player.GridPosNum.Y);

            // Boss rush — deferred to first Tick since TileMap may not be loaded yet at OnEnter time
            _bossTarget = null;
            _bossRushComplete = false;
            _bossTargetResolved = false;
            _bossRushDebug = "";

            var rushBossEnabled = ctx.Settings.Mapping.RushBoss.Value;
            ctx.Log($"[Mapping] InitMapState: rushBoss={rushBossEnabled} tileMapLoaded={ctx.TileMap.IsLoaded} area={gc.Area?.CurrentArea?.Name}");

            if (rushBossEnabled)
            {
                _phase = MappingPhase.RushingBoss;
                Status = "Waiting for tile data to resolve boss position...";
                _bossRushDebug = "Pending tile map load";
            }
            else
            {
                _phase = MappingPhase.Exploring;
                Status = "Started";
            }

            ctx.Log($"Mapping — {ctx.Exploration.TotalWalkableCells} cells, " +
                    $"{ctx.Exploration.ActiveBlob?.Regions.Count ?? 0} regions");
        }

        public void OnExit()
        {
            _phase = MappingPhase.Idle;
            _navTarget = null;
            _bossTarget = null;
            _bossRushComplete = false;
            _bossTargetResolved = false;
            _bossRushDebug = "";
            _bossKillVerified = false;
            _bossKillCheckPositions = null;
            _bossAreaArrivalTime = null;
            _priorityTarget = null;
            _priorityTargetReason = "";
            _visitedIconIds.Clear();
            _pendingMechanic = null;
            _mechanicActive = false;
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
            _hideoutFlow.Start(MapDeviceSystem.IsStandardMap, ShouldStashItem,
                targetMapName: mapName, minMapTier: minTier);
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
                    _exploreTargetsVisited = 0;
                    _navFailures = 0;
                    _bossKillCheckPositions = null;
                    _bossAreaArrivalTime = null;
                    _visitedIconIds.Clear();

                    // Start settle timer — block actions until terrain/entities load
                    _zoneSettleUntil = DateTime.Now.AddSeconds(ZoneSettleSeconds);
                    _zoneSettleExplorationDone = false;
                    _phase = MappingPhase.Exploring;
                    Status = "Zone transition — settling...";

                    ctx.Log($"[Mapping] Zone settle started ({ZoneSettleSeconds}s)");
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

                // Boss rush only in parent map, not sub-zones
                if (!_isInSubZone && ctx.Settings.Mapping.RushBoss.Value && !_bossRushComplete)
                {
                    _bossTarget = null;
                    _bossTargetResolved = false;
                    _bossRushDebug = "New zone — re-resolving boss target";
                    _phase = MappingPhase.RushingBoss;
                    ctx.Log("[Mapping] Post-settle — re-triggering boss rush");
                }
                else
                {
                    _phase = MappingPhase.Exploring;
                }
                ModeHelpers.EnableDefaultCombat(ctx);
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

            // ── Boss Rush (navigate to boss, fight on the way, don't stop) ──
            if (_phase == MappingPhase.RushingBoss)
            {
                // Lazy resolve: TileMap may not have been loaded when OnEnter ran
                if (!_bossTargetResolved)
                {
                    if (!ctx.TileMap.IsLoaded)
                    {
                        Status = "Rush boss — waiting for tile map...";
                        _bossRushDebug = "TileMap not loaded yet";
                        return;
                    }

                    _bossTargetResolved = true;
                    var areaName = gc.Area?.CurrentArea?.Name ?? "";
                    var bossTiles = ctx.MapDatabase.GetBossTiles(areaName);

                    if (bossTiles == null || bossTiles.Count == 0)
                    {
                        _bossRushDebug = $"No boss tiles in database for '{areaName}'";
                        ctx.Log($"[Mapping] Boss rush: {_bossRushDebug}");
                        _phase = MappingPhase.Exploring;
                        ModeHelpers.EnableDefaultCombat(ctx);
                        Decision = _bossRushDebug;
                    }
                    else
                    {
                        // Find the first boss tile key that has positions in the tile map
                        foreach (var tileKey in bossTiles)
                        {
                            var positions = ctx.TileMap.GetPositions(tileKey);
                            if (positions != null && positions.Count > 0)
                            {
                                // Tile center = gridPos + half tile (23/2 ≈ 11.5)
                                _bossTarget = positions
                                    .OrderBy(p => Vector2.Distance(playerGrid, p))
                                    .First() + new Vector2(11.5f, 11.5f);
                                _bossRushDebug = $"Found '{tileKey}' at ({_bossTarget.Value.X:F0}, {_bossTarget.Value.Y:F0})";
                                break;
                            }
                            else
                            {
                                _bossRushDebug = $"Tile key '{tileKey}' not found in TileMap ({ctx.TileMap.TileCount} tiles)";
                            }
                        }

                        if (_bossTarget.HasValue)
                        {
                            ctx.Combat.SetProfile(new CombatProfile { Enabled = true, Positioning = CombatPositioning.Aggressive });
                            ctx.Log($"[Mapping] Boss rush: {_bossRushDebug}");
                        }
                        else
                        {
                            ctx.Log($"[Mapping] Boss rush failed: {_bossRushDebug}");
                            _phase = MappingPhase.Exploring;
                            ModeHelpers.EnableDefaultCombat(ctx);
                            Decision = $"Boss rush failed: {_bossRushDebug}";
                        }
                    }

                    if (_phase != MappingPhase.RushingBoss) return;
                }

                if (_bossTarget.HasValue)
                {
                    var distToBoss = Vector2.Distance(playerGrid, _bossTarget.Value);

                    // Arrived at boss area (within 2 tiles = 46 grid units)
                    if (distToBoss < 46)
                    {
                        _bossRushComplete = true;
                        _bossTarget = null;
                        _phase = MappingPhase.Exploring;
                        ModeHelpers.EnableDefaultCombat(ctx);
                        Decision = "Boss area reached — switching to explore+clear";
                        _bossRushDebug = "Complete — arrived at boss area";
                        ctx.Log("Boss rush complete — switching to normal exploration");
                    }
                    else
                    {
                        if (!ctx.Navigation.IsNavigating)
                        {
                            ctx.Log($"[BossRush] Starting nav to ({_bossTarget.Value.X:F0}, {_bossTarget.Value.Y:F0}) dist={distToBoss:F0}");
                            var success = ctx.Navigation.NavigateTo(ctx.Game, _bossTarget.Value);
                            if (!success)
                            {
                                ctx.Log($"[BossRush] NavigateTo failed — no path");
                                _bossRushComplete = true;
                                _bossTarget = null;
                                _phase = MappingPhase.Exploring;
                                ModeHelpers.EnableDefaultCombat(ctx);
                                Decision = "Can't path to boss — exploring normally";
                                _bossRushDebug = "Failed — no path to boss";
                            }
                            else
                            {
                                ctx.Log($"[BossRush] Nav started, waypoints={ctx.Navigation.CurrentNavPath?.Count ?? 0}");
                            }
                        }
                        Status = $"Rushing boss — dist: {distToBoss:F0} nav={ctx.Navigation.IsNavigating}";
                        Decision = $"Rush boss ({distToBoss:F0} away, nav={ctx.Navigation.IsNavigating})";
                        return; // Skip loot, mechanics, explore — just rush
                    }
                }
            }

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
                    _zoneSettleUntil = DateTime.Now.AddSeconds(ZoneSettleSeconds);
                    _zoneSettleExplorationDone = false;
                    _phase = MappingPhase.Exploring;
                    var entryHash = gc.IngameState?.Data?.CurrentAreaHash ?? 0;
                    Status = $"Entered wish zone — settling... hash={entryHash} parent={_parentZoneHash}";
                    ctx.Log($"[Mapping] Wish zone settle started ({ZoneSettleSeconds}s) hash={entryHash} parent={_parentZoneHash}");
                    return;
                }

                // Check if ExitAfter mechanics are all done → exit map
                if (result == MechanicResult.Complete && !_isInSubZone &&
                    ctx.Mechanics.AreExitMechanicsComplete(ctx.Settings) &&
                    !ctx.Mechanics.HasPendingMechanics())
                {
                    ctx.Log("[Mapping] All ExitAfter mechanics complete — exiting map");
                    EnterExitMap(ctx, gc);
                    return;
                }
                // Fall through to normal loot/explore
            }

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

            // ── Boss kill verification (runs every tick when in boss area) ──
            if (ctx.Settings.Mapping.KillBoss.Value && !_bossKillVerified)
                TickBossKillCheck(ctx, gc, playerGrid);

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
                // Priority 2: Boss kill — navigate to boss area if not yet verified
                else if (ctx.Settings.Mapping.KillBoss.Value && !_bossKillVerified && _bossKillCheckPositions != null)
                {
                    var nearest = _bossKillCheckPositions
                        .OrderBy(p => Vector2.Distance(playerGrid, p))
                        .First();
                    var dist = Vector2.Distance(playerGrid, nearest);
                    if (dist > ctx.Settings.Mapping.BossKillRadius.Value)
                    {
                        NavigateToTarget(ctx, gc, playerGrid, nearest);
                        Decision = $"Boss kill: navigating to boss area ({dist:F0}g away)";
                    }
                }
                // Priority 3: Normal exploration
                else if (coverage >= _minCoverage)
                {
                    // Check if outstanding requirements prevent completion
                    bool needsBossKill = ctx.Settings.Mapping.KillBoss.Value && !_bossKillVerified;

                    var nextTarget = ctx.Exploration.GetNextExplorationTarget(playerGrid);
                    if (nextTarget.HasValue)
                    {
                        NavigateToTarget(ctx, gc, playerGrid, nextTarget.Value);
                    }
                    else if (needsBossKill)
                    {
                        // Exploration exhausted but boss not killed — keep waiting near boss area
                        Status = $"Waiting for boss kill — coverage {coverage:P1}";
                    }
                    else
                    {
                        // Truly complete — check for exit portal (sub-zone) or exit map
                        var zoneTime = (DateTime.Now - _zoneEnteredAt).TotalSeconds;
                        ctx.Log($"[Mapping] Explore complete path: coverage={coverage:P1} zoneTime={zoneTime:F1}s noTarget=true noBoss=true subZone={_isInSubZone}");
                        var exitPortal = FindExitPortal(ctx, gc, ctx.Combat, "explore-complete");
                        if (exitPortal != null)
                        {
                            _exitPortal = exitPortal;
                            _phase = MappingPhase.Exiting;
                            var elapsed = (DateTime.Now - _startTime).TotalSeconds;
                            Status = $"Explored {coverage:P1} in {elapsed:F0}s — using exit portal";
                            Decision = $"EXIT: explore-complete coverage={coverage:P1} zoneTime={zoneTime:F0}s subZone={_isInSubZone}";
                            ctx.Log($"[Mapping] Exploration complete, found exit portal — returning to parent map");
                        }
                        else if (!_isInSubZone)
                        {
                            // In parent map — exit via portal scroll
                            ctx.Log("[Mapping] Exploration complete — exiting map");
                            EnterExitMap(ctx, gc);
                        }
                        else
                        {
                            _phase = MappingPhase.Complete;
                            var elapsed = (DateTime.Now - _startTime).TotalSeconds;
                            Status = $"COMPLETE — {coverage:P1} coverage in {elapsed:F0}s";
                            Decision = $"{ctx.Exploration.FailedRegions.Count} unreachable regions";
                        }
                    }
                }
                else
                {
                    // Under coverage threshold — check for priority targets mixed with exploration
                    NavigateToNextExploreTarget(ctx, gc, playerGrid);
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
                    var elapsed = (DateTime.Now - _startTime).TotalSeconds;
                    var zoneTime = (DateTime.Now - _zoneEnteredAt).TotalSeconds;
                    var blob = ctx.Exploration.ActiveBlob;
                    var regionCount = blob?.Regions.Count ?? 0;
                    var failedCount = ctx.Exploration.FailedRegions.Count;
                    var totalCells = blob?.WalkableCells.Count ?? 0;
                    var seenCells = blob?.SeenCells.Count ?? 0;
                    ctx.Log($"[Mapping] No explore target: coverage={coverage:P1} zoneTime={zoneTime:F1}s regions={regionCount} failed={failedCount} cells={seenCells}/{totalCells} attempt={attempt}");

                    // Only consider exit portals after meaningful exploration (>10% coverage).
                    // Prevents immediately exiting wish zones / sub-zones before exploring them
                    // (exploration can return null briefly on fresh zone init).
                    var exitPortal = coverage > 0.10f ? FindExitPortal(ctx, gc, ctx.Combat, "no-targets") : null;
                    if (exitPortal != null)
                    {
                        _exitPortal = exitPortal;
                        _phase = MappingPhase.Exiting;
                        Status = $"Explored {coverage:P1} in {elapsed:F0}s — using exit portal";
                        Decision = $"EXIT: no-targets coverage={coverage:P1} zoneTime={zoneTime:F0}s subZone={_isInSubZone}";
                        ctx.Log($"[Mapping] No more targets, found exit portal — returning");
                    }
                    else if (coverage > 0.10f && !_isInSubZone)
                    {
                        ctx.Log("[Mapping] No more targets — exiting map");
                        EnterExitMap(ctx, gc);
                    }
                    else if (coverage > 0.10f)
                    {
                        _phase = MappingPhase.Complete;
                        Status = $"COMPLETE — {coverage:P1} coverage in {elapsed:F0}s";
                        Decision = $"No more targets ({ctx.Exploration.FailedRegions.Count} unreachable)";
                    }
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

                if (_mapCompleted)
                {
                    // Map done — start new cycle
                    _mapCompleted = false;
                    _phase = MappingPhase.InHideout;
                    StartMappingHideoutFlow(ctx);
                    Status = "Back in hideout — starting new map";
                    ctx.Log("[Mapping] Map completed, starting new hideout flow");
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
                _bossKillVerified = false;
                _bossKillCheckPositions = null;
                _bossAreaArrivalTime = null;
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
        // Boss Kill Verification
        // =================================================================

        /// <summary>
        /// Resolve boss tile positions from MapDatabase. Called lazily on first check.
        /// </summary>
        private void ResolveBossKillPositions(BotContext ctx, GameController gc)
        {
            if (_bossKillCheckPositions != null) return;
            _bossKillCheckPositions = new List<Vector2>();

            var areaName = gc.Area?.CurrentArea?.Name ?? "";
            var bossTiles = ctx.MapDatabase.GetBossTiles(areaName);
            if (bossTiles == null || bossTiles.Count == 0) return;

            foreach (var tileKey in bossTiles)
            {
                var positions = ctx.TileMap.GetPositions(tileKey);
                if (positions != null)
                {
                    foreach (var p in positions)
                        _bossKillCheckPositions.Add(p + new Vector2(11.5f, 11.5f)); // tile center
                }
            }

            if (_bossKillCheckPositions.Count > 0)
                ctx.Log($"[BossKill] Resolved {_bossKillCheckPositions.Count} boss check positions");
        }

        /// <summary>
        /// Check if the boss has been killed. Runs each tick when KillBoss is enabled.
        /// Logic: player is within boss radius + dwell time elapsed + no alive unique monsters in radius.
        /// </summary>
        private void TickBossKillCheck(BotContext ctx, GameController gc, Vector2 playerGrid)
        {
            if (_bossKillVerified) return;

            // Lazy resolve boss positions
            if (!ctx.TileMap.IsLoaded) return;
            ResolveBossKillPositions(ctx, gc);
            if (_bossKillCheckPositions == null || _bossKillCheckPositions.Count == 0)
            {
                // No boss data — can't verify, treat as verified to not block completion
                _bossKillVerified = true;
                ctx.Log("[BossKill] No boss tile data — skipping verification");
                return;
            }

            float bossRadius = ctx.Settings.Mapping.BossKillRadius.Value;

            // Check if player is within radius of any boss tile
            bool inBossArea = false;
            foreach (var pos in _bossKillCheckPositions)
            {
                if (Vector2.Distance(playerGrid, pos) <= bossRadius)
                {
                    inBossArea = true;
                    break;
                }
            }

            if (!inBossArea)
            {
                _bossAreaArrivalTime = null;
                return;
            }

            // Start dwell timer
            if (!_bossAreaArrivalTime.HasValue)
            {
                _bossAreaArrivalTime = DateTime.Now;
                return;
            }

            // Wait for entities to load
            if ((DateTime.Now - _bossAreaArrivalTime.Value).TotalSeconds < BossAreaDwellSeconds)
                return;

            // Check for alive unique monsters in boss radius
            var bossEntry = ctx.MapDatabase.GetEntry(gc.Area?.CurrentArea?.Name ?? "");
            var bossPath = bossEntry?.BossEntityPath;

            foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
            {
                if (entity.Type != EntityType.Monster) continue;
                if (!entity.IsAlive || !entity.IsHostile) continue;
                if (entity.Rarity != MonsterRarity.Unique) continue;

                // Check if within boss radius
                bool nearBoss = false;
                foreach (var pos in _bossKillCheckPositions)
                {
                    if (Vector2.Distance(entity.GridPosNum, pos) <= bossRadius)
                    {
                        nearBoss = true;
                        break;
                    }
                }
                if (!nearBoss) continue;

                // If we have a specific boss path, only match that
                if (bossPath != null && !entity.Path.Contains(bossPath, StringComparison.OrdinalIgnoreCase))
                    continue;

                // Found alive unique in boss area — not killed yet
                return;
            }

            // No alive uniques in boss area — boss killed
            _bossKillVerified = true;
            ctx.Log("[BossKill] Boss kill verified — no alive uniques in boss area");
            Decision = "Boss killed!";
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

            // Crafting recipes (click to unlock)
            if (settings.IsEnabled(settings.CraftingRecipes))
            {
                var radius = RadiusFor(settings.CraftingRecipes);
                foreach (var entity in gc.EntityListWrapper.ValidEntitiesByType[EntityType.IngameIcon])
                {
                    if (!entity.IsTargetable) continue;
                    if (_failedInteractables.Contains(entity.Id)) continue;
                    if (!entity.Path.Contains("CraftingUnlocks/RecipeUnlock")) continue;

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
                MappingPhase.RushingBoss => SharpDX.Color.Gold,
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
                var fillColor = coverage >= _minCoverage
                    ? new SharpDX.Color(0, 200, 0, 200)
                    : new SharpDX.Color(200, 200, 0, 200);
                g.DrawBox(new SharpDX.RectangleF(hudX, hudY, fillWidth, barHeight), fillColor);
                var targetX = hudX + barWidth * _minCoverage;
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

            // Boss status (only when kill boss is enabled or rushing)
            if (_bossTarget.HasValue)
            {
                var dist = Vector2.Distance(playerGrid, _bossTarget.Value);
                g.DrawText($"Boss: {dist:F0}g away", new Vector2(hudX, hudY), SharpDX.Color.Gold);
                hudY += lineH;
            }
            else if (ctx.Settings.Mapping.KillBoss.Value)
            {
                var bossColor = _bossKillVerified ? SharpDX.Color.LimeGreen : SharpDX.Color.OrangeRed;
                g.DrawText(_bossKillVerified ? "Boss: KILLED" : "Boss: ALIVE", new Vector2(hudX, hudY), bossColor);
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

            // Boss target world marker
            if (_bossTarget.HasValue)
            {
                var bossWorld = Pathfinding.GridToWorld3D(gc, _bossTarget.Value);
                g.DrawCircleInWorld(bossWorld, 60f, SharpDX.Color.Gold, 3f);
                var bossScreen = cam.WorldToScreen(bossWorld);
                if (bossScreen.X > -200 && bossScreen.X < 2400)
                    g.DrawText("BOSS", bossScreen + new Vector2(-15, -20), SharpDX.Color.Gold);
            }

            // Active mechanic overlay
            if (ctx.Mechanics.ActiveMechanic != null)
                ctx.Mechanics.ActiveMechanic.Render(ctx);
        }
    }
}
