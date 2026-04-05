using System.Numerics;
using ExileCore;
using ExileCore.PoEMemory;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.Elements;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using AutoExile.Systems;
using SharpDX;
using RectangleF = SharpDX.RectangleF;
using Pathfinding = AutoExile.Systems.Pathfinding;
using Vector2 = System.Numerics.Vector2;

namespace AutoExile.Mechanics
{
    public enum RitualPhase
    {
        Idle,
        NavigateToAltar,
        ClickAltar,
        WaitForActivation,
        Fighting,
        Looting,
        Complete,
        Abandoned,
        Failed,
    }

    /// <summary>
    /// Ritual encounter mechanic handler (BETA).
    ///
    /// Lifecycle:
    ///   Detect altar → Navigate → Click to activate → Fight (locked in by RitualBlocker) →
    ///   Monsters die → Loot → Complete
    ///
    /// Entity structure:
    ///   - RitualRuneInteractable: IngameIcon, clickable, has StateMachine + MinimapIcon
    ///     StateMachine: current_state (1=fresh, 2=active, 3=complete), interaction_enabled (0/1)
    ///     MinimapIcon: "RitualRune" (fresh) → "RitualRuneFinished" (complete)
    ///   - RitualRuneObject: Terrain, visual only, mirrors current_state
    ///   - RitualRuneLight: Terrain, visual only
    ///   - RitualBlocker: Terrain, invisible wall during active encounter, despawns when done
    ///
    /// Reward shop:
    ///   IngameUi.RitualWindow — typed panel with Items (List&lt;NormalInventoryItem&gt;)
    ///   Strategy: complete all rituals before spending tribute. Shop interaction deferred to future.
    /// </summary>
    public class RitualMechanic : IMapMechanic
    {
        public string Name => "Ritual";
        public string Status { get; private set; } = "";
        public Vector2? AnchorGridPos { get; private set; }
        public bool IsEncounterActive => _phase == RitualPhase.Fighting;
        public bool IsComplete => _phase is RitualPhase.Complete
                                       or RitualPhase.Abandoned
                                       or RitualPhase.Failed;
        public bool IsRepeatable => true;

        // ── Phase machine ──
        private RitualPhase _phase = RitualPhase.Idle;
        private DateTime _phaseStartTime = DateTime.Now;

        // ── Entity references ──
        private Entity? _altar;
        private uint _altarId;
        private Vector2 _altarGridPos;

        // ── Encounter state ──
        private string _altarType = "";
        private string _altarMods = "";
        private int _completedCount;
        private int _totalDetected;
        private readonly HashSet<Vector2> _knownAltarPositions = new();

        // ── Click state ──
        private DateTime _lastClickTime = DateTime.MinValue;
        private int _clickAttempts;
        private const float ClickCooldownMs = 500;
        private const float BasePhaseTimeoutSeconds = 30;

        // ── Combat tracking ──
        private DateTime _lastCombatTime;

        // ── Combat profile (saved/restored around encounter) ──
        private CombatProfile? _savedCombatProfile;
        private const float RitualLeashRadius = 40f; // grid units — ritual circle radius

        // ── Loot tracking ──
        private DateTime _lootStartTime;
        private DateTime _lastLootScan = DateTime.MinValue;
        private uint _pendingLootId;
        private string? _pendingLootName;
        private double _pendingLootValue;

        // ── Overlay state ──
        public RitualPhase Phase => _phase;
        public string AltarType => _altarType;
        public int CompletedCount => _completedCount;

        // ══════════════════════════════════════════════════════════════
        // IMapMechanic
        // ══════════════════════════════════════════════════════════════

