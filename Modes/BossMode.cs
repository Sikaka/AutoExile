using ExileCore;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using AutoExile.Modes.BossEncounters;
using AutoExile.Modes.Shared;
using AutoExile.Systems;
using System.Numerics;
using System.Windows.Forms;

namespace AutoExile.Modes
{
    /// <summary>
    /// Boss farming mode: hideout → insert fragment → enter → kill boss → loot → exit → repeat.
    /// Delegates in-zone logic to a selected IBossEncounter. Handles the hideout loop,
    /// death/retry, loot sweep, and exit — encounters focus only on the fight.
    /// </summary>
    public class BossMode : IBotMode
    {
        public string Name => "Boss";

        // ── Encounter registry ──
        private readonly Dictionary<string, IBossEncounter> _encounters = new();
        private IBossEncounter? _activeEncounter;
        public IReadOnlyCollection<string> EncounterNames => _encounters.Keys;

        // ── Phase machine ──
        private BossPhase _phase = BossPhase.Idle;
        private DateTime _phaseStartTime = DateTime.Now;
        private DateTime _lastActionTime = DateTime.MinValue;
        private const float MajorActionCooldownMs = 500f;

        // ── Hideout flow ──
        private readonly HideoutFlow _hideoutFlow = new();
        private readonly LootPickupTracker _lootTracker = new();

        // ── Run state ──
        private bool _mapCompleted;
        private string _lastAreaName = "";
        private string _lastBossType = "";
        private int _deathCount;
        private int _runsCompleted;
        private int _targetItemsLooted;
        private DateTime _sessionStartTime;
        private DateTime _runStartTime;
        private double _totalRunTimeMs;   // cumulative run time across completed runs

        // ── Exit map state ──
        private bool _portalKeyPressed;
        private Vector2? _entryPortalPos; // cached on zone entry so we can navigate back

        // ── Dodge ──
        private DateTime _lastDodgeTime = DateTime.MinValue;
        /// <summary>Last dodge result for recording — cleared each tick, set by TryDodge.</summary>
        public string LastDodgeAction { get; private set; } = "";

        // ── Status ──
        public string Status { get; private set; } = "";
        public string Decision { get; private set; } = "";
        public BossPhase Phase => _phase;

        // ── Stats (for web UI) ──
        public int RunsCompleted => _runsCompleted;
        public int Deaths => _deathCount;
        public int TargetItemsLooted => _targetItemsLooted;
        public double AvgRunTimeSeconds => _runsCompleted > 0 ? (_totalRunTimeMs / _runsCompleted) / 1000.0 : 0;
        public double RunsPerDrop => _targetItemsLooted > 0 ? (double)_runsCompleted / _targetItemsLooted : 0;
        public double SessionSeconds => (DateTime.Now - _sessionStartTime).TotalSeconds;
        public DateTime RunStartTime => _runStartTime;
        public double ChaosPerHour(int keyDropValue) =>
            SessionSeconds > 60 ? _targetItemsLooted * keyDropValue / (SessionSeconds / 3600.0) : 0;

        public enum BossPhase
        {
            Idle,
            InHideout,
            EnterPortal,
            InBossZone,
            LootSweep,
            ExitMap,
            Done,
        }

        // ── Registration ──

        public void Register(IBossEncounter encounter)
        {
            _encounters[encounter.Name] = encounter;
        }

        /// <summary>Called by BotCore when player dies.</summary>
        public void IncrementDeathCount() => _deathCount++;

        // ── Mode lifecycle ──

