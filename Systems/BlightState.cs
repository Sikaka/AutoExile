using ExileCore;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using System.Numerics;

namespace AutoExile.Systems
{
    /// <summary>
    /// Tracks blight encounter state using entity events + per-tick dynamic updates.
    /// Entity lifecycle (add/remove) is handled by OnEntityAdded/OnEntityRemoved.
    /// Per-tick updates handle dynamic data (monster positions, pump StateMachine).
    ///
    /// All cached positions are in GRID coordinates (entity.GridPosNum).
    /// Convert to world via * Pathfinding.GridToWorld for NavigateTo/WorldToScreen.
    /// </summary>
    public class BlightState
    {
        // Pump tracking (cached as grid position + ID, not entity ref)
        public Vector2? PumpPosition { get; private set; }
        public long PumpEntityId { get; private set; }
        public bool IsPumpInRange { get; private set; }

        // Encounter state (derived from pump StateMachine)
        public bool IsEncounterActive { get; set; }
        public bool IsEncounterDone { get; private set; }
        public bool IsTimerDone { get; set; }
        public bool EncounterSucceeded { get; private set; }
        public DateTime? TimerDoneAt { get; set; }
        public DateTime? EncounterStartedAt { get; private set; }

        // Fast-forward
        public bool HasClickedFastForward { get; set; }

        // Countdown UI text
        public string CountdownText { get; private set; } = "";

        // Chest positions (grid coords, cleaned up via events)
        public HashSet<Vector2> ChestPositions { get; } = new();

        // --- Cached entity data (survives going off-screen) ---

        // Towers: cached with position/type/tier so we can navigate to off-screen towers
        public Dictionary<long, CachedTower> CachedTowers { get; } = new();
        public HashSet<long> FullyUpgradedTowerIds { get; } = new();

        // Foundations: cached with position and built state
        public Dictionary<long, CachedFoundation> CachedFoundations { get; } = new();

        // Monsters: last-known positions, assumed alive until confirmed dead
        public Dictionary<long, CachedMonster> CachedMonsters { get; } = new();

        // Legacy accessors (updated via events)
        public HashSet<long> KnownTowerEntityIds { get; } = new();

        // Lane tracker
        public BlightLaneTracker LaneTracker { get; private set; } = new();
        public string LaneDebug { get; private set; } = "";

        // Danger awareness
        public bool PumpUnderAttack { get; private set; }
        public int AliveMonsterCount { get; private set; }

        // Blight currency (read from UI each tick)
        public int Currency { get; private set; }

        // Portal tracking — cache position when first seen in map for exit navigation
        public Vector2? PortalPosition { get; set; }

        // Map completion tracking
        public bool MapComplete { get; set; }
        public int DeathCount { get; set; }

        // Debug diagnostics
        public string FoundationDebug { get; private set; } = "";

        // Timestamps for tower actions (used to trigger lane rescan)
        public DateTime LastTowerBuildAt { get; set; } = DateTime.MinValue;
        public DateTime LastTowerUpgradeAt { get; set; } = DateTime.MinValue;

        // Distance thresholds (grid units)
        private const float PumpRejectDistance = 92f;   // ~1000 world
        private static float RenderRange => Pathfinding.NetworkBubbleRadius;
        private const float PumpDangerRadius = 28f;     // ~300 world

        // Internal state
        private bool _wasEncounterActive;
        private bool _wasTimerRunning;
        private int _timerCheckTicks; // ticks since encounter active without ever seeing timer
        private DateTime _lastThreatUpdateAt = DateTime.MinValue;
        private DateTime _lastCoverageUpdateAt = DateTime.MinValue;
        private DateTime _prevTowerBuildAt = DateTime.MinValue;
        private DateTime _prevTowerUpgradeAt = DateTime.MinValue;

        // Chest ID→position mapping (needed for removal by event)
        private readonly Dictionary<long, Vector2> _chestEntityPositions = new();


