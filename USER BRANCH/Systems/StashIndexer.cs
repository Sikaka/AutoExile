using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using ExileCore;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;

namespace AutoExile.Systems
{
    /// <summary>
    /// Scans all stash tabs once (when stash is open) and builds a searchable index:
    ///   path substring → list of (tabName, count)
    /// Also exposes convenience lookups for well-known items.
    /// </summary>
    public class StashIndexer
    {
        // ── Result types ────────────────────────────────────────────────────────

        public record TabItemEntry(string TabName, int TabIndex, string ItemPath, string BaseName, int Stack, int MapTier);

        public class TabSummary
        {
            public string Name { get; init; } = "";
            public int Index { get; init; }
            public string TabType { get; init; } = "";
            public string Affinity { get; init; } = "";
            /// <summary>All items found in this tab during scan.</summary>
            public List<TabItemEntry> Items { get; } = new();
        }

        // ── State ───────────────────────────────────────────────────────────────

        private enum IndexPhase { Idle, Starting, SwitchingTab, ReadingTab, Done, Failed }

        private IndexPhase _phase = IndexPhase.Idle;
        private DateTime _lastActionTime = DateTime.MinValue;
        private DateTime _phaseStartTime = DateTime.MinValue;
        private int _targetTabIndex;
        private int _totalTabs;

        private const float TabSwitchSettleMs  = 400f;
        private const float TabReadSettleMs    = 350f;  // extra wait after arriving before reading
        private const float TabReadMaxWaitMs   = 2000f; // give up waiting for items after this
        private const float TimeoutSeconds     = 120f;

        // ── Results ─────────────────────────────────────────────────────────────

        public bool IsRunning  => _phase != IndexPhase.Idle && _phase != IndexPhase.Done && _phase != IndexPhase.Failed;
        public bool IsComplete => _phase == IndexPhase.Done;
        public string Status   { get; private set; } = "Idle";

        /// All tabs with their items (populated after scan).
        public List<TabSummary> Tabs { get; } = new();

        /// Flat item index: path substring search.
        private readonly List<TabItemEntry> _allItems = new();

        // ── Public API ──────────────────────────────────────────────────────────

        /// <summary>Start a full stash scan. Stash must already be open.</summary>
        public void Start()
        {
            Tabs.Clear();
            _allItems.Clear();
            _phase = IndexPhase.Starting;
            _phaseStartTime = DateTime.Now;
            _targetTabIndex = 0;
            Status = "Starting stash index scan…";
        }

        public void Reset()
        {
            _phase = IndexPhase.Idle;
            Status = "Idle";
        }

        /// <summary>Drive the scan. Call every tick while stash is open.</summary>
        public void Tick(GameController gc)
        {
            if (_phase == IndexPhase.Idle || _phase == IndexPhase.Done || _phase == IndexPhase.Failed)
                return;

            var stashEl = gc.IngameState.IngameUi.StashElement;
            if (stashEl?.IsVisible != true)
            {
                Status = "Stash closed — index scan aborted";
                _phase = IndexPhase.Failed;
                return;
            }

            if ((DateTime.Now - _phaseStartTime).TotalSeconds > TimeoutSeconds)
            {
                Status = "Index scan timed out";
                _phase = IndexPhase.Failed;
                return;
            }

            var names = stashEl.AllStashNames;
            if (names == null || names.Count == 0)
            {
                Status = "No stash tab names — retrying";
                return;
            }

            _totalTabs = names.Count;

            switch (_phase)
            {
                case IndexPhase.Starting:
                    _targetTabIndex = 0;
                    _phase = IndexPhase.SwitchingTab;
                    _lastActionTime = DateTime.MinValue;
                    break;

                case IndexPhase.SwitchingTab:
                    TickSwitchTab(gc, stashEl, names);
                    break;

                case IndexPhase.ReadingTab:
                    TickReadTab(gc, stashEl, names);
                    break;
            }
        }

        // ── Internal state machine ───────────────────────────────────────────────