        public void OnEnter(BotContext ctx)
        {
            var gc = ctx.Game;
            _lastAreaName = gc.Area?.CurrentArea?.Name ?? "";

            // Resolve selected encounter
            var selectedName = ctx.Settings.Boss.BossType.Value;
            _lastBossType = selectedName ?? "";
            if (string.IsNullOrEmpty(selectedName) || !_encounters.TryGetValue(selectedName, out _activeEncounter))
            {
                _activeEncounter = _encounters.Values.FirstOrDefault();
                if (_activeEncounter == null)
                {
                    Status = "No boss encounters registered";
                    _phase = BossPhase.Idle;
                    return;
                }
            }

            _deathCount = 0;
            _runsCompleted = 0;
            _targetItemsLooted = 0;
            _totalRunTimeMs = 0;
            _mapCompleted = false;
            _portalKeyPressed = false;
            _sessionStartTime = DateTime.Now;
            _runStartTime = DateTime.Now;

            if (gc.Area.CurrentArea.IsHideout || gc.Area.CurrentArea.IsTown)
            {
                StartHideoutFlow(ctx);
            }
            else
            {
                // Already in a map — assume boss zone
                _activeEncounter.OnEnterZone(ctx);
                _phase = BossPhase.InBossZone;
                ModeHelpers.EnableDefaultCombat(ctx);
            }

            ctx.Log($"[Boss] Mode entered — encounter: {_activeEncounter.Name}");
        }

        public void OnExit()
        {
            _phase = BossPhase.Idle;
            _activeEncounter?.Reset();
            _hideoutFlow.Cancel();
            _lootTracker.Reset();
        }

        public void Tick(BotContext ctx)
        {
            var gc = ctx.Game;
            if (gc?.Player == null) return;

            // Detect boss type change mid-session (user changed setting while paused or between runs)
            var currentBossType = ctx.Settings.Boss.BossType.Value ?? "";
            if (currentBossType != _lastBossType && !string.IsNullOrEmpty(currentBossType)
                && (_phase == BossPhase.InHideout || _phase == BossPhase.Idle))
            {
                ctx.Log($"[Boss] Boss type changed: {_lastBossType} → {currentBossType}");
                _lastBossType = currentBossType;
                if (gc.Area.CurrentArea.IsHideout || gc.Area.CurrentArea.IsTown)
                {
                    ModeHelpers.CancelAllSystems(ctx);
                    _hideoutFlow.Cancel();
                    StartHideoutFlow(ctx);
                }
            }

            // Area change detection
            var currentArea = gc.Area?.CurrentArea?.Name ?? "";
            if (currentArea != _lastAreaName && !string.IsNullOrEmpty(currentArea))
            {
                _lastAreaName = currentArea;
                OnAreaChanged(ctx, currentArea);
            }

            // Walk-only mode during pre-fight setup — no dash, no combat, just move-only key
            ctx.Navigation.WalkOnly = _activeEncounter?.SuppressCombat == true;

            // Combat — skip entirely when encounter says so (pre-fight setup), suppress positioning in maze/loot
            if ((_phase == BossPhase.InBossZone || _phase == BossPhase.LootSweep)
                && _activeEncounter?.SuppressCombat != true)
            {
                // Dodge BEFORE combat — dodge signal only lasts 1 tick.
                // If dodge fires, skip combat entirely this tick to prevent async cursor races.
                LastDodgeAction = "";
                TryDodge(ctx);
                bool dodged = LastDodgeAction.StartsWith("blinked");

                if (!dodged)
                {
                    var suppressPos = _activeEncounter?.SuppressCombatPositioning == true
                        || _phase == BossPhase.LootSweep;
                    ctx.Combat.SuppressPositioning = suppressPos;
                    ctx.Navigation.RelaxedPathing = _activeEncounter?.RelaxedPathing == true;
                    ctx.Combat.Tick(ctx);
                    ctx.Combat.SuppressPositioning = false;
                }
            }
            else
            {
                ctx.Navigation.RelaxedPathing = false;
            }

            // Tick interaction system + track target item pickups across all phases
            var hadPendingLoot = _lootTracker.HasPending;
            var pendingLootName = _lootTracker.PendingItemName;
            var interactionResult = ctx.Interaction.Tick(gc);
            _lootTracker.HandleResult(interactionResult, ctx);

            if (hadPendingLoot && interactionResult == InteractionResult.Succeeded)
            {
                // Check if this was a target item
                if (_activeEncounter?.MustLootItems is { Count: > 0 } mustLoot &&
                    !string.IsNullOrEmpty(pendingLootName) &&
                    mustLoot.Any(m => pendingLootName.Contains(m, StringComparison.OrdinalIgnoreCase)))
                {
                    _targetItemsLooted++;
                    ctx.Log($"[Boss] Target item looted: {pendingLootName} (total: {_targetItemsLooted})");
                }
            }

            switch (_phase)
            {
                case BossPhase.InHideout:
                case BossPhase.EnterPortal:
                    TickHideout(ctx);
                    break;

                case BossPhase.InBossZone:
                    TickBossZone(ctx);
                    break;

                case BossPhase.LootSweep:
                    TickLootSweep(ctx, gc, interactionResult);
                    break;

                case BossPhase.ExitMap:
                    TickExitMap(ctx, gc);
                    break;

                case BossPhase.Done:
                    Status = $"Boss farming complete — {_runsCompleted} runs";
                    break;
            }
        }