        /// <summary>
        /// Reset all state for a new area/encounter.
        /// </summary>
        public void Reset()
        {
            PumpPosition = null;
            PumpEntityId = 0;
            IsPumpInRange = false;
            IsEncounterActive = false;
            IsEncounterDone = false;
            IsTimerDone = false;
            EncounterSucceeded = false;
            TimerDoneAt = null;
            EncounterStartedAt = null;
            HasClickedFastForward = false;
            CountdownText = "";
            ChestPositions.Clear();
            CachedTowers.Clear();
            CachedFoundations.Clear();
            CachedMonsters.Clear();
            KnownTowerEntityIds.Clear();
            FullyUpgradedTowerIds.Clear();
            _chestEntityPositions.Clear();
            LaneTracker = new BlightLaneTracker();
            LaneDebug = "";
            PumpUnderAttack = false;
            AliveMonsterCount = 0;
            LastTowerBuildAt = DateTime.MinValue;
            LastTowerUpgradeAt = DateTime.MinValue;
            _wasEncounterActive = false;
            _wasTimerRunning = false;
            _timerCheckTicks = 0;
            _lastThreatUpdateAt = DateTime.MinValue;
            _lastCoverageUpdateAt = DateTime.MinValue;
            _prevTowerBuildAt = DateTime.MinValue;
            _prevTowerUpgradeAt = DateTime.MinValue;
            PortalPosition = null;
            MapComplete = false;
            DeathCount = 0;
        }

        // =================================================================
        // Entity event handlers — called from BotCore.EntityAdded/Removed
        // =================================================================

        /// <summary>
        /// Called when an entity enters the client's entity list (spawned or entered render range).
        /// </summary>
        public void OnEntityAdded(Entity entity)
        {
            if (entity.Path == null) return;

            // Pump
            if (entity.Type == EntityType.IngameIcon && entity.Path.EndsWith("/BlightPump"))
            {
                var pos = entity.GridPosNum;
                if (PumpPosition.HasValue && Vector2.Distance(pos, PumpPosition.Value) > PumpRejectDistance)
                    return; // garbage position — reject
                PumpPosition = pos;
                PumpEntityId = entity.Id;
                IsPumpInRange = true;
                return;
            }

            // Tower (skip target markers — they share the "BlightTower" prefix but aren't real towers)
            if (entity.Path.Contains("BlightTower") && !entity.Path.Contains("TargetMarker"))
            {
                var btId = BlightLaneTracker.GetBlightTowerId(entity);

                if (!CachedTowers.TryGetValue(entity.Id, out var ct))
                {
                    ct = new CachedTower { EntityId = entity.Id };
                    CachedTowers[entity.Id] = ct;
                }

                ct.Position = entity.GridPosNum;
                ct.BlightTowerId = btId;
                ct.TowerType = btId != null ? BlightLaneTracker.GetTypeFromBlightTowerId(btId) : null;
                ct.Tier = btId != null ? BlightLaneTracker.GetTierFromBlightTowerId(btId) : 1;
                ct.LastSeen = DateTime.Now;
                ct.IsVisible = true;
                KnownTowerEntityIds.Add(entity.Id);

                // Read radius from game data (Info.Radius is already in grid units)
                if (entity.TryGetComponent<BlightTower>(out var bt) && bt.Info != null && bt.Info.Radius > 0)
                    ct.Radius = bt.Info.Radius;

                return;
            }

            // Foundation
            if (entity.Path.Contains("BlightFoundation"))
            {
                if (!CachedFoundations.TryGetValue(entity.Id, out var cf))
                {
                    cf = new CachedFoundation { EntityId = entity.Id };
                    CachedFoundations[entity.Id] = cf;
                }

                cf.Position = entity.GridPosNum;
                cf.LastSeen = DateTime.Now;
                cf.IsVisible = true;
                // Re-entering render range: if it wasn't built before, still not built
                // (IsBuilt stays true if it was already marked built)
                if (!cf.IsBuilt)
                    cf.IsBuilt = false;
                return;
            }

            // Monster
            if (entity.Type == EntityType.Monster && entity.IsHostile)
            {
                if (!CachedMonsters.TryGetValue(entity.Id, out var cm))
                {
                    cm = new CachedMonster { EntityId = entity.Id };
                    CachedMonsters[entity.Id] = cm;
                }

                cm.Position = entity.GridPosNum;
                cm.Rarity = entity.Rarity;
                cm.AssumedAlive = entity.IsAlive && entity.IsTargetable;
                cm.LastSeen = DateTime.Now;
                cm.IsVisible = true;
                return;
            }

            // Portal — cache position for exit navigation
            if (entity.Type == EntityType.TownPortal)
            {
                PortalPosition = entity.GridPosNum;
                return;
            }

            // Chest
            if (entity.Type == EntityType.Chest)
            {
                var pos = entity.GridPosNum;
                if (!entity.IsOpened)
                {
                    ChestPositions.Add(pos);
                    _chestEntityPositions[entity.Id] = pos;
                }
                else
                {
                    // Re-entered render range already opened — clean up stale cache
                    ChestPositions.Remove(pos);
                    _chestEntityPositions.Remove(entity.Id);
                }
                return;
            }
        }

