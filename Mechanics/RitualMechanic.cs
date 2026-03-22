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
        private const float PhaseTimeoutSeconds = 30;

        // ── Combat tracking ──
        private DateTime _lastCombatTime;

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
                if ((DateTime.Now - _phaseStartTime).TotalSeconds > PhaseTimeoutSeconds)
                {
                    ctx.Log($"[Ritual] Phase {_phase} timed out after {PhaseTimeoutSeconds}s");
                    Status = $"Timed out in {_phase}";
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

            // Check if already activated (state transitioned to 2)
            int state = GetAltarState(_altar);
            if (state == 2)
            {
                _lastCombatTime = DateTime.Now;
                SetPhase(RitualPhase.WaitForActivation, "Altar activated");
                return MechanicResult.InProgress;
            }

            if (!_altar.IsTargetable)
            {
                SetPhase(RitualPhase.WaitForActivation, "Altar no longer targetable");
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
            var cam = gc.IngameState.Camera;
            var screenPos = cam.WorldToScreen(_altar.BoundsCenterPosNum);
            var windowRect = gc.Window.GetWindowRectangleTimeCache;

            if (screenPos.X > 0 && screenPos.X < windowRect.Width &&
                screenPos.Y > 0 && screenPos.Y < windowRect.Height)
            {
                var absPos = new Vector2(windowRect.X + screenPos.X, windowRect.Y + screenPos.Y);
                BotInput.Click(absPos);
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
                    SetPhase(RitualPhase.Fighting, "Ritual active — fighting!");
                    ctx.Log("[Ritual] Encounter activated, fighting");
                    return MechanicResult.InProgress;
                }
            }

            // Also detect via combat starting
            if (ctx.Combat.InCombat)
            {
                _lastCombatTime = DateTime.Now;
                SetPhase(RitualPhase.Fighting, "Combat detected — fighting!");
                ctx.Log("[Ritual] Combat started");
                return MechanicResult.InProgress;
            }

            Status = "Waiting for ritual activation...";
            return MechanicResult.InProgress;
        }

        private MechanicResult TickFighting(BotContext ctx)
        {
            ctx.Combat.Tick(ctx);

            if (ctx.Combat.InCombat)
            {
                _lastCombatTime = DateTime.Now;
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
                    _lootStartTime = DateTime.Now;
                    return MechanicResult.InProgress;
                }
            }

            // Done looting after timeout
            if (elapsed >= settings.LootSweepSeconds.Value)
            {
                SetPhase(RitualPhase.Complete, "Loot sweep done");
                ctx.Log("[Ritual] Loot sweep complete, encounter done");
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