        // ── Hideout ──

        private void StartHideoutFlow(BotContext ctx, bool resetRun = true)
        {
            _phase = BossPhase.InHideout;
            _phaseStartTime = DateTime.Now;
            _mapCompleted = false;
            _portalKeyPressed = false;

            // Re-read boss type selection (user may have changed it between runs)
            var selectedName = ctx.Settings.Boss.BossType.Value;
            if (!string.IsNullOrEmpty(selectedName) && _encounters.TryGetValue(selectedName, out var newEncounter))
            {
                if (_activeEncounter != newEncounter)
                {
                    ctx.Log($"[Boss] Switched encounter: {_activeEncounter?.Name} → {newEncounter.Name}");
                    _activeEncounter = newEncounter;
                    resetRun = true; // force reset when switching encounters
                }
            }

            if (resetRun)
            {
                _deathCount = 0;
                _activeEncounter?.Reset();
            }
            ctx.Loot.MustLootItems.Clear();

            var bossSettings = ctx.Settings.Boss;
            _hideoutFlow.Start(_activeEncounter!.MapFilter,
                stashItemFilter: GetStashFilter(),
                inventoryFragmentPath: _activeEncounter.InventoryFragmentPath,
                stashItemThreshold: bossSettings.StashItemThreshold.Value,
                dumpTabName: string.IsNullOrWhiteSpace(bossSettings.DumpTabName.Value) ? null : bossSettings.DumpTabName.Value,
                resourceTabName: string.IsNullOrWhiteSpace(bossSettings.ResourceTabName.Value) ? null : bossSettings.ResourceTabName.Value,
                withdrawFragmentPath: _activeEncounter.InventoryFragmentPath,
                fragmentStock: bossSettings.FragmentStock.Value,
                minFragments: _activeEncounter.FragmentCost);
            Status = $"Hideout — preparing {_activeEncounter.Name}";
        }

        /// <summary>Stash filter that keeps boss fragments in inventory.</summary>
        private Func<ExileCore.PoEMemory.MemoryObjects.ServerInventory.InventSlotItem, bool>? GetStashFilter()
        {
            if (_activeEncounter?.InventoryFragmentPath == null) return null;
            var fragPath = _activeEncounter.InventoryFragmentPath;
            return item =>
            {
                var path = item.Item?.Path;
                if (path != null && path.Contains(fragPath, StringComparison.OrdinalIgnoreCase))
                    return false; // keep fragments
                return true; // stash everything else
            };
        }

        private void TickHideout(BotContext ctx)
        {
            var signal = _hideoutFlow.Tick(ctx);
            Status = _hideoutFlow.Status;

            switch (signal)
            {
                case HideoutSignal.PortalTimeout:
                    StartHideoutFlow(ctx, resetRun: false);
                    Status = "No portal found — retrying";
                    break;
                case HideoutSignal.NoFragments:
                    _phase = BossPhase.Done;
                    Status = $"Out of fragments — {_runsCompleted} runs completed";
                    ctx.Log("[Boss] No fragments available — stopping");
                    break;
            }
        }