        private void TickSwitchTab(GameController gc, ExileCore.PoEMemory.Elements.StashElement stashEl, IList<string> names)
        {
            // All tabs scanned?
            if (_targetTabIndex >= _totalTabs)
            {
                FinalizeIndex(gc);
                return;
            }

            var currentIdx = stashEl.IndexVisibleStash;

            if (currentIdx == _targetTabIndex)
            {
                // Arrived — wait a settle period then read
                if ((DateTime.Now - _lastActionTime).TotalMilliseconds < TabReadSettleMs)
                    return;
                _phase = IndexPhase.ReadingTab;
                _phaseStartTime = DateTime.Now;
                Status = $"Reading tab {_targetTabIndex + 1}/{_totalTabs}: {names[_targetTabIndex]}";
                return;
            }

            // Need to move
            if ((DateTime.Now - _lastActionTime).TotalMilliseconds < TabSwitchSettleMs)
                return;

            if (!BotInput.CanAct) return;

            var key = currentIdx < _targetTabIndex ? System.Windows.Forms.Keys.Right : System.Windows.Forms.Keys.Left;
            BotInput.PressKey(key);
            _lastActionTime = DateTime.Now;
            Status = $"Navigating to tab {_targetTabIndex + 1}/{_totalTabs}: {names[_targetTabIndex]}";
        }

        private void TickReadTab(GameController gc, ExileCore.PoEMemory.Elements.StashElement stashEl, IList<string> names)
        {
            // Minimum settle after arriving
            if ((DateTime.Now - _phaseStartTime).TotalMilliseconds < TabReadSettleMs)
                return;

            var tabName = names[_targetTabIndex];
            var visibleInv = stashEl.VisibleStash;
            var items = visibleInv?.VisibleInventoryItems;

            // Wait until items collection is non-null, unless we've exceeded the max wait
            var elapsed = (DateTime.Now - _phaseStartTime).TotalMilliseconds;
            if (items == null && elapsed < TabReadMaxWaitMs)
            {
                Status = $"Waiting for tab {_targetTabIndex + 1}/{_totalTabs} '{tabName}' to load… ({elapsed:F0}ms)";
                return;
            }

            var summary = new TabSummary
            {
                Name     = tabName,
                Index    = _targetTabIndex,
                TabType  = visibleInv?.InvType.ToString() ?? "?",
                Affinity = GetTabAffinity(gc, tabName),
            };

            if (items != null)
            {
                foreach (var slot in items)
                {
                    var entity = slot?.Item ?? slot?.Entity;
                    if (entity == null) continue;

                    var path = entity.Path ?? "";
                    var baseName = entity.GetComponent<Base>()?.Name ?? "";
                    var stackComp = entity.GetComponent<Stack>();
                    var stack = (stackComp != null && stackComp.Size > 0) ? stackComp.Size : 1;
                    int mapTier = 0;
                    if (entity.TryGetComponent<MapKey>(out var mk))
                        mapTier = mk.Tier;

                    var entry = new TabItemEntry(tabName, _targetTabIndex, path, baseName, stack, mapTier);
                    summary.Items.Add(entry);
                    _allItems.Add(entry);
                }

                int simCount = summary.Items.Count(i => i.ItemPath.Contains("CurrencyAfflictionFragment"));
                if (simCount > 0)
                    DebugWindow.LogMsg($"[StashIndexer] Found {simCount} Simulacrum fragments in tab '{tabName}'");
            }

            Tabs.Add(summary);
            Status = $"Tab {_targetTabIndex + 1}/{_totalTabs} '{tabName}': {summary.Items.Count} items";

            // Move to next tab
            _targetTabIndex++;
            _phase = IndexPhase.SwitchingTab;
            _lastActionTime = DateTime.Now;
        }

