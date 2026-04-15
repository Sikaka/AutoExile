using ExileCore;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using AutoExile.Systems;
using System.Numerics;
using static AutoExile.BotSettings;

namespace AutoExile.Systems
{
    public class HeistState
    {
        // Cached positions (grid coordinates)
        public Vector2? ExitPosition { get; private set; }
        public Vector2? CurioTargetPosition { get; private set; }
        public long CompanionEntityId { get; private set; }
        public Vector2? CompanionPosition { get; private set; }

        // Alert level
        public float AlertPercent { get; private set; }
        public bool IsAlertPanelVisible { get; private set; }
        public bool IsLockdown { get; private set; }
        private bool _alertWasVisible;

        // Companion channeling state
        public float CompanionLockPickProgress { get; private set; }
        public bool CompanionIsBusy { get; private set; }

        // Cached entities
        public Dictionary<long, CachedHeistChest> RewardChests { get; } = new();
        public Dictionary<long, CachedHeistDoor> Doors { get; } = new();
        public HashSet<long> OpenedEntities { get; } = new();

        // Full map navigation graph from TileEntities (available at zone load)
        public List<Vector2> PathNodes { get; } = new();

        // Route planning — ordered list of targets (chests + curio) to visit
        public List<RouteTarget> PlannedRoute { get; } = new();
        public int CurrentRouteIndex { get; set; }
        public RouteTarget? CurrentTarget => CurrentRouteIndex < PlannedRoute.Count ? PlannedRoute[CurrentRouteIndex] : null;

        // Death tracking
        public int DeathCount { get; set; }

        // Status
        public string Status { get; set; } = "";

        public void Initialize(GameController gc)
        {
            // Scan TileEntities for map-wide pre-placed data (available at zone load,
            // covers entire map including entities far outside network bubble).
            // This is the same data source the game uses for minimap icons.
            var tileEntities = gc.IngameState.Data.TileEntities;
            if (tileEntities != null)
            {
                foreach (var entity in tileEntities)
                {
                    if (entity?.Path == null) continue;

                    // Cache exit (HeistEscapeRoute)
                    try
                    {
                        var mic = entity.GetComponent<MinimapIcon>();
                        if (mic?.Name == "HeistEscapeRoute")
                            ExitPosition = entity.GridPosNum;
                    }
                    catch { }

                    // Cache curio marker
                    if (entity.Path.Contains("CurioDisplayRoomMarker"))
                        CurioTargetPosition = entity.GridPosNum;

                    // Cache reward chests from tile data (positions known map-wide)
                    if (entity.Path.Contains("HeistChest"))
                    {
                        try
                        {
                            var cached = CachedHeistChest.FromEntity(entity);
                            RewardChests[entity.Id] = cached;
                        }
                        catch { }
                    }

                    // Cache HeistPathNode/Endpoint positions for navigation graph
                    if (entity.Path.Contains("HeistPathNode") || entity.Path.Contains("HeistPathEndpoint")
                        || entity.Path.Contains("HeistPathStart"))
                    {
                        PathNodes.Add(entity.GridPosNum);
                    }
                }
            }

            // Scan live entity list for runtime entities (companion, doors, curio chest)
            foreach (var entity in gc.EntityListWrapper.Entities)
            {
                if (entity?.Path == null) continue;

                // Cache companion
                if (entity.Path.Contains("LeagueHeist/NPCAllies") && !entity.IsHostile && entity.IsAlive)
                {
                    CompanionEntityId = entity.Id;
                    CompanionPosition = entity.GridPosNum;
                }

                // Cache curio primary target entity (distinct from the marker)
                if (entity.Path.Contains("HeistChestPrimaryTarget"))
                    CurioTargetPosition = entity.GridPosNum;

                // Cache reward chests with live state (overrides tile data if same ID)
                if (entity.Path.Contains("HeistChest"))
                {
                    try
                    {
                        var cached = CachedHeistChest.FromEntity(entity);
                        var sm = entity.GetComponent<StateMachine>();
                        cached.HeistLocked = (int)GetStateValue(sm, "heist_locked");
                        RewardChests[entity.Id] = cached;
                    }
                    catch { }
                }

                // Cache NPC/Vault doors (only from live entities — not in TileEntities)
                if ((entity.Path.Contains("Door_NPC") && !entity.Path.Contains("Alternate"))
                    || entity.Path.Contains("Vault"))
                {
                    try
                    {
                        var sm = entity.GetComponent<StateMachine>();
                        var locked = GetStateValue(sm, "heist_locked");
                        if (locked >= 0)
                        {
                            Doors[entity.Id] = new CachedHeistDoor
                            {
                                Id = entity.Id,
                                GridPos = entity.GridPosNum,
                                HeistLocked = (int)locked,
                                Path = entity.Path
                            };
                        }
                    }
                    catch { }
                }
            }

            // If no curio marker found in TileEntities, use farthest HeistPathEndpoint from exit
            if (CurioTargetPosition == null && ExitPosition != null)
            {
                float farthestDist = 0;
                Vector2? farthest = null;
                foreach (var node in PathNodes)
                {
                    var dist = Vector2.Distance(node, ExitPosition.Value);
                    if (dist > farthestDist)
                    {
                        farthestDist = dist;
                        farthest = node;
                    }
                }
                CurioTargetPosition = farthest;
            }

            Status = $"Init: exit={ExitPosition != null} curio={CurioTargetPosition != null} companion={CompanionEntityId != 0} rewards={RewardChests.Count} doors={Doors.Count} nodes={PathNodes.Count}";
        }

