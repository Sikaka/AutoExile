using ExileCore;
using ExileCore.PoEMemory;
using ExileCore.PoEMemory.MemoryObjects;
using AutoExile.Systems;
using System.Numerics;

namespace AutoExile.Modes.Shared
{
    /// <summary>
    /// Shared hideout flow: settle → stash → open map via MapDevice → enter portal.
    /// Used by BlightMode and SimulacrumMode to replace 5 duplicated hideout methods.
    /// </summary>
    public class HideoutFlow
    {
        private HideoutPhase _phase = HideoutPhase.Idle;
        private DateTime _phaseStartTime = DateTime.Now;
        private DateTime _lastActionTime = DateTime.MinValue;
        private bool _restockRequired; // Reactive flag set when Map Device is confirmed empty
        private bool _storageChecked;  // true once CheckDevice has run this hideout visit

        // Configuration set via Start()
        private Func<Element, bool>? _mapFilter;
        private Func<ServerInventory.InventSlotItem, bool>? _stashItemFilter;
        private string? _targetMapName;
        private string? _inventoryFragmentPath;
        private int _minMapTier;
        private int _stashItemThreshold; // only stash when item count >= this (0 = always stash)
        private List<string>? _dumpTabNames;
        private string? _resourceTabName;
        private string? _withdrawFragmentPath;
        private int _fragmentStock; // target number of fragments to maintain in inventory
        private int _minFragments; // minimum fragments needed to open (0 = any amount works)
        private bool _fragmentRequired; // when false, missing fragments don't stop the run
        private List<(string TabName, string PathSubstring)>? _scarabSlots;
        private bool _autoRestock;
        private string? _mapRestockPath; // path substring for inventory restock ("Maps/" = any map)
        private int _mapRestockMinTier; // minimum tier when restocking maps (0 = no tier filter)
        private List<(string TabName, string PathSubstring, int Count, int MinTier)>? _extraWithdrawals;
        private int _deviceStorageRefillThreshold;

        private const float BasePortalTimeoutSeconds = 15f;
        private const float MapDeviceRetrySeconds = 10f;
        private const float ActionCooldownMs = 500f;

        /// <summary>Called after the stash index scan completes. Used by modes to re-configure
        /// HideoutFlow parameters (e.g. tab names) with fresh index data, then call Start() again.</summary>
        public Action<BotContext>? OnIndexComplete { get; set; }

        public string Status { get; private set; } = "";
        public bool IsActive => _phase != HideoutPhase.Idle;

        /// <summary>
        /// Start a full hideout flow: settle → stash → open map → enter portal.
        /// </summary>
        public void Start(Func<Element, bool> mapFilter,
            Func<ServerInventory.InventSlotItem, bool>? stashItemFilter = null,
            string? targetMapName = null, int minMapTier = 0,
            string? inventoryFragmentPath = null,
            int stashItemThreshold = 0,
            List<string>? dumpTabNames = null,
            string? resourceTabName = null,
            string? withdrawFragmentPath = null,
            int fragmentStock = 0,
            int minFragments = 1,
            bool fragmentRequired = true,
            List<(string TabName, string PathSubstring)>? scarabSlots = null,
            bool autoRestock = false,
            string? mapRestockPath = null,
            int mapRestockMinTier = 0,
            List<(string TabName, string PathSubstring, int Count, int MinTier)>? extraWithdrawals = null,
            int deviceStorageRefillThreshold = 0)
        {
            _mapFilter = mapFilter;
            _stashItemFilter = stashItemFilter;
            _targetMapName = targetMapName;
            _inventoryFragmentPath = inventoryFragmentPath;
            _minMapTier = minMapTier;
            _stashItemThreshold = stashItemThreshold;
            _dumpTabNames = dumpTabNames;
            _resourceTabName = resourceTabName;
            _withdrawFragmentPath = withdrawFragmentPath;
            _fragmentStock = fragmentStock;
            _minFragments = minFragments;
            _fragmentRequired = fragmentRequired;
            _scarabSlots = scarabSlots;
            _autoRestock = autoRestock;
            _mapRestockPath = mapRestockPath;
            _mapRestockMinTier = mapRestockMinTier;
            _extraWithdrawals = extraWithdrawals;
            _deviceStorageRefillThreshold = deviceStorageRefillThreshold;
            _restockRequired = false;
            // Don't reset _storageChecked here — if OnIndexComplete triggers a re-Start(),
            // we want to preserve the already-completed storage check.
            _phase = HideoutPhase.Settle;
            _phaseStartTime = DateTime.Now;
            Status = "Hideout — settling";
        }

        /// <summary>
        /// Start portal re-entry flow (after death): find portal → navigate → click.
        /// </summary>
        public void StartPortalReentry()
        {
            _mapFilter = null;
            _phase = HideoutPhase.EnterPortal;
            _phaseStartTime = DateTime.Now;
            Status = "Re-entering map via portal";
        }

