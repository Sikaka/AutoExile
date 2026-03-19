using ExileCore;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using System.Numerics;

namespace AutoExile.Systems
{
    /// <summary>
    /// Scans visible ground item labels and decides what to pick up.
    /// Respects the in-game loot filter (only visible labels are candidates).
    /// Always picks nearest item first for efficient pathing.
    /// </summary>
    public class LootSystem
    {
        // Ninja pricer bridge — resolved lazily
        private Func<Entity, double>? _getNinjaValue;
        private bool _ninjaBridgeResolved;

        // Configurable thresholds
        public float MinUniqueChaosValue { get; set; } = 5f;
        public bool SkipLowValueUniques { get; set; } = true;

        /// <summary>
        /// Minimum chaos-per-inventory-slot to pick up a unique.
        /// Set to 0 to disable size-based filtering (only use flat MinUniqueChaosValue).
        /// </summary>
        public float MinChaosPerSlot { get; set; } = 0f;

        /// <summary>
        /// Grid distance threshold for direct pickup vs navigation.
        /// Items within this radius are clicked directly; items beyond require pathing first.
        /// </summary>
        public float LootRadius { get; set; } = 20f;

        /// <summary>
        /// Skip quest items (heist contracts, etc.) during loot scans.
        /// </summary>
        public bool IgnoreQuestItems { get; set; } = true;

        // State
        public bool HasLootNearby { get; private set; }
        public int LootableCount { get; private set; }
        public string LastSkipReason { get; private set; } = "";
        public string NinjaBridgeStatus { get; private set; } = "not resolved";

        // Cached loot candidates from last scan — always sorted nearest-first
        private readonly List<LootCandidate> _candidates = new();
        public IReadOnlyList<LootCandidate> Candidates => _candidates;

        // Failed pickup tracking — items that couldn't be picked up are skipped in future scans
        private readonly Dictionary<long, FailedLootEntry> _failedEntities = new();

        public int FailedCount => _failedEntities.Count;
        public IReadOnlyDictionary<long, FailedLootEntry> FailedEntries => _failedEntities;

        /// <summary>
        /// Mark an entity as failed to pick up — it will be excluded from future scans.
        /// </summary>
        public void MarkFailed(long entityId, string reason = "unknown")
        {
            var prevCount = _failedEntities.TryGetValue(entityId, out var prev) ? prev.FailCount : 0;
            _failedEntities[entityId] = new FailedLootEntry
            {
                EntityId = entityId,
                Reason = reason,
                FailedAt = DateTime.Now,
                FailCount = prevCount + 1,
            };
        }

        /// <summary>
        /// Clear the failed entity list. Call on area change or phase reset.
        /// </summary>
        public void ClearFailed() => _failedEntities.Clear();

        /// <summary>
        /// Scan visible ground items and build a prioritized pickup list.
        /// Excludes items previously marked as failed.
        /// Always sorts by distance (nearest first) for efficient pathing.
        /// Call each tick when not busy to get fresh data.
        /// </summary>
        public void Scan(GameController gc)
        {
            _candidates.Clear();
            HasLootNearby = false;
            LootableCount = 0;
            LastSkipReason = "";

            // Resolve Ninja bridge once
            if (!_ninjaBridgeResolved)
            {
                try
                {
                    _getNinjaValue = gc.PluginBridge.GetMethod<Func<Entity, double>>("NinjaPrice.GetValue");
                    NinjaBridgeStatus = _getNinjaValue != null ? "connected" : "method not found";
                }
                catch (Exception ex)
                {
                    NinjaBridgeStatus = $"error: {ex.Message}";
                }
                _ninjaBridgeResolved = true;
            }

            try
            {
                var labels = gc.IngameState.IngameUi.ItemsOnGroundLabelElement.VisibleGroundItemLabels;
                if (labels == null) return;

                foreach (var label in labels)
                {
                    if (label.Label == null || !label.Label.IsVisible)
                        continue;
                    if (label.Entity == null)
                        continue;

                    var worldItemEntity = label.Entity;
                    if (_failedEntities.TryGetValue(worldItemEntity.Id, out var failEntry) && !failEntry.IsExpired)
                        continue;

                    var itemName = label.Label.Text ?? "?";

                    // Get the actual item entity inside the WorldItem container
                    Entity? itemEntity = null;
                    if (worldItemEntity.TryGetComponent<WorldItem>(out var worldItem))
                        itemEntity = worldItem.ItemEntity;

                    // Skip gold — auto-pickup on walk-over, no click needed
                    if (itemName.EndsWith(" Gold"))
                        continue;

                    // Skip quest items (heist quest contracts, etc.)
                    if (IgnoreQuestItems && itemEntity is { IsValid: true } &&
                        itemEntity.Path.Contains("/Quest", StringComparison.OrdinalIgnoreCase))
                        continue;

                    // Use inner item for pricing/sizing, fall back to outer entity
                    var priceEntity = (itemEntity is { IsValid: true }) ? itemEntity : worldItemEntity;

                    // Check if this is a unique we should skip
                    var chaosValue = GetChaosValue(priceEntity);
                    var invSlots = GetInventorySlots(priceEntity);
                    var chaosPerSlot = invSlots > 0 ? chaosValue / invSlots : chaosValue;

                    if (SkipLowValueUniques && ShouldSkipUnique(priceEntity, chaosValue, chaosPerSlot, itemName))
                        continue;

                    _candidates.Add(new LootCandidate
                    {
                        Entity = worldItemEntity,
                        ItemName = itemName,
                        Distance = worldItemEntity.DistancePlayer,
                        ChaosValue = chaosValue,
                        InventorySlots = invSlots,
                        ChaosPerSlot = chaosPerSlot,
                    });
                }

                // Always sort nearest first — efficient pathing beats value optimization
                _candidates.Sort((a, b) => a.Distance.CompareTo(b.Distance));

                LootableCount = _candidates.Count;
                HasLootNearby = _candidates.Count > 0;
            }
            catch { }
        }