        /// <summary>
        /// Build an ordered route of targets (reward chests + curio) sorted by corridor order
        /// (distance from exit). Only includes chests matching enabled reward type settings.
        /// </summary>
        public void BuildRoute(BotSettings.HeistSettings settings)
        {
            PlannedRoute.Clear();
            CurrentRouteIndex = 0;
            var exitPos = ExitPosition ?? Vector2.Zero;

            // Add enabled reward chests
            foreach (var chest in RewardChests.Values)
            {
                // Only route to valuable chests — skip filler path chests
                if (chest.ChestType == HeistChestType.Normal) continue;

                // Filter by user's per-reward-type toggles
                if (chest.RewardType != HeistRewardType.None && !settings.IsRewardTypeEnabled(chest.RewardType))
                    continue;

                PlannedRoute.Add(new RouteTarget
                {
                    GridPos = chest.GridPos,
                    Type = RouteTargetType.RewardChest,
                    Label = chest.RewardLabel,
                    EntityId = chest.Id,
                    RewardType = chest.RewardType,
                    DistFromExit = Vector2.Distance(exitPos, chest.GridPos),
                });
            }

            // Add curio as final target (always)
            if (CurioTargetPosition != null)
            {
                PlannedRoute.Add(new RouteTarget
                {
                    GridPos = CurioTargetPosition.Value,
                    Type = RouteTargetType.Curio,
                    Label = "Curio",
                    DistFromExit = Vector2.Distance(exitPos, CurioTargetPosition.Value),
                });
            }

            // Sort by distance from exit — visits chests in corridor order on the way to curio
            PlannedRoute.Sort((a, b) => a.DistFromExit.CompareTo(b.DistFromExit));
        }

