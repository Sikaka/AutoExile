using System.Numerics;
using AutoExile.Mechanics;
using AutoExile.Modes.Shared;
using AutoExile.Systems;
using ExileCore.PoEMemory.MemoryObjects;

namespace AutoExile.Modes.WaveFarm
{
    /// <summary>
    /// Wave-based map farming mode. The bot is always a wave moving forward through the map.
    /// Combat happens continuously while exploring. Mechanics are deferred and engaged strategically.
    ///
    /// Orchestrates: hideout flow → map entry → wave tick loop → exit → repeat.
    /// Delegates in-map decisions to WaveTick.
    /// </summary>
    public class WaveFarmMode : IBotMode
    {
        public string Name => "Wave Farming";

        private readonly WaveTick _wave = new();
        private readonly HideoutFlow _hideoutFlow = new();
        private readonly ZoneStateCache _zoneCache = new();
        private readonly Dictionary<string, IFarmPlan> _plans = new();
        private IFarmPlan? _activePlan;

        private WaveFarmPhase _phase = WaveFarmPhase.Idle;
        private DateTime _zoneEnteredAt;
        private DateTime _startTime;
        private long _lastZoneHash;
        private string _lastAreaName = "";
        private int _runsCompleted;
        private bool _mapCompleted;

        // Exit map
        private bool _portalKeyPressed;
        private DateTime _portalKeyTime;
        private DateTime _lastClickTime;

        // Zone settle
        private DateTime? _zoneSettleUntil;
        private const float ZoneSettleSeconds = 1f;

        // Sub-zone (wish/mirage) tracking
        private bool _isInSubZone;
        private long _parentZoneHash;
        private Vector2? _exitPortalGridPos; // Cached SekhemaPortal position

        public string Status { get; private set; } = "";
        public string Decision => _wave.Decision;
        public int RunsCompleted => _runsCompleted;

        // ══════════════════════════════════════════════════════════════
        // Plan registration
        // ══════════════════════════════════════════════════════════════

        public void Register(IFarmPlan plan) => _plans[plan.Name] = plan;

        /// <summary>
        /// Called when the user picks a farm strategy in the web UI. Pushes the
        /// plan's defaults — map device scarab slots and altar mod weight overrides —
        /// into <paramref name="settings"/> so the bot is configured for that strategy
        /// without manual setup. Existing user values are overwritten; users save
        /// their own profile if they want personalized overrides.
        /// </summary>
        public void ApplyPlanDefaults(string planName, BotSettings settings, Action<string>? log = null)
        {
            if (!_plans.TryGetValue(planName, out var plan)) return;

            // Map device slots
            settings.MapDevice.SetDefaults(plan.AtlasConfig.Scarabs);

            // Altar mod weights — replace entirely, not merge, so de-emphasized
            // mods from a previous plan don't linger.
            if (plan.AltarWeightDefaults.Count > 0)
            {
                settings.Mechanics.EldritchAltar.ModWeights =
                    new Dictionary<string, int>(plan.AltarWeightDefaults, StringComparer.OrdinalIgnoreCase);
            }
            else
            {
                // Empty plan defaults = revert to engine defaults (let Eldritch
                // handler use its built-in DefaultModWeights).
                settings.Mechanics.EldritchAltar.ModWeights = new Dictionary<string, int>();
            }

            log?.Invoke($"[WaveFarm] Applied '{planName}' defaults: " +
                $"{plan.AtlasConfig.Scarabs.Count} scarab slots, {plan.AltarWeightDefaults.Count} altar overrides, " +
                $"{plan.MustLootItems.Count} must-loot keywords");
        }

        /// <summary>
        /// Push the active plan's MustLootItems into the live LootSystem. Called on
        /// mode entry and after each area transition — these get cleared when other
        /// modes (Boss/Sim) own the loot system, so we re-assert ownership each map.
        /// </summary>
        private void ReassertMustLoot(BotContext ctx)
        {
            if (_activePlan == null) return;
            var loot = ctx.Loot;
            loot.MustLootItems.Clear();
            foreach (var keyword in _activePlan.MustLootItems)
                loot.MustLootItems.Add(keyword);
        }

        // ══════════════════════════════════════════════════════════════
        // Mode lifecycle
        // ══════════════════════════════════════════════════════════════

        public void OnEnter(BotContext ctx)
        {
            _startTime = DateTime.Now;
            _zoneEnteredAt = DateTime.Now;
            _lastZoneHash = ctx.Game?.IngameState?.Data?.CurrentAreaHash ?? 0;
            _runsCompleted = 0;
            _mapCompleted = false;
            ctx.Perf.Reset();

            // Resolve plan
            var selectedName = ctx.Settings.Farming.FarmStrategy.Value;
            if (string.IsNullOrEmpty(selectedName) || !_plans.TryGetValue(selectedName, out _activePlan))
                _activePlan = _plans.Values.FirstOrDefault();

            if (_activePlan == null)
            {
                Status = "No farm plans registered";
                _phase = WaveFarmPhase.Idle;
                return;
            }

            // Apply plan's mechanic mode overrides
            ctx.Mechanics.SetPlanOverrides(_activePlan.MechanicModeOverrides.Count > 0
                ? _activePlan.MechanicModeOverrides : null);

            var gc = ctx.Game;
            _lastAreaName = gc.Area?.CurrentArea?.Name ?? "";

            if (gc.Area.CurrentArea.IsHideout || gc.Area.CurrentArea.IsTown)
                StartHideoutFlow(ctx);
            else
                InitMapState(ctx, gc);

            // Take ownership of LootSystem.MustLootItems so the active plan's
            // bypass keywords (Stacked Decks, Blueprints, etc.) are honored.
            // Boss/Sim modes clear this on their own entry, so we re-assert
            // each time we become the active mode.
            ReassertMustLoot(ctx);

            ctx.Log($"[WaveFarm] Mode entered — plan: {_activePlan.Name}");
        }