        /// <summary>
        /// Called when an entity leaves the client's entity list (destroyed or left render range).
        /// Uses render-range check to distinguish "truly gone" from "went off-screen".
        /// playerPos is in grid coordinates.
        /// </summary>
        public void OnEntityRemoved(Entity entity, Vector2 playerPos)
        {
            var id = entity.Id;

            // Pump
            if (id == PumpEntityId)
            {
                IsPumpInRange = false;
                return;
            }

            // Foundation removed: if within render range, it was built (tower replaced it)
            if (CachedFoundations.TryGetValue(id, out var cf))
            {
                cf.IsVisible = false;
                if (Vector2.Distance(playerPos, cf.Position) < RenderRange)
                    cf.IsBuilt = true;
                UpdateFoundationDebugText();
                return;
            }

            // Tower removed
            if (CachedTowers.TryGetValue(id, out var ct))
            {
                ct.IsVisible = false;
                KnownTowerEntityIds.Remove(id);
                if (Vector2.Distance(playerPos, ct.Position) < RenderRange)
                {
                    // Close enough to see but gone = destroyed
                    CachedTowers.Remove(id);
                    FullyUpgradedTowerIds.Remove(id);
                }
                return;
            }

            // Monster removed: if within render range, confirmed dead
            if (CachedMonsters.TryGetValue(id, out var cm))
            {
                cm.IsVisible = false;
                if (Vector2.Distance(playerPos, cm.Position) < RenderRange)
                    cm.AssumedAlive = false;
                return;
            }

            // Chest removed (opened or went off-screen)
            if (_chestEntityPositions.TryGetValue(id, out var chestPos))
            {
                if (Vector2.Distance(playerPos, chestPos) < RenderRange)
                {
                    // Within network bubble = confirmed gone (opened/destroyed)
                    ChestPositions.Remove(chestPos);
                    _chestEntityPositions.Remove(id);
                }
                // Beyond range = went off-screen, keep both caches so we can navigate back
                return;
            }
        }

        /// <summary>
        /// Populate caches from entities already in the world (call on mode enter).
        /// </summary>
        public void InitializeFromCurrentEntities(GameController gc)
        {
            foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
                OnEntityAdded(entity);
            UpdateFoundationDebugText();
        }

        /// <summary>
        /// Actively scan entity list for the pump and read its StateMachine.
        /// Call each tick during FindPump when PumpPosition is not set — handles re-entry
        /// after death where EntityAdded events may not re-fire for cached entities.
        /// </summary>
        public void ScanForPump(GameController gc)
        {
            foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
            {
                if (entity.Type != EntityType.IngameIcon || entity.Path == null || !entity.Path.EndsWith("/BlightPump"))
                    continue;

                var pos = entity.GridPosNum;
                if (PumpPosition.HasValue && Vector2.Distance(pos, PumpPosition.Value) > PumpRejectDistance)
                    continue;

                PumpPosition = pos;
                PumpEntityId = entity.Id;
                IsPumpInRange = true;

                // Read StateMachine immediately — don't wait for next Tick()
                if (entity.TryGetComponent<StateMachine>(out var states))
                {
                    var activated = GetStateValue(states, "activated");
                    if (activated > 0)
                        IsEncounterActive = true;

                    var encounterDone = GetStateValue(states, "encounter_done");
                    var success = GetStateValue(states, "success");
                    var fail = GetStateValue(states, "fail");
                    if (encounterDone > 0 || success > 0 || fail > 0)
                    {
                        IsEncounterDone = true;
                        IsTimerDone = true;
                        EncounterSucceeded = success > 0;
                        TimerDoneAt ??= DateTime.Now;
                    }
                }

                // Fallback: non-targetable pump means the encounter has been activated.
                // The "activated" StateMachine state may not stay >0 for the entire encounter.
                if (!IsEncounterActive && !entity.IsTargetable)
                    IsEncounterActive = true;
                break;
            }
        }

