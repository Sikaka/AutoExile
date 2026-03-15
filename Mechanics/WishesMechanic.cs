using System.Numerics;
using ExileCore;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using AutoExile.Systems;
using Pathfinding = AutoExile.Systems.Pathfinding;

namespace AutoExile.Mechanics
{
    public enum WishesPhase
    {
        Idle,
        NavigateToEncounter,
        WaitForCombatClear,
        NavigateToNPC,
        TalkToNPC,
        SelectWish,
        ConfirmWish,
        WaitForPortal,
        NavigateToPortal,
        EnterPortal,
        Complete,
    }

    public class WishesMechanic : IMapMechanic
    {
        public string Name => "Wishes";
        public string Status { get; private set; } = "";
        public Vector2? AnchorGridPos { get; private set; }
        public bool IsEncounterActive => _phase is WishesPhase.WaitForCombatClear;
        public bool IsComplete => _phase == WishesPhase.Complete;

        private WishesPhase _phase = WishesPhase.Idle;
        private DateTime _phaseStartTime = DateTime.Now;

        // Entity references
        private Entity? _initiator;     // FaridunInitiatorTEMP — encounter anchor
        private Entity? _npc;           // Varashta NPC
        private Entity? _portal;        // DjinnPortal
        private uint _initiatorId;
        private uint _npcId;
        private uint _portalId;

        // Positions (grid)
        private Vector2 _initiatorGridPos;
        private Vector2 _npcGridPos;
        private Vector2 _portalGridPos;

        // UI interaction state
        private int _wishClickAttempts;
        private int _confirmClickAttempts;
        private DateTime _lastClickTime = DateTime.MinValue;
        private const float ClickCooldownMs = 500;
        private const float PhaseTimeoutSeconds = 30;

        // Wishes panel — discovered dynamically, cached until reset
        private int _wishesPanelIndex = -1;

        public bool Detect(BotContext ctx)
        {
            if (_phase != WishesPhase.Idle) return true;

            var gc = ctx.Game;

            // Don't detect in wish zones (mirage maps).
            // SekhemaPortal = return portal, only exists in wish zones.
            foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
            {
                if (entity.Path != null && entity.Path.Contains("SekhemaPortal") && entity.IsTargetable)
                    return false;
            }

            // Look for original map encounter anchor
            foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
            {
                if (entity.Path == null) continue;
                if (!entity.Path.Contains("Faridun/FaridunInitiator")) continue;

                var dist = Vector2.Distance(
                    new Vector2(gc.Player.GridPosNum.X, gc.Player.GridPosNum.Y),
                    new Vector2(entity.GridPosNum.X, entity.GridPosNum.Y));

                if (dist > Pathfinding.NetworkBubbleRadius) continue;

                _initiator = entity;
                _initiatorId = entity.Id;
                _initiatorGridPos = new Vector2(entity.GridPosNum.X, entity.GridPosNum.Y);
                AnchorGridPos = _initiatorGridPos;

                ScanFaridunEntities(ctx);
                return true;
            }

            return false;
        }

        public MechanicResult Tick(BotContext ctx)
        {
            RefreshEntities(ctx);

            if (_phase != WishesPhase.Idle && _phase != WishesPhase.Complete)
            {
                if ((DateTime.Now - _phaseStartTime).TotalSeconds > PhaseTimeoutSeconds)
                {
                    ctx.Log($"[Wishes] Phase {_phase} timed out after {PhaseTimeoutSeconds}s");
                    Status = $"Timed out in {_phase}";
                    _phase = WishesPhase.Complete;
                    return MechanicResult.Failed;
                }
            }

            return _phase switch
            {
                WishesPhase.Idle => TickIdle(ctx),
                WishesPhase.NavigateToEncounter => TickNavigateToEncounter(ctx),
                WishesPhase.WaitForCombatClear => TickWaitForCombatClear(ctx),
                WishesPhase.NavigateToNPC => TickNavigateToNPC(ctx),
                WishesPhase.TalkToNPC => TickTalkToNPC(ctx),
                WishesPhase.SelectWish => TickSelectWish(ctx),
                WishesPhase.ConfirmWish => TickConfirmWish(ctx),
                WishesPhase.WaitForPortal => TickWaitForPortal(ctx),
                WishesPhase.NavigateToPortal => TickNavigateToPortal(ctx),
                WishesPhase.EnterPortal => TickEnterPortal(ctx),
                WishesPhase.Complete => MechanicResult.Complete,
                _ => MechanicResult.Idle,
            };
        }