        /// <summary>
        /// Tick the hideout flow. Returns a signal for the mode to act on.
        /// </summary>
        public HideoutSignal Tick(BotContext ctx)
        {
            switch (_phase)
            {
                case HideoutPhase.Settle:
                    return TickSettle(ctx);
                case HideoutPhase.CheckDevice:
                    return TickCheckDevice(ctx);
                case HideoutPhase.CleanDevice:
                    // CleanDevice is now pipelined inside CheckDevice's single atlas session.
                    // This case is never reached in normal flow; advance to Settle as a safety net.
                    _phase = HideoutPhase.Settle;
                    _phaseStartTime = DateTime.MinValue;
                    return HideoutSignal.InProgress;
                case HideoutPhase.IndexStash:
                    return TickIndexStash(ctx);
                case HideoutPhase.Stash:
                    return TickStash(ctx);
                case HideoutPhase.StockDevice:
                    return TickStockDevice(ctx);
                case HideoutPhase.FaustusRestock:
                    return TickFaustusRestock(ctx);
                case HideoutPhase.OpenMap:
                    return TickOpenMap(ctx);
                case HideoutPhase.EnterPortal:
                    return TickEnterPortal(ctx);
                default:
                    return HideoutSignal.InProgress;
            }
        }

        public void Cancel()
        {
            _phase = HideoutPhase.Idle;
            _mapFilter = null;
            _stashItemFilter = null;
            _targetMapName = null;
            _inventoryFragmentPath = null;
            _minMapTier = 0;
            _stashItemThreshold = 0;
            _dumpTabNames = null;
            _resourceTabName = null;
            _withdrawFragmentPath = null;
            _fragmentStock = 0;
            _minFragments = 1;
            _fragmentRequired = true;
            _scarabSlots = null;
            _autoRestock = false;
            _mapRestockPath = null;
            _mapRestockMinTier = 0;
            _deviceStorageRefillThreshold = 0;
            _storageChecked = false;  // reset for next hideout visit
            Status = "";
        }

        // ── Phases ──