        public void Tick(GameController gc)
        {
            // Re-resolve companions — grand heists have multiple, track the one that's working
            CompanionLockPickProgress = 0;
            CompanionIsBusy = false;
            CompanionPosition = null;
            foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
            {
                if (entity?.Path == null) continue;
                if (!entity.Path.Contains("LeagueHeist/NPCAllies") || entity.IsHostile || !entity.IsAlive)
                    continue;

                // Track first companion for position (backwards compat)
                if (CompanionEntityId == 0)
                    CompanionEntityId = entity.Id;
                if (CompanionPosition == null)
                    CompanionPosition = entity.GridPosNum;

                var sm = entity.GetComponent<StateMachine>();
                var progress = GetStateValue(sm, "lock_pick_progress");
                var jobIdx = GetStateValue(sm, "current_job_index");
                var busy = jobIdx != 4294967295 && jobIdx >= 0;

                // If ANY companion is busy or has progress, report that
                if (progress > CompanionLockPickProgress)
                    CompanionLockPickProgress = progress;
                if (busy)
                    CompanionIsBusy = true;
            }

            // Read alert from UI
            ReadAlertLevel(gc);

            // Detect lockdown
            if (_alertWasVisible && !IsAlertPanelVisible)
                IsLockdown = true;
            if (IsAlertPanelVisible)
                _alertWasVisible = true;

            // Refresh nearby door/chest states
            RefreshNearbyEntityStates(gc);

            // Check for curio entity appearing
            if (CurioTargetPosition == null)
            {
                foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
                {
                    if (entity?.Path != null && entity.Path.Contains("HeistChestPrimaryTarget"))
                    {
                        CurioTargetPosition = entity.GridPosNum;
                        break;
                    }
                }
            }
        }

        private void ReadAlertLevel(GameController gc)
        {
            try
            {
                var ui = gc.IngameState.IngameUi;
                var alertPanel = ui.GetChildAtIndex(15)?.GetChildAtIndex(0);
                if (alertPanel != null && alertPanel.IsVisible && alertPanel.ChildCount >= 4)
                {
                    // Verify it's the alert panel
                    var label = alertPanel.GetChildAtIndex(3)?.GetChildAtIndex(0);
                    if (label?.Text?.Contains("Alert") == true)
                    {
                        var maxRef = alertPanel.GetChildAtIndex(0)?.GetChildAtIndex(0)?.GetClientRect().Width ?? 0;
                        var current = alertPanel.GetChildAtIndex(1)?.GetClientRect().Width ?? 0;
                        if (maxRef > 0)
                        {
                            AlertPercent = current / maxRef * 100f;
                            IsAlertPanelVisible = true;
                            return;
                        }
                    }
                }
            }
            catch { }
            IsAlertPanelVisible = false;
        }

        private void RefreshNearbyEntityStates(GameController gc)
        {
            foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
            {
                if (entity?.Path == null) continue;

                // Update door states
                if (Doors.TryGetValue(entity.Id, out var door))
                {
                    var sm = entity.GetComponent<StateMachine>();
                    door.HeistLocked = (int)GetStateValue(sm, "heist_locked");
                    door.IsTargetable = entity.IsTargetable;
                    if (door.HeistLocked == 0 || !entity.IsTargetable)
                        OpenedEntities.Add(entity.Id);
                }

                // Update or discover chests
                if (entity.Path.Contains("HeistChest"))
                {
                    if (RewardChests.TryGetValue(entity.Id, out var chest))
                    {
                        // Update existing
                        var chestComp = entity.GetComponent<Chest>();
                        if (chestComp?.IsOpened == true || !entity.IsTargetable)
                            OpenedEntities.Add(entity.Id);
                    }
                    else
                    {
                        // Discover new chest (came into range after init)
                        try
                        {
                            var cached = CachedHeistChest.FromEntity(entity);
                            var sm = entity.GetComponent<StateMachine>();
                            cached.HeistLocked = (int)GetStateValue(sm, "heist_locked");
                            RewardChests[entity.Id] = cached;
                        }
                        catch { }
                    }
                }

                // Detect basic and generic doors
                bool isClickDoor = entity.Path.Contains("Door_Basic")
                    || entity.Path == "Metadata/MiscellaneousObjects/Door";
                if (isClickDoor && entity.IsTargetable && entity.DistancePlayer < 25)
                {
                    // For Door_Basic, check open state. For generic doors, targetable=closed.
                    var sm = entity.GetComponent<StateMachine>();
                    var open = GetStateValue(sm, "open");
                    if (open <= 0) // 0=closed for Door_Basic, -1=no state for generic doors
                    {
                        if (!Doors.ContainsKey(entity.Id))
                        {
                            Doors[entity.Id] = new CachedHeistDoor
                            {
                                Id = entity.Id,
                                GridPos = entity.GridPosNum,
                                HeistLocked = 0,
                                Path = entity.Path,
                                IsBasicDoor = true,
                                IsTargetable = true
                            };
                        }
                    }
                }
            }
        }

