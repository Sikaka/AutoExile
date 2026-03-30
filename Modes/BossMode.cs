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
        private int _deathCount;
        private int _runsCompleted;
        private int _targetItemsLooted;
        private DateTime _sessionStartTime;
        private DateTime _runStartTime;
        private double _totalRunTimeMs;   // cumulative run time across completed runs

        // ── Exit map state ──
        private bool _portalKeyPressed;

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

            // Area change detection
            var currentArea = gc.Area?.CurrentArea?.Name ?? "";
            if (currentArea != _lastAreaName && !string.IsNullOrEmpty(currentArea))
            {
                _lastAreaName = currentArea;
                OnAreaChanged(ctx, currentArea);
            }

            // Combat — suppress positioning in maze/loot but still fire skills at nearby enemies
            if (_phase == BossPhase.InBossZone || _phase == BossPhase.LootSweep)
            {
                var suppressPos = _activeEncounter?.SuppressCombatPositioning == true
                    || _phase == BossPhase.LootSweep;
                ctx.Combat.SuppressPositioning = suppressPos;
                ctx.Navigation.RelaxedPathing = _activeEncounter?.RelaxedPathing == true;
                ctx.Combat.Tick(ctx);
                ctx.Combat.SuppressPositioning = false;
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

        private void StartHideoutFlow(BotContext ctx)
        {
            _phase = BossPhase.InHideout;
            _phaseStartTime = DateTime.Now;
            _mapCompleted = false;
            _portalKeyPressed = false;
            _deathCount = 0;
            _activeEncounter?.Reset();
            ctx.Loot.MustLootItems.Clear();

            // Stash filter: keep boss fragments in inventory, stash everything else
            Func<ExileCore.PoEMemory.MemoryObjects.ServerInventory.InventSlotItem, bool>? stashFilter = null;
            if (_activeEncounter!.InventoryFragmentPath != null)
            {
                var fragPath = _activeEncounter.InventoryFragmentPath;
                stashFilter = item =>
                {
                    // Return true = stash this item, false = keep it
                    var path = item.Item?.Path;
                    if (path != null && path.Contains(fragPath, StringComparison.OrdinalIgnoreCase))
                        return false; // keep fragments
                    return true; // stash everything else
                };
            }

            _hideoutFlow.Start(_activeEncounter.MapFilter,
                stashItemFilter: stashFilter,
                inventoryFragmentPath: _activeEncounter.InventoryFragmentPath);
            Status = $"Hideout — preparing {_activeEncounter.Name}";
        }

        private void TickHideout(BotContext ctx)
        {
            var signal = _hideoutFlow.Tick(ctx);
            Status = _hideoutFlow.Status;

            if (signal == HideoutSignal.PortalTimeout)
            {
                _phase = BossPhase.InHideout;
                _phaseStartTime = DateTime.Now;
                _hideoutFlow.Start(_activeEncounter!.MapFilter);
                Status = "No portal found — retrying";
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

            // Handle pending pickup
            if (ctx.Interaction.IsBusy)
            {
                if (interactionResult == InteractionResult.Succeeded)
                {
                    ctx.Loot.Scan(gc);
                    _lastLootScan = DateTime.Now;
                }
                Status = $"Looting: {_lootTracker.PendingItemName}";
                return;
            }

            // Pick up next item
            if (ctx.Loot.HasLootNearby)
            {
                var (_, candidate) = ctx.Loot.PickupNext(ctx.Interaction, ctx.Navigation);
                if (candidate != null && ctx.Interaction.IsBusy)
                {
                    _lootTracker.SetPending(candidate.Entity.Id, candidate.ItemName, candidate.ChaosValue);
                    Status = $"Looting: {candidate.ItemName}";
                    return;
                }
            }

            // Label toggle unstick
            if (ctx.Loot.TogglePhase != LootSystem.LabelTogglePhase.Idle)
            {
                ctx.Loot.TickLabelToggle(gc);
                Status = $"Label toggle: {ctx.Loot.ToggleStatus}";
                return;
            }
            if (ctx.Loot.ShouldToggleLabels(gc))
            {
                ctx.Loot.StartLabelToggle(gc);
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

            if ((DateTime.Now - _phaseStartTime).TotalSeconds > 30)
            {
                _phase = BossPhase.Done;
                Status = "Exit timeout";
                return;
            }

            if (!ModeHelpers.CanAct(_lastActionTime, MajorActionCooldownMs)) return;

            // Close panels
            if (gc.IngameState.IngameUi.StashElement?.IsVisible == true ||
                gc.IngameState.IngameUi.InventoryPanel?.IsVisible == true)
            {
                BotInput.PressKey(Keys.Escape);
                _lastActionTime = DateTime.Now;
                return;
            }

            // Try existing portals first (boss zones have pre-placed exit portals like RitualBossPortal)
            var portal = FindExitPortal(gc);
            if (portal != null)
            {
                if (ctx.Interaction.IsBusy)
                {
                    Status = $"Clicking exit portal ({ctx.Interaction.Status})";
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
                Status = $"Clicking exit portal (attempt {_exitPortalAttempts + 1})";
                return;
            }

            // No existing portal — open one with portal key
            if (!_portalKeyPressed)
            {
                var portalKey = ctx.Settings.Boss.PortalKey.Value;
                BotInput.PressKey(portalKey);
                _portalKeyPressed = true;
                _lastActionTime = DateTime.Now;
                Status = "Opening portal...";
                return;
            }

            Status = "Waiting for portal...";
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
                // Entered boss zone
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