        public void OnExit()
        {
            _phase = WaveFarmPhase.Idle;
            _hideoutFlow.Cancel();
            _wave.Reset();
            _zoneCache.Clear();
            _mapCompleted = false;
        }

        // ══════════════════════════════════════════════════════════════
        // Main tick
        // ══════════════════════════════════════════════════════════════

        public void Tick(BotContext ctx)
        {
            if (_activePlan == null) return;
            var gc = ctx.Game;
            if (gc?.Player == null || !gc.InGame) return;

            // Check for plan change (user switched in settings while paused in hideout)
            if (_phase == WaveFarmPhase.InHideout)
            {
                var selectedName = ctx.Settings.Farming.FarmStrategy.Value;
                if (!string.IsNullOrEmpty(selectedName) && _plans.TryGetValue(selectedName, out var newPlan)
                    && newPlan != _activePlan)
                {
                    _activePlan = newPlan;
                    ctx.Mechanics.SetPlanOverrides(_activePlan.MechanicModeOverrides.Count > 0
                        ? _activePlan.MechanicModeOverrides : null);
                    ctx.Log($"[WaveFarm] Plan changed to: {_activePlan.Name}");
                }
            }

            // Area change detection
            var currentArea = gc.Area?.CurrentArea?.Name ?? "";
            if (currentArea != _lastAreaName)
            {
                _lastAreaName = currentArea;
                OnAreaChanged(ctx, currentArea);
            }

            // Zone hash change (sub-zone transitions within same area name)
            // Wishes/mirage zones share the parent map's area name but have different terrain,
            // entities, and pathfinding data. Must fully reinitialize like a new map.
            var currentHash = gc.IngameState?.Data?.CurrentAreaHash ?? 0;
            if (currentHash != _lastZoneHash && _lastZoneHash != 0)
            {
                ctx.Log($"[WaveFarm] Zone hash changed: {_lastZoneHash} -> {currentHash} (sub-zone transition)");

                // Full reinitialization — navigation, exploration, mechanics, combat
                ctx.Navigation.Stop(gc);
                ModeHelpers.CancelAllSystems(ctx);

                if (_phase == WaveFarmPhase.InMap)
                {
                    // Save current zone's exploration + threat map + mechanics state before transitioning.
                    // Mechanics state matters: e.g., if Wishes was completed in the parent map (entered
                    // the mirage portal), the parent zone's mechanic completion must persist across the
                    // sub-zone round-trip so the bot doesn't think Wishes is still pending on return.
                    _zoneCache.Save(_lastZoneHash, ctx.Exploration, ctx.ThreatMap, _wave, ctx.Mechanics);
                    ctx.Log($"[WaveFarm] Saved zone state for hash={_lastZoneHash} (coverage={ctx.Exploration.ActiveBlobCoverage:P0}, threats={ctx.ThreatMap.TotalAlive} alive)");

                    if (!_isInSubZone && currentHash != _parentZoneHash)
                    {
                        // Potential sub-zone entry — validate with concrete signals before
                        // committing. Hash changes can happen within the same zone (wave events,
                        // obelisk state changes) without being a mirage/wish zone transition.
                        // Require exit button OR SekhemaPortal entity as proof.
                        bool confirmedSubZone = false;
                        _exitPortalGridPos = null;

                        var exitBtn = FindWishZoneExitButton(gc);
                        if (exitBtn?.IsVisible == true)
                        {
                            confirmedSubZone = true;
                            ctx.Log("[WaveFarm] Sub-zone confirmed via exit button");
                        }

                        if (!confirmedSubZone)
                        {
                            foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
                            {
                                if (entity.Path != null && entity.Path.Contains("SekhemaPortal"))
                                {
                                    _exitPortalGridPos = entity.GridPosNum;
                                    confirmedSubZone = true;
                                    ctx.Log($"[WaveFarm] Sub-zone confirmed via SekhemaPortal at ({entity.GridPosNum.X:F0},{entity.GridPosNum.Y:F0})");
                                    break;
                                }
                            }
                        }

                        if (confirmedSubZone)
                        {
                            _parentZoneHash = _lastZoneHash;
                            _isInSubZone = true;

                            // Cache SekhemaPortal if not already found above
                            if (!_exitPortalGridPos.HasValue)
                            {
                                foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
                                {
                                    if (entity.Path != null && entity.Path.Contains("SekhemaPortal"))
                                    {
                                        _exitPortalGridPos = entity.GridPosNum;
                                        ctx.Log($"[WaveFarm] Cached exit portal at ({entity.GridPosNum.X:F0},{entity.GridPosNum.Y:F0})");
                                        break;
                                    }
                                }
                            }
                            ctx.Log($"[WaveFarm] Entered sub-zone — parent hash={_parentZoneHash}, exitPortal={(_exitPortalGridPos.HasValue ? "found" : "NOT FOUND")}");
                        }
                        else
                        {
                            ctx.Log($"[WaveFarm] Hash changed {_lastZoneHash} -> {currentHash} but no sub-zone signals — treating as same-zone hash change");
                        }
                    }
                    else if (_isInSubZone && currentHash == _parentZoneHash)
                    {
                        // Returning to parent map
                        _isInSubZone = false;
                        _exitPortalGridPos = null;
                        ctx.Log("[WaveFarm] Returned to parent map from sub-zone");
                    }

                    // Always reset entity-ID-based state (entities differ per zone)
                    ctx.Loot.ClearFailed();
                    ctx.Combat.ClearUnreachable();
                    ctx.Entities.Rebuild(gc.EntityListWrapper.OnlyValidEntities);
                    ctx.Mechanics.Reset();
                    ModeHelpers.EnableDefaultCombat(ctx);
                    _mapCompleted = false;

                    // Try restoring cached state for this zone (parent returning from sub-zone).
                    // WaveTick is reset first, then metrics are restored from cache if available.
                    _wave.Reset();
                    _wave.Initialize(_activePlan!);
                    if (_zoneCache.TryRestore(currentHash, ctx.Exploration, ctx.ThreatMap, _wave, ctx.Mechanics))
                    {
                        ctx.Log($"[WaveFarm] Restored zone state for hash={currentHash} (coverage={ctx.Exploration.ActiveBlobCoverage:P0}, threats={ctx.ThreatMap.TotalAlive} alive)");
                    }
                    else
                    {
                        // Fresh zone — initialize ThreatMap from pathfinding grid
                        var pfGrid = gc.IngameState?.Data?.RawPathfindingData;
                        if (pfGrid != null)
                        {
                            ctx.ThreatMap.Initialize(pfGrid);
                            ctx.ThreatMap.RebuildFromEntities(ctx.Entities.Monsters);
                        }
                    }
                    // Exploration will be initialized in the settle handler below if not restored
                }

                _lastZoneHash = currentHash;
                _zoneEnteredAt = DateTime.Now;
                _zoneSettleUntil = DateTime.Now.AddSeconds(ZoneSettleSeconds);
            }

            // Zone settle gate
            if (_zoneSettleUntil.HasValue && DateTime.Now < _zoneSettleUntil.Value)
            {
                Status = "Zone settling...";
                return;
            }
            if (_zoneSettleUntil.HasValue)
            {
                // Settle just ended — reinitialize exploration with fresh terrain data
                _zoneSettleUntil = null;
                var pfGrid = gc.IngameState?.Data?.RawPathfindingData;
                var tgtGrid = gc.IngameState?.Data?.RawTerrainTargetingData;
                if (pfGrid != null && gc.Player != null)
                {
                    var playerPos = gc.Player.GridPosNum;
                    ctx.Exploration.Initialize(pfGrid, tgtGrid, playerPos,
                        ctx.Settings.Build.BlinkRange.Value);
                    ctx.Log($"[WaveFarm] Exploration reinitialized after settle — {ctx.Exploration.TotalWalkableCells} cells");
                    _wave.ClearPlan.Initialize(ctx);
                }

                // Cache exit portal if we're in a sub-zone and haven't found it yet
                // Cache exit portal + detect sub-zone via UI button
                if (!_isInSubZone)
                {
                    var exitBtn = FindWishZoneExitButton(gc);
                    if (exitBtn?.IsVisible == true)
                    {
                        _isInSubZone = true;
                        ctx.Log("[WaveFarm] Sub-zone detected via exit button visibility");
                    }
                }
                if (_isInSubZone && !_exitPortalGridPos.HasValue)
                {
                    foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
                    {
                        if (entity.Path != null && entity.Path.Contains("SekhemaPortal"))
                        {
                            _exitPortalGridPos = entity.GridPosNum;
                            ctx.Log($"[WaveFarm] Cached exit portal (post-settle) at ({entity.GridPosNum.X:F0},{entity.GridPosNum.Y:F0})");
                            break;
                        }
                    }
                }
            }

            switch (_phase)
            {
                case WaveFarmPhase.InHideout:
                    TickHideout(ctx);
                    break;
                case WaveFarmPhase.InMap:
                    TickInMap(ctx);
                    break;
                case WaveFarmPhase.ExitMap:
                    TickExitMap(ctx, gc);
                    break;
            }
        }