        public void Reset()
        {
            _phase = WishesPhase.Idle;
            _initiator = null;
            _npc = null;
            _portal = null;
            _initiatorId = 0;
            _npcId = 0;
            _portalId = 0;
            AnchorGridPos = null;
            Status = "";
            _wishClickAttempts = 0;
            _confirmClickAttempts = 0;
            _wishesPanelIndex = -1;
        }

        // ═══════════════════════════════════════════════════
        // Phase handlers
        // ═══════════════════════════════════════════════════

        private MechanicResult TickIdle(BotContext ctx)
        {
            if (_initiator == null) return MechanicResult.Idle;

            if (_npc != null && _npc.IsTargetable)
            {
                SetPhase(WishesPhase.NavigateToNPC, "NPC available, going to talk");
                return MechanicResult.InProgress;
            }

            if (_portal != null && _portal.IsTargetable)
            {
                SetPhase(WishesPhase.NavigateToPortal, "Portal ready");
                return MechanicResult.InProgress;
            }

            SetPhase(WishesPhase.NavigateToEncounter, "Navigating to encounter area");
            return MechanicResult.InProgress;
        }

        private MechanicResult TickNavigateToEncounter(BotContext ctx)
        {
            var gc = ctx.Game;
            ctx.Combat.Tick(ctx);

            ScanFaridunEntities(ctx);
            if (_npc != null && _npc.IsTargetable)
            {
                SetPhase(WishesPhase.NavigateToNPC, "NPC available");
                return MechanicResult.InProgress;
            }

            var playerGrid = new Vector2(gc.Player.GridPosNum.X, gc.Player.GridPosNum.Y);
            var dist = Vector2.Distance(playerGrid, _initiatorGridPos);

            if (dist < 40)
            {
                SetPhase(WishesPhase.WaitForCombatClear, "In encounter area, fighting");
                return MechanicResult.InProgress;
            }

            var worldTarget = _initiatorGridPos * Pathfinding.GridToWorld;
            ctx.Navigation.NavigateTo(gc, worldTarget);
            Status = $"[Nav] Walking to encounter ({dist:F0}g away)";
            return MechanicResult.InProgress;
        }

        private MechanicResult TickWaitForCombatClear(BotContext ctx)
        {
            var gc = ctx.Game;
            ctx.Combat.Tick(ctx);

            ScanFaridunEntities(ctx);
            if (_npc != null && _npc.IsTargetable)
            {
                SetPhase(WishesPhase.NavigateToNPC, "NPC available, going to talk");
                return MechanicResult.InProgress;
            }

            var monsterCount = CountFaridunMonsters(ctx);
            if (monsterCount == 0)
            {
                var elapsed = (DateTime.Now - _phaseStartTime).TotalSeconds;
                Status = $"[Combat] Monsters dead, waiting for NPC ({elapsed:F0}s)";
                return MechanicResult.InProgress;
            }

            Status = $"[Combat] Fighting ({monsterCount} Faridun monsters)";
            return MechanicResult.InProgress;
        }

        private MechanicResult TickNavigateToNPC(BotContext ctx)
        {
            var gc = ctx.Game;
            ctx.Combat.Tick(ctx);

            if (ctx.Navigation.IsPaused)
                ctx.Navigation.Resume(gc);

            if (_npc == null || !_npc.IsTargetable)
            {
                ScanFaridunEntities(ctx);
                if (_npc == null)
                {
                    Status = "[NPC] Can't find Varashta";
                    return MechanicResult.InProgress;
                }
            }

            var playerGrid = new Vector2(gc.Player.GridPosNum.X, gc.Player.GridPosNum.Y);
            _npcGridPos = new Vector2(_npc.GridPosNum.X, _npc.GridPosNum.Y);
            var dist = Vector2.Distance(playerGrid, _npcGridPos);

            if (dist < 15)
            {
                SetPhase(WishesPhase.TalkToNPC, "Clicking NPC");
                return MechanicResult.InProgress;
            }

            var worldTarget = _npcGridPos * Pathfinding.GridToWorld;
            ctx.Navigation.NavigateTo(gc, worldTarget);
            Status = $"[Nav] Walking to Varashta ({dist:F0}g away)";
            return MechanicResult.InProgress;
        }