        private HideoutSignal TickSettle(BotContext ctx)
        {
            var elapsed = (DateTime.Now - _phaseStartTime).TotalSeconds;
            if (elapsed < ctx.Settings.AreaSettleSeconds.Value)
            {
                Status = $"Hideout — waiting for game state ({elapsed:F1}s)";
                return HideoutSignal.InProgress;
            }

            // Check device storage first (once per hideout visit, before deciding how much to withdraw)
            if (!_storageChecked)
            {
                _phase = HideoutPhase.CheckDevice;
                _phaseStartTime = DateTime.Now;
                Status = "Checking map device storage…";
                return HideoutSignal.InProgress;
            }

            // Count fragments and non-fragment loot in inventory
            int fragmentsInInventory = StashSystem.CountInventoryItems(ctx.Game, _withdrawFragmentPath);
            int lootItems = StashSystem.CountNonMatchingItems(ctx.Game, _withdrawFragmentPath);

            // Only withdraw when below minimum needed — don't top up each run
            bool usesFragments = !string.IsNullOrEmpty(_withdrawFragmentPath);
            int minNeeded = _minFragments > 0 ? _minFragments : 1;
            bool canWithdraw = usesFragments
                && !string.IsNullOrEmpty(_resourceTabName)
                && _fragmentStock > 0;
            bool needWithdraw = canWithdraw && fragmentsInInventory < minNeeded;
            int withdrawNeeded = needWithdraw ? _fragmentStock : 0;

            // AutoRestock check: only trigger if we've already checked the Map Device and found it empty
            bool needRestock = false;
            if (_autoRestock && _restockRequired && ctx.StashIndex.IsComplete)
            {
                if (!string.IsNullOrEmpty(_mapRestockPath) &&
                    StashSystem.CountInventoryItems(ctx.Game, _mapRestockPath, _mapRestockMinTier) == 0)
                {
                    var tab = FindSourceTab(ctx, _mapRestockPath, _mapRestockMinTier);
                    if (tab != null) { 
                        needRestock = true; 
                        ctx.Log($"[HideoutFlow] Map missing from inventory. Found replacement in index tab '{tab}'");
                    }
                }
                if (!needRestock && _scarabSlots != null)
                {
                    foreach (var s in _scarabSlots.Where(x => string.IsNullOrEmpty(x.TabName)))
                    {
                        if (StashSystem.CountInventoryItems(ctx.Game, s.PathSubstring) == 0)
                        {
                            if (FindSourceTab(ctx, s.PathSubstring) != null) { needRestock = true; break; }
                        }
                    }
                }
            }

            // Check if any ExtraWithdrawals (like Simulacrum fragments) are missing from inventory.
            if (!needRestock && _extraWithdrawals != null)
            {
                foreach (var ex in _extraWithdrawals)
                {
                    int invCount = StashSystem.CountInventoryItems(ctx.Game, ex.PathSubstring, ex.MinTier);
                    if (invCount >= ex.Count) continue;

                    if (!ctx.StashIndex.IsComplete)
                    {
                        // Index hasn't run yet — we can't confirm stash has items, but we need to run
                        // the index to find out. Trigger a stash visit which will run IndexStash first.
                        needRestock = true;
                        ctx.Log($"[HideoutFlow] '{ex.PathSubstring}' missing from inventory ({invCount}/{ex.Count}), stash index not yet complete — triggering IndexStash.");
                        break;
                    }

                    var tab = !string.IsNullOrEmpty(ex.TabName) ? ex.TabName : FindSourceTab(ctx, ex.PathSubstring, ex.MinTier);
                    if (tab != null)
                    {
                        needRestock = true;
                        ctx.Log($"[HideoutFlow] '{ex.PathSubstring}' missing ({invCount}/{ex.Count}). Found in stash tab '{tab}'.");
                        break;
                    }
                    else
                    {
                        ctx.Log($"[HideoutFlow] '{ex.PathSubstring}' missing ({invCount}/{ex.Count}) and NOT found in stash index (index has {ctx.StashIndex.CountByPath(ex.PathSubstring, ex.MinTier)} matches). Cannot restock.");
                    }
                }
            }

            // Not enough fragments and no way to get more — signal stop (only for modes that use fragments)
            if (usesFragments && fragmentsInInventory < minNeeded && !canWithdraw)
            {
                if (_fragmentRequired)
                {
                    Status = "No fragments in inventory";
                    _phase = HideoutPhase.Idle;
                    return HideoutSignal.NoFragments;
                }
                // Optional fragment (e.g. omen) — just proceed without it
                Status = "Optional item not available — proceeding anyway";
                needWithdraw = false;
            }

            // Stash loot only if non-fragment items exceed threshold
            bool needStore = false;
            if (StashSystem.HasStashableItems(ctx.Game, _stashItemFilter))
                needStore = _stashItemThreshold <= 0 || lootItems >= _stashItemThreshold;

            if (needWithdraw || needStore || needRestock)
            {
                // Run stash indexer first if not yet complete this session
                if (!ctx.StashIndex.IsComplete)
                {
                    _phase = HideoutPhase.IndexStash;
                    _phaseStartTime = DateTime.Now;
                    Status = "Indexing stash tabs…";
                    return HideoutSignal.InProgress;
                }
                _phase = HideoutPhase.Stash;
                _phaseStartTime = DateTime.Now;
                var baseFilter = needStore ? _stashItemFilter : (_ => false);
                // Skip items matching the withdraw path (e.g. omen) — they were just pulled from stash
                if (!string.IsNullOrEmpty(_withdrawFragmentPath))
                {
                    var omenPath = _withdrawFragmentPath;
                    var inner = baseFilter;
                    baseFilter = si => si.Item?.Path?.Contains(omenPath, StringComparison.OrdinalIgnoreCase) == true
                        ? false
                        : (inner == null || inner(si));
                }
                if (ctx.Settings.Loot.UseOmenOfAmelioration.Value)
                {
                    var inner = baseFilter;
                    baseFilter = si => si.PosX == 11 && si.PosY == 4 ? false : (inner == null || inner(si));
                }
                ctx.Stash.ItemFilter = baseFilter;
                ctx.Stash.StoreTabNames = needStore ? _dumpTabNames : null;
                ctx.Stash.WithdrawTabName = needWithdraw ? _resourceTabName : null;
                ctx.Stash.WithdrawFragmentPath = needWithdraw ? _withdrawFragmentPath : null;
                ctx.Stash.WithdrawCount = withdrawNeeded;
                ctx.Stash.ExtraWithdrawals.Clear();
                // Caller-supplied extra withdrawals (e.g. portal scrolls, splinters).
                // For items that go into device storage, scale the count by empty storage slots.
                int storageEmpty = ctx.MapDevice.StorageEmptySlots; // -1 if unknown or no storage
                if (_extraWithdrawals != null)
                {
                    foreach (var e in _extraWithdrawals)
                    {
                        // Resolve tab name lazily — may have been empty at Start() if index wasn't ready yet
                        var resolvedTab = string.IsNullOrEmpty(e.TabName)
                            ? FindSourceTab(ctx, e.PathSubstring, e.MinTier) ?? ""
                            : e.TabName;
                        if (string.IsNullOrEmpty(resolvedTab)) continue;
                        bool isDeviceStockItem = !string.IsNullOrEmpty(_inventoryFragmentPath)
                            && e.PathSubstring.Contains(_inventoryFragmentPath, StringComparison.OrdinalIgnoreCase);
                        
                        int count = e.Count;
                        if (isDeviceStockItem && _deviceStorageRefillThreshold > 0)
                        {
                            if (storageEmpty < 0)
                            {
                                // No device storage read (likely because device has no storage UI, e.g. Simulacrum).
                                // Withdraw the requested count.
                                count = e.Count;
                            }
                            else if (storageEmpty >= _deviceStorageRefillThreshold)
                            {
                                count = Math.Max(e.Count, storageEmpty);
                            }
                            else
                            {
                                count = 0; // We have enough in the device, do not withdraw
                            }
                        }
                        
                        if (count > 0)
                        {
                            ctx.Stash.ExtraWithdrawals.Add((resolvedTab, e.PathSubstring, count, e.MinTier));
                        }
                    }
                }
                // Scarabs: only add to ExtraWithdrawals if a stash tab is specified.
                // Slots with no tab rely on MapDeviceSystem finding the item already in inventory.
                if (_scarabSlots != null)
                    foreach (var s in _scarabSlots)
                        if (!string.IsNullOrEmpty(s.TabName))
                            ctx.Stash.ExtraWithdrawals.Add((s.TabName, s.PathSubstring, 1, 0));

                // AutoRestock: pull missing items using indexer to find the right tab
                if (_autoRestock)
                {
                    // Maps
                    if (!string.IsNullOrEmpty(_mapRestockPath) && 
                        StashSystem.CountInventoryItems(ctx.Game, _mapRestockPath, _mapRestockMinTier) == 0)
                    {
                        var tab = FindSourceTab(ctx, _mapRestockPath, _mapRestockMinTier);
                        if (tab != null) ctx.Stash.ExtraWithdrawals.Add((tab, _mapRestockPath, 1, _mapRestockMinTier));
                    }

                    // Scarabs
                    if (_scarabSlots != null)
                    {
                        foreach (var s in _scarabSlots.Where(x => string.IsNullOrEmpty(x.TabName)))
                        {
                            if (StashSystem.CountInventoryItems(ctx.Game, s.PathSubstring) == 0)
                            {
                                var tab = FindSourceTab(ctx, s.PathSubstring);
                                if (tab != null) ctx.Stash.ExtraWithdrawals.Add((tab, s.PathSubstring, 1, 0));
                            }
                        }
                    }
                }

                ctx.Stash.Start();
                var parts = new List<string>();
                if (needWithdraw) parts.Add($"withdraw {withdrawNeeded} fragments");
                if (needStore) parts.Add($"stash {lootItems} loot items");
                if (needRestock) parts.Add("restock maps/scarabs");
                Status = string.Join(" & ", parts);
                return HideoutSignal.InProgress;
            }

            // No items to stash — run indexer if needed, then Faustus restock
            if (!ctx.StashIndex.IsComplete)
            {
                _phase = HideoutPhase.IndexStash;
                _phaseStartTime = DateTime.Now;
                Status = "Indexing stash tabs…";
                return HideoutSignal.InProgress;
            }
            _phase = HideoutPhase.FaustusRestock;
            _phaseStartTime = DateTime.Now;
            return HideoutSignal.InProgress;
        }