        // =================================================================
        // Per-tick updates — dynamic data only
        // =================================================================

        /// <summary>
        /// Update dynamic state each tick. Entity lifecycle is handled by events.
        /// </summary>
        public void Tick(GameController gc)
        {
            UpdateDynamicEntityData(gc);
            TrackLanes(gc);
            TrackCountdown(gc);
            TrackEncounterCompletion(gc);
            TrackDanger(gc);
            TrackCurrency(gc);
            UpdateFoundationDebugText();
        }

        /// <summary>
        /// Single pass over visible entities to update dynamic data
        /// (monster positions, tower tiers, pump StateMachine).
        /// </summary>
        private void UpdateDynamicEntityData(GameController gc)
        {
            bool prevEncounterActive = IsEncounterActive;

            foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
            {
                // Monster position + alive update
                if (CachedMonsters.TryGetValue(entity.Id, out var cm))
                {
                    cm.Position = entity.GridPosNum;
                    cm.Rarity = entity.Rarity;
                    cm.AssumedAlive = entity.IsAlive && entity.IsTargetable;
                    cm.LastSeen = DateTime.Now;
                    continue;
                }

                // Tower tier/radius update (changes on upgrade)
                if (CachedTowers.TryGetValue(entity.Id, out var ct))
                {
                    var btId = BlightLaneTracker.GetBlightTowerId(entity);
                    ct.BlightTowerId = btId;
                    ct.TowerType = btId != null ? BlightLaneTracker.GetTypeFromBlightTowerId(btId) : null;
                    ct.Tier = btId != null ? BlightLaneTracker.GetTierFromBlightTowerId(btId) : 1;
                    ct.LastSeen = DateTime.Now;

                    if (entity.TryGetComponent<BlightTower>(out var bt) && bt.Info != null && bt.Info.Radius > 0)
                        ct.Radius = bt.Info.Radius;

                    continue;
                }

                // Pump StateMachine
                if (entity.Id == PumpEntityId)
                {
                    var pos = entity.GridPosNum;
                    if (!PumpPosition.HasValue || Vector2.Distance(pos, PumpPosition.Value) < PumpRejectDistance)
                        PumpPosition = pos;

                    IsPumpInRange = true;
                    if (entity.TryGetComponent<StateMachine>(out var states))
                    {
                        var activated = GetStateValue(states, "activated");
                        if (activated > 0)
                            IsEncounterActive = true;
                    }
                    // Fallback: non-targetable pump means encounter is active.
                    // The "activated" state is a trigger that may not stay >0 for the
                    // entire encounter — once set, IsEncounterActive should never flip back.
                    if (!IsEncounterActive && !entity.IsTargetable)
                        IsEncounterActive = true;
                    continue;
                }
            }

            // Clean up opened chests — check all cached chest entities that are still visible
            var staleChestIds = new List<long>();
            foreach (var (id, pos) in _chestEntityPositions)
            {
                var chestEntity = gc.EntityListWrapper.OnlyValidEntities.FirstOrDefault(e => e.Id == id);
                if (chestEntity != null && chestEntity.IsOpened)
                {
                    ChestPositions.Remove(pos);
                    staleChestIds.Add(id);
                }
            }
            foreach (var id in staleChestIds)
                _chestEntityPositions.Remove(id);

            // Track encounter start
            if (IsEncounterActive && !prevEncounterActive)
                EncounterStartedAt = DateTime.Now;
            _wasEncounterActive = IsEncounterActive;
        }