        // ══════════════════════════════════════════════════════════════
        // Hideout
        // ══════════════════════════════════════════════════════════════

        private void StartHideoutFlow(BotContext ctx)
        {
            _phase = WaveFarmPhase.InHideout;
            ModeHelpers.CancelAllSystems(ctx);
            ModeHelpers.EnableDefaultCombat(ctx);

            var stash = ctx.Settings.Stash;

            // ── Build the per-stash-trip withdrawal list ──────────────────────
            //
            // BATCH model: pull enough consumables for ~20 runs in one trip so we
            // don't spend half our time round-tripping to stash. Every entry below
            // counts CURRENT inventory and only withdraws the deficit, so a partially-
            // stocked inventory just tops up.
            //
            // PATH TRANSLATION: the user's MapDevice slots store display names
            // ("Divination Scarab of The Cloister"). Entity paths use suffixes
            // ("ScarabDivinationCardsNew1"). ScarabDatabase translates one to the
            // other; without this, every scarab withdrawal silently fails because
            // the substring match on entity path never hits.
            const int RunsPerStashTrip = 20;
            var displayNames = ctx.Settings.MapDevice.ActiveSlots();
            var resolvedScarabPaths = new List<string>();
            var withdrawals = new List<(string PathSubstring, int Count)>();

            // De-dupe slot entries by resolved path — if all 5 slots are the same
            // scarab type (Stacked Deck case), we want one withdrawal of 5 × runs,
            // not five separate withdrawals each demanding 1 × runs.
            var perPathPerRun = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var displayName in displayNames)
            {
                var path = ResolveScarabPath(displayName);
                resolvedScarabPaths.Add(path);
                perPathPerRun[path] = perPathPerRun.GetValueOrDefault(path) + 1;
            }
            foreach (var (path, perRun) in perPathPerRun)
                withdrawals.Add((path, perRun * RunsPerStashTrip));

            // Maps: 1 per run. We match on "Maps/MapKey" — generic enough to match
            // any map in the supplies tab, narrow enough to ignore non-map items.
            // Assumes the user keeps only the map type they want in this tab.
            withdrawals.Add(("Maps/MapKey", RunsPerStashTrip));

            // Portal scrolls — single stack covers a session. PoE1 path:
            // Metadata/Items/Currency/CurrencyPortal
            withdrawals.Add(("CurrencyPortal", 20));