        private string? FindSourceTab(BotContext ctx, string pathSubstring, int minTier = 0)
        {
            // If dump tabs are specified, check them first via index
            if (_dumpTabNames != null && _dumpTabNames.Count > 0)
            {
                foreach (var tabName in _dumpTabNames)
                {
                    if (ctx.StashIndex.Tabs.Any(t => string.Equals(t.Name, tabName, StringComparison.OrdinalIgnoreCase)
                        && t.Items.Any(i => i.ItemPath.Contains(pathSubstring, StringComparison.OrdinalIgnoreCase) && i.MapTier >= minTier)))
                        return tabName;
                }
            }
            // Fallback: use indexer to find the best tab overall
            var best = ctx.StashIndex.BestTabForPath(pathSubstring, minTier)?.Name;
            if (!string.IsNullOrEmpty(best)) return best;

            // Fallback: affinity tabs if indexer fails to see items inside sub-tabs
            if (pathSubstring.Contains("MapFragments", StringComparison.OrdinalIgnoreCase) || 
                pathSubstring.Contains("CurrencyAfflictionFragment", StringComparison.OrdinalIgnoreCase))
                return ctx.StashIndex.FragmentTab?.Name ?? ctx.StashIndex.DeliriumTab?.Name;
                
            if (pathSubstring.Contains("Scarabs", StringComparison.OrdinalIgnoreCase))
                return ctx.StashIndex.FindTabByAffinity("Scarab")?.Name;

            if (pathSubstring.Contains("Currency", StringComparison.OrdinalIgnoreCase))
                return ctx.StashIndex.CurrencyTab?.Name;

            return null;
        }

        private HideoutSignal TickIndexStash(BotContext ctx)
        {
            var gc = ctx.Game;
            var stashEl = gc.IngameState.IngameUi.StashElement;

            // Open stash if not already open
            if (stashEl?.IsVisible != true)
            {
                var openResult = ctx.Stash.TickOpenOnly(gc, ctx.Navigation);
                if (openResult == StashResult.Failed)
                {
                    Status = "Could not open stash for index — skipping";
                    _phase = HideoutPhase.FaustusRestock;
                    _phaseStartTime = DateTime.Now;
                    return HideoutSignal.InProgress;
                }
                Status = "Opening stash for tab index scan…";
                return HideoutSignal.InProgress;
            }

            // Start the indexer on first entry
            if (!ctx.StashIndex.IsRunning && !ctx.StashIndex.IsComplete)
                ctx.StashIndex.Start();

            // Drive the indexer
            ctx.StashIndex.Tick(gc);
            Status = $"Index: {ctx.StashIndex.Status}";

            if (ctx.StashIndex.IsComplete)
            {
                // Log findings
                LogIndexSummary(ctx);

                // Invoke callback so modes can reconfigure HideoutFlow with tab data,
                // then loop back to Settle to re-evaluate with the updated config.
                OnIndexComplete?.Invoke(ctx);

                // If OnIndexComplete called Start() it already set _phase = Settle.
                // If not, advance to Settle ourselves.
                if (_phase == HideoutPhase.IndexStash)
                {
                    _phase = HideoutPhase.Settle;
                    _phaseStartTime = DateTime.Now;
                }
            }

            return HideoutSignal.InProgress;
        }

        private void LogIndexSummary(BotContext ctx)
        {
            var idx = ctx.StashIndex;
            ctx.Log($"[StashIndex] Total found: {idx.TotalSow} SoW, {idx.TotalScarabs} Scarabs, {idx.TotalSimulacrums} Simulacrums.");
            if (idx.TotalSimulacrums > 0)
                ctx.Log($"[StashIndex] Simulacrum source: tab '{idx.SimulacrumTabName}'");
            ctx.Log($"[StashIndex] Special tabs: Currency='{idx.CurrencyTab?.Name ?? "none"}', Fragment='{idx.FragmentTab?.Name ?? "none"}'");
        }