        public bool Detect(BotContext ctx)
        {
            if (_phase != RitualPhase.Idle) return true;

            var gc = ctx.Game;
            var playerGrid = new Vector2(gc.Player.GridPosNum.X, gc.Player.GridPosNum.Y);

            foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
            {
                if (entity.Path == null) continue;
                if (!entity.Path.Contains("Ritual/RitualRuneInteractable")) continue;

                var entityGrid = new Vector2(entity.GridPosNum.X, entity.GridPosNum.Y);
                var dist = Vector2.Distance(playerGrid, entityGrid);
                if (dist > Pathfinding.NetworkBubbleRadius) continue;

                // Track all known altar positions for counting
                _knownAltarPositions.Add(new Vector2((int)entityGrid.X, (int)entityGrid.Y));

                // Check StateMachine — only interact with fresh altars (state=1)
                int currentState = GetAltarState(entity);
                if (currentState == 3)
                {
                    // Already completed — count it
                    continue;
                }
                if (currentState == 2)
                {
                    // Active — shouldn't happen normally, but skip
                    continue;
                }
                if (currentState != 1) continue;

                // Fresh altar — check targetable
                if (!entity.IsTargetable) continue;

                _altar = entity;
                _altarId = entity.Id;
                _altarGridPos = entityGrid;
                AnchorGridPos = _altarGridPos;

                // Read altar info from label
                ReadAltarLabel(gc, entity);

                ctx.Log($"[Ritual] Detected fresh altar at ({_altarGridPos.X:F0}, {_altarGridPos.Y:F0}) — {_altarType}");
                return true;
            }

            return false;
        }

        public MechanicResult Tick(BotContext ctx)
        {
            RefreshAltar(ctx);
            UpdateAltarCounts(ctx);

            // Phase timeout
            if (_phase != RitualPhase.Idle && _phase != RitualPhase.Complete &&
                _phase != RitualPhase.Abandoned && _phase != RitualPhase.Failed)
            {
                var phaseTimeout = BasePhaseTimeoutSeconds + ctx.Settings.ExtraLatencyMs.Value / 1000f;
                if ((DateTime.Now - _phaseStartTime).TotalSeconds > phaseTimeout)
                {
                    ctx.Log($"[Ritual] Phase {_phase} timed out after {phaseTimeout}s");
                    Status = $"Timed out in {_phase}";
                    RestoreRitualCombatProfile(ctx);
                    _phase = RitualPhase.Failed;
                    return MechanicResult.Failed;
                }
            }

            return _phase switch
            {
                RitualPhase.Idle => TickIdle(ctx),
                RitualPhase.NavigateToAltar => TickNavigateToAltar(ctx),
                RitualPhase.ClickAltar => TickClickAltar(ctx),
                RitualPhase.WaitForActivation => TickWaitForActivation(ctx),
                RitualPhase.Fighting => TickFighting(ctx),
                RitualPhase.Looting => TickLooting(ctx),
                RitualPhase.Complete or RitualPhase.Abandoned or RitualPhase.Failed
                    => _phase == RitualPhase.Complete ? MechanicResult.Complete
                     : _phase == RitualPhase.Abandoned ? MechanicResult.Abandoned
                     : MechanicResult.Failed,
                _ => MechanicResult.Idle,
            };
        }

        public void Render(BotContext ctx)
        {
            var g = ctx.Graphics;
            var gc = ctx.Game;
            if (g == null || gc?.Player == null || !gc.InGame) return;

            // ═══ HUD Panel ═══
            if (_phase != RitualPhase.Idle)
                RenderMechanicHud(ctx, g);

            // Shop overlay is rendered globally from BotCore.Render() so it works in any mode
        }

        public void Reset()
        {
            _phase = RitualPhase.Idle;
            _altar = null;
            _altarId = 0;
            AnchorGridPos = null;
            Status = "";
            _altarType = "";
            _altarMods = "";
            _clickAttempts = 0;
            _pendingLootId = 0;
            _pendingLootName = null;
            // Ensure combat profile is always restored on reset (covers abandon/fail/area change)
            _savedCombatProfile = null;
        }

        // ══════════════════════════════════════════════════════════════
        // Phase handlers
        // ══════════════════════════════════════════════════════════════

        private MechanicResult TickIdle(BotContext ctx)
        {
            if (_altar == null) return MechanicResult.Idle;

            SetPhase(RitualPhase.NavigateToAltar, "Navigating to ritual altar");
            return MechanicResult.InProgress;
        }