        // ── Boss zone ──

        private void TickBossZone(BotContext ctx)
        {
            if (_activeEncounter == null) return;

            // Update exploration
            var playerGrid = new Vector2(ctx.Game.Player.GridPosNum.X, ctx.Game.Player.GridPosNum.Y);
            ctx.Exploration.Update(playerGrid);

            var result = _activeEncounter.Tick(ctx);
            Status = $"[{_activeEncounter.Name}] {_activeEncounter.Status}";

            switch (result)
            {
                case BossEncounterResult.Complete:
                    _mapCompleted = true;
                    _phase = BossPhase.LootSweep;
                    _phaseStartTime = DateTime.Now;
                    _lootTracker.ResetCount();
                    ctx.Log($"[Boss] {_activeEncounter.Name} complete — looting");
                    break;

                case BossEncounterResult.Failed:
                    _mapCompleted = true;
                    _phase = BossPhase.ExitMap;
                    _phaseStartTime = DateTime.Now;
                    _exitPortalAttempts = 0;
                    ctx.Log($"[Boss] {_activeEncounter.Name} failed — exiting");
                    break;
            }
        }

        // ── Loot sweep ──

        private DateTime _lastLootScan = DateTime.MinValue;
        private const float LootScanIntervalMs = 500;

        private void TickLootSweep(BotContext ctx, GameController gc, InteractionResult interactionResult)
        {
            var timeout = ctx.Settings.Boss.LootSweepTimeoutSeconds.Value;
            if ((DateTime.Now - _phaseStartTime).TotalSeconds > timeout)
            {
                _phase = BossPhase.ExitMap;
                _phaseStartTime = DateTime.Now;
                _exitPortalAttempts = 0;
                ctx.Log("[Boss] Loot sweep timeout — exiting");
                return;
            }

            // Scan for loot
            if ((DateTime.Now - _lastLootScan).TotalMilliseconds >= LootScanIntervalMs)
            {
                ctx.Loot.Scan(gc);
                _lastLootScan = DateTime.Now;
            }

            var sweepElapsed = (DateTime.Now - _phaseStartTime).TotalSeconds;
            var remaining = timeout - sweepElapsed;
            var countdown = $"({remaining:F0}s left)";

            // Handle pending pickup
            if (ctx.Interaction.IsBusy)
            {
                if (interactionResult == InteractionResult.Succeeded)
                {
                    ctx.Loot.Scan(gc);
                    _lastLootScan = DateTime.Now;
                }
                Status = $"Looting: {_lootTracker.PendingItemName} {countdown}";
                return;
            }

            // Pick up next item
            if (ctx.Loot.HasLootNearby)
            {
                var (_, candidate) = ctx.Loot.PickupNext(ctx.Interaction, ctx.Navigation);
                if (candidate != null && ctx.Interaction.IsBusy)
                {
                    _lootTracker.SetPending(candidate.Entity.Id, candidate.ItemName, candidate.ChaosValue);
                    Status = $"Looting: {candidate.ItemName} {countdown}";
                    return;
                }
            }

            // Label toggle unstick
            if (ctx.Loot.TogglePhase != LootSystem.LabelTogglePhase.Idle)
            {
                ctx.Loot.TickLabelToggle(gc);
                Status = $"Label toggle: {ctx.Loot.ToggleStatus} {countdown}";
                return;
            }
            if (ctx.Loot.ShouldToggleLabels(gc))
            {
                ctx.Loot.StartLabelToggle(gc);
                return;
            }

            // Wait for loot to drop — labels need time to appear after boss death
            if (remaining > 0)
            {
                Status = $"Waiting for loot drops {countdown}";
                return;
            }

            // No more loot — exit
            _phase = BossPhase.ExitMap;
            _phaseStartTime = DateTime.Now;
            _exitPortalAttempts = 0;
            ctx.Log("[Boss] Loot sweep done — exiting");
        }