        private HideoutSignal TickStash(BotContext ctx)
        {
            var result = ctx.Stash.Tick(ctx.Game, ctx.Navigation);

            switch (result)
            {
                case StashResult.Succeeded:
                case StashResult.Failed:
                {
                    // Verify we have enough fragments before proceeding to map device
                    if (!string.IsNullOrEmpty(_withdrawFragmentPath) && !string.IsNullOrEmpty(_resourceTabName))
                    {
                        int frags = StashSystem.CountInventoryItems(ctx.Game, _withdrawFragmentPath);
                        int needed = _minFragments > 0 ? _minFragments : 1;
                        if (frags < needed)
                        {
                            if (_fragmentRequired)
                            {
                                Status = $"Not enough fragments ({frags}/{needed}) — stopping";
                                _phase = HideoutPhase.Idle;
                                return HideoutSignal.NoFragments;
                            }
                            Status = $"Optional item not in inventory ({frags}/{needed}) — proceeding anyway";
                        }
                    }

                    Status = result == StashResult.Succeeded
                        ? $"Stash done ({ctx.Stash.ItemsStored} stored) — stocking device storage"
                        : $"Stash issue: {ctx.Stash.Status} — stocking device storage";
                    _phase = HideoutPhase.StockDevice;
                    _phaseStartTime = DateTime.Now;
                    break;
                }
                default:
                    Status = $"Stashing: {ctx.Stash.Status}";
                    break;
            }
            return HideoutSignal.InProgress;
        }

        private HideoutSignal TickCheckDevice(BotContext ctx)
        {
            if (!ctx.MapDevice.IsBusy)
            {
                // Build the clean filter upfront and pass it to StartCheckStorage so
                // Check + Clean happen in a single atlas session (one device navigate+open instead of two).
                var cleanFilter = BuildCleanStorageFilter();
                if (!ctx.MapDevice.StartCheckStorage(cleanFilter))
                {
                    // Couldn't start (shouldn't happen) — skip storage check
                    _storageChecked = true;
                    _phase = HideoutPhase.Settle;
                    _phaseStartTime = DateTime.Now;
                    return HideoutSignal.InProgress;
                }
            }

            var result = ctx.MapDevice.Tick(ctx.Game, ctx.Navigation);
            Status = $"Checking/cleaning device storage: {ctx.MapDevice.Status}";

            if (result == MapDeviceResult.Succeeded || result == MapDeviceResult.Failed)
            {
                _storageChecked = true;
                ctx.Log($"[HideoutFlow] Device storage: {ctx.MapDevice.StorageFilledSlots}/{ctx.MapDevice.StorageTotalSlots} filled, {ctx.MapDevice.StorageEmptySlots} empty slots");
                // Check+Clean are now combined — skip CleanDevice phase, go straight to Settle.
                // If Clean ejected items, we still need a Settle pass so they get stashed.
                _phase = HideoutPhase.Settle;
                _phaseStartTime = DateTime.MinValue; // skip re-settle wait
            }
            return HideoutSignal.InProgress;
        }

        /// <summary>Builds the storage clean filter: keep scarabs and current-run maps/fragments, eject everything else.</summary>
        private Func<ExileCore.PoEMemory.MemoryObjects.Entity, bool> BuildCleanStorageFilter()
        {
            return (entity) =>
            {
                // Keep scarabs matching configured slots
                if (_scarabSlots != null && _scarabSlots.Count > 0)
                {
                    var path = entity.Path;
                    if (!string.IsNullOrEmpty(path) && _scarabSlots.Any(s => path.Contains(s.PathSubstring, StringComparison.OrdinalIgnoreCase)))
                        return true;
                }

                // Keep primary restock path (map or fragment)
                if (!string.IsNullOrEmpty(_mapRestockPath))
                {
                    if (entity.Path?.Contains(_mapRestockPath, StringComparison.OrdinalIgnoreCase) == true)
                    {
                        if (_mapRestockMinTier > 0)
                        {
                            if (entity.TryGetComponent<ExileCore.PoEMemory.Components.MapKey>(out var mk) && mk.Tier >= _mapRestockMinTier)
                                return true;
                            return false; // wrong tier
                        }
                        return true;
                    }
                }
                if (!string.IsNullOrEmpty(_inventoryFragmentPath))
                {
                    if (entity.Path?.Contains(_inventoryFragmentPath, StringComparison.OrdinalIgnoreCase) == true)
                        return true;
                }

                return false;
            };
        }