        private MechanicResult TickTalkToNPC(BotContext ctx)
        {
            var gc = ctx.Game;

            if (IsWishesPanelOpen(gc))
            {
                SetPhase(WishesPhase.SelectWish, "Wishes panel open");
                return MechanicResult.InProgress;
            }

            if (_npc == null || !_npc.IsTargetable)
            {
                Status = "[NPC] Varashta not targetable";
                return MechanicResult.InProgress;
            }

            // Ensure we're close enough — walk closer if needed (interact range ~8 grid)
            var playerGrid = new Vector2(gc.Player.GridPosNum.X, gc.Player.GridPosNum.Y);
            _npcGridPos = new Vector2(_npc.GridPosNum.X, _npc.GridPosNum.Y);
            var dist = Vector2.Distance(playerGrid, _npcGridPos);
            if (dist > 10)
            {
                var worldTarget = _npcGridPos * Pathfinding.GridToWorld;
                ctx.Navigation.NavigateTo(gc, worldTarget);
                Status = $"[NPC] Walking closer to Varashta ({dist:F0}g)";
                return MechanicResult.InProgress;
            }

            if (!CanClick()) return MechanicResult.InProgress;

            var screenPos = gc.IngameState.Camera.WorldToScreen(_npc.BoundsCenterPosNum);
            var windowRect = gc.Window.GetWindowRectangleTimeCache;
            var absPos = new Vector2(screenPos.X + windowRect.X, screenPos.Y + windowRect.Y);

            if (BotInput.Click(absPos))
            {
                _lastClickTime = DateTime.Now;
                Status = $"[NPC] Clicked Varashta (dist={dist:F0}), waiting for Wishes panel";
            }

            return MechanicResult.InProgress;
        }

        private MechanicResult TickSelectWish(BotContext ctx)
        {
            var gc = ctx.Game;

            if (!IsWishesPanelOpen(gc))
            {
                SetPhase(WishesPhase.TalkToNPC, "Wishes panel closed, retrying NPC");
                return MechanicResult.InProgress;
            }

            var panel = GetWishesPanel(gc);
            if (panel == null) return MechanicResult.InProgress;

            var wishesContainer = panel.GetChildAtIndex(4);
            if (wishesContainer == null) return MechanicResult.InProgress;

            var preferred = ctx.Settings.Mechanics.Wishes.PreferredWish.Value;
            int targetOption = FindBestWishOption(wishesContainer, preferred);

            var option = wishesContainer.GetChildAtIndex(targetOption);
            if (option == null || !option.IsVisible)
            {
                Status = $"[Wish] Option {targetOption} not visible";
                return MechanicResult.InProgress;
            }

            bool isSelected = false;
            if (option.ChildCount > 5)
            {
                var selectionIndicator = option.GetChildAtIndex(5);
                isSelected = selectionIndicator != null && selectionIndicator.IsVisible;
            }

            if (isSelected)
            {
                var wishName = GetWishName(option);
                SetPhase(WishesPhase.ConfirmWish, $"Selected '{wishName}', confirming");
                return MechanicResult.InProgress;
            }

            if (!CanClick()) return MechanicResult.InProgress;

            var rect = option.GetClientRect();
            var windowRect = gc.Window.GetWindowRectangleTimeCache;
            var center = new Vector2(
                rect.X + rect.Width / 2 + windowRect.X,
                rect.Y + rect.Height / 2 + windowRect.Y);

            if (BotInput.Click(center))
            {
                _lastClickTime = DateTime.Now;
                _wishClickAttempts++;
                var wishName = GetWishName(option);
                Status = $"[Wish] Clicking '{wishName}' (attempt {_wishClickAttempts})";
            }

            if (_wishClickAttempts > 5)
            {
                ctx.Log("[Wishes] Failed to select wish after 5 attempts");
                _phase = WishesPhase.Complete;
                return MechanicResult.Failed;
            }

            return MechanicResult.InProgress;
        }

        private MechanicResult TickConfirmWish(BotContext ctx)
        {
            var gc = ctx.Game;

            if (!IsWishesPanelOpen(gc))
            {
                SetPhase(WishesPhase.WaitForPortal, "Wish confirmed, waiting for portal");
                return MechanicResult.InProgress;
            }

            if (!CanClick()) return MechanicResult.InProgress;

            var panel = GetWishesPanel(gc);
            if (panel == null) return MechanicResult.InProgress;

            var wishesContainer = panel.GetChildAtIndex(4);
            if (wishesContainer == null) return MechanicResult.InProgress;

            var confirmBtn = wishesContainer.GetChildAtIndex(6);
            if (confirmBtn == null || !confirmBtn.IsVisible)
            {
                Status = "[Wish] Confirm button not visible";
                return MechanicResult.InProgress;
            }

            var rect = confirmBtn.GetClientRect();
            var windowRect = gc.Window.GetWindowRectangleTimeCache;
            var center = new Vector2(
                rect.X + rect.Width / 2 + windowRect.X,
                rect.Y + rect.Height / 2 + windowRect.Y);

            if (BotInput.Click(center))
            {
                _lastClickTime = DateTime.Now;
                _confirmClickAttempts++;
                Status = $"[Wish] Clicked confirm (attempt {_confirmClickAttempts})";
            }

            if (_confirmClickAttempts > 5)
            {
                ctx.Log("[Wishes] Failed to confirm wish after 5 attempts");
                _phase = WishesPhase.Complete;
                return MechanicResult.Failed;
            }

            return MechanicResult.InProgress;
        }