        /// <summary>Clear stale curio target so exploration takes over.</summary>
        public void ClearCurioTarget() => CurioTargetPosition = null;

        /// <summary>Force lockdown state (e.g., bot started mid-lockdown).</summary>
        public void ForceLockdown() => IsLockdown = true;

        /// <summary>Scan current entities for the heist exit (HeistEscapeRoute minimap icon).</summary>
        public void ScanForExit(GameController gc)
        {
            if (ExitPosition != null) return;
            foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
            {
                if (entity?.Path == null) continue;
                try
                {
                    var mic = entity.GetComponent<MinimapIcon>();
                    if (mic?.Name == "HeistEscapeRoute")
                    {
                        ExitPosition = entity.GridPosNum;
                        return;
                    }
                }
                catch { }
            }
        }

        public Entity? FindCompanion(GameController gc)
        {
            if (CompanionEntityId == 0) return null;
            return FindEntityById(gc, CompanionEntityId);
        }

        public Entity? FindCurioEntity(GameController gc)
        {
            foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
            {
                if (entity?.Path != null && entity.Path.Contains("HeistChestPrimaryTarget") && entity.IsTargetable)
                    return entity;
            }
            return null;
        }

        public Entity? FindExitEntity(GameController gc)
        {
            foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
            {
                if (entity?.Path != null && entity.Path.Contains("AreaTransition") && entity.IsTargetable)
                {
                    try
                    {
                        var mic = entity.GetComponent<MinimapIcon>();
                        if (mic?.Name == "HeistEscapeRoute")
                            return entity;
                    }
                    catch { }
                }
            }
            return null;
        }

        public void OnAreaChanged()
        {
            ExitPosition = null;
            CurioTargetPosition = null;
            CompanionEntityId = 0;
            CompanionPosition = null;
            AlertPercent = 0;
            IsAlertPanelVisible = false;
            IsLockdown = false;
            _alertWasVisible = false;
            CompanionLockPickProgress = 0;
            CompanionIsBusy = false;
            RewardChests.Clear();
            Doors.Clear();
            OpenedEntities.Clear();
            PathNodes.Clear();
            PlannedRoute.Clear();
            CurrentRouteIndex = 0;
            Status = "";
        }

        public void Reset()
        {
            OnAreaChanged();
            DeathCount = 0;
        }

        private static Entity? FindEntityById(GameController gc, long id)
        {
            foreach (var e in gc.EntityListWrapper.OnlyValidEntities)
                if (e.Id == id) return e;
            return null;
        }

        public static float GetStateValue(StateMachine? sm, string name)
        {
            if (sm?.States == null) return -1;
            foreach (var s in sm.States)
                if (s?.Name == name) return s.Value;
            return -1;
        }
    }

    public enum HeistChestType
    {
        Normal,      // Side corridor chests (cost alert)
        RewardRoom,  // Reward room chests (bigger loot)
        Smugglers,   // Smugglers cache (free, no alert cost)
        Safe,        // Path safe (generic)
    }

    public enum HeistRewardType
    {
        None,
        Currency,
        QualityCurrency,
        Armour,
        Weapons,
        Jewellery,
        Jewels,
        Maps,
        DivinationCards,
        StackedDecks,
        Gems,
        Corrupted,
        Uniques,
        Essences,
        Fragments,
        Smugglers,
        Safe,
    }

    public class CachedHeistChest
    {
        public long Id { get; set; }
        public Vector2 GridPos { get; set; }
        public string Path { get; set; } = "";
        public HeistChestType ChestType { get; set; }
        public HeistRewardType RewardType { get; set; }
        public string RewardLabel { get; set; } = "";
        public int HeistLocked { get; set; }