        private HideoutSignal TickStockDevice(BotContext ctx)
        {
            string? stockPath = _inventoryFragmentPath ?? _mapRestockPath;
            int stockMinTier = string.IsNullOrEmpty(_inventoryFragmentPath) ? _mapRestockMinTier : 0;
            int emptySlots = ctx.MapDevice.StorageEmptySlots;

            if (string.IsNullOrEmpty(stockPath))
            {
                _phase = HideoutPhase.FaustusRestock;
                _phaseStartTime = DateTime.Now;
                return HideoutSignal.InProgress;
            }

            if (!ctx.MapDevice.IsBusy)
            {
                int inInv = StashSystem.CountInventoryItems(ctx.Game, stockPath, stockMinTier);
                if (inInv == 0 || emptySlots <= 0)
                {
                    Status = inInv == 0 
                        ? $"No '{stockPath}' in inventory to stock into device storage"
                        : "Device storage is full — skipping stock phase";
                    _phase = HideoutPhase.FaustusRestock;
                    _phaseStartTime = DateTime.Now;
                    return HideoutSignal.InProgress;
                }

                int toStock = Math.Min(inInv, emptySlots);
                if (!ctx.MapDevice.StartStockStorage(stockPath, stockMinTier, toStock))
                {
                    _phase = HideoutPhase.FaustusRestock;
                    _phaseStartTime = DateTime.Now;
                    return HideoutSignal.InProgress;
                }
            }

            var result = ctx.MapDevice.Tick(ctx.Game, ctx.Navigation);
            Status = $"Stocking device storage: {ctx.MapDevice.Status}";

            if (result == MapDeviceResult.Succeeded || result == MapDeviceResult.Failed)
            {
                _phase = HideoutPhase.FaustusRestock;
                _phaseStartTime = DateTime.Now;
            }
            return HideoutSignal.InProgress;
        }

        private HideoutSignal TickFaustusRestock(BotContext ctx)
        {
            var settings = ctx.Settings.Faustus;

            if (!settings.EnableFaustusRestock.Value)
            {
                // Restock disabled — go straight to map
                _phase = HideoutPhase.OpenMap;
                _phaseStartTime = DateTime.Now;
                StartMapDevice(ctx);
                return HideoutSignal.InProgress;
            }

            var faustus = ctx.Faustus;

            // If Faustus system is currently running, tick it
            if (faustus.IsBusy)
            {
                var result = faustus.Tick(ctx);
                Status = $"Faustus restock: {faustus.Status}";
                if (result == FaustusResult.Succeeded || result == FaustusResult.Failed)
                {
                    // Done (success or failure) — move on to opening map
                    _phase = HideoutPhase.OpenMap;
                    _phaseStartTime = DateTime.Now;
                    StartMapDevice(ctx);
                }
                return HideoutSignal.InProgress;
            }

            var payCurrency = settings.FaustusPayCurrency.Value;

            // 1) Check Boss fragments if a fragment path is mandated by the current mode
            if (!string.IsNullOrEmpty(_inventoryFragmentPath))
            {
                // Using BossSettings FragmentStock or a default fallback if we don't have BossSettings specifically
                int needed = ctx.Settings.Boss?.FragmentStock?.Value > 0 ? ctx.Settings.Boss.FragmentStock.Value : 1;
                int fragInv = StashSystem.CountInventoryItems(ctx.Game, _inventoryFragmentPath);
                int fragStash = ctx.StashIndex.CountByPath(_inventoryFragmentPath, 0);

                if (fragInv + fragStash < needed)
                {
                    int deficit = needed - (fragInv + fragStash);
                    // Map internal path suffix → Faustus search bar display name
                    string baseName = FaustusFragmentNames.Resolve(_inventoryFragmentPath);
                    
                    var fragPriceResult = ctx.NinjaPrice.GetPrice(baseName, NinjaPriceCategory.Fragment);
                    int chaosCost = 0;
                    if (fragPriceResult.MaxChaosValue > 0)
                    {
                        chaosCost = (int)Math.Ceiling(deficit * fragPriceResult.MaxChaosValue);
                        ctx.Log($"[Faustus] Buying {deficit} boss fragments ({baseName}) @ ~{fragPriceResult.MaxChaosValue:F1}c each ≈ {chaosCost}c total limit");
                    }

                    Status = $"Faustus: buying boss fragments ({fragInv} inv + {fragStash} stash < {needed})";
                    faustus.Start(_inventoryFragmentPath, baseName, deficit, payCurrency, payCurrency, chaosCost,
                        ninjaName: baseName, ninjaCategory: "Fragment", ninjaLeague: "");
                    return HideoutSignal.InProgress;
                }
            }

            // 2) Check scarab count (Inventory + Stash)
            int scarabInv = StashSystem.CountInventoryItems(ctx.Game, "Metadata/Items/Scarabs/");
            if (scarabInv + ctx.StashIndex.TotalScarabs < settings.ScarabRestockThreshold.Value)
            {
                int deficit = settings.ScarabRestockThreshold.Value - (scarabInv + ctx.StashIndex.TotalScarabs);
                int scarabBuyQty = settings.ScarabBuyAmount.Value > 0 ? settings.ScarabBuyAmount.Value : deficit;

                var scarabPriceResult = ctx.NinjaPrice.GetPrice("Scarab", NinjaPriceCategory.Scarab);
                int chaosCost = 0;
                if (scarabPriceResult.MaxChaosValue > 0)
                {
                    chaosCost = (int)Math.Ceiling(scarabBuyQty * scarabPriceResult.MaxChaosValue);
                    ctx.Log($"[Faustus] Buying {scarabBuyQty} scarabs @ ~{scarabPriceResult.MaxChaosValue:F1}c each ≈ {chaosCost}c total limit");
                }

                Status = $"Faustus: buying scarabs ({scarabInv} inv + {ctx.StashIndex.TotalScarabs} stash < {settings.ScarabRestockThreshold.Value})";
                var scarabSearchName = FaustusFragmentNames.Resolve("Metadata/Items/Scarabs/");
                faustus.Start("Metadata/Items/Scarabs/", scarabSearchName, scarabBuyQty, payCurrency, payCurrency, chaosCost,
                    ninjaName: scarabSearchName, ninjaCategory: "Scarab", ninjaLeague: "");
                return HideoutSignal.InProgress;
            }

            // 3) Check simulacrum count (Inventory + Stash)
            int simInv = StashSystem.CountInventoryItems(ctx.Game, "CurrencyAfflictionFragment");
            if (simInv < settings.SimulacrumRestockThreshold.Value)
            {
                // Check stash before buying
                int simStash = 0;
                var simSettings = ctx.Settings.Simulacrum;
                if (!string.IsNullOrWhiteSpace(simSettings.SimulacrumTabName.Value))
                {
                    simStash = ctx.StashIndex.Tabs
                        .FirstOrDefault(t => string.Equals(t.Name, simSettings.SimulacrumTabName.Value, StringComparison.OrdinalIgnoreCase))
                        ?.Items.Where(i => i.ItemPath.Contains("CurrencyAfflictionFragment", StringComparison.OrdinalIgnoreCase)).Sum(i => i.Stack) ?? 0;
                }
                else
                {
                    simStash = ctx.StashIndex.TotalSimulacrums;
                }

                if (simInv + simStash < settings.SimulacrumRestockThreshold.Value)
                {
                    int deficit = settings.SimulacrumRestockThreshold.Value - (simInv + simStash);
                    int simBuyQty = settings.SimulacrumBuyAmount.Value > 0 ? settings.SimulacrumBuyAmount.Value : deficit;

                    var simPriceResult = ctx.NinjaPrice.GetPrice("Simulacrum", NinjaPriceCategory.Fragment);
                    int chaosCost = 0;
                    if (simPriceResult.MaxChaosValue > 0)
                    {
                        chaosCost = (int)Math.Ceiling(simBuyQty * simPriceResult.MaxChaosValue);
                        ctx.Log($"[Faustus] Buying {simBuyQty} full simulacrums @ ~{simPriceResult.MaxChaosValue:F1}c each ≈ {chaosCost}c total limit");
                    }

                    Status = $"Faustus: buying simulacrums ({simInv} inv + {simStash} stash < {settings.SimulacrumRestockThreshold.Value})";
                    var simSearchName = FaustusFragmentNames.Resolve("CurrencyAfflictionFragment");
                    faustus.Start("CurrencyAfflictionFragment", simSearchName, simBuyQty, payCurrency, payCurrency, chaosCost,
                        ninjaName: simSearchName, ninjaCategory: "Fragment", ninjaLeague: "");
                    return HideoutSignal.InProgress;
                }
            }

            // Nothing to restock — open map
            Status = "Faustus restock: nothing needed — opening map";
            _phase = HideoutPhase.OpenMap;
            _phaseStartTime = DateTime.Now;
            StartMapDevice(ctx);
            return HideoutSignal.InProgress;
        }

