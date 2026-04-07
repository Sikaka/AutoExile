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

            _hideoutFlow.Start(
                mapFilter: _ => true, // accept any map
                stashItemThreshold: 0
            );

            Status = "Hideout — preparing next run";
        }

        private void TickHideout(BotContext ctx)
        {
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
            }

            ctx.Mechanics.Reset();
            ctx.Loot.ClearFailed();

            Status = "In map — wave farming";
            ctx.Log($"[WaveFarm] Map initialized: {gc.Area?.CurrentArea?.Name}");
        }

        private void TickInMap(BotContext ctx)
        {
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

            // Sub-zone exit: find and click SekhemaPortal (exit back to parent map)
            if (_isInSubZone)
            {
                TickExitSubZone(ctx, gc);
                return;
            }

            if (!_portalKeyPressed)
            {
                // Press portal key from settings
                var portalKey = ctx.Settings.Farming.PortalKey.Value;
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
                    ctx.Navigation.MoveToward(gc, exitPortal.GridPosNum);
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
                var playerPos = new Vector2(gc.Player.GridPosNum.X, gc.Player.GridPosNum.Y);
                ctx.Navigation.MoveToward(gc, _exitPortalGridPos.Value);
                Status = $"Walking to cached exit ({Vector2.Distance(playerPos, _exitPortalGridPos.Value):F0} away)";
            }
            else
            {
                // No exit button, no entity, no cached position.
                // Explore to reveal the portal entity.
                var playerPos = gc.Player.GridPosNum;
                ctx.Exploration.Update(playerPos);
                var target = ctx.Exploration.GetNextExplorationTarget(playerPos);
                if (target.HasValue)
                    ctx.Navigation.MoveToward(gc, target.Value);
                Status = "No exit found — exploring to reveal portal...";
            }
        }

        /// <summary>
        /// Find the wish zone exit portal UI button. Visible only inside wish/mirage sub-zones.
        /// Located at bottom-right of screen. Returns the clickable element or null.
        /// </summary>
        private static ExileCore.PoEMemory.Element? FindWishZoneExitButton(ExileCore.GameController gc)
        {
            try
            {
                // The button is at IngameUi[?][1][142][7][17][0]
                // Index 142 may shift between patches, so we search [7]'s children
                // for a container with 11 children where child[0] is visible in bottom-right
                var ui = gc.IngameState.IngameUi;
                var windowRect = gc.Window.GetWindowRectangle();
                var minX = windowRect.Width * 0.6f;
                var minY = windowRect.Height * 0.75f;

                // Try the known path first (fast path)
                try
                {
                    var btn = ui.GetChildFromIndices(142, 7, 17, 0);
                    if (btn?.IsVisible == true)
                    {
                        var r = btn.GetClientRect();
                        if (r.X > minX && r.Y > minY && r.Width > 50 && r.Width < 200)
                            return btn;
                    }
                }
                catch { }

                // Fallback: scan [142][7] children for the right container
                try
                {
                    var container = ui.GetChildFromIndices(142, 7);
                    if (container == null) return null;

                    for (int i = 0; i < container.ChildCount; i++)
                    {
                        var child = container.GetChildAtIndex(i);
                        if (child == null || child.ChildCount != 11 || !child.IsVisible)
                            continue;

                        var btn = child.GetChildAtIndex(0);
                        if (btn?.IsVisible != true) continue;

                        var r = btn.GetClientRect();
                        if (r.X > minX && r.Y > minY && r.Width > 50 && r.Width < 200)
                            return btn;
                    }
                }
                catch { }
            }
            catch { }
            return null;
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
