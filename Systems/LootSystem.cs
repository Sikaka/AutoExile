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
        // Price service — set by BotCore each tick
        public NinjaPriceService? PriceService { get; set; }

        // Configurable thresholds
        public int MinUniqueChaosValue { get; set; } = 5;
        public bool SkipLowValueUniques { get; set; } = true;

        /// <summary>
        /// Minimum chaos-per-inventory-slot to pick up a unique.
        /// Set to 0 to disable size-based filtering (only use flat MinUniqueChaosValue).
        /// </summary>
        public int MinChaosPerSlot { get; set; } = 0;

        /// <summary>
        /// Skip quest items (heist contracts, etc.) during loot scans.
        /// </summary>
        public bool IgnoreQuestItems { get; set; } = true;

        // ── Cluster jewel filtering ──
        public bool FilterClusterJewels { get; set; }
        public int MinClusterJewelChaosValue { get; set; }

        // ── Skill gem filtering ──
        public bool FilterSkillGems { get; set; }
        public int MinGemChaosValue { get; set; } = 5;
        public bool AlwaysLoot20QualityGems { get; set; } = true;

        // ── Synthesised item filtering ──
        public bool FilterSynthesisedItems { get; set; }
        /// <summary>Parsed whitelist entries (lowercase). Set from settings comma-separated string.</summary>
        public List<string> SynthesisedWhitelist { get; set; } = new();

        // ── Must-loot uniques (always pick up regardless of value) ──
        /// <summary>Unique item names that bypass value filtering. Case-insensitive matching.</summary>
        public HashSet<string> MustLootUniques { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Callback fired when an item is skipped during scan (value filter, cooldown block, etc.)
        /// or when a pickup fails. Args: (itemName, reason, chaosValue).
        /// Deduped per entity ID — each item only fires once per area.
        /// </summary>
        public Action<string, string, double>? OnItemSkipped { get; set; }

        // State
        public bool HasLootNearby { get; private set; }
        public int LootableCount { get; private set; }
        public string LastSkipReason { get; private set; } = "";
        public string NinjaBridgeStatus => PriceService?.Status ?? "no price service";

        // Cached loot candidates from last scan — always sorted nearest-first
        private readonly List<LootCandidate> _candidates = new();
        public IReadOnlyList<LootCandidate> Candidates => _candidates;

        // Debug: schedule a delayed dump when specific items are seen on the ground
        /// <summary>
        /// When non-null, BotCore should fire both game state + recorder dumps at this time.
        /// Set by Scan() when a unique Large Cluster Jewel is first spotted.
        /// BotCore clears this after firing.
        /// </summary>

        // Failed pickup tracking — items that couldn't be picked up are skipped in future scans
        private readonly Dictionary<long, FailedLootEntry> _failedEntities = new();

        // Dedup for OnItemSkipped — each entity only fires one skip event per area
        private readonly HashSet<long> _loggedSkipIds = new();

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
        public void ClearFailed()
        {
            _failedEntities.Clear();
            _loggedSkipIds.Clear();
        }

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
                    {
                        LogSkipEvent(worldItemEntity.Id, label.Label.Text ?? "?",
                            $"blocked by previous failure: {failEntry.Reason} (attempt {failEntry.FailCount}, cooldown {failEntry.Cooldown.TotalSeconds:F0}s)", 0);
                        continue;
                    }

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
                    var priceResult = GetPriceResult(gc, priceEntity);
                    var chaosValue = priceResult.MaxChaosValue; // Use max for loot decisions
                    var invSlots = GetInventorySlots(priceEntity);
                    var chaosPerSlot = invSlots > 0 ? chaosValue / invSlots : chaosValue;

                    if (SkipLowValueUniques && ShouldSkipUnique(priceEntity, priceResult, chaosPerSlot, itemName))
                    {
                        LogSkipEvent(worldItemEntity.Id, itemName, LastSkipReason, chaosValue);
                        continue;
                    }

                    // Cluster jewel value filter
                    if (FilterClusterJewels && MinClusterJewelChaosValue > 0 &&
                        IsNonUniqueClusterJewel(priceEntity, itemName) &&
                        ShouldSkipClusterJewel(priceResult, itemName))
                    {
                        LogSkipEvent(worldItemEntity.Id, itemName, LastSkipReason, chaosValue);
                        continue;
                    }

                    // Skill gem value filter
                    if (FilterSkillGems && IsSkillGem(priceEntity) && ShouldSkipGem(priceEntity, priceResult, itemName))
                    {
                        LogSkipEvent(worldItemEntity.Id, itemName, LastSkipReason, chaosValue);
                        continue;
                    }

                    // Synthesised implicit whitelist filter
                    if (FilterSynthesisedItems && ShouldSkipSynthesised(priceEntity, itemName))
                    {
                        LogSkipEvent(worldItemEntity.Id, itemName, LastSkipReason, chaosValue);
                        continue;
                    }

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
        /// Items within interactRadius are clicked directly; items beyond require navigation.
        /// Returns (wasInRadius, candidate) if pickup was initiated, or (false, null) if nothing to pick up.
        /// Callers should record to LootTracker only after InteractionResult.Succeeded.
        /// </summary>
        public (bool WasInRadius, LootCandidate? Candidate) PickupNext(InteractionSystem interaction, NavigationSystem nav)
        {
            if (interaction.IsBusy)
                return (false, null);

            // Find best candidate that isn't in the failed list (stale scan results
            // may still contain items that were picked up since the last Scan() call)
            LootCandidate? best = null;
            foreach (var c in _candidates)
            {
                if (_failedEntities.TryGetValue(c.Entity.Id, out var fail) && !fail.IsExpired)
                    continue;
                best = c;
                break;
            }
            if (best == null)
                return (false, null);

            var withinRadius = best.Distance <= interaction.InteractRadius;
            interaction.PickupGroundItem(best.Entity, nav,
                requireProximity: !withinRadius);

            return (withinRadius, best);
        }

        /// <summary>
        /// Check if a non-unique cluster jewel should be skipped based on value.
        /// </summary>
        private bool ShouldSkipClusterJewel(PriceResult priceResult, string itemName)
        {
            var maxValue = priceResult.MaxChaosValue;
            // Can't price it — don't skip (might be valuable)
            if (maxValue <= 0) return false;

            if (maxValue < MinClusterJewelChaosValue)
            {
                LastSkipReason = $"Skipped cluster '{itemName}' ({maxValue:F0}c < {MinClusterJewelChaosValue}c)";
                return true;
            }
            return false;
        }

        private static bool IsNonUniqueClusterJewel(Entity entity, string itemName)
        {
            if (!itemName.EndsWith("Cluster Jewel")) return false;
            if (!entity.TryGetComponent<Mods>(out var mods)) return false;
            return mods.ItemRarity != ItemRarity.Unique;
        }

        /// <summary>
        /// Check if a skill gem should be skipped based on value and quality.
        /// </summary>
        private bool ShouldSkipGem(Entity entity, PriceResult priceResult, string itemName)
        {
            // Check quality — 20q gems always picked up if setting enabled
            if (AlwaysLoot20QualityGems && entity.TryGetComponent<Quality>(out var quality) && quality.ItemQuality >= 20)
                return false;

            var maxValue = priceResult.MaxChaosValue;
            if (maxValue <= 0)
                return false; // Can't price it, don't skip

            if (maxValue < MinGemChaosValue)
            {
                LastSkipReason = $"Skipped gem '{itemName}' ({maxValue:F0}c < {MinGemChaosValue}c)";
                return true;
            }

            return false;
        }

        /// <summary>
        /// Check if a synthesised item should be skipped based on its implicit mods.
        /// Returns true if the item is synthesised and none of its implicits match the whitelist.
        /// </summary>
        private bool ShouldSkipSynthesised(Entity entity, string itemName)
        {
            if (!entity.TryGetComponent<Mods>(out var mods))
                return false;

            // Check if item has synthesised implicits
            // Synthesised items have implicit mods with RawName starting with "Synthesis"
            var implicitMods = mods.ImplicitMods;
            if (implicitMods == null) return false;

            bool isSynthesised = false;
            var implicitTexts = new List<string>();
            foreach (var mod in implicitMods)
            {
                if (mod.RawName != null && mod.RawName.StartsWith("Synthesis", StringComparison.Ordinal))
                {
                    isSynthesised = true;
                    if (!string.IsNullOrEmpty(mod.Translation))
                        implicitTexts.Add(mod.Translation);
                }
            }

            if (!isSynthesised) return false;

            // Check whitelist — if any implicit matches any whitelist entry, keep it
            foreach (var implicitText in implicitTexts)
            {
                foreach (var entry in SynthesisedWhitelist)
                {
                    if (implicitText.Contains(entry, StringComparison.OrdinalIgnoreCase))
                        return false; // Matches whitelist, don't skip
                }
            }

            var implicitSummary = implicitTexts.Count > 0 ? string.Join("; ", implicitTexts) : "unknown";
            LastSkipReason = $"Skipped synthesised '{itemName}' (implicits: {implicitSummary})";
            return true;
        }

        /// <summary>
        /// Check if a skill gem entity based on path.
        /// </summary>
        private static bool IsSkillGem(Entity entity)
        {
            var path = entity.Path;
            if (path == null) return false;
            return path.Contains("Metadata/Items/Gems/", StringComparison.Ordinal);
        }

        /// <summary>
        /// Check if a unique item should be skipped based on value and size.
        /// Uses MaxChaosValue — if the item COULD be valuable, don't skip it.
        /// </summary>
        private bool ShouldSkipUnique(Entity entity, PriceResult priceResult, double chaosPerSlot, string itemName)
        {
            if (!entity.TryGetComponent<Mods>(out var mods))
                return false;

            if (mods.ItemRarity != ItemRarity.Unique)
                return false;

            // Must-loot override — check unique name, art candidates, and display name
            if (MustLootUniques.Count > 0)
            {
                var uniqueName = mods.UniqueName;
                if (!string.IsNullOrEmpty(uniqueName) && MustLootUniques.Contains(uniqueName))
                    return false;

                // For unidentified uniques, resolve art → candidate names
                if (!mods.Identified && PriceService != null)
                {
                    var candidates = PriceService.GetCandidateNames(entity);
                    foreach (var candidate in candidates)
                    {
                        if (MustLootUniques.Contains(candidate))
                            return false;
                    }
                }

                // Also check the label text (visible name)
                if (MustLootUniques.Contains(itemName))
                    return false;
            }

            var maxValue = priceResult.MaxChaosValue;

            // If we can't price it, don't skip (might be valuable)
            if (maxValue <= 0)
                return false;

            // Flat value check — use max (optimistic: don't skip if it could be valuable)
            if (maxValue < MinUniqueChaosValue)
            {
                if (priceResult.MatchCount > 1)
                    LastSkipReason = $"Skipped '{itemName}' (max {maxValue:F0}c across {priceResult.MatchCount} candidates < {MinUniqueChaosValue}c)";
                else
                    LastSkipReason = $"Skipped '{itemName}' ({maxValue:F0}c < {MinUniqueChaosValue}c threshold)";
                return true;
            }

            // Per-slot value check (if enabled)
            if (MinChaosPerSlot > 0 && chaosPerSlot < MinChaosPerSlot)
            {
                var slots = GetInventorySlots(entity);
                LastSkipReason = $"Skipped '{itemName}' ({maxValue:F0}c / {slots} slots = {chaosPerSlot:F1}c/slot < {MinChaosPerSlot}c/slot)";
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

        /// <summary>
        /// Log an item skip/failure event (deduped by entity ID).
        /// Called internally from Scan() and externally from LootPickupTracker on failure.
        /// </summary>
        public void LogSkipEvent(long entityId, string itemName, string reason, double chaosValue)
        {
            if (OnItemSkipped == null) return;
            if (!_loggedSkipIds.Add(entityId)) return; // Already logged this entity
            OnItemSkipped(itemName, reason, chaosValue);
        }

        private PriceResult GetPriceResult(GameController gc, Entity entity)
        {
            if (PriceService == null || !PriceService.IsLoaded)
                return PriceResult.Zero;

            try
            {
                return PriceService.GetPrice(gc, entity);
            }
            catch
            {
                return PriceResult.Zero;
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
        /// Cooldown before retry. Successfully picked up items get 30s (prevent flicker re-pickup).
        /// Flicker (entity gone before click) gets 0.5s.
        /// Actual click failures escalate: 5s, 15s, 30s.
        /// </summary>
        public TimeSpan Cooldown
        {
            get
            {
                if (Reason == "picked up")
                    return TimeSpan.FromSeconds(30);
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