        /// <summary>
        /// Get the best candidate to pick up (nearest visible item).
        /// Does NOT remove from the list — the next Scan() call will rebuild with fresh data.
        /// Returns null if no candidates.
        /// </summary>
        public LootCandidate? GetBestCandidate()
        {
            return _candidates.Count > 0 ? _candidates[0] : null;
        }

        /// <summary>
        /// Start picking up the nearest candidate using the interaction system.
        /// Items within LootRadius are clicked directly; items beyond require navigation.
        /// Returns (wasInRadius, candidate) if pickup was initiated, or (false, null) if nothing to pick up.
        /// Callers should record to LootTracker only after InteractionResult.Succeeded.
        /// </summary>
        public (bool WasInRadius, LootCandidate? Candidate) PickupNext(InteractionSystem interaction, NavigationSystem nav)
        {
            if (interaction.IsBusy)
                return (false, null);

            var best = GetBestCandidate();
            if (best == null)
                return (false, null);

            var withinRadius = best.Distance <= LootRadius;
            interaction.PickupGroundItem(best.Entity, nav,
                requireProximity: !withinRadius);

            return (withinRadius, best);
        }

        /// <summary>
        /// Check if a unique item should be skipped based on value and size.
        /// </summary>
        private bool ShouldSkipUnique(Entity entity, double chaosValue, double chaosPerSlot, string itemName)
        {
            if (!entity.TryGetComponent<Mods>(out var mods))
                return false;

            if (mods.ItemRarity != ItemRarity.Unique)
                return false;

            // If we can't price it, don't skip (might be valuable)
            if (chaosValue <= 0)
                return false;

            // Flat value check
            if (chaosValue < MinUniqueChaosValue)
            {
                LastSkipReason = $"Skipped '{itemName}' ({chaosValue:F0}c < {MinUniqueChaosValue}c threshold)";
                return true;
            }

            // Per-slot value check (if enabled)
            if (MinChaosPerSlot > 0 && chaosPerSlot < MinChaosPerSlot)
            {
                var slots = GetInventorySlots(entity);
                LastSkipReason = $"Skipped '{itemName}' ({chaosValue:F0}c / {slots} slots = {chaosPerSlot:F1}c/slot < {MinChaosPerSlot}c/slot)";
                return true;
            }

            return false;
        }

        /// <summary>
        /// Get inventory slot count for an item (width * height).
        /// Returns 1 as minimum (for items where we can't read size).
        /// </summary>
        private int GetInventorySlots(Entity entity)
        {
            if (entity.TryGetComponent<Base>(out var baseComp))
            {
                var w = baseComp.ItemCellsSizeX;
                var h = baseComp.ItemCellsSizeY;
                if (w > 0 && h > 0)
                    return w * h;
            }
            return 1;
        }

        private double GetChaosValue(Entity entity)
        {
            if (_getNinjaValue == null)
                return 0;

            try
            {
                return _getNinjaValue(entity);
            }
            catch
            {
                return 0;
            }
        }
    }

    public class LootCandidate
    {
        public required Entity Entity;
        public string ItemName = "";
        public float Distance;
        public double ChaosValue;
        public int InventorySlots = 1;
        public double ChaosPerSlot;
    }

    public class FailedLootEntry
    {
        public long EntityId;
        public string Reason = "";
        public DateTime FailedAt;
        public int FailCount;

        /// <summary>
        /// Cooldown before retry. Flicker (entity gone before click) gets 0.5s.
        /// Actual click failures escalate: 5s, 15s, 30s.
        /// </summary>
        public TimeSpan Cooldown
        {
            get
            {
                if (Reason == "entity gone before click")
                    return TimeSpan.FromSeconds(0.5);

                return FailCount switch
                {
                    1 => TimeSpan.FromSeconds(5),
                    2 => TimeSpan.FromSeconds(15),
                    _ => TimeSpan.FromSeconds(30),
                };
            }
        }

        public bool IsExpired => DateTime.Now >= FailedAt + Cooldown;
    }
}