        private void StartMapDevice(BotContext ctx)
        {
            if (ctx.MapDevice.IsBusy)
                ctx.MapDevice.Cancel(ctx.Game, ctx.Navigation);

            ctx.MapDevice.TargetMapName = _targetMapName;
            ctx.MapDevice.MinMapTier = _minMapTier;

            if (_mapFilter != null && !ctx.MapDevice.Start(_mapFilter, _inventoryFragmentPath))
                Status = $"MapDevice.Start failed (phase={ctx.MapDevice.Phase})";
        }

        private HideoutSignal TickOpenMap(BotContext ctx)
        {
            var result = ctx.MapDevice.Tick(ctx.Game, ctx.Navigation);

            switch (result)
            {
                case MapDeviceResult.Succeeded:
                    Status = "Map opened — entering";
                    // Area change will fire when player enters the portal
                    break;
                case MapDeviceResult.Failed:
                    string deviceStatus = ctx.MapDevice.Status;
                    Status = $"Map device failed: {deviceStatus}";

                    // Check if failure is due to missing maps/fragments
                    bool isMissingItem = deviceStatus.Contains("No matching maps") || 
                                        deviceStatus.Contains("No fragments");

                    if (isMissingItem)
                    {
                        // Query the stash index to see if we actually have replacements
                        string missingPath = _inventoryFragmentPath ?? _mapRestockPath ?? "Maps/";
                        int minTier = _inventoryFragmentPath != null ? 0 : _mapRestockMinTier;
                        int indexCount = ctx.StashIndex.CountByPath(missingPath, minTier);
                        var bestTab = ctx.StashIndex.BestTabForPath(missingPath, minTier);

                        if (indexCount == 0)
                        {
                            // Genuinely out of items or name mismatch — set terminal error status
                            string displayItem = missingPath.Split('/').Last().ToUpper();
                            Status = $"OUT OF {displayItem} (0 matches for '{missingPath}')";

                            ctx.Log($"[HideoutFlow] ERROR: Map Device empty and StashIndex search for '{missingPath}' (minTier: {minTier}) returned 0 results.");
                            ctx.Log($"[HideoutFlow] Index state: IsComplete={ctx.StashIndex.IsComplete}, Tabs={ctx.StashIndex.Tabs.Count}, TotalItems={ctx.StashIndex.CountByPath("", 0)}, TotalSims={ctx.StashIndex.TotalSimulacrums}.");
                            ctx.Log($"[HideoutFlow] _extraWithdrawals={((_extraWithdrawals == null) ? "null" : _extraWithdrawals.Count.ToString()+" entries")}, _inventoryFragmentPath='{_inventoryFragmentPath}'.");
                            var sample = ctx.StashIndex.FindByPath("").Take(10).Select(e => e.ItemPath.Split('/').Last());
                            ctx.Log($"[HideoutFlow] First 10 items in index: {string.Join(", ", sample)}");
                            ctx.Log($"[HideoutFlow] Check StashIndex.txt in the plugin folder to verify naming.");
                            _phase = HideoutPhase.Idle;
                            return HideoutSignal.NoFragments;
                        }

                        // Items exist in stash! Set the flag and bounce back to Settle to go fetch them.
                        if ((DateTime.Now - _phaseStartTime).TotalSeconds > MapDeviceRetrySeconds)
                        {
                            _restockRequired = true;
                            _phase = HideoutPhase.Settle;
                            _phaseStartTime = DateTime.Now;
                            ctx.Log($"[HideoutFlow] Device empty, but index has {indexCount} items. Bouncing to stash.");
                            return HideoutSignal.InProgress;
                        }
                    }
                    else if ((DateTime.Now - _phaseStartTime).TotalSeconds > MapDeviceRetrySeconds)
                    {
                        // Generic failure (e.g. click missed) — retry
                        _phaseStartTime = DateTime.Now;
                        StartMapDevice(ctx);
                    }
                    break;
                default:
                    Status = $"Map device: {ctx.MapDevice.Status}";
                    break;
            }
            return HideoutSignal.InProgress;
        }