        private MechanicResult TickNavigateToAltar(BotContext ctx)
        {
            var gc = ctx.Game;
            var playerGrid = new Vector2(gc.Player.GridPosNum.X, gc.Player.GridPosNum.Y);
            var dist = Vector2.Distance(playerGrid, _altarGridPos);

            if (dist < 25)
            {
                SetPhase(RitualPhase.ClickAltar, "In range, clicking altar");
                return MechanicResult.InProgress;
            }

            ctx.Navigation.NavigateTo(gc, _altarGridPos);
            Status = $"[Nav] Walking to altar ({dist:F0}g away)";
            return MechanicResult.InProgress;
        }

        private MechanicResult TickClickAltar(BotContext ctx)
        {
            var gc = ctx.Game;

            if (_altar == null)
            {
                _phase = RitualPhase.Failed;
                Status = "Failed: altar entity lost";
                return MechanicResult.Failed;
            }

            // Check if already activated (state transitioned to 2 or 3)
            int state = GetAltarState(_altar);
            if (state == 2)
            {
                _lastCombatTime = DateTime.Now;
                ApplyRitualCombatProfile(ctx);
                SetPhase(RitualPhase.Fighting, "Altar already active — fighting!");
                ctx.Log("[Ritual] Altar state=2 during click phase, jumping to fight");
                return MechanicResult.InProgress;
            }
            if (state == 3)
            {
                SetPhase(RitualPhase.Complete, "Altar already complete");
                return MechanicResult.Complete;
            }

            // If altar is state 1 (fresh) but not targetable, we need to get closer
            if (!_altar.IsTargetable)
            {
                var gc2 = ctx.Game;
                var playerGrid2 = new Vector2(gc2.Player.GridPosNum.X, gc2.Player.GridPosNum.Y);
                var distToAltar = Vector2.Distance(playerGrid2, _altarGridPos);
                if (distToAltar > 15f)
                {
                    // Too far — navigate closer
                    ctx.Navigation.MoveToward(gc2, _altarGridPos);
                    Status = $"Moving closer to altar ({distToAltar:F0}g)";
                    return MechanicResult.InProgress;
                }
                // Close but not targetable — wait briefly, might be loading
                Status = "Altar not targetable — waiting";
                return MechanicResult.InProgress;
            }

            if (!CanClick()) return MechanicResult.InProgress;

            if (_clickAttempts >= 10)
            {
                ctx.Log("[Ritual] Too many click attempts on altar");
                _phase = RitualPhase.Failed;
                Status = "Failed: too many click attempts";
                return MechanicResult.Failed;
            }

            // Click the altar entity
            if (BotInput.ClickEntity(gc, _altar))
            {
                _clickAttempts++;
                _lastClickTime = DateTime.Now;
                Status = $"Clicking altar (attempt {_clickAttempts})";
                ctx.Log($"[Ritual] Clicking altar to start (attempt {_clickAttempts})");
            }

            return MechanicResult.InProgress;
        }

        private MechanicResult TickWaitForActivation(BotContext ctx)
        {
            // Check if altar transitioned to state 2 (active)
            if (_altar != null)
            {
                int state = GetAltarState(_altar);
                if (state == 2)
                {
                    _lastCombatTime = DateTime.Now;
                    ApplyRitualCombatProfile(ctx);
                    SetPhase(RitualPhase.Fighting, "Ritual active — fighting!");
                    ctx.Log("[Ritual] Encounter activated, fighting");
                    return MechanicResult.InProgress;
                }
            }

            // Also detect via combat starting
            if (ctx.Combat.InCombat)
            {
                _lastCombatTime = DateTime.Now;
                ApplyRitualCombatProfile(ctx);
                SetPhase(RitualPhase.Fighting, "Combat detected — fighting!");
                ctx.Log("[Ritual] Combat started");
                return MechanicResult.InProgress;
            }

            Status = "Waiting for ritual activation...";
            return MechanicResult.InProgress;
        }