            ctx.Log($"[WaveFarm] Stash trip: {withdrawals.Count} entries, supply tab='{stash.MappingSuppliesTabName.Value}'");
            foreach (var (p, c) in withdrawals)
                ctx.Log($"[WaveFarm]   want {c}× '{p}'");

            // Build the "keep in inventory" filter: every withdrawal entry's path
            // becomes a keep-keyword. Without this the StashSystem deposits ALL
            // inventory contents (including the maps/scarabs/portals we just pulled)
            // into the dump tab the moment storing kicks in.
            var keepPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (path, _) in withdrawals)
                keepPaths.Add(path);
            // Also explicit broad categories in case a withdrawal path is more
            // specific than the inventory item path (e.g. plan-specific scarab).
            keepPaths.Add("Maps/MapKey");
            keepPaths.Add("Metadata/Items/Scarabs/");
            keepPaths.Add("CurrencyPortal");

            Func<ExileCore.PoEMemory.MemoryObjects.ServerInventory.InventSlotItem, bool> stashFilter = item =>
            {
                var entity = item.Item;
                var path = entity?.Path;
                if (path == null) return true;

                // Maps: only keep IDENTIFIED maps (= our pre-rolled supplies). Maps
                // that drop in the world are unidentified — those are loot, dump them.
                // Without this gate, the bot would happily run a random unidentified
                // map dropped by a mob instead of our pre-rolled City Square stack.
                if (path.Contains("Maps/MapKey", StringComparison.OrdinalIgnoreCase))
                {
                    if (entity!.TryGetComponent<ExileCore.PoEMemory.Components.Mods>(out var mods)
                        && mods.Identified)
                    {
                        return false; // KEEP — pre-rolled map
                    }
                    return true; // STASH — unidentified drop
                }

                foreach (var keep in keepPaths)
                    if (path.Contains(keep, StringComparison.OrdinalIgnoreCase))
                        return false; // KEEP — don't stash
                return true;          // stash this loot item
            };

            // Map filter: if user picked a specific map name, the device's named-map
            // flow handles it via TargetMapName. Plan-driven flow uses _ => true so
            // any in-stash map matches as a fallback.
            var targetMapName = ctx.Settings.Farming.MapName.Value;

            _hideoutFlow.Start(
                mapFilter: _ => true,
                stashItemFilter: stashFilter,
                targetMapName: string.IsNullOrWhiteSpace(targetMapName) ? null : targetMapName,
                // Tells MapDevice to look in player INVENTORY for the map (not just
                // the device's stash panel) when the named-flow can't find it in
                // stash. Required because we just withdrew the maps to inventory
                // — they're not in the device's view anymore.
                inventoryFragmentPath: "Maps/MapKey",
                stashItemThreshold: 0,
                dumpTabName:     string.IsNullOrWhiteSpace(stash.DumpTabName.Value)
                                    ? null : stash.DumpTabName.Value,
                resourceTabName: string.IsNullOrWhiteSpace(stash.MappingSuppliesTabName.Value)
                                    ? null : stash.MappingSuppliesTabName.Value,
                withdrawList: withdrawals,
                // After map insertion, ctrl+click each of these into device slots
                // before pressing activate. Use resolved paths so the substring
                // match against inventory entity paths actually works.
                scarabPaths: resolvedScarabPaths.Count > 0 ? resolvedScarabPaths : null);