        /// <summary>
        /// Classify chest type and reward from entity path.
        /// Path format: Metadata/Chests/LeagueHeist/HeistChest{Theme}{JobType}{RewardType}
        /// </summary>
        public static CachedHeistChest FromEntity(Entity entity)
        {
            var path = entity.Path ?? "";
            var cached = new CachedHeistChest
            {
                Id = entity.Id,
                GridPos = entity.GridPosNum,
                Path = path,
            };

            // Determine chest type
            if (path.Contains("RewardRoom"))
                cached.ChestType = HeistChestType.RewardRoom;
            else if (path.Contains("Smugglers"))
                cached.ChestType = HeistChestType.Smugglers;
            else if (path.Contains("Safe"))
                cached.ChestType = HeistChestType.Safe;
            else
                cached.ChestType = HeistChestType.Normal;

            // Clean path to extract reward keyword
            // Strip common prefixes/themes to isolate the reward type
            var name = path
                .Replace("Metadata/Chests/LeagueHeist/HeistChest", "")
                .Replace("Metadata/Chests/LeaguesHeist/HeistChest", "")
                .Replace("Metadata/Chests/LeagueHeist/Heist", "");

            // Strip theme names (Military, Thug, Science, Robot)
            foreach (var theme in new[] { "Military", "Thug", "Science", "Robot" })
                name = name.Replace(theme, "");

            // Strip secondary/room markers
            name = name.Replace("Secondary", "").Replace("RewardRoom", "");

            // Strip job type names
            foreach (var job in new[] { "LockPicking", "BruteForce", "Perception", "Demolition",
                "CounterThaumaturge", "TrapDisarmament", "Agility", "Deception", "Engineering" })
                name = name.Replace(job, "");

            // Now classify reward from remaining name
            cached.RewardType = ClassifyReward(name);
            cached.RewardLabel = cached.RewardType != HeistRewardType.None
                ? cached.RewardType.ToString()
                : cached.ChestType.ToString();

            return cached;
        }

        private static HeistRewardType ClassifyReward(string name)
        {
            // Order matters — check more specific first
            if (name.Contains("Smugglers")) return HeistRewardType.Smugglers;
            if (name.Contains("Safe")) return HeistRewardType.Safe;
            if (name.Contains("Quality")) return HeistRewardType.QualityCurrency;
            if (name.Contains("Currency")) return HeistRewardType.Currency;
            if (name.Contains("Armour")) return HeistRewardType.Armour;
            if (name.Contains("Weapons")) return HeistRewardType.Weapons;
            if (name.Contains("Trinkets") || name.Contains("Jewellery")) return HeistRewardType.Jewellery;
            if (name.Contains("Jewels")) return HeistRewardType.Jewels;
            if (name.Contains("Maps")) return HeistRewardType.Maps;
            if (name.Contains("Divination")) return HeistRewardType.DivinationCards;
            if (name.Contains("Stacked")) return HeistRewardType.StackedDecks;
            if (name.Contains("Gems")) return HeistRewardType.Gems;
            if (name.Contains("Corrupted")) return HeistRewardType.Corrupted;
            if (name.Contains("Uniques")) return HeistRewardType.Uniques;
            if (name.Contains("Essences")) return HeistRewardType.Essences;
            if (name.Contains("Fragments")) return HeistRewardType.Fragments;
            return HeistRewardType.None;
        }
    }

    public class CachedHeistDoor
    {
        public long Id { get; set; }
        public Vector2 GridPos { get; set; }
        public int HeistLocked { get; set; }
        public string Path { get; set; } = "";
        public bool IsBasicDoor { get; set; }
        public bool IsTargetable { get; set; } = true;
    }

    public enum RouteTargetType
    {
        RewardChest,
        Curio,
    }

    public class RouteTarget
    {
        public Vector2 GridPos { get; set; }
        public RouteTargetType Type { get; set; }
        public string Label { get; set; } = "";
        public long EntityId { get; set; }
        public HeistRewardType RewardType { get; set; }
        public float DistFromExit { get; set; }
        public bool Reached { get; set; }
        public bool Skipped { get; set; }
    }
}