        private MechanicResult TickFighting(BotContext ctx)
        {
            var gc = ctx.Game;

            // Combat fires skills — positioning is allowed (not suppressed)
            ctx.Combat.SuppressPositioning = false;
            ctx.Combat.Tick(ctx);

            // Scan loot periodically during combat — items drop from kills mid-encounter.
            // Pick up items opportunistically between waves instead of waiting for loot phase.
            // CRITICAL: Only loot items INSIDE the ritual circle. Items outside the blocker
            // wall will cause the bot to path out of the encounter, getting stuck against the wall.
            var playerGrid = new Vector2(gc.Player.GridPosNum.X, gc.Player.GridPosNum.Y);

            if ((DateTime.Now - _lastLootScan).TotalMilliseconds >= 500)
            {
                ctx.Loot.Scan(gc);
                _lastLootScan = DateTime.Now;
            }

            // Tick pending loot pickup
            if (ctx.Interaction.IsBusy)
            {
                var result = ctx.Interaction.Tick(gc);
                if (result == InteractionResult.Succeeded && _pendingLootName != null)
                {
                    ctx.LootTracker.RecordItem(_pendingLootName, _pendingLootValue, _pendingLootId);
                    if (_pendingLootId > 0)
                        ctx.Loot.MarkFailed(_pendingLootId, "picked up");
                    ctx.Log($"[Ritual] Mid-fight pickup: {_pendingLootName} ({_pendingLootValue:F0}c)");
                    _pendingLootName = null;
                }
                else if (result == InteractionResult.Failed && _pendingLootId > 0)
                {
                    ctx.Loot.MarkFailed(_pendingLootId);
                    _pendingLootName = null;
                }
            }

            // Pick up nearby loot when not busy — only items within the ritual circle
            if (!ctx.Interaction.IsBusy && ctx.Loot.HasLootNearby)
            {
                // Filter candidates to items within leash radius of the altar
                LootCandidate? bestCandidate = null;
                foreach (var candidate in ctx.Loot.Candidates)
                {
                    var itemDist = Vector2.Distance(candidate.Entity.GridPosNum, _altarGridPos);
                    if (itemDist > RitualLeashRadius) continue; // Outside ritual circle — skip
                    bestCandidate = candidate;
                    break; // Candidates are pre-sorted by priority
                }

                if (bestCandidate != null)
                {
                    ctx.Interaction.PickupGroundItem(bestCandidate.Entity, ctx.Navigation,
                        requireProximity: bestCandidate.Entity.DistancePlayer > ctx.Interaction.InteractRadius);
                    if (ctx.Interaction.IsBusy)
                    {
                        _pendingLootId = bestCandidate.Entity.Id;
                        _pendingLootName = bestCandidate.ItemName;
                        _pendingLootValue = bestCandidate.ChaosValue;
                    }
                }
            }

            if (ctx.Combat.InCombat)
            {
                _lastCombatTime = DateTime.Now;

                // Actively navigate into the densest cluster of ritual monsters.
                // Without this, the bot stands at the altar and only damages monsters
                // that happen to walk into its range. For RF/melee this means nothing dies.
                if (!ctx.Interaction.IsBusy) // Don't override loot pickup navigation
                {
                    var clusterCenter = ctx.Combat.DenseClusterCenter;
                    var distToCluster = Vector2.Distance(playerGrid, clusterCenter);

                    if (distToCluster > 8f)
                    {
                        // Clamp target to leash radius around altar
                        var target = clusterCenter;
                        var distFromAltar = Vector2.Distance(target, _altarGridPos);
                        if (distFromAltar > RitualLeashRadius)
                        {
                            var dir = Vector2.Normalize(target - _altarGridPos);
                            target = _altarGridPos + dir * RitualLeashRadius;
                        }

                        ctx.Navigation.MoveToward(gc, target);
                    }
                }

                Status = $"[Combat] Fighting ritual monsters ({ctx.Combat.NearbyMonsterCount} nearby)";

                // Reset phase timeout while actively fighting
                _phaseStartTime = DateTime.Now;
                return MechanicResult.InProgress;
            }

            // Check if altar transitioned to state 3 (complete)
            if (_altar != null)
            {
                int state = GetAltarState(_altar);
                if (state == 3)
                {
                    _completedCount++;
                    _lootStartTime = DateTime.Now;
                    _lastLootScan = DateTime.MinValue;
                    RestoreRitualCombatProfile(ctx);
                    SetPhase(RitualPhase.Looting, "Ritual complete, looting");
                    ctx.Log($"[Ritual] Encounter complete ({_completedCount} done)");
                    return MechanicResult.InProgress;
                }
            }

            // Wait 2 seconds after combat ends to confirm
            var timeSinceCombat = (DateTime.Now - _lastCombatTime).TotalSeconds;
            if (timeSinceCombat < 2.0)
            {
                Status = $"[Combat] Waiting for clear ({timeSinceCombat:F1}s)";
                return MechanicResult.InProgress;
            }

            // If no combat and altar still at state 2, keep waiting
            Status = "Waiting for ritual to end...";
            return MechanicResult.InProgress;
        }