        private void TrackLanes(GameController gc)
        {
            LaneTracker.Tick(gc);

            if (IsEncounterActive && LaneTracker.HasLaneData)
            {
                var now = DateTime.Now;
                bool needsDanger = false;

                if ((now - _lastThreatUpdateAt).TotalMilliseconds >= 250)
                {
                    LaneTracker.UpdateThreat(gc);
                    _lastThreatUpdateAt = now;
                    needsDanger = true;
                }

                bool towerStateChanged = LastTowerBuildAt != _prevTowerBuildAt || LastTowerUpgradeAt != _prevTowerUpgradeAt;
                if (towerStateChanged || (now - _lastCoverageUpdateAt).TotalMilliseconds >= 2000)
                {
                    LaneTracker.UpdateCoverage(CachedTowers.Values);
                    _lastCoverageUpdateAt = now;
                    needsDanger = true;
                }

                if (needsDanger)
                    LaneTracker.UpdateDanger();
            }

            LaneDebug = LaneTracker.GetDebugText();
        }

        private void TrackCountdown(GameController gc)
        {
            try
            {
                var countdownElement = gc.IngameState.IngameUi.Parent
                    .GetChildFromIndices(1, 25, 4, 0, 0, 0, 0);
                CountdownText = countdownElement?.Text ?? "";
            }
            catch
            {
                CountdownText = "";
            }

            // Detect timer done — countdown reaching zero
            if (!IsTimerDone && IsEncounterActive)
            {
                bool timerRunning = !string.IsNullOrEmpty(CountdownText) &&
                    CountdownText.Trim() != "0:00" && CountdownText.Trim() != "00:00";

                if (_wasTimerRunning && !timerRunning)
                {
                    IsTimerDone = true;
                    TimerDoneAt = DateTime.Now;
                }
                else if (!_wasTimerRunning && !timerRunning)
                {
                    // Never saw the timer running — encounter may have been re-entered
                    // after the timer already ended. Only trigger this fallback after the
                    // encounter has been active for 5+ seconds, so the countdown UI has
                    // time to load. Without this guard, a slow UI load on fresh encounters
                    // causes false "timer done" within 0.5s of encounter start.
                    var encounterAge = EncounterStartedAt.HasValue
                        ? (DateTime.Now - EncounterStartedAt.Value).TotalSeconds : 0;
                    if (encounterAge > 5)
                    {
                        _timerCheckTicks++;
                        if (_timerCheckTicks > 30) // ~0.5s after the 5s grace
                        {
                            IsTimerDone = true;
                            TimerDoneAt = DateTime.Now;
                        }
                    }
                }
                _wasTimerRunning = timerRunning;
            }
        }

        private void TrackEncounterCompletion(GameController gc)
        {
            if (IsEncounterDone) return;
            if (PumpEntityId == 0) return;

            // Find pump by cached ID
            Entity? pump = null;
            foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
            {
                if (entity.Id == PumpEntityId)
                {
                    pump = entity;
                    break;
                }
            }

            if (pump == null) return;

            if (pump.TryGetComponent<StateMachine>(out var states))
            {
                var encounterDone = GetStateValue(states, "encounter_done");
                var success = GetStateValue(states, "success");
                var fail = GetStateValue(states, "fail");

                if (encounterDone > 0 || success > 0 || fail > 0)
                {
                    IsEncounterDone = true;
                    IsTimerDone = true;
                    EncounterSucceeded = success > 0;
                    TimerDoneAt ??= DateTime.Now;
                }
            }
        }

        private void TrackDanger(GameController gc)
        {
            PumpUnderAttack = false;
            AliveMonsterCount = 0;

            if (!PumpPosition.HasValue) return;
            var pumpPos = PumpPosition.Value;

            foreach (var cm in CachedMonsters.Values)
            {
                if (!cm.AssumedAlive) continue;
                AliveMonsterCount++;

                if (!PumpUnderAttack && Vector2.Distance(cm.Position, pumpPos) < PumpDangerRadius)
                    PumpUnderAttack = true;
            }
        }