        // ── Exit map ──

        private int _exitPortalAttempts;

        private void TickExitMap(BotContext ctx, GameController gc)
        {
            if (gc.Area.CurrentArea.IsHideout)
                return;

            var exitElapsed = (DateTime.Now - _phaseStartTime).TotalSeconds;
            if (exitElapsed > 30)
            {
                _phase = BossPhase.Done;
                Status = "Exit timeout";
                return;
            }
            var exitCountdown = $"({30 - exitElapsed:F0}s left)";

            if (!ModeHelpers.CanAct(_lastActionTime, MajorActionCooldownMs)) return;

            // Close panels
            if (gc.IngameState.IngameUi.StashElement?.IsVisible == true ||
                gc.IngameState.IngameUi.InventoryPanel?.IsVisible == true)
            {
                BotInput.PressKey(Keys.Escape);
                _lastActionTime = DateTime.Now;
                return;
            }

            // Descension altar panel — click the X button instead of Escape (Escape opens pause menu)
            var ascPanel = gc.IngameState.IngameUi.AscendancySelectPanel;
            if (ascPanel?.IsVisible == true)
            {
                // Close button is at [0][1] in the panel child tree
                var closeBtn = ascPanel.GetChildAtIndex(0)?.GetChildAtIndex(1);
                if (closeBtn?.IsVisible == true)
                {
                    var rect = closeBtn.GetClientRect();
                    var windowRect = gc.Window.GetWindowRectangle();
                    var absPos = new Vector2(windowRect.X + rect.Center.X, windowRect.Y + rect.Center.Y);
                    BotInput.Click(absPos);
                    _lastActionTime = DateTime.Now;
                    ctx.Log("[Boss] Closing descension altar panel (misclick)");
                }
                return;
            }

            // Try existing portals first (boss zones have pre-placed exit portals like RitualBossPortal)
            var portal = FindExitPortal(gc);
            if (portal != null)
            {
                if (ctx.Interaction.IsBusy)
                {
                    Status = $"Clicking exit portal ({ctx.Interaction.Status}) {exitCountdown}";
                    return;
                }

                // Check if previous attempt failed
                if (!string.IsNullOrEmpty(ctx.Interaction.LastFailReason))
                {
                    _exitPortalAttempts++;
                    ctx.Log($"[Boss] Portal click failed: {ctx.Interaction.LastFailReason} (attempt {_exitPortalAttempts})");
                }

                // Retry — start new interaction
                ctx.Interaction.InteractWithEntity(portal, ctx.Navigation);
                Status = $"Clicking exit portal (attempt {_exitPortalAttempts + 1}) {exitCountdown}";
                return;
            }

            // No portal found nearby — navigate back to cached entry position
            if (_entryPortalPos.HasValue)
            {
                var distToEntry = Vector2.Distance(gc.Player.GridPosNum, _entryPortalPos.Value);
                if (distToEntry > 30)
                {
                    if (!ctx.Navigation.IsNavigating)
                        ctx.Navigation.NavigateTo(gc, _entryPortalPos.Value);
                    Status = $"Returning to entry portal ({distToEntry:F0}g away) {exitCountdown}";
                    return;
                }
            }

            // At entry position but still no portal — open one with portal key
            if (!_portalKeyPressed)
            {
                var portalKey = ctx.Settings.Boss.PortalKey.Value;
                BotInput.PressKey(portalKey);
                _portalKeyPressed = true;
                _lastActionTime = DateTime.Now;
                Status = $"Opening portal... {exitCountdown}";
                return;
            }

            Status = $"Waiting for portal... {exitCountdown}";
        }