        private MechanicResult TickLooting(BotContext ctx)
        {
            var gc = ctx.Game;
            var settings = ctx.Settings.Mechanics.Ritual;
            var elapsed = (DateTime.Now - _lootStartTime).TotalSeconds;

            // Run combat in case stragglers appear
            ctx.Combat.Tick(ctx);

            // Scan for loot periodically
            if ((DateTime.Now - _lastLootScan).TotalMilliseconds >= 500)
            {
                ctx.Loot.Scan(gc);
                _lastLootScan = DateTime.Now;
            }

            // Tick interaction system for pickup results
            if (ctx.Interaction.IsBusy)
            {
                var result = ctx.Interaction.Tick(gc);
                if (result == InteractionResult.Succeeded && _pendingLootName != null)
                {
                    ctx.LootTracker.RecordItem(_pendingLootName, _pendingLootValue, _pendingLootId);
                    if (_pendingLootId > 0)
                        ctx.Loot.MarkFailed(_pendingLootId, "picked up");
                    ctx.Log($"[Ritual] Picked up: {_pendingLootName} ({_pendingLootValue:F0}c)");
                    _pendingLootName = null;
                }
                else if (result == InteractionResult.Failed && _pendingLootId > 0)
                {
                    ctx.Loot.MarkFailed(_pendingLootId);
                    _pendingLootName = null;
                }
                Status = $"Looting ({elapsed:F1}s)";
                return MechanicResult.InProgress;
            }

            // Pick up nearby items
            if (ctx.Loot.HasLootNearby)
            {
                var (wasInRadius, candidate) = ctx.Loot.PickupNext(ctx.Interaction, ctx.Navigation);
                if (candidate != null && ctx.Interaction.IsBusy)
                {
                    _pendingLootId = candidate.Entity.Id;
                    _pendingLootName = candidate.ItemName;
                    _pendingLootValue = candidate.ChaosValue;
                    Status = $"Picking up: {candidate.ItemName}";
                    _lootStartTime = DateTime.Now; // Reset timer — still looting
                    return MechanicResult.InProgress;
                }
            }

            // Done looting after timeout — but ONLY if there's nothing left to grab.
            // Ritual rewards can spawn with a 1-2s delay after encounter completion.
            // If items are still on the ground, extend the sweep.
            var totalLootTime = (DateTime.Now - _phaseStartTime).TotalSeconds;
            if (elapsed >= settings.LootSweepSeconds.Value)
            {
                // Re-scan one final time before giving up
                ctx.Loot.Scan(gc);

                if (ctx.Loot.HasLootNearby && totalLootTime < 15.0)
                {
                    // Still items nearby — keep looting, reset idle timer
                    _lootStartTime = DateTime.Now;
                    Status = $"Looting: items still on ground ({totalLootTime:F1}s total)";
                    return MechanicResult.InProgress;
                }

                SetPhase(RitualPhase.Complete, ctx.Loot.HasLootNearby
                    ? "Loot sweep hard cap (15s)" : "Loot sweep done");
                ctx.Log($"[Ritual] Loot sweep complete ({totalLootTime:F1}s total)");
                return MechanicResult.Complete;
            }

            Status = $"Looting ({elapsed:F1}s / {settings.LootSweepSeconds.Value:F0}s)";
            return MechanicResult.InProgress;
        }

        // ══════════════════════════════════════════════════════════════
        // Entity management
        // ══════════════════════════════════════════════════════════════