            Status = "Hideout — preparing next run";
        }

        /// <summary>
        /// Translate a scarab display name (what the user sees and what the plan
        /// declares — e.g. "Divination Scarab of The Cloister") into the path
        /// suffix that appears in entity metadata paths (e.g. "ScarabDivinationCardsNew1").
        /// Falls back to the original string if the lookup misses, on the assumption
        /// the user typed a raw path substring directly.
        /// </summary>
        private static string ResolveScarabPath(string displayName)
        {
            if (string.IsNullOrWhiteSpace(displayName)) return displayName;
            return Systems.ScarabDatabase.NameToPath.TryGetValue(displayName, out var pathSuffix)
                ? pathSuffix
                : displayName;
        }

        private void TickHideout(BotContext ctx)
        {
            // Drive the InteractionSystem state machine. Without this, anything that
            // uses ctx.Interaction (map device portal entry, NPC clicks, etc.) starts
            // an interaction but never advances — bot stays stuck on the initial
            // "Interacting: ..." status forever. Other modes (Boss/Sim/Lab/Heist)
            // tick this in their own main loop; WaveFarm's hideout path needs it too.
            ctx.Interaction.Tick(ctx.Game);

            if (!_hideoutFlow.IsActive)
            {
                StartHideoutFlow(ctx);
                return;
            }

            var signal = _hideoutFlow.Tick(ctx);
            Status = _hideoutFlow.Status;

            if (signal == HideoutSignal.PortalTimeout)
            {
                ctx.Log("[WaveFarm] Portal timeout — retrying hideout flow");
                StartHideoutFlow(ctx);
            }
        }

        // ══════════════════════════════════════════════════════════════
        // In map
        // ══════════════════════════════════════════════════════════════

        private void InitMapState(BotContext ctx, ExileCore.GameController gc)
        {
            _phase = WaveFarmPhase.InMap;
            _mapCompleted = false;
            _portalKeyPressed = false;

            _wave.Initialize(_activePlan!);
            _activePlan!.Reset();

            // Enable combat
            ModeHelpers.EnableDefaultCombat(ctx);

            // Initialize exploration
            var pfGrid = gc.IngameState?.Data?.RawPathfindingData;
            var tgtGrid = gc.IngameState?.Data?.RawTerrainTargetingData;
            if (pfGrid != null)
            {
                var playerPos = gc.Player.GridPosNum;
                var blinkRange = ctx.Settings.Build.BlinkRange.Value;
                ctx.Exploration.Initialize(pfGrid, tgtGrid, playerPos, blinkRange);
                _wave.ClearPlan.Initialize(ctx);
            }

            ctx.Mechanics.Reset();
            ctx.Loot.ClearFailed();

            Status = "In map — wave farming";
            ctx.Log($"[WaveFarm] Map initialized: {gc.Area?.CurrentArea?.Name}");
        }

        private void TickInMap(BotContext ctx)
        {
            // Continuous sub-zone detection — the one-shot check at hash-change can
            // miss SekhemaPortal if entities near portals haven't streamed in yet
            // (documented: ~3s post-transition). If EITHER signal ever shows up, we
            // are in a mirage/wish sub-zone. This is a LATCHING check: once true,
            // it stays true until the hash-change handler flips us back on return
            // to the parent map. Portal-scroll exit is never allowed when this is set.
            RefreshSubZoneSignals(ctx, ctx.Game);

            // Keep danger zones updated — prevents clicking loot near exit portals
            ctx.Interaction.DangerZones.Clear();
            if (_isInSubZone && _exitPortalGridPos.HasValue)
                ctx.Interaction.DangerZones.Add(_exitPortalGridPos.Value);

            // Update exploration
            var playerPos = ctx.Game.Player.GridPosNum;
            ctx.Exploration.Update(playerPos);

            // Wave tick — returns true when map is done
            bool done = _wave.Tick(ctx);
            Status = _wave.Status;

            if (done)
            {
                // TODO: Ritual shop — open shop, buy items with accumulated tribute, then exit.
                // For now, skip straight to exit. Uncomment when shop purchasing is implemented.
                // if (HasRitualTribute(ctx)) { _phase = WaveFarmPhase.RitualShop; return; }

                // Final sub-zone re-check right before we commit to an exit path.
                // Even with the per-tick refresh above, re-verify now: pressing a
                // portal scroll inside a mirage zone is catastrophic (the scroll
                // does nothing, we get stuck). This is cheap and authoritative.
                RefreshSubZoneSignals(ctx, ctx.Game);

                if (_isInSubZone)
                {
                    // Sub-zone complete — exit via SekhemaPortal back to parent map
                    _phase = WaveFarmPhase.ExitMap;
                    _portalKeyPressed = true; // Skip portal key — we use SekhemaPortal instead
                    _portalKeyTime = DateTime.Now;
                    Status = "Sub-zone complete — finding exit portal";
                    ctx.Log("[WaveFarm] Sub-zone map done (ritual shop: skipped), looking for SekhemaPortal");
                }
                else
                {
                    _phase = WaveFarmPhase.ExitMap;
                    _portalKeyPressed = false;
                    Status = "Map complete — exiting";
                    ctx.Log("[WaveFarm] Map done (ritual shop: skipped), opening portal");
                }
            }
        }

        // ══════════════════════════════════════════════════════════════
        // Exit map
        // ══════════════════════════════════════════════════════════════

        private void TickExitMap(BotContext ctx, ExileCore.GameController gc)
        {
            ModeHelpers.CancelAllSystems(ctx);

            // Last-chance sub-zone check before touching the portal key. Pressing a
            // portal scroll in a mirage zone does nothing and leaves us stuck; a
            // live re-check here closes the final window where _isInSubZone could
            // be stale (e.g. if we somehow reached ExitMap phase without an InMap
            // tick detecting it first).
            RefreshSubZoneSignals(ctx, gc);

            // Sub-zone exit: find and click SekhemaPortal (exit back to parent map)
            if (_isInSubZone)
            {
                TickExitSubZone(ctx, gc);
                return;
            }

            if (!_portalKeyPressed)
            {
                // Press portal key from settings
                var portalKey = ctx.Settings.Run.PortalKey.Value;
                if (BotInput.CanAct && BotInput.PressKey(portalKey))
                {
                    _portalKeyPressed = true;
                    _portalKeyTime = DateTime.Now;
                    Status = "Opening portal...";
                }
                return;
            }

            // Wait for portal to appear then click it
            if ((DateTime.Now - _portalKeyTime).TotalSeconds < 1.5)
            {
                Status = "Waiting for portal...";
                return;
            }

            // Find and click portal
            var portal = ModeHelpers.FindNearestPortal(gc);
            if (portal != null)
            {
                _mapCompleted = true;
                ModeHelpers.ClickEntity(gc, portal, ref _lastClickTime);
                Status = "Clicking portal...";
            }
            else if ((DateTime.Now - _portalKeyTime).TotalSeconds > 3)
            {
                // Portal scroll didn't work — check if we're in a wish zone (no portals allowed).
                // Check UI exit button first (fastest), then entity search.
                var exitBtn = FindWishZoneExitButton(gc);
                if (exitBtn?.IsVisible == true)
                {
                    _isInSubZone = true;
                    ctx.Log("[WaveFarm] Portal failed — wish zone exit button found, switching to sub-zone exit");
                    return;
                }

                foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
                {
                    if (entity.Path != null && entity.Path.Contains("SekhemaPortal"))
                    {
                        _isInSubZone = true;
                        _exitPortalGridPos = entity.GridPosNum;
                        ctx.Log("[WaveFarm] Portal failed — SekhemaPortal found, switching to sub-zone exit");
                        return;
                    }
                }

                // Genuinely no portal — retry
                _portalKeyPressed = false;
                Status = "Portal not found — retrying";
            }
        }

        private void TickExitSubZone(BotContext ctx, ExileCore.GameController gc)
        {
            // Wish zones have a UI button in the bottom-right that teleports to the exit portal.
            // Much faster and more reliable than walking to the SekhemaPortal entity.
            var exitButton = FindWishZoneExitButton(gc);
            if (exitButton != null && exitButton.IsVisible)
            {
                if (BotInput.CanAct && (DateTime.Now - _lastClickTime).TotalMilliseconds > 500)
                {
                    var r = exitButton.GetClientRect();
                    var wr = gc.Window.GetWindowRectangle();
                    var absPos = new Vector2(wr.X + r.X + r.Width / 2, wr.Y + r.Y + r.Height / 2);
                    BotInput.Click(absPos);
                    _lastClickTime = DateTime.Now;
                    Status = "Clicking exit portal button...";
                    ctx.Log("[WaveFarm] Clicking wish zone exit button");
                }
                else
                {
                    Status = "Waiting to click exit button...";
                }
                return;
            }

            // Fallback: find and click SekhemaPortal entity directly
            Entity? exitPortal = null;
            foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
            {
                if (entity.Path != null && entity.Path.Contains("SekhemaPortal") && entity.IsTargetable)
                {
                    exitPortal = entity;
                    _exitPortalGridPos = entity.GridPosNum;
                    break;
                }
            }

            if (exitPortal != null)
            {
                var dist = exitPortal.DistancePlayer;
                if (dist > 20f)
                {
                    // A* path to the portal — the portal can be across walls,
                    // so raw MoveToward just wall-bangs. NavigateTo handles the route.
                    if (!ctx.Navigation.IsNavigating ||
                        Vector2.Distance(ctx.Navigation.Destination ?? Vector2.Zero, exitPortal.GridPosNum) > 10f)
                    {
                        ctx.Navigation.NavigateTo(gc, exitPortal.GridPosNum);
                    }
                    ctx.Navigation.Tick(gc);
                    Status = $"Walking to exit portal ({dist:F0} away)";
                }
                else
                {
                    ModeHelpers.ClickEntity(gc, exitPortal, ref _lastClickTime);
                    Status = "Clicking exit portal...";
                }
            }
            else if (_exitPortalGridPos.HasValue)
            {
                // Cached portal position from an earlier sighting (portal has since
                // streamed out of the bubble). Use A* pathfinding — not MoveToward —
                // since the cached spot can be across walls/corridors that raw
                // cursor-direction movement can't navigate. If pathfinding refuses,
                // the cache is stale (stale entity id, portal gone) — drop it and
                // fall through to exploration on the next tick.
                var playerPos = new Vector2(gc.Player.GridPosNum.X, gc.Player.GridPosNum.Y);
                var cached = _exitPortalGridPos.Value;
                var cachedDist = Vector2.Distance(playerPos, cached);
                if (cachedDist <= 20f)
                {
                    Status = $"At cached exit ({cachedDist:F0}g) — no live portal entity, re-scanning";
                    return;
                }
                bool pathOk = !ctx.Navigation.IsNavigating ||
                              Vector2.Distance(ctx.Navigation.Destination ?? Vector2.Zero, cached) > 10f
                    ? ctx.Navigation.NavigateTo(gc, cached)
                    : true;
                if (!pathOk)
                {
                    ctx.Log($"[WaveFarm] Cached exit ({cached.X:F0},{cached.Y:F0}) unreachable — clearing and exploring");
                    _exitPortalGridPos = null;
                    return;
                }
                ctx.Navigation.Tick(gc);
                Status = $"Walking to cached exit ({cachedDist:F0} away)";
            }
            else
            {
                // No exit button, no entity, no cached position.
                // Explore to reveal the portal entity.
                var playerPos = gc.Player.GridPosNum;
                ctx.Exploration.Update(playerPos);
                var target = ctx.Exploration.GetNextExplorationTarget(playerPos);
                if (target.HasValue)
                {
                    if (!ctx.Navigation.IsNavigating ||
                        Vector2.Distance(ctx.Navigation.Destination ?? Vector2.Zero, target.Value) > 20f)
                    {
                        ctx.Navigation.NavigateTo(gc, target.Value);
                    }
                    ctx.Navigation.Tick(gc);
                }
                Status = "No exit found — exploring to reveal portal...";
            }
        }

        /// <summary>
        /// Latching sub-zone detector. Checks SekhemaPortal entity AND the wish-zone
        /// exit UI button. If either is present, we are in a mirage / wish sub-zone
        /// and MUST exit via the portal entity / exit button — portal scrolls do not
        /// work. Sets <see cref="_isInSubZone"/> to true on detection; caches portal
        /// position for danger-zone avoidance. Idempotent and cheap — safe to call
        /// every tick. Only the hash-change handler clears <see cref="_isInSubZone"/>,
        /// so a transient signal loss (entity briefly out of bubble) doesn't unflag us.
        /// </summary>
        private void RefreshSubZoneSignals(BotContext ctx, ExileCore.GameController gc)
        {
            // Signal 1: SekhemaPortal entity in the live entity list. Authoritative
            // (doesn't depend on UI indices). Also refresh the cached grid pos each
            // time we see it in case the zone has multiple portals or the entity id
            // rotated.
            bool portalSeen = false;
            try
            {
                foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
                {
                    if (entity.Path == null) continue;
                    if (!entity.Path.Contains("SekhemaPortal")) continue;
                    portalSeen = true;
                    _exitPortalGridPos = entity.GridPosNum;
                    break;
                }
            }
            catch { /* entity list can race during zone transitions */ }

            // Signal 2: wish zone exit button in the bottom-right UI. Fallback for
            // the brief window where the portal entity hasn't streamed in yet.
            bool buttonSeen = false;
            try
            {
                var btn = FindWishZoneExitButton(gc);
                buttonSeen = btn?.IsVisible == true;
            }
            catch { /* UI reads can fail during transitions */ }

            if ((portalSeen || buttonSeen) && !_isInSubZone)
            {
                _isInSubZone = true;
                ctx.Log($"[WaveFarm] Sub-zone detected mid-map (portal={portalSeen}, button={buttonSeen}) — latching, will exit via SekhemaPortal");
            }
        }

        /// <summary>
        /// Find the wish zone exit portal UI button. Visible only inside wish/mirage sub-zones.
        /// Located at bottom-right of screen. Returns the clickable element or null.
        /// Walks the UI tree manually with ChildCount guards — never calls
        /// <c>GetChildFromIndices</c>, which logs an INFO line on every missing
        /// intermediate (producing noise at tick rate when indices have drifted
        /// across patches, as they currently have for this button).
        /// </summary>
        private static ExileCore.PoEMemory.Element? FindWishZoneExitButton(ExileCore.GameController gc)
        {
            try
            {
                var ui = gc.IngameState.IngameUi;
                if (ui == null) return null;
                var windowRect = gc.Window.GetWindowRectangle();
                var minX = windowRect.Width * 0.6f;
                var minY = windowRect.Height * 0.75f;

                // Manual walk: [142][7][17][0], bail silently on any missing index.
                var c142 = GetChildSafe(ui, 142);
                if (c142 == null) return null;
                var c7 = GetChildSafe(c142, 7);
                if (c7 == null) return null;

                // Fast path: the known deep path first.
                var btnFast = GetChildSafe(GetChildSafe(c7, 17), 0);
                if (btnFast?.IsVisible == true)
                {
                    var rF = btnFast.GetClientRect();
                    if (rF.X > minX && rF.Y > minY && rF.Width > 50 && rF.Width < 200)
                        return btnFast;
                }

                // Fallback: scan [142][7] children for a container with 11 kids
                // whose [0] lives in the bottom-right.
                for (int i = 0; i < c7.ChildCount; i++)
                {
                    var child = GetChildSafe(c7, i);
                    if (child == null || child.ChildCount != 11 || !child.IsVisible)
                        continue;

                    var btn = GetChildSafe(child, 0);
                    if (btn?.IsVisible != true) continue;

                    var r = btn.GetClientRect();
                    if (r.X > minX && r.Y > minY && r.Width > 50 && r.Width < 200)
                        return btn;
                }
            }
            catch { }
            return null;
        }

        /// <summary>GetChildAtIndex wrapper that respects ChildCount and never throws
        /// or logs on out-of-range indices. Returns null for any missing child.</summary>
        private static ExileCore.PoEMemory.Element? GetChildSafe(ExileCore.PoEMemory.Element? parent, int index)
        {
            if (parent == null) return null;
            try
            {
                if (index < 0 || index >= parent.ChildCount) return null;
                return parent.GetChildAtIndex(index);
            }
            catch { return null; }
        }

        // ══════════════════════════════════════════════════════════════
        // Area changes
        // ══════════════════════════════════════════════════════════════

        private void OnAreaChanged(BotContext ctx, string newArea)
        {
            var gc = ctx.Game;
            ModeHelpers.CancelAllSystems(ctx);
            _hideoutFlow.Cancel();

            if (gc.Area.CurrentArea.IsHideout || gc.Area.CurrentArea.IsTown)
            {
                ctx.Mechanics.Reset();
                _wave.Reset();

                if (_mapCompleted)
                {
                    _mapCompleted = false;
                    _runsCompleted++;
                    StartHideoutFlow(ctx);
                    ctx.Log($"[WaveFarm] Run #{_runsCompleted} complete");
                }
                else
                {
                    // Died or unexpected return — re-enter via portal
                    _phase = WaveFarmPhase.InHideout;
                    _hideoutFlow.StartPortalReentry();
                    Status = "Returned to hideout — re-entering";
                    ctx.Log("[WaveFarm] Unexpected hideout return — portal re-entry");
                }
            }
            else
            {
                // Entered a map zone
                ctx.Log($"[WaveFarm] Entered: {newArea}");
                _zoneEnteredAt = DateTime.Now;
                _zoneSettleUntil = DateTime.Now.AddSeconds(ZoneSettleSeconds);
                InitMapState(ctx, gc);
                // Re-assert plan's must-loot keywords each map — Boss/Sim modes
                // clear LootSystem.MustLootItems, so without this we'd lose the
                // plan's bypass list after any cross-mode session.
                ReassertMustLoot(ctx);
            }
        }

        public void Render(BotContext ctx)
        {
            var g = ctx.Graphics;
            if (g == null) return;

            var x = 100f;
            var y = 120f;
            const float lineH = 16f;
            var dim = new SharpDX.Color(180, 180, 180, 255);
            var bright = SharpDX.Color.White;
            var accent = SharpDX.Color.LimeGreen;
            var warn = SharpDX.Color.Yellow;
            var bad = SharpDX.Color.OrangeRed;

            // Phase + Status
            var subZoneTag = _isInSubZone ? " [SUB-ZONE]" : "";
            g.DrawText($"[{_phase}]{subZoneTag} {Status}", new Vector2(x, y), _isInSubZone ? warn : accent);
            y += lineH;

            // Decision
            g.DrawText($"Decision: {_wave.Decision}", new Vector2(x, y), bright);
            y += lineH;

            // Exploration
            var cov = ctx.Exploration.ActiveBlobCoverage;
            var covColor = cov > 0.9f ? accent : cov > 0.5f ? bright : warn;
            g.DrawText($"Coverage: {cov:P0}  Regions: {ctx.Exploration.ActiveBlob?.Regions.Count ?? 0}", new Vector2(x, y), covColor);
            y += lineH;

            // ThreatMap
            if (ctx.ThreatMap.IsInitialized)
            {
                var tm = ctx.ThreatMap;
                g.DrawText($"Threats: {tm.TotalAlive} alive / {tm.TotalTracked} seen / {tm.TotalDead} dead", new Vector2(x, y), dim);
                y += lineH;
            }

            // ClearPlan — chunk-queue status
            var plan = _wave.ClearPlan;
            if (plan.IsInitialized)
            {
                var planColor = plan.IsComplete ? accent : plan.CurrentTarget.HasValue ? bright : warn;
                g.DrawText($"ClearPlan: {plan.VisitedCount}/{plan.TotalCount} chunks — {plan.Status}",
                    new Vector2(x, y), planColor);
                y += lineH;
                if (plan.CurrentTarget.HasValue)
                {
                    var t = plan.CurrentTarget.Value;
                    g.DrawText($"  → target ({t.X:F0},{t.Y:F0})", new Vector2(x, y), dim);
                    y += lineH;
                }
            }

            // Combat
            var combat = ctx.Combat;
            if (combat.InCombat)
            {
                g.DrawText($"Combat: {combat.NearbyMonsterCount} mobs (w={combat.WeightedDensity})  Best: {combat.BestTarget?.RenderName ?? "?"}", new Vector2(x, y), bad);
                y += lineH;
                if (combat.DeprioritizedCount > 0)
                {
                    g.DrawText($"Deprioritized: {combat.DeprioritizedCount} targets", new Vector2(x, y), warn);
                    y += lineH;
                }
            }
            else
            {
                g.DrawText("Combat: idle", new Vector2(x, y), dim);
                y += lineH;
            }

            // Navigation
            var nav = ctx.Navigation;
            if (nav.IsNavigating)
            {
                var dest = nav.Destination ?? Vector2.Zero;
                var wp = nav.CurrentWaypointIndex;
                var total = nav.CurrentNavPath.Count;
                g.DrawText($"Nav: wp {wp}/{total} → ({dest.X:F0},{dest.Y:F0})  Stuck: {nav.StuckRecoveries}", new Vector2(x, y), bright);
            }
            else
            {
                g.DrawText($"Nav: stopped", new Vector2(x, y), dim);
            }
            y += lineH;

            // Input rate
            var inputRate = BotInput.RawInputEventsPerSecond;
            var inputColor = inputRate > 10 ? bad : inputRate > 5 ? warn : dim;
            g.DrawText($"Input: {inputRate} events/sec", new Vector2(x, y), inputColor);
            y += lineH;

            // Movement layer
            if (BotInput.IsMovementActive)
            {
                var suspended = BotInput.IsMovementSuspended ? " [SUSPENDED]" : "";
                g.DrawText($"Move: active{suspended}", new Vector2(x, y), accent);
            }
            else
            {
                g.DrawText("Move: off", new Vector2(x, y), dim);
            }
            y += lineH;

            // Loot
            if (ctx.Loot.HasLootNearby)
            {
                g.DrawText($"Loot: {ctx.Loot.Candidates.Count} items nearby", new Vector2(x, y), warn);
                y += lineH;
            }

            // Interaction
            if (ctx.Interaction.IsBusy)
            {
                g.DrawText($"Interact: {ctx.Interaction.Status}", new Vector2(x, y), warn);
                y += lineH;
            }

            // Loot stats
            var lm = _wave.LootMetrics;
            if (lm.PickupAttempts > 0)
            {
                g.DrawText($"Loot: {lm.PickupSuccesses}/{lm.PickupAttempts} ({lm.SuccessRate:P0})", new Vector2(x, y), lm.SuccessRate > 0.7f ? bright : warn);
                y += lineH;
            }
            // Loot decision debug — shows why loot was/wasn't chosen
            if (!string.IsNullOrEmpty(_wave.LootDebug))
            {
                g.DrawText($"LootDbg: {_wave.LootDebug}", new Vector2(x, y), dim);
                y += lineH;
            }

            // Active mechanic
            if (ctx.Mechanics.ActiveMechanic != null)
            {
                g.DrawText($"Mechanic: {ctx.Mechanics.ActiveMechanic.Name} — {ctx.Mechanics.ActiveMechanic.Status}", new Vector2(x, y), accent);
                y += lineH;
            }

            // Skill action
            if (!string.IsNullOrEmpty(combat.LastSkillAction))
            {
                g.DrawText($"Skill: {combat.LastSkillAction}", new Vector2(x, y), dim);
                y += lineH;
            }

            // Performance overlay (opt-in — off by default)
            if (ctx.Settings.DebugPerfOverlay.Value)
            {
                y += lineH / 2;
                g.DrawText("── Perf (ms) avg / p95 / max ──", new Vector2(x, y), bright);
                y += lineH;
                foreach (var (name, st) in ctx.Perf.TopSections(8))
                {
                    var c = st.MaxMs > 20 ? bad : st.MaxMs > 8 ? warn : dim;
                    g.DrawText($"  {name,-26} {st.AvgMs,5:F2} / {st.P95Ms,5:F2} / {st.MaxMs,5:F2}  (n={st.Count})",
                        new Vector2(x, y), c);
                    y += lineH;
                }

                var lootSkipTotal = ctx.Perf.FailureTotal("lootSkip");
                var lootTotal = ctx.Perf.FailureTotal("loot");
                var interactTotal = ctx.Perf.FailureTotal("interact");
                var exploreTotal = ctx.Perf.FailureTotal("explore");
                g.DrawText($"── Failures — lootSkip:{lootSkipTotal}  lootClick:{lootTotal}  interact:{interactTotal}  explore:{exploreTotal} ──",
                    new Vector2(x, y), bright);
                y += lineH;
                foreach (var cat in new[] { "loot", "interact", "lootSkip", "explore" })
                {
                    foreach (var (reason, count) in ctx.Perf.TopFailures(cat, 3))
                    {
                        g.DrawText($"  [{cat}] {reason}: {count}", new Vector2(x, y), warn);
                        y += lineH;
                    }
                }
            }
        }
    }

    internal enum WaveFarmPhase
    {
        Idle,
        InHideout,
        InMap,
        ExitMap,
    }
}