        private MechanicResult TickWaitForPortal(BotContext ctx)
        {
            ScanFaridunEntities(ctx);

            if (_portal != null && _portal.IsTargetable)
            {
                _portalGridPos = new Vector2(_portal.GridPosNum.X, _portal.GridPosNum.Y);
                SetPhase(WishesPhase.NavigateToPortal, "Portal ready");
                return MechanicResult.InProgress;
            }

            if (_portal != null)
            {
                var sm = _portal.GetComponent<StateMachine>();
                var ready = GetState(sm, "ready");
                Status = $"[Portal] Waiting for portal (ready={ready})";
            }
            else
            {
                Status = "[Portal] Waiting for portal entity";
            }

            return MechanicResult.InProgress;
        }

        private MechanicResult TickNavigateToPortal(BotContext ctx)
        {
            if (_portal == null || !_portal.IsTargetable)
            {
                ScanFaridunEntities(ctx);
                if (_portal == null || !_portal.IsTargetable)
                {
                    Status = "[Portal] Portal not ready";
                    return MechanicResult.InProgress;
                }
            }

            var gc = ctx.Game;
            var playerGrid = new Vector2(gc.Player.GridPosNum.X, gc.Player.GridPosNum.Y);
            _portalGridPos = new Vector2(_portal.GridPosNum.X, _portal.GridPosNum.Y);
            var dist = Vector2.Distance(playerGrid, _portalGridPos);

            if (dist < 15)
            {
                SetPhase(WishesPhase.EnterPortal, "Clicking portal");
                return MechanicResult.InProgress;
            }

            var worldTarget = _portalGridPos * Pathfinding.GridToWorld;
            ctx.Navigation.NavigateTo(gc, worldTarget);
            Status = $"[Nav] Walking to portal ({dist:F0}g away)";
            return MechanicResult.InProgress;
        }

        private MechanicResult TickEnterPortal(BotContext ctx)
        {
            var gc = ctx.Game;

            var stash = gc.IngameState.IngameUi.StashElement;
            var inv = gc.IngameState.IngameUi.InventoryPanel;
            if ((stash != null && stash.IsVisible) || (inv != null && inv.IsVisible))
            {
                if (CanClick())
                    BotInput.PressKey(Keys.Escape);
                return MechanicResult.InProgress;
            }

            if (_portal == null || !_portal.IsTargetable)
            {
                // Portal gone — area change handles the transition.
                // BotCore will reset mechanics on area change.
                // In the wish zone, Detect() won't re-fire (SekhemaPortal check).
                var elapsed = (DateTime.Now - _phaseStartTime).TotalSeconds;
                Status = $"[Enter] Portal gone, waiting for area change ({elapsed:F0}s)";
                return MechanicResult.InProgress;
            }

            if (!CanClick()) return MechanicResult.InProgress;

            var screenPos = gc.IngameState.Camera.WorldToScreen(_portal.BoundsCenterPosNum);
            var windowRect = gc.Window.GetWindowRectangleTimeCache;
            var absPos = new Vector2(screenPos.X + windowRect.X, screenPos.Y + windowRect.Y);

            if (BotInput.Click(absPos))
            {
                _lastClickTime = DateTime.Now;
                Status = "[Enter] Clicked portal";
            }

            return MechanicResult.InProgress;
        }

        // ═══════════════════════════════════════════════════
        // Helpers
        // ═══════════════════════════════════════════════════

        private void SetPhase(WishesPhase phase, string status)
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