        /// <summary>
        /// Find exit portal — checks both TownPortal entities and AreaTransition entities
        /// that lead to hideout (boss zones use pre-placed portals, not player-opened ones).
        /// </summary>
        private Entity? FindExitPortal(GameController gc)
        {
            // Check regular town portals first
            var townPortal = ModeHelpers.FindNearestPortal(gc);
            if (townPortal != null) return townPortal;

            // Check area transitions (boss exit portals like RitualBossPortal)
            Entity? best = null;
            float bestDist = float.MaxValue;
            foreach (var entity in gc.EntityListWrapper.ValidEntitiesByType[EntityType.AreaTransition])
            {
                if (!entity.IsTargetable) continue;
                // Boss exit portals typically have "Portal" in their path
                if (!entity.Path.Contains("Portal", StringComparison.OrdinalIgnoreCase)) continue;
                if (entity.DistancePlayer < bestDist)
                {
                    bestDist = entity.DistancePlayer;
                    best = entity;
                }
            }
            return best;
        }

        // ── Dodge ──

        private void TryDodge(BotContext ctx)
        {
            var threatSettings = ctx.Settings.Threat;
            if (!threatSettings.AutoDodge.Value) return;
            if (!ctx.Threat.DodgeUrgent)  return;

            if ((DateTime.Now - _lastDodgeTime).TotalMilliseconds < threatSettings.DodgeCooldownMs.Value)
            {
                LastDodgeAction = $"skip:cooldown for:{ctx.Threat.ThreatSkillName}";
                return;
            }
            // Find a ready movement skill (blink/dash) — don't check CanAct, dodge bypasses gate
            MovementSkillInfo? blinkSkill = null;
            foreach (var ms in ctx.Navigation.MovementSkills)
            {
                if (!ms.IsReady) continue;
                if (ms.MinCastIntervalMs > 0 &&
                    (DateTime.Now - ms.LastUsedAt).TotalMilliseconds < ms.MinCastIntervalMs)
                    continue;
                blinkSkill = ms;
                break;
            }
            if (blinkSkill == null)
            {
                LastDodgeAction = $"skip:no_blink for:{ctx.Threat.ThreatSkillName}";
                return;
            }

            // Aim perpendicular to attack vector
            var gc = ctx.Game;
            var playerGrid = gc.Player.GridPosNum;
            var dodgeTarget = playerGrid + ctx.Threat.DodgeDirection * threatSettings.DodgeDistance.Value;
            var screenPos = Pathfinding.GridToScreen(gc, dodgeTarget);
            var windowRect = gc.Window.GetWindowRectangle();

            if (screenPos.X <= 0 || screenPos.X >= windowRect.Width ||
                screenPos.Y <= 0 || screenPos.Y >= windowRect.Height)
            {
                LastDodgeAction = $"skip:offscreen for:{ctx.Threat.ThreatSkillName}";
                return;
            }

            var absPos = new Vector2(windowRect.X + screenPos.X, windowRect.Y + screenPos.Y);
            if (BotInput.ForceCursorPressKey(absPos, blinkSkill.Key))
            {
                _lastDodgeTime = DateTime.Now;
                LastDodgeAction = $"blinked:{blinkSkill.Key} for:{ctx.Threat.ThreatSkillName} prog:{ctx.Threat.ThreatProgress:F2}";
                ctx.Log($"[Boss] Dodge! {LastDodgeAction}");
            }
            else
            {
                LastDodgeAction = $"skip:input_rejected for:{ctx.Threat.ThreatSkillName}";
            }
        }

        // ── Area change ──