        private void FinalizeIndex(GameController gc)
        {
            _phase = IndexPhase.Done;
            Status = $"Index complete: {Tabs.Count} tabs, {_allItems.Count} items total";

            // Dump the full index to Desktop for debugging
            try
            {
                var pluginDir = BotCore.Instance?.DirectoryFullName ?? "";
                var filePath = Path.Combine(pluginDir, "StashIndex.txt");
                var lines = new List<string> { $"=== Stash Index Dump {DateTime.Now} ===" };
                lines.Add($"Total Items: {_allItems.Count}");
                foreach (var tab in Tabs)
                {
                    lines.Add($"Tab: {tab.Name} (Type: {tab.TabType}, Items: {tab.Items.Count})");
                    foreach (var item in tab.Items)
                        lines.Add($"  - [{item.MapTier}] {item.ItemPath}");
                }
                File.WriteAllLines(filePath, lines);
            }
            catch { }
        }

        private string GetTabAffinity(GameController gc, string tabName)
        {
            try
            {
                var tabs = gc.IngameState.ServerData?.PlayerStashTabs;
                if (tabs == null) return "";
                var match = tabs.FirstOrDefault(t =>
                    string.Equals(t.Name, tabName, StringComparison.OrdinalIgnoreCase));
                return match?.Affinity.ToString() ?? "";
            }
            catch { return ""; }
        }

        // ── Query API ────────────────────────────────────────────────────────────

        /// Find all entries whose path contains the given substring.
        public IEnumerable<TabItemEntry> FindByPath(string pathSubstring, int minTier = 0) =>
            _allItems.Where(e => e.ItemPath.Contains(pathSubstring, StringComparison.OrdinalIgnoreCase) && e.MapTier >= minTier);

        /// Total stack count of items matching a path substring.
        public int CountByPath(string pathSubstring, int minTier = 0) =>
            FindByPath(pathSubstring, minTier).Sum(e => e.Stack);

        /// Find which tab has the most of a given item (by path substring).
        public TabSummary? BestTabForPath(string pathSubstring, int minTier = 0) =>
            Tabs.OrderByDescending(t => t.Items
                    .Where(i => i.ItemPath.Contains(pathSubstring, StringComparison.OrdinalIgnoreCase) && i.MapTier >= minTier)
                    .Sum(i => i.Stack))
                .FirstOrDefault(t => t.Items.Any(i =>
                    i.ItemPath.Contains(pathSubstring, StringComparison.OrdinalIgnoreCase) && i.MapTier >= minTier));

        /// Find the tab with a specific affinity (e.g. "Currency", "Fragment", "Scarab").
        public TabSummary? FindTabByAffinity(string affinity) =>
            Tabs.FirstOrDefault(t =>
                t.Affinity.Equals(affinity, StringComparison.OrdinalIgnoreCase));

        // ── Convenience properties (set after scan) ──────────────────────────────

        /// Name of the tab containing the most Scrolls of Wisdom.
        public string? SowTabName => BestTabForPath("CurrencyIdentification")?.Name;

        /// Total Scrolls of Wisdom across all tabs.
        public int TotalSow => CountByPath("CurrencyIdentification");

        /// Name of the tab containing the most scarabs.
        public string? ScarabTabName => BestTabForPath("Metadata/Items/Scarabs/")?.Name;

        /// Total scarab count across all tabs.
        public int TotalScarabs => CountByPath("Metadata/Items/Scarabs/");

        /// Name of the tab with the most full Simulacrum items.
        public string? SimulacrumTabName => BestTabForPath("CurrencyAfflictionFragment")?.Name;

        /// Total full Simulacrum count.
        public int TotalSimulacrums => CountByPath("CurrencyAfflictionFragment");

        /// Currency tab (by affinity).
        public TabSummary? CurrencyTab => FindTabByAffinity("Currency");

        /// Fragment tab (by affinity).
        public TabSummary? FragmentTab => FindTabByAffinity("Fragment");

        /// Map tab (by affinity).
        public TabSummary? MapTab => FindTabByAffinity("Map");

        /// Delirium tab (by affinity).
        public TabSummary? DeliriumTab => FindTabByAffinity("Delirium");
    }
}
