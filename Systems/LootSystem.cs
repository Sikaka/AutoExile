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
        public bool SkipLowValueUniques { get; set; } = false;

        /// <summary>
        /// Minimum chaos-per-inventory-slot to pick up a unique.
        /// Set to 0 to disable size-based filtering (only use flat MinUniqueChaosValue).
        /// </summary>
        public int MinChaosPerSlot { get; set; } = 0;

        /// <summary>
        /// Skip quest items (heist contracts, etc.) during loot scans.
        /// </summary>
        public bool IgnoreQuestItems { get; set; } = false;

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

        // ── Must-loot items (any rarity — mode-specific whitelist) ──
        /// <summary>Item label names to always pick up. Set by active mode (e.g., boss encounters).
        /// Bypasses ALL value filtering. Case-insensitive substring match.</summary>
        public HashSet<string> MustLootItems { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Callback fired when an item is skipped during scan (value filter, cooldown block, etc.)
        /// or when a pickup fails. Args: (itemName, reason, chaosValue).
        /// Deduped per entity ID — each item only fires once per area.
        /// </summary>
        public Action<string, string, double>? OnItemSkipped { get; set; }

        /// <summary>
        /// Callback fired once per distinct scan fault in an area. Args:
        /// (stage, item name, exception type). Kept separate from item skips so a
        /// transient component read cannot suppress a later filter/pickup event.
        /// </summary>
        public Action<string, string, string>? OnScanFault { get; set; }

        /// <summary>
        /// Bounded evidence emitted only when ExileCore reports visible labels but
        /// the scan produces no candidates. Repeated identical evidence is throttled.
        /// </summary>
        public Action<LootScanEvidence>? OnScanMismatch { get; set; }

        // ── Label toggle unstick ──
        public bool LabelToggleUnstick { get; set; } = true;
        public float LabelToggleCooldownSeconds { get; set; } = 5f;

        /// <summary>Current label toggle state machine phase.</summary>
        public LabelTogglePhase TogglePhase { get; private set; } = LabelTogglePhase.Idle;
        public string ToggleStatus { get; private set; } = "";
        private DateTime _toggleStartTime;
        private DateTime _lastToggleAttempt = DateTime.MinValue;
        private const int ToggleDelayMs = 500;

        public enum LabelTogglePhase
        {
            Idle,
            PressingOff,    // Atomic Z/Z pair is in progress
            WaitingOff,     // Retained for hot-reload compatibility
            PressingOn,     // Retained for hot-reload compatibility
            WaitingOn,      // Short settle period before allowing another scan
        }

        // State
        public bool HasLootNearby { get; private set; }
        /// <summary>
        /// True while a visible, filter-approved item still needs pickup work, including
        /// short price-hydration waits and bounded retries after transient failures.
        /// </summary>
        public bool HasUnresolvedLootNearby { get; private set; }
        public int LootableCount { get; private set; }
        public string LastSkipReason { get; private set; } = "";
        public string NinjaBridgeStatus => PriceService?.Status ?? "no price service";
        public int LastLabelEntryCount { get; private set; }
        public int LastVisibleLabelCount { get; private set; }
        public int LastScanFaultCount { get; private set; }
        public string LastScanFault { get; private set; } = "";

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
        private readonly HashSet<string> _loggedScanFaults = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ScanFaultWindow> _scanFaultWindows = new(StringComparer.Ordinal);
        private uint _loggedSkipAreaHash;
        private readonly Dictionary<string, long> _scanMismatchLoggedAtMs = new(StringComparer.Ordinal);
        private int _scanMismatchSuppressed;
        private const long ScanMismatchWindowMs = 30000;
        private const int ScanMismatchMaxFingerprints = 16;
        private static readonly TimeSpan ScanFaultProgressBlockDuration = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan ScanFaultWindowResetAfter = TimeSpan.FromSeconds(5);

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
            _loggedScanFaults.Clear();
            _scanFaultWindows.Clear();
            _scanMismatchLoggedAtMs.Clear();
            _scanMismatchSuppressed = 0;
            _scanCache.Clear();
        }

        /// <summary>
        /// Scan visible ground items and build a prioritized pickup list.
        /// Excludes items previously marked as failed.
        /// Always sorts by distance (nearest first) for efficient pathing.
        /// Call each tick when not busy to get fresh data.
        /// </summary>
        // ── Scan result cache: avoid re-reading components for items we've already evaluated ──
        // Keyed by entity ID. Cleared on area change. Newly spawned item components can take
        // a few frames to hydrate, so zero-valued results receive a short bounded refresh window.
        private readonly Dictionary<long, CachedScanResult> _scanCache = new(128);
        private const int MaxZeroPriceAttempts = 6;
        private static readonly TimeSpan ZeroPriceRetryDelay = TimeSpan.FromMilliseconds(200);

        private struct CachedScanResult
        {
            public bool ShouldLoot;
            public double ChaosValue;
            public int InventorySlots;
            public double ChaosPerSlot;
            public string ItemName;
            public bool PricePending;
            public PriceResult? Price;
            public int PriceAttempts;
            public DateTime EvaluatedAtUtc;
        }

        public void Scan(GameController gc)
        {
            var areaHash = gc?.IngameState?.Data?.CurrentAreaHash ?? 0;
            if (areaHash != 0 && areaHash != _loggedSkipAreaHash)
            {
                _loggedSkipAreaHash = areaHash;
                _loggedSkipIds.Clear();
            }

            _candidates.Clear();
            HasLootNearby = false;
            HasUnresolvedLootNearby = false;
            LootableCount = 0;
            LastSkipReason = "";
            LastLabelEntryCount = 0;
            LastVisibleLabelCount = 0;
            LastScanFaultCount = 0;
            LastScanFault = "";

            int nullLabels = 0;
            int invisibleLabels = 0;
            int nullEntities = 0;
            int failedCooldownLabels = 0;
            int autoPickupLabels = 0;
            int cacheHits = 0;
            int evaluatedLabels = 0;
            int pricePendingLabels = 0;
            int filteredLabels = 0;
            var sampleLabels = new List<string>(5);

            try
            {
                var labels = gc.IngameState.IngameUi.ItemsOnGroundLabelElement.VisibleGroundItemLabels;
                if (labels == null) return;

                foreach (var label in labels)
                {
                    LastLabelEntryCount++;
                    long scanEntityId = 0;
                    string scanItemName = "?";
                    string scanStage = "label_reference";
                    try
                    {
                        // VisibleGroundItemLabels is already the game's visible-label set. ExileCore
                        // can briefly report the inherited IsVisible flag as false while the local
                        // label itself is rendered, so accept either visibility signal. Requiring
                        // only IsVisible caused rendered loot to disappear from the candidate list.
                        if (label.Label == null)
                        {
                            nullLabels++;
                            continue;
                        }
                        scanStage = "label_visibility";
                        if (!label.Label.IsVisible && !label.Label.IsVisibleLocal)
                        {
                            invisibleLabels++;
                            continue;
                        }
                        LastVisibleLabelCount++;
                        scanStage = "entity_reference";
                        if (label.Entity == null)
                        {
                            nullEntities++;
                            continue;
                        }

                        var worldItemEntity = label.Entity;
                        scanStage = "entity_id";
                        scanEntityId = worldItemEntity.Id;
                        scanStage = "label_text";
                        scanItemName = label.Label.Text ?? "?";
                        if (sampleLabels.Count < 5)
                            sampleLabels.Add($"{scanEntityId}:{scanItemName}");
                        scanStage = "failure_cache";
                        if (_failedEntities.TryGetValue(worldItemEntity.Id, out var failEntry) && !failEntry.IsExpired)
                        {
                            failedCooldownLabels++;
                            // A transient pickup failure must not look like an empty floor to
                            // callers deciding whether it is safe to advance the encounter.
                            // Confirmed pickups and portal-adjacent items are intentionally final;
                            // other failures block progress only for a bounded retry budget.
                            if (failEntry.BlocksProgress)
                                HasUnresolvedLootNearby = true;

                            // A confirmed pickup can leave a stale label for a few frames.
                            // It is intentionally cached to prevent a duplicate click, but
                            // it is not skipped loot and must not pollute loss forensics.
                            if (failEntry.Reason != "picked up")
                            {
                                LogSkipEvent(worldItemEntity.Id, scanItemName,
                                    $"blocked by previous failure: {failEntry.Reason} (attempt {failEntry.FailCount}, cooldown {failEntry.Cooldown.TotalSeconds:F0}s)", 0);
                            }
                            continue;
                        }

                        // Skip gold — auto-pickup on walk-over, no click needed
                        if (scanItemName.EndsWith(" Gold"))
                        {
                            autoPickupLabels++;
                            continue;
                        }

                        // ── Check scan cache: skip component reads for already-evaluated items ──
                        scanStage = "scan_cache";
                        if (_scanCache.TryGetValue(worldItemEntity.Id, out var cached))
                        {
                            cacheHits++;
                            if (cached.PricePending &&
                                cached.PriceAttempts < MaxZeroPriceAttempts &&
                                DateTime.UtcNow - cached.EvaluatedAtUtc >= ZeroPriceRetryDelay)
                            {
                                var refreshed = EvaluateItem(gc, worldItemEntity, scanItemName);
                                refreshed.PriceAttempts = cached.PriceAttempts + 1;
                                refreshed.EvaluatedAtUtc = DateTime.UtcNow;
                                cached = refreshed;
                                _scanCache[worldItemEntity.Id] = cached;
                            }

                            // Do not pick up an item while its newly spawned components may still
                            // hydrate. After the bounded window, preserve the old safe behavior and
                            // loot genuinely unpriced items instead of leaving them on the ground.
                            if (cached.PricePending && cached.PriceAttempts < MaxZeroPriceAttempts)
                            {
                                pricePendingLabels++;
                                HasUnresolvedLootNearby = true;
                                continue;
                            }

                            if (cached.ShouldLoot)
                            {
                                HasUnresolvedLootNearby = true;
                                _candidates.Add(new LootCandidate
                                {
                                    Entity = worldItemEntity,
                                    ItemName = cached.ItemName,
                                    Distance = worldItemEntity.DistancePlayer,
                                    ChaosValue = cached.ChaosValue,
                                    InventorySlots = cached.InventorySlots,
                                    ChaosPerSlot = cached.ChaosPerSlot,
                                });
                            }
                            else
                            {
                                filteredLabels++;
                            }
                            continue;
                        }

                        // ── First time seeing this item — evaluate with full component reads ──
                        scanStage = "evaluate_item";
                        var evaluated = EvaluateItem(gc, worldItemEntity, scanItemName);
                        evaluatedLabels++;
                        evaluated.PriceAttempts = evaluated.PricePending ? 1 : 0;
                        evaluated.EvaluatedAtUtc = DateTime.UtcNow;
                        _scanCache[worldItemEntity.Id] = evaluated;

                        if (evaluated.PricePending && evaluated.PriceAttempts < MaxZeroPriceAttempts)
                        {
                            pricePendingLabels++;
                            HasUnresolvedLootNearby = true;
                            continue;
                        }

                        if (evaluated.ShouldLoot)
                        {
                            HasUnresolvedLootNearby = true;
                            _candidates.Add(new LootCandidate
                            {
                                Entity = worldItemEntity,
                                ItemName = evaluated.ItemName,
                                Distance = worldItemEntity.DistancePlayer,
                                ChaosValue = evaluated.ChaosValue,
                                InventorySlots = evaluated.InventorySlots,
                                ChaosPerSlot = evaluated.ChaosPerSlot,
                            });
                        }
                        else
                        {
                            filteredLabels++;
                        }
                    }
                    catch (Exception ex)
                    {
                        // ExileCore label/entity/component objects can go stale independently.
                        // One bad label must not erase valid siblings or let the encounter advance
                        // as if the floor were empty. Retry it on the next scan and log once.
                        HasUnresolvedLootNearby |= RecordScanFault(
                            scanStage, scanEntityId, scanItemName, ex);
                    }
                }

                // Failed-item labels may flicker out for a frame even though the WorldItem
                // still exists. Preserve the progress blocker from the world entity so a
                // hidden transient failure cannot open the next wave during its cooldown.
                if (!HasUnresolvedLootNearby)
                {
                    foreach (var entity in gc.EntityListWrapper.ValidEntitiesByType[EntityType.WorldItem])
                    {
                        if (entity.DistancePlayer > 30) continue;
                        if (_failedEntities.TryGetValue(entity.Id, out var failed) &&
                            !failed.IsExpired && failed.BlocksProgress)
                        {
                            HasUnresolvedLootNearby = true;
                            break;
                        }
                    }
                }

            }
            catch (Exception ex)
            {
                // Preserve any candidates already built before a stale collection/entity
                // aborted enumeration. Treat the scan as unresolved instead of loot-clear.
                HasUnresolvedLootNearby |= RecordScanFault("collection", 0, "?", ex);
            }
            finally
            {
                // Finalization must run even when ExileCore invalidates one object mid-scan.
                _candidates.Sort((a, b) => a.Distance.CompareTo(b.Distance));
                LootableCount = _candidates.Count;
                HasLootNearby = _candidates.Count > 0;

                if (LastVisibleLabelCount > 0 && LootableCount == 0)
                {
                    RecordScanMismatch(new LootScanEvidence
                    {
                        LabelEntries = LastLabelEntryCount,
                        VisibleLabels = LastVisibleLabelCount,
                        NullLabels = nullLabels,
                        InvisibleLabels = invisibleLabels,
                        NullEntities = nullEntities,
                        FailedCooldownLabels = failedCooldownLabels,
                        AutoPickupLabels = autoPickupLabels,
                        CacheHits = cacheHits,
                        EvaluatedLabels = evaluatedLabels,
                        PricePendingLabels = pricePendingLabels,
                        FilteredLabels = filteredLabels,
                        Candidates = LootableCount,
                        ScanFaults = LastScanFaultCount,
                        FirstFault = LastScanFault,
                        Unresolved = HasUnresolvedLootNearby,
                        SampleLabels = sampleLabels.ToArray(),
                    });
                }
            }
        }

        /// <summary>
        /// Full evaluation of an item — reads components, checks price, applies filters.
        /// Called once per item (result cached for subsequent scans).
        /// </summary>
        private CachedScanResult EvaluateItem(GameController gc, Entity worldItemEntity, string itemName)
        {
            // Must-loot items — mode whitelist, bypass all value filtering
            if (MustLootItems.Count > 0 && MustLootItems.Any(m =>
                itemName.Contains(m, StringComparison.OrdinalIgnoreCase)))
            {
                return new CachedScanResult
                {
                    ShouldLoot = true, ItemName = itemName,
                    ChaosValue = 0, InventorySlots = 1, ChaosPerSlot = 0,
                };
            }

            // Get inner item entity (component read)
            Entity? itemEntity = null;
            if (worldItemEntity.TryGetComponent<WorldItem>(out var worldItem))
                itemEntity = worldItem.ItemEntity;

            // Skip quest items
            if (IgnoreQuestItems && itemEntity is { IsValid: true } &&
                itemEntity.Path.Contains("/Quest", StringComparison.OrdinalIgnoreCase))
                return new CachedScanResult { ShouldLoot = false, ItemName = itemName };

            var priceEntity = (itemEntity is { IsValid: true }) ? itemEntity : worldItemEntity;

            var priceResult = GetPriceResult(gc, priceEntity);
            var chaosValue = priceResult.MaxChaosValue;
            var invSlots = GetInventorySlots(priceEntity);
            var chaosPerSlot = invSlots > 0 ? chaosValue / invSlots : chaosValue;

            // Apply filters
            if (SkipLowValueUniques && ShouldSkipUnique(priceEntity, priceResult, chaosPerSlot, itemName))
            {
                LogSkipEvent(worldItemEntity.Id, itemName, LastSkipReason, chaosValue);
                return new CachedScanResult { ShouldLoot = false, ItemName = itemName };
            }

            if (FilterClusterJewels && MinClusterJewelChaosValue > 0 &&
                IsNonUniqueClusterJewel(priceEntity, itemName) &&
                ShouldSkipClusterJewel(priceResult, itemName))
            {
                LogSkipEvent(worldItemEntity.Id, itemName, LastSkipReason, chaosValue);
                return new CachedScanResult { ShouldLoot = false, ItemName = itemName };
            }

            if (FilterSkillGems && IsSkillGem(priceEntity) && ShouldSkipGem(priceEntity, priceResult, itemName))
            {
                LogSkipEvent(worldItemEntity.Id, itemName, LastSkipReason, chaosValue);
                return new CachedScanResult { ShouldLoot = false, ItemName = itemName };
            }

            if (FilterSynthesisedItems && ShouldSkipSynthesised(priceEntity, itemName))
            {
                LogSkipEvent(worldItemEntity.Id, itemName, LastSkipReason, chaosValue);
                return new CachedScanResult { ShouldLoot = false, ItemName = itemName };
            }

            return new CachedScanResult
            {
                ShouldLoot = true,
                ItemName = itemName,
                ChaosValue = chaosValue,
                InventorySlots = invSlots,
                ChaosPerSlot = chaosPerSlot,
                PricePending = PriceService?.IsLoaded == true && chaosValue <= 0,
                Price = priceResult,
            };
        }

        /// <summary>
        /// Price evaluated for a ground item during this area's scans. Pickup decisions use its
        /// MaxChaosValue; statistics use EstimatedChaosValue (the cheapest line when ambiguous).
        /// </summary>
        public PriceResult? GetCachedPrice(long entityId)
            => _scanCache.TryGetValue(entityId, out var cached) ? cached.Price : null;

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

            if (!interaction.PickupGroundItem(best.Entity, nav,
                    requireProximity: !withinRadius))
                return (false, null);

            return (withinRadius, best);
        }

        // =================================================================
        // Label Toggle Unstick
        // =================================================================

        /// <summary>
        /// Check if a label toggle is needed: no loot candidates but WorldItem entities
        /// exist nearby (labels stacked off-screen after loot explosion).
        /// </summary>
        public bool ShouldToggleLabels(GameController gc)
        {
            if (!LabelToggleUnstick) return false;
            if (TogglePhase != LabelTogglePhase.Idle) return false;
            if ((DateTime.Now - _lastToggleAttempt).TotalSeconds < LabelToggleCooldownSeconds) return false;
            if (_candidates.Count > 0) return false; // still have lootable candidates

            // Check if WorldItem entities exist nearby but we have no candidates
            int nearbyItems = 0;
            foreach (var entity in gc.EntityListWrapper.ValidEntitiesByType[EntityType.WorldItem])
            {
                if (entity.DistancePlayer > 30) continue; // within loot range
                if (_failedEntities.TryGetValue(entity.Id, out var failed) && !failed.IsExpired)
                {
                    // A click/label failure is exactly when a Z/Z layout refresh is useful.
                    // Confirmed pickups and path-only failures must not create toggle spam.
                    var labelFailure = failed.Reason.Contains("click", StringComparison.OrdinalIgnoreCase) ||
                        failed.Reason.Contains("label", StringComparison.OrdinalIgnoreCase);
                    if (!failed.BlocksProgress || !labelFailure)
                        continue;
                }
                nearbyItems++;
                break;
            }

            return nearbyItems >= 1;
        }

        /// <summary>
        /// Start the label toggle sequence. Call when ShouldToggleLabels returns true
        /// and the interaction system is idle.
        /// </summary>
        public bool StartLabelToggle(GameController gc)
        {
            if (TogglePhase != LabelTogglePhase.Idle) return false;

            _lastToggleAttempt = DateTime.Now;
            _toggleStartTime = DateTime.Now;

            // Keep Z/Z atomic. A lone first press hides labels and makes every
            // subsequent empty scan look legitimate.
            if (!BotInput.PressKeyPair(System.Windows.Forms.Keys.Z, ToggleDelayMs)) return false;

            TogglePhase = LabelTogglePhase.PressingOff;
            ToggleStatus = "Refreshing loot labels...";
            return true;
        }

        /// <summary>
        /// Tick the label toggle state machine. Returns true while busy.
        /// Caller should not do other loot actions while this returns true.
        /// </summary>
        public bool TickLabelToggle(GameController gc)
        {
            if (TogglePhase == LabelTogglePhase.Idle) return false;

            var elapsed = (DateTime.Now - _toggleStartTime).TotalMilliseconds;

            switch (TogglePhase)
            {
                case LabelTogglePhase.PressingOff:
                    if (BotInput.IsKeyPairBusy || !BotInput.CanAct) return true;
                    TogglePhase = LabelTogglePhase.WaitingOn;
                    _toggleStartTime = DateTime.Now;
                    ToggleStatus = "Waiting for labels to show...";
                    return true;

                case LabelTogglePhase.WaitingOff:
                case LabelTogglePhase.PressingOn:
                    // Old transient phases can only survive a hot reload. Fail safe by
                    // waiting for any active pair, then enter the normal settle phase.
                    if (BotInput.IsKeyPairBusy || !BotInput.CanAct) return true;
                    TogglePhase = LabelTogglePhase.WaitingOn;
                    _toggleStartTime = DateTime.Now;
                    ToggleStatus = "Waiting for labels to show...";
                    return true;

                case LabelTogglePhase.WaitingOn:
                {
                    if (elapsed < ToggleDelayMs) return true;

                    TogglePhase = LabelTogglePhase.Idle;
                    ToggleStatus = "";
                    return false;
                }
            }

            return false;
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

            // If we can't price it: check if art mapping has candidates.
            // If art resolved to known names but none have ninja prices, the item is unlisted junk — skip it.
            // Only keep "might be valuable" for items with NO art mapping entry (truly unknown).
            if (maxValue <= 0)
            {
                if (!mods.Identified && PriceService != null)
                {
                    var candidates = PriceService.GetCandidateNames(entity);
                    if (candidates.Count > 0)
                    {
                        LastSkipReason = $"Skipped '{itemName}' (art mapped to {string.Join(", ", candidates)} but no ninja price)";
                        return true; // Known art, no price = junk
                    }
                }
                return false; // Unknown art = might be valuable
            }

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

        private bool RecordScanFault(string stage, long entityId, string itemName, Exception error)
        {
            LastScanFaultCount++;
            LastScanFault = $"{stage}: {error.GetType().Name}";

            var fingerprint = $"{stage}:{entityId}:{error.GetType().FullName}";
            var now = DateTime.UtcNow;
            if (!_scanFaultWindows.TryGetValue(fingerprint, out var window) ||
                now - window.LastSeenUtc >= ScanFaultWindowResetAfter)
            {
                window = new ScanFaultWindow { FirstSeenUtc = now };
                _scanFaultWindows[fingerprint] = window;
            }
            window.LastSeenUtc = now;

            if (_loggedScanFaults.Add(fingerprint))
            {
                try { OnScanFault?.Invoke(stage, itemName, error.GetType().Name); }
                catch { }
            }

            // A transient ExileAPI pointer fault must delay progression long enough
            // for the label/entity to hydrate, but a permanently stale object must not
            // deadlock every following wave in the same area.
            return now - window.FirstSeenUtc < ScanFaultProgressBlockDuration;
        }

        private void RecordScanMismatch(LootScanEvidence evidence)
        {
            if (evidence.NullLabels == 0 && evidence.NullEntities == 0 && evidence.ScanFaults == 0 &&
                evidence.PricePendingLabels == 0 && !evidence.Unresolved &&
                evidence.VisibleLabels == evidence.FilteredLabels + evidence.AutoPickupLabels + evidence.FailedCooldownLabels)
                return;

            var fingerprint = $"{evidence.NullLabels > 0}:{evidence.NullEntities > 0}:{evidence.ScanFaults > 0}:" +
                $"{evidence.PricePendingLabels > 0}:{evidence.Unresolved}:{evidence.FirstFault}";
            var now = Environment.TickCount64;
            if (_scanMismatchLoggedAtMs.TryGetValue(fingerprint, out var last) &&
                now - last < ScanMismatchWindowMs)
            {
                _scanMismatchSuppressed++;
                return;
            }

            if (!_scanMismatchLoggedAtMs.ContainsKey(fingerprint) &&
                _scanMismatchLoggedAtMs.Count >= ScanMismatchMaxFingerprints)
            {
                var oldestKey = "";
                var oldest = long.MaxValue;
                foreach (var kv in _scanMismatchLoggedAtMs)
                    if (kv.Value < oldest) { oldest = kv.Value; oldestKey = kv.Key; }
                _scanMismatchLoggedAtMs.Remove(oldestKey);
            }
            _scanMismatchLoggedAtMs[fingerprint] = now;

            if (_scanMismatchSuppressed > 0)
            {
                var labels = new string[evidence.SampleLabels.Length + 1];
                evidence.SampleLabels.CopyTo(labels, 0);
                labels[^1] = $"suppressedCount={_scanMismatchSuppressed}";
                evidence = new LootScanEvidence
                {
                    LabelEntries = evidence.LabelEntries,
                    VisibleLabels = evidence.VisibleLabels,
                    NullLabels = evidence.NullLabels,
                    InvisibleLabels = evidence.InvisibleLabels,
                    NullEntities = evidence.NullEntities,
                    FailedCooldownLabels = evidence.FailedCooldownLabels,
                    AutoPickupLabels = evidence.AutoPickupLabels,
                    CacheHits = evidence.CacheHits,
                    EvaluatedLabels = evidence.EvaluatedLabels,
                    PricePendingLabels = evidence.PricePendingLabels,
                    FilteredLabels = evidence.FilteredLabels,
                    Candidates = evidence.Candidates,
                    ScanFaults = evidence.ScanFaults,
                    Unresolved = evidence.Unresolved,
                    FirstFault = evidence.FirstFault,
                    SampleLabels = labels,
                };
                _scanMismatchSuppressed = 0;
            }
            try { OnScanMismatch?.Invoke(evidence); }
            catch { }
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
        /// Entity-gone failures escalate: first time 0.5s (might be flicker), then permanent.
        /// Actual click failures escalate: 5s, 15s, 30s.
        /// </summary>
        public TimeSpan Cooldown
        {
            get
            {
                if (Reason == "picked up")
                    return TimeSpan.FromSeconds(30);
                if (Reason == "entity gone before click")
                {
                    // First time: brief cooldown (might be label flicker).
                    // Second time: entity is definitively gone — long block.
                    return FailCount <= 1
                        ? TimeSpan.FromSeconds(0.5)
                        : TimeSpan.FromSeconds(120);
                }

                return FailCount switch
                {
                    1 => TimeSpan.FromSeconds(5),
                    2 => TimeSpan.FromSeconds(15),
                    _ => TimeSpan.FromSeconds(30),
                };
            }
        }

        public bool IsExpired => DateTime.Now >= FailedAt + Cooldown;

        /// <summary>
        /// Whether encounter progress should wait for this visible item to become retryable.
        /// Transient failures receive two cooldown-backed retries; safety skips and confirmed
        /// pickups never hold the encounter open indefinitely.
        /// </summary>
        public bool BlocksProgress => Reason switch
        {
            "picked up" => false,
            "near portal" => false,
            "entity gone before click" => FailCount <= 1,
            _ => FailCount <= 2,
        };
    }

    internal sealed class ScanFaultWindow
    {
        public DateTime FirstSeenUtc;
        public DateTime LastSeenUtc;
    }

    public sealed class LootScanEvidence
    {
        public int LabelEntries { get; init; }
        public int VisibleLabels { get; init; }
        public int NullLabels { get; init; }
        public int InvisibleLabels { get; init; }
        public int NullEntities { get; init; }
        public int FailedCooldownLabels { get; init; }
        public int AutoPickupLabels { get; init; }
        public int CacheHits { get; init; }
        public int EvaluatedLabels { get; init; }
        public int PricePendingLabels { get; init; }
        public int FilteredLabels { get; init; }
        public int Candidates { get; init; }
        public int ScanFaults { get; init; }
        public bool Unresolved { get; init; }
        public string FirstFault { get; init; } = "";
        public string[] SampleLabels { get; init; } = Array.Empty<string>();
    }
}