        private void OnAreaChanged(BotContext ctx, string newArea)
        {
            var gc = ctx.Game;
            ModeHelpers.CancelAllSystems(ctx);
            _hideoutFlow.Cancel();

            if (gc.Area.CurrentArea.IsHideout || gc.Area.CurrentArea.IsTown)
            {
                if (_mapCompleted)
                {
                    _totalRunTimeMs += (DateTime.Now - _runStartTime).TotalMilliseconds;
                    _runsCompleted++;
                    _mapCompleted = false;
                    _runStartTime = DateTime.Now;
                    StartHideoutFlow(ctx);
                    ctx.Log($"[Boss] Run {_runsCompleted} complete ({AvgRunTimeSeconds:F0}s avg) — starting next");
                }
                else if (_deathCount > 0 && _deathCount < ctx.Settings.Boss.MaxDeaths.Value)
                {
                    _phase = BossPhase.EnterPortal;
                    _phaseStartTime = DateTime.Now;
                    _hideoutFlow.StartPortalReentry();
                    Status = $"Died ({_deathCount}) — re-entering";
                }
                else if (_deathCount >= ctx.Settings.Boss.MaxDeaths.Value)
                {
                    _totalRunTimeMs += (DateTime.Now - _runStartTime).TotalMilliseconds;
                    _runsCompleted++;
                    _runStartTime = DateTime.Now;
                    StartHideoutFlow(ctx);
                    ctx.Log("[Boss] Too many deaths — starting new run");
                }
                else
                {
                    StartHideoutFlow(ctx);
                }
            }
            else
            {
                // Entered boss zone — cache entry portal position before moving away
                _entryPortalPos = null;
                foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
                {
                    if (entity.Type == EntityType.TownPortal && entity.IsTargetable)
                    {
                        _entryPortalPos = entity.GridPosNum;
                        ctx.Log($"[Boss] Cached entry portal at grid ({entity.GridPosNum.X:F0},{entity.GridPosNum.Y:F0})");
                        break;
                    }
                }
                // Also check AreaTransition portals (e.g. MemoryBossPortal)
                if (_entryPortalPos == null)
                {
                    foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
                    {
                        if (entity.Type == EntityType.AreaTransition && entity.IsTargetable &&
                            entity.Path?.Contains("Portal", StringComparison.OrdinalIgnoreCase) == true)
                        {
                            _entryPortalPos = entity.GridPosNum;
                            ctx.Log($"[Boss] Cached entry transition at grid ({entity.GridPosNum.X:F0},{entity.GridPosNum.Y:F0})");
                            break;
                        }
                    }
                }
                // Fallback: cache player spawn position (portal is always near spawn)
                if (_entryPortalPos == null)
                {
                    _entryPortalPos = gc.Player.GridPosNum;
                    ctx.Log($"[Boss] No portal found — cached spawn position ({gc.Player.GridPosNum.X:F0},{gc.Player.GridPosNum.Y:F0})");
                }

                _activeEncounter?.OnEnterZone(ctx);
                _phase = BossPhase.InBossZone;
                _phaseStartTime = DateTime.Now;
                _portalKeyPressed = false;
                ModeHelpers.EnableDefaultCombat(ctx);
                ctx.Loot.ClearFailed();

                // Apply encounter-specific loot whitelist
                if (_activeEncounter?.MustLootItems is { Count: > 0 } items)
                    ctx.Loot.MustLootItems = new HashSet<string>(items, StringComparer.OrdinalIgnoreCase);
                else
                    ctx.Loot.MustLootItems.Clear();

                Status = $"Entered {newArea}";
                ctx.Log($"[Boss] Entered zone: {newArea}");
            }
        }

        // ── Render ──

        public void Render(BotContext ctx)
        {
            var g = ctx.Graphics;
            if (g == null) return;

            float hudX = 20, hudY = 200, lineH = 18;
            g.DrawText($"Boss: {_activeEncounter?.Name ?? "none"}", new Vector2(hudX, hudY), SharpDX.Color.Orange);
            hudY += lineH;
            g.DrawText($"Phase: {_phase}  Runs: {_runsCompleted}  Deaths: {_deathCount}  Drops: {_targetItemsLooted}",
                new Vector2(hudX, hudY), SharpDX.Color.White);
            hudY += lineH;
            g.DrawText(Status, new Vector2(hudX, hudY), SharpDX.Color.Gray);

            _activeEncounter?.Render(ctx);
        }
    }
}