        private void RefreshAltar(BotContext ctx)
        {
            if (_altarId == 0) return;

            var gc = ctx.Game;
            foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
            {
                if (entity.Id == _altarId)
                {
                    _altar = entity;
                    return;
                }
            }

            // Entity ID may have changed (left/re-entered bubble) — find by position
            foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
            {
                if (entity.Path == null || !entity.Path.Contains("Ritual/RitualRuneInteractable"))
                    continue;
                var entityGrid = new Vector2(entity.GridPosNum.X, entity.GridPosNum.Y);
                if (Vector2.Distance(entityGrid, _altarGridPos) < 5)
                {
                    _altar = entity;
                    _altarId = entity.Id;
                    return;
                }
            }
        }

        private static int GetAltarState(Entity entity)
        {
            try
            {
                var sm = entity.GetComponent<StateMachine>();
                if (sm == null) return -1;
                foreach (var state in sm.States)
                {
                    if (state.Name == "current_state")
                        return (int)state.Value;
                }
            }
            catch { }
            return -1;
        }

        private void UpdateAltarCounts(BotContext ctx)
        {
            var gc = ctx.Game;
            int completed = 0;
            foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
            {
                if (entity.Path == null || !entity.Path.Contains("Ritual/RitualRuneInteractable"))
                    continue;

                var entityGrid = new Vector2(entity.GridPosNum.X, entity.GridPosNum.Y);
                _knownAltarPositions.Add(new Vector2((int)entityGrid.X, (int)entityGrid.Y));

                int state = GetAltarState(entity);
                if (state == 3) completed++;
            }

            _totalDetected = _knownAltarPositions.Count;
            if (completed > _completedCount)
                _completedCount = completed;
        }

        // ══════════════════════════════════════════════════════════════
        // Label parsing
        // ══════════════════════════════════════════════════════════════

        private void ReadAltarLabel(GameController gc, Entity altar)
        {
            _altarType = "";
            _altarMods = "";

            foreach (var label in gc.IngameState.IngameUi.ItemsOnGroundLabelsVisible)
            {
                if (label.ItemOnGround?.Id != altar.Id) continue;
                var lbl = label.Label;
                if (lbl == null || !lbl.IsVisible || lbl.ChildCount < 3) continue;

                // label[1][3] = altar type name
                var child1 = lbl.GetChildAtIndex(1);
                if (child1 != null && child1.ChildCount >= 4)
                {
                    var typeEl = child1.GetChildAtIndex(3);
                    if (typeEl != null && !string.IsNullOrEmpty(typeEl.Text))
                        _altarType = typeEl.Text;
                }

                // label[2][0] = modifier description
                var child2 = lbl.GetChildAtIndex(2);
                if (child2 != null && child2.ChildCount >= 1)
                {
                    var modEl = child2.GetChildAtIndex(0);
                    if (modEl != null && !string.IsNullOrEmpty(modEl.Text))
                        _altarMods = modEl.Text;
                }
                break;
            }
        }

        // ══════════════════════════════════════════════════════════════
        // Rendering
        // ══════════════════════════════════════════════════════════════

        private void RenderMechanicHud(BotContext ctx, ExileCore.Graphics g)
        {
            var hudX = 20f;
            var hudY = 550f;
            var lineH = 18f;

            var titleColor = _phase switch
            {
                RitualPhase.Fighting => SharpDX.Color.Red,
                RitualPhase.ClickAltar or RitualPhase.WaitForActivation => SharpDX.Color.Yellow,
                RitualPhase.NavigateToAltar => SharpDX.Color.Cyan,
                RitualPhase.Complete => SharpDX.Color.LimeGreen,
                RitualPhase.Failed => SharpDX.Color.Red,
                _ => SharpDX.Color.Cyan,
            };

            g.DrawText("=== RITUAL (BETA) ===", new Vector2(hudX, hudY), titleColor);
            hudY += lineH;

            g.DrawText($"Phase: {_phase}", new Vector2(hudX, hudY), titleColor);
            hudY += lineH;

            if (!string.IsNullOrEmpty(_altarType))
            {
                g.DrawText($"Altar: {_altarType}", new Vector2(hudX, hudY), SharpDX.Color.White);
                hudY += lineH;
            }

            g.DrawText($"Progress: {_completedCount}/{_totalDetected} altars",
                new Vector2(hudX, hudY), SharpDX.Color.White);
            hudY += lineH;

            g.DrawText(Status, new Vector2(hudX, hudY), SharpDX.Color.Gray);
        }