        private HideoutSignal TickEnterPortal(BotContext ctx)
        {
            var gc = ctx.Game;

            if (!gc.Area.CurrentArea.IsHideout)
                return HideoutSignal.InProgress;

            if ((DateTime.Now - _phaseStartTime).TotalSeconds > BasePortalTimeoutSeconds + ctx.Settings.ExtraLatencyMs.Value / 1000f)
            {
                Status = "No portal found";
                ctx.Interaction.Cancel(gc);
                _phase = HideoutPhase.Idle;
                return HideoutSignal.PortalTimeout;
            }

            // Close any open panels (stash/inventory/atlas) before clicking portal
            if (gc.IngameState.IngameUi.StashElement?.IsVisible == true ||
                gc.IngameState.IngameUi.InventoryPanel?.IsVisible == true ||
                gc.IngameState.IngameUi.Atlas?.IsVisible == true)
            {
                if (ModeHelpers.CanAct(_lastActionTime, ActionCooldownMs))
                {
                    BotInput.PressKey(System.Windows.Forms.Keys.Escape);
                    _lastActionTime = DateTime.Now;
                    Status = "Closing panels before portal";
                }
                return HideoutSignal.InProgress;
            }

            // Use InteractionSystem for portal clicking — handles navigation,
            // screen bounds, click verification, and retries automatically.
            // InteractionSystem is already ticked by the mode before this runs.
            if (ctx.Interaction.IsBusy)
            {
                Status = $"Entering portal: {ctx.Interaction.Status}";
                return HideoutSignal.InProgress;
            }

            var portal = ModeHelpers.FindNearestPortal(gc);
            if (portal == null)
            {
                Status = "Looking for portal to re-enter...";
                return HideoutSignal.InProgress;
            }

            ctx.Interaction.InteractWithEntity(portal, ctx.Navigation, requireProximity: true);
            Status = "Interacting with portal";
            return HideoutSignal.InProgress;
        }

        private enum HideoutPhase
        {
            Idle,
            Settle,
            CheckDevice,  // read device storage slot counts (once per hideout visit)
            CleanDevice,  // remove unexpected items from device storage
            IndexStash,   // scan all tabs once per session
            Stash,
            StockDevice,  // deposit items into device storage after stashing
            FaustusRestock,
            OpenMap,
            EnterPortal,
        }
    }

    internal static class FaustusFragmentNames
    {
        // Maps internal path → Faustus search bar display name (Faustus rejects internal names).
        // Full trimmed path checked first, then last segment as fallback.
        private static readonly Dictionary<string, string> _map = new(StringComparer.OrdinalIgnoreCase)
        {
            // Boss invitations
            { "RitualBossFragment",         "An Audience With The King" },
            { "CurrencyHarvestBossKey",     "Sacred Blossom" },
            { "CurrencyUberBossKeyAnger",   "Echo of Trauma" },
            { "CurrencyMavenKey",           "The Maven's Writ" },
            // Simulacrum
            { "CurrencyAfflictionFragment", "Simulacrum" },
            // Scarabs (passed as directory prefix "Metadata/Items/Scarabs/")
            { "Metadata/Items/Scarabs",     "Scarab" },
        };

        public static string Resolve(string fragmentPath)
        {
            var trimmed = fragmentPath.TrimEnd('/');
            if (_map.TryGetValue(trimmed, out var fullMatch)) return fullMatch;
            var key = trimmed.Split('/').Last();
            return _map.TryGetValue(key, out var name) ? name : key;
        }
    }

    public enum HideoutSignal
    {
        InProgress,
        PortalTimeout,
        NoFragments,
    }
}