        private void ScanFaridunEntities(BotContext ctx)
        {
            var gc = ctx.Game;
            foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
            {
                if (entity.Path == null) continue;

                if (entity.Path.Contains("Faridun/FaridunInitiator") && _initiator == null)
                {
                    _initiator = entity;
                    _initiatorId = entity.Id;
                    _initiatorGridPos = new Vector2(entity.GridPosNum.X, entity.GridPosNum.Y);
                    AnchorGridPos = _initiatorGridPos;
                }
                else if (entity.Path.Contains("Kubera/Varashta"))
                {
                    _npc = entity;
                    _npcId = entity.Id;
                    _npcGridPos = new Vector2(entity.GridPosNum.X, entity.GridPosNum.Y);
                }
                else if (entity.Path.Contains("Faridun/DjinnPortal"))
                {
                    _portal = entity;
                    _portalId = entity.Id;
                    _portalGridPos = new Vector2(entity.GridPosNum.X, entity.GridPosNum.Y);
                }
            }
        }

        private void RefreshEntities(BotContext ctx)
        {
            var gc = ctx.Game;
            foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
            {
                if (entity.Id == _initiatorId) _initiator = entity;
                else if (entity.Id == _npcId) _npc = entity;
                else if (entity.Id == _portalId) _portal = entity;
            }
        }

        private int CountFaridunMonsters(BotContext ctx)
        {
            var gc = ctx.Game;
            var playerGrid = new Vector2(gc.Player.GridPosNum.X, gc.Player.GridPosNum.Y);
            int count = 0;

            foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
            {
                if (entity.Path == null) continue;
                if (!entity.Path.Contains("FaridunLeague")) continue;
                if (!entity.IsTargetable) continue;

                var entityGrid = new Vector2(entity.GridPosNum.X, entity.GridPosNum.Y);
                if (Vector2.Distance(playerGrid, entityGrid) < 120)
                    count++;
            }

            return count;
        }

        private static int GetState(StateMachine? sm, string name)
        {
            if (sm == null) return 0;
            foreach (var state in sm.States)
            {
                if (state.Name == name)
                    return (int)state.Value;
            }
            return 0;
        }

        private bool IsWishesPanelOpen(GameController gc)
        {
            var panel = GetWishesPanel(gc);
            return panel != null && panel.IsVisible;
        }

        private ExileCore.PoEMemory.Element? GetWishesPanel(GameController gc)
        {
            var ui = gc.IngameState.IngameUi;

            // Try cached index first
            if (_wishesPanelIndex >= 0 && _wishesPanelIndex < (int)ui.ChildCount)
            {
                var cached = ui.GetChildAtIndex(_wishesPanelIndex);
                if (cached != null && cached.IsVisible && IsWishesStructure(cached))
                    return cached;
            }

            // Scan all IngameUi children for the wishes panel structure
            var childCount = (int)ui.ChildCount;
            for (int i = 0; i < childCount; i++)
            {
                var child = ui.GetChildAtIndex(i);
                if (child == null || !child.IsVisible) continue;
                if (!IsWishesStructure(child)) continue;

                _wishesPanelIndex = i;
                return child;
            }

            return null;
        }

        /// <summary>
        /// Check if an element matches the wishes panel structure:
        /// panel has 5+ children, child[4] (wishes container) has 7+ children
        /// with wish options at indices 3-5 and confirm button at 6.
        /// </summary>
        private static bool IsWishesStructure(ExileCore.PoEMemory.Element panel)
        {
            if (panel.ChildCount < 5) return false;
            var container = panel.GetChildAtIndex(4);
            if (container == null || container.ChildCount < 7) return false;

            // Verify wish options exist at expected indices (3, 4, 5)
            // Each wish option should have 3+ children (icon, border, title, etc.)
            for (int i = 3; i <= 5; i++)
            {
                var option = container.GetChildAtIndex(i);
                if (option == null || option.ChildCount < 3) return false;
            }

            return true;
        }

        private static string GetWishName(ExileCore.PoEMemory.Element option)
        {
            if (option.ChildCount > 2)
            {
                var titleEl = option.GetChildAtIndex(2);
                if (titleEl?.Text != null) return titleEl.Text;
            }
            return "Unknown";
        }

        private static int FindBestWishOption(ExileCore.PoEMemory.Element container, string preferred)
        {
            if (string.IsNullOrEmpty(preferred) || preferred == "Any")
                return 3;

            for (int i = 3; i <= 5; i++)
            {
                var option = container.GetChildAtIndex(i);
                if (option == null || !option.IsVisible) continue;

                var name = GetWishName(option);
                if (name.Contains(preferred, StringComparison.OrdinalIgnoreCase))
                    return i;
            }

            return 3;
        }
    }
}