        /// <summary>
        /// Render ritual shop overlay with Ninja prices. Called from BotCore.Render()
        /// so it works regardless of active mode/mechanic.
        /// </summary>
        public static void RenderShopOverlay(BotContext ctx, ExileCore.Graphics g, GameController gc)
        {
            Element? ritualWindow = null;
            try
            {
                ritualWindow = gc.IngameState.IngameUi.RitualWindow;
            }
            catch { return; }

            if (ritualWindow == null || !ritualWindow.IsVisible) return;

            // Get tribute remaining text
            string tributeText = "";
            try
            {
                tributeText = ritualWindow.GetChildAtIndex(7)?.GetChildAtIndex(0)?.Text ?? "";
            }
            catch { }

            // Get reroll count
            string rerollText = "";
            try
            {
                rerollText = ritualWindow.GetChildAtIndex(12)?.GetChildAtIndex(0)?.Text ?? "";
            }
            catch { }

            // Get items via typed RitualWindow.Items
            List<(string Name, string Rarity, double NinjaValue, string Cost)> itemData = new();
            try
            {
                var rw = gc.IngameState.IngameUi.RitualWindow;
                var items = rw.Items;
                Func<Entity, double>? ninjaMethod = null;
                try
                {
                    ninjaMethod = gc.PluginBridge.GetMethod<Func<Entity, double>>("NinjaPrice.GetValue");
                }
                catch { }

                foreach (var nii in items)
                {
                    var item = nii.Item;
                    if (item == null) continue;

                    var path = item.Path ?? "?";
                    var shortPath = path.Contains("/") ? path.Substring(path.LastIndexOf("/") + 1) : path;
                    var mods = item.GetComponent<Mods>();
                    var rarity = mods?.ItemRarity.ToString() ?? "?";
                    var name = mods?.UniqueName;
                    if (string.IsNullOrEmpty(name)) name = shortPath;

                    // Stack count
                    if (nii.ChildCount > 0)
                    {
                        var stackText = nii.GetChildAtIndex(0)?.Text;
                        if (!string.IsNullOrEmpty(stackText))
                            name = $"{name} x{stackText}";
                    }

                    double chaosVal = 0;
                    if (ninjaMethod != null)
                        try { chaosVal = ninjaMethod(item); } catch { }

                    // Read cost from NII element tree (visible without hover)
                    // then fall back to tooltip (requires hover)
                    string costStr = FindCostInElementTree(nii) ?? FindCostInTooltip(nii) ?? "?";

                    itemData.Add((name, rarity, chaosVal, costStr));
                }
            }
            catch { }

            if (itemData.Count == 0) return;

            // ═══ Draw overlay panel ═══
            var panelX = 970f;
            var panelY = 42f;
            var lineH = 16f;
            var panelWidth = 380f;
            var panelHeight = (itemData.Count + 4) * lineH + 10;

            // Background
            g.DrawBox(new RectangleF(panelX, panelY, panelWidth, panelHeight),
                new SharpDX.Color(20, 20, 20, 200));

            var textX = panelX + 6;
            var y = panelY + 4;

            g.DrawText("=== RITUAL SHOP (BETA) ===", new Vector2(textX, y), SharpDX.Color.Cyan);
            y += lineH;
            g.DrawText($"{tributeText}  |  Rerolls: {rerollText}", new Vector2(textX, y), SharpDX.Color.Gold);
            y += lineH;
            g.DrawText("Item                          Ninja    Cost", new Vector2(textX, y), SharpDX.Color.Gray);
            y += lineH;

            foreach (var (name, rarity, ninja, cost) in itemData)
            {
                var nameColor = rarity switch
                {
                    "Unique" => new SharpDX.Color(175, 96, 37),
                    "Rare" => new SharpDX.Color(255, 255, 119),
                    "Magic" => new SharpDX.Color(136, 136, 255),
                    _ => SharpDX.Color.White,
                };

                // Truncate name for display
                var displayName = name.Length > 28 ? name.Substring(0, 25) + "..." : name;
                var ninjaStr = ninja > 0 ? $"{ninja:F1}c" : "-";
                var line = $"{displayName,-28} {ninjaStr,7}  {cost,8}";

                g.DrawText(line, new Vector2(textX, y), nameColor);
                y += lineH;
            }

        }