        private void TrackCurrency(GameController gc)
        {
            // Don't search for blight HUD after encounter is done — UI is gone
            if (IsEncounterDone) return;

            // Blight currency is in the blight HUD overlay.
            // Path: IngameUi[11][0][3][2][0][1] — sibling of "Pump Durability" at [1].
            // Try direct path first, fall back to searching for "Pump Durability" landmark.
            var ui = gc.IngameState.IngameUi;
            if (ui == null) return;

            try
            {
                // Direct path — fast. Check child counts before accessing to avoid ExileCore index errors.
                var c11 = ui.GetChildAtIndex(11);
                if (c11?.IsVisible == true && c11.ChildCount > 0)
                {
                    var c0 = c11.GetChildAtIndex(0);
                    if (c0 != null && c0.ChildCount > 3)
                    {
                        var hud = c0.GetChildAtIndex(3);
                        if (hud?.IsVisible == true && hud.ChildCount > 2)
                        {
                            var textEl = hud.GetChildFromIndices(2, 0, 1);
                            if (textEl?.IsVisible == true && TryParseCurrency(textEl.Text, out var val))
                            {
                                Currency = val;
                                return;
                            }
                        }
                    }
                }
            }
            catch { }

            // Fallback: search top-level children for the blight HUD
            // Identified by containing a "Pump Durability" label
            try
            {
                for (int i = 0; i < ui.ChildCount && i < 40; i++)
                {
                    var top = ui.GetChildAtIndex(i);
                    if (top == null || !top.IsVisible || top.ChildCount < 1) continue;

                    var inner = top.GetChildAtIndex(0);
                    if (inner == null || inner.ChildCount <= 3) continue;

                    var hud = inner.GetChildAtIndex(3);
                    if (hud == null || !hud.IsVisible || hud.ChildCount < 3) continue;

                    // Check for "Pump Durability" landmark at [1][0][0]
                    var durLabel = hud.GetChildFromIndices(1, 0, 0);
                    if (durLabel?.Text == null || !durLabel.Text.Contains("Pump")) continue;

                    // Currency text is at [2][0][1]
                    var textEl = hud.GetChildFromIndices(2, 0, 1);
                    if (textEl != null && TryParseCurrency(textEl.Text, out var val))
                    {
                        Currency = val;
                        return;
                    }
                }
            }
            catch { }
        }

        private static bool TryParseCurrency(string? text, out int value)
        {
            value = 0;
            if (string.IsNullOrEmpty(text)) return false;
            return int.TryParse(text.Replace(",", ""), out value) && value >= 0;
        }

        private void UpdateFoundationDebugText()
        {
            int visible = 0, built = 0;
            foreach (var cf in CachedFoundations.Values)
            {
                if (cf.IsVisible) visible++;
                if (cf.IsBuilt) built++;
            }
            FoundationDebug = $"Foundations: {visible} visible, {built} built, {CachedFoundations.Count} cached, Towers: {CachedTowers.Count}";
        }

        private static long GetStateValue(StateMachine states, string name)
        {
            if (states?.States == null) return 0;
            foreach (var s in states.States)
            {
                if (s.Name == name)
                    return s.Value;
            }
            return 0;
        }

        /// <summary>
        /// Convert a grid position to world coordinates for NavigateTo / WorldToScreen.
        /// </summary>
        public static Vector2 ToWorld(Vector2 gridPos) => gridPos * Pathfinding.GridToWorld;
        public static Vector3 ToWorld3(Vector2 gridPos, float z = 0) => new(gridPos.X * Pathfinding.GridToWorld, gridPos.Y * Pathfinding.GridToWorld, z);
    }

    // --- Cache data structures (all positions in grid coordinates) ---

    public class CachedTower
    {
        public long EntityId;
        public Vector2 Position;       // grid coords
        public string? BlightTowerId;  // e.g. "ChillingTower2"
        public string? TowerType;      // e.g. "Chilling"
        public int Tier;
        public float Radius;           // grid units (from BlightTower.Info.Radius)
        public DateTime LastSeen;
        public bool IsVisible;         // currently in entity list
    }

    public class CachedFoundation
    {
        public long EntityId;
        public Vector2 Position;       // grid coords
        public bool IsBuilt;           // true when entity removed from within render range
        public DateTime LastSeen;
        public bool IsVisible;
    }

    public class CachedMonster
    {
        public long EntityId;
        public Vector2 Position;       // grid coords
        public MonsterRarity Rarity;
        public bool AssumedAlive;      // true until confirmed dead via removal event
        public DateTime LastSeen;
        public bool IsVisible;
    }
}