        // ══════════════════════════════════════════════════════════════
        // Helpers
        // ══════════════════════════════════════════════════════════════

        private void SetPhase(RitualPhase phase, string status)
        {
            _phase = phase;
            _phaseStartTime = DateTime.Now;
            Status = status;
        }

        /// <summary>
        /// Save the current combat profile and apply a leashed profile for the ritual circle.
        /// Constrains combat positioning to stay within the ritual area around the altar.
        /// </summary>
        private void ApplyRitualCombatProfile(BotContext ctx)
        {
            if (_savedCombatProfile != null) return; // Already saved

            _savedCombatProfile = new CombatProfile
            {
                Enabled = ctx.Combat.Profile.Enabled,
                Positioning = ctx.Combat.Profile.Positioning,
                LeashAnchor = ctx.Combat.Profile.LeashAnchor,
                LeashRadius = ctx.Combat.Profile.LeashRadius,
            };

            ctx.Combat.SetProfile(new CombatProfile
            {
                Enabled = true,
                Positioning = ctx.Combat.Profile.Positioning,
                LeashAnchor = _altarGridPos,
                LeashRadius = RitualLeashRadius,
            });
        }

        /// <summary>
        /// Restore the combat profile saved before the ritual encounter.
        /// </summary>
        private void RestoreRitualCombatProfile(BotContext ctx)
        {
            if (_savedCombatProfile != null)
            {
                ctx.Combat.SetProfile(_savedCombatProfile);
                _savedCombatProfile = null;
            }
        }

        private bool CanClick()
        {
            if (!BotInput.CanAct) return false;
            return (DateTime.Now - _lastClickTime).TotalMilliseconds >= ClickCooldownMs;
        }

        /// <summary>
        /// Search the NII element's own children (not tooltip) for cost text.
        /// The cost is displayed visually on each item card — it's somewhere in the tree.
        /// </summary>
        private static string? FindCostInElementTree(Element nii)
        {
            try
            {
                // Walk the NII's parent container — the grid cell may have sibling/child
                // elements with cost text. Search breadth-first up to 3 levels deep.
                return ScanForCostText(nii, 0, 4);
            }
            catch { return null; }
        }

        /// <summary>
        /// Recursively scan element children for text that looks like a tribute cost
        /// (digits, commas, optional "x" suffix — e.g., "1,425x" or "1425").
        /// </summary>
        private static string? ScanForCostText(Element? el, int depth, int maxDepth)
        {
            if (el == null || depth > maxDepth) return null;

            var text = el.Text;
            if (!string.IsNullOrEmpty(text))
            {
                // Cost format: digits/commas optionally followed by "x" (e.g., "1,425x", "500x")
                var trimmed = text.Trim();
                if (trimmed.Length > 0 && char.IsDigit(trimmed[0]) &&
                    (trimmed.EndsWith("x") || trimmed.EndsWith("x ")))
                {
                    return trimmed.TrimEnd(' ');
                }
            }

            for (int i = 0; i < (int)el.ChildCount && i < 20; i++)
            {
                var result = ScanForCostText(el.GetChildAtIndex(i), depth + 1, maxDepth);
                if (result != null) return result;
            }
            return null;
        }

        /// <summary>
        /// Fall back to reading cost from the tooltip (only works when hovered).
        /// </summary>
        private static string? FindCostInTooltip(Element nii)
        {
            try
            {
                var ttInner = nii.Tooltip?.GetChildAtIndex(0)?.GetChildAtIndex(1);
                if (ttInner == null) return null;

                for (int ci = (int)ttInner.ChildCount - 1; ci >= 0; ci--)
                {
                    var costChild = ttInner.GetChildAtIndex(ci);
                    var container = costChild?.GetChildAtIndex(0);
                    if (container != null && container.ChildCount >= 2)
                    {
                        var labelEl = container.GetChildAtIndex(0);
                        if (labelEl?.Text != null && labelEl.Text.StartsWith("Cost"))
                            return container.GetChildAtIndex(1)?.GetChildAtIndex(0)?.Text;
                    }
                }
            }
            catch { }
            return null;
        }

    }
}
