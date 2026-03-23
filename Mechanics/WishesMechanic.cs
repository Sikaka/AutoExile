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
        KillGuards,
        WaitForNPC,
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
        public bool IsEncounterActive => _phase is WishesPhase.KillGuards or WishesPhase.WaitForNPC;
        public bool IsComplete => _phase == WishesPhase.Complete;
        public bool TriggersSubZone => true;

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


        // Completed initiator IDs — never re-detect these (survives Reset)
        private readonly HashSet<uint> _completedInitiatorIds = new();

        // UI interaction state
        private int _wishClickAttempts;
        private int _confirmClickAttempts;
        private int _npcClickAttempts;
        private int _portalClickAttempts;
        private int _navFailCount;
        private DateTime _lastClickTime = DateTime.MinValue;
        private const float ClickCooldownMs = 500;
        private const float PhaseTimeoutSeconds = 60;
        private const int MaxClickAttempts = 8;
        private const int MaxNavFails = 10;
        // (removed _areaHashOnPortalClick — wish zones don't change area hash)

        // Wishes panel — discovered dynamically, cached until reset
        private int _wishesPanelIndex = -1;

        public bool Detect(BotContext ctx)
        {
            // Already active (including wish zone phases) — stay detected
            if (_phase != WishesPhase.Idle) return true;

            var gc = ctx.Game;

            // Don't detect NEW encounters in wish zones (mirage maps).
            // SekhemaPortal = return portal, only exists in wish zones.
            foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
            {
                if (entity.Path != null && entity.Path.Contains("SekhemaPortal") && entity.IsTargetable)
                    return false;
            }

            // Detect FaridunInitiator — even non-targetable (guards still alive).
            // Skip initiators we've already completed (survives Reset — prevents re-entry loop).
            var playerGrid = new Vector2(gc.Player.GridPosNum.X, gc.Player.GridPosNum.Y);

            foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
            {
                if (entity.Path == null) continue;
                if (!entity.Path.Contains("Faridun/FaridunInitiator")) continue;
                if (_completedInitiatorIds.Contains(entity.Id)) continue;

                var dist = Vector2.Distance(playerGrid, entity.GridPosNum);
                if (dist > Pathfinding.NetworkBubbleRadius) continue;

                _initiator = entity;
                _initiatorId = entity.Id;
                _initiatorGridPos = new Vector2(entity.GridPosNum.X, entity.GridPosNum.Y);
                AnchorGridPos = _initiatorGridPos;

                ScanFaridunEntities(ctx);
                ctx.Log($"[Wishes] Detected initiator at ({AnchorGridPos.Value.X:F0}, {AnchorGridPos.Value.Y:F0}) targetable={entity.IsTargetable}");
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
                    MarkInitiatorCompleted();
                    _phase = WishesPhase.Complete;
                    return MechanicResult.Failed;
                }
            }

            return _phase switch
            {
                WishesPhase.Idle => TickIdle(ctx),
                WishesPhase.NavigateToEncounter => TickNavigateToEncounter(ctx),
                WishesPhase.KillGuards => TickKillGuards(ctx),
                WishesPhase.WaitForNPC => TickWaitForNPC(ctx),
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

        /// <summary>Mark current initiator as done so it's never re-detected (survives Reset).</summary>
        private void MarkInitiatorCompleted()
        {
            if (_initiatorId != 0)
                _completedInitiatorIds.Add(_initiatorId);
        }

        public void Reset()
        {
            // Mark initiator done BEFORE clearing state — if we were mid-flow,
            // we don't want to re-detect this same encounter after reset
            MarkInitiatorCompleted();

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
            _npcClickAttempts = 0;
            _portalClickAttempts = 0;
            // (removed _areaHashOnPortalClick)
            _navFailCount = 0;
            _wishesPanelIndex = -1;
        }

        // ═══════════════════════════════════════════════════
        // Phase handlers
        // ═══════════════════════════════════════════════════

        private MechanicResult TickIdle(BotContext ctx)
        {
            // Portal ready — skip straight to entering it
            if (_portal != null && _portal.IsTargetable)
            {
                SetPhase(WishesPhase.NavigateToPortal, "Portal ready");
                return MechanicResult.InProgress;
            }

            // NPC ready — go talk
            if (_npc != null && _npc.IsTargetable)
            {
                SetPhase(WishesPhase.NavigateToNPC, "NPC available, going to talk");
                return MechanicResult.InProgress;
            }

            // Need initiator for the navigate-to-encounter path
            if (_initiator == null) return MechanicResult.Idle;

            // Initiator exists but not targetable = guards still alive
            // Navigate there and kill guards first
            SetPhase(WishesPhase.NavigateToEncounter, "Navigating to encounter area");
            return MechanicResult.InProgress;
        }

        private MechanicResult TickNavigateToEncounter(BotContext ctx)
        {
            var gc = ctx.Game;

            // Resume navigation if combat paused it
            if (ctx.Navigation.IsPaused)
                ctx.Navigation.Resume(gc);

            // Fire skills at nearby monsters but don't let combat positioning
            // fight with navigation for cursor control
            ctx.Combat.SuppressPositioning = true;
            ctx.Combat.Tick(ctx);
            ctx.Combat.SuppressPositioning = false;

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
                SetPhase(WishesPhase.KillGuards, "In encounter area, killing guards");
                return MechanicResult.InProgress;
            }

            if (!ctx.Navigation.NavigateTo(gc, _initiatorGridPos))
            {
                _navFailCount++;
                Status = $"[Nav] Can't path to encounter (fail {_navFailCount}/{MaxNavFails})";
                if (_navFailCount >= MaxNavFails)
                {
                    ctx.Log("[Wishes] Navigation failed too many times, abandoning");
                    _phase = WishesPhase.Complete;
                    return MechanicResult.Abandoned;
                }
                return MechanicResult.InProgress;
            }

            _navFailCount = 0;
            Status = $"[Nav] Walking to encounter ({dist:F0}g away)";
            return MechanicResult.InProgress;
        }

        private MechanicResult TickKillGuards(BotContext ctx)
        {
            var gc = ctx.Game;

            // Resume navigation if combat paused it
            if (ctx.Navigation.IsPaused)
                ctx.Navigation.Resume(gc);

            ctx.Combat.Tick(ctx);

            ScanFaridunEntities(ctx);

            // NPC targetable = guards dead, skip straight to talking
            if (_npc != null && _npc.IsTargetable)
            {
                SetPhase(WishesPhase.NavigateToNPC, "NPC available, going to talk");
                return MechanicResult.InProgress;
            }

            // Initiator became targetable = guards dead, wait for NPC spawn
            if (_initiator != null && _initiator.IsTargetable)
            {
                SetPhase(WishesPhase.WaitForNPC, "Guards cleared, waiting for NPC");
                return MechanicResult.InProgress;
            }

            var guardCount = CountFaridunGuards(ctx);
            if (guardCount == 0)
            {
                // No guards visible but initiator not yet targetable — may still be loading
                var elapsed = (DateTime.Now - _phaseStartTime).TotalSeconds;
                Status = $"[Guards] No guards visible, waiting for trigger ({elapsed:F0}s)";
                return MechanicResult.InProgress;
            }

            Status = $"[Guards] Killing guards ({guardCount} alive)";
            return MechanicResult.InProgress;
        }

        private MechanicResult TickWaitForNPC(BotContext ctx)
        {
            ScanFaridunEntities(ctx);

            if (_npc != null && _npc.IsTargetable)
            {
                SetPhase(WishesPhase.NavigateToNPC, "NPC spawned, going to talk");
                return MechanicResult.InProgress;
            }

            var elapsed = (DateTime.Now - _phaseStartTime).TotalSeconds;
            Status = $"[NPC] Waiting for Varashta to spawn ({elapsed:F0}s)";

            // Short timeout — NPC spawns ~1-2s after last guard dies
            if (elapsed > 10)
            {
                ctx.Log("[Wishes] WaitForNPC timed out (10s) — abandoning");
                MarkInitiatorCompleted();
                _phase = WishesPhase.Complete;
                return MechanicResult.Failed;
            }

            return MechanicResult.InProgress;
        }

        private MechanicResult TickNavigateToNPC(BotContext ctx)
        {
            var gc = ctx.Game;

            // Resume navigation if combat paused it
            if (ctx.Navigation.IsPaused)
                ctx.Navigation.Resume(gc);

            // Fire skills but suppress positioning — navigation to NPC owns movement
            ctx.Combat.SuppressPositioning = true;
            ctx.Combat.Tick(ctx);
            ctx.Combat.SuppressPositioning = false;

            if (_npc == null || !_npc.IsTargetable)
            {
                ScanFaridunEntities(ctx);
                if (_npc == null || !_npc.IsTargetable)
                {
                    // NPC disappeared — go back to waiting
                    SetPhase(WishesPhase.KillGuards, "NPC not ready, returning to guard area");
                    return MechanicResult.InProgress;
                }
            }

            var playerGrid = new Vector2(gc.Player.GridPosNum.X, gc.Player.GridPosNum.Y);
            _npcGridPos = new Vector2(_npc.GridPosNum.X, _npc.GridPosNum.Y);
            var dist = Vector2.Distance(playerGrid, _npcGridPos);

            if (dist < ctx.Interaction.InteractRadius)
            {
                SetPhase(WishesPhase.TalkToNPC, "Clicking NPC");
                return MechanicResult.InProgress;
            }

            if (!ctx.Navigation.NavigateTo(gc, _npcGridPos))
            {
                Status = $"[Nav] Can't path to Varashta ({dist:F0}g)";
                return MechanicResult.InProgress;
            }

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
                ScanFaridunEntities(ctx);
                if (_npc == null || !_npc.IsTargetable)
                {
                    Status = "[NPC] Varashta not targetable, waiting";
                    return MechanicResult.InProgress;
                }
            }

            // Ensure we're close enough to click
            var playerGrid = new Vector2(gc.Player.GridPosNum.X, gc.Player.GridPosNum.Y);
            _npcGridPos = new Vector2(_npc.GridPosNum.X, _npc.GridPosNum.Y);
            var dist = Vector2.Distance(playerGrid, _npcGridPos);
            if (dist > ctx.Interaction.InteractRadius)
            {
                ctx.Navigation.NavigateTo(gc, _npcGridPos);
                Status = $"[NPC] Walking closer to Varashta ({dist:F0}g)";
                return MechanicResult.InProgress;
            }

            if (!CanClick()) return MechanicResult.InProgress;

            if (BotInput.ClickEntity(gc, _npc))
            {
                _lastClickTime = DateTime.Now;
                _npcClickAttempts++;
                Status = $"[NPC] Clicked Varashta (attempt {_npcClickAttempts}/{MaxClickAttempts})";
            }

            if (_npcClickAttempts >= MaxClickAttempts)
            {
                ctx.Log("[Wishes] Failed to talk to NPC after max attempts, abandoning");
                _phase = WishesPhase.Complete;
                return MechanicResult.Abandoned;
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
                Status = $"[Wish] Clicking '{wishName}' (attempt {_wishClickAttempts}/{MaxClickAttempts})";
            }

            if (_wishClickAttempts >= MaxClickAttempts)
            {
                ctx.Log("[Wishes] Failed to select wish after max attempts, abandoning");
                _phase = WishesPhase.Complete;
                return MechanicResult.Abandoned;
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
                Status = $"[Wish] Clicked confirm (attempt {_confirmClickAttempts}/{MaxClickAttempts})";
            }

            if (_confirmClickAttempts >= MaxClickAttempts)
            {
                ctx.Log("[Wishes] Failed to confirm wish after max attempts, abandoning");
                _phase = WishesPhase.Complete;
                return MechanicResult.Abandoned;
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

            if (dist < ctx.Interaction.InteractRadius)
            {
                SetPhase(WishesPhase.EnterPortal, "Clicking portal");
                return MechanicResult.InProgress;
            }

            ctx.Navigation.NavigateTo(gc, _portalGridPos);
            Status = $"[Nav] Walking to portal ({dist:F0}g away)";
            return MechanicResult.InProgress;
        }

        private MechanicResult TickEnterPortal(BotContext ctx)
        {
            var gc = ctx.Game;

            // Detect completed transition: wish zones don't change area hash or name.
            // The ONLY reliable signal is a targetable SekhemaPortal entity — it only exists
            // inside the wish zone (parent map has a non-targetable minimap icon version).
            if (_portalClickAttempts > 0)
            {
                foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
                {
                    if (entity.Path != null && entity.Path.Contains("SekhemaPortal") && entity.IsTargetable)
                    {
                        ctx.Log("[Wishes] Targetable SekhemaPortal found — portal transition complete");
                        _phase = WishesPhase.Complete;
                        return MechanicResult.Complete;
                    }
                }
            }

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
                // DjinnPortal gone — either we transitioned (SekhemaPortal check above will catch it
                // on next tick) or it despawned. Wait briefly for entities to load.
                var elapsed = (DateTime.Now - _phaseStartTime).TotalSeconds;
                if (elapsed > 10)
                {
                    // 10s with no DjinnPortal and no SekhemaPortal — something went wrong
                    ctx.Log("[Wishes] Portal gone for 10s with no SekhemaPortal — abandoning");
                    MarkInitiatorCompleted();
                    _phase = WishesPhase.Complete;
                    return MechanicResult.Failed;
                }
                Status = $"[Enter] Portal gone, waiting for SekhemaPortal ({elapsed:F0}s)";
                return MechanicResult.InProgress;
            }

            // After clicking the portal once, wait much longer before retrying.
            // Zone transitions take 1-3 seconds. A quick re-click gets queued by the game
            // and fires inside the wish zone — hitting the SekhemaPortal (exit) at the same
            // screen position, sending the player right back to the parent map.
            var portalClickCooldown = _portalClickAttempts > 0 ? 5000f : ClickCooldownMs;
            if (!BotInput.CanAct) return MechanicResult.InProgress;
            var msSinceClick = (DateTime.Now - _lastClickTime).TotalMilliseconds;
            if (msSinceClick < portalClickCooldown)
            {
                Status = $"[Enter] Waiting for transition ({(portalClickCooldown - msSinceClick) / 1000:F1}s) attempts={_portalClickAttempts}";
                return MechanicResult.InProgress;
            }

            if (BotInput.ClickEntity(gc, _portal))
            {
                _lastClickTime = DateTime.Now;
                _portalClickAttempts++;
                Status = $"[Enter] Clicked portal (attempt {_portalClickAttempts}/{MaxClickAttempts})";
            }

            if (_portalClickAttempts >= MaxClickAttempts)
            {
                ctx.Log("[Wishes] Failed to enter portal after max attempts, abandoning");
                MarkInitiatorCompleted();
                _phase = WishesPhase.Complete;
                return MechanicResult.Abandoned;
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

        /// <summary>Count alive FaridunLeague guards near the initiator position (~80 grid units).</summary>
        private int CountFaridunGuards(BotContext ctx)
        {
            var gc = ctx.Game;
            int count = 0;

            foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
            {
                if (entity.Path == null) continue;
                if (!entity.Path.Contains("FaridunLeague")) continue;
                if (!entity.IsAlive || !entity.IsHostile) continue;

                var entityGrid = new Vector2(entity.GridPosNum.X, entity.GridPosNum.Y);
                if (Vector2.Distance(_initiatorGridPos, entityGrid) < 80)
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

            // Match on reward tooltip text (e.g. "Coin of Power", "Coin of Skill", "Coin of Knowledge")
            // Tooltip is on option[4] (the reward icon element at bottom of each wish card)
            for (int i = 3; i <= 5; i++)
            {
                var option = container.GetChildAtIndex(i);
                if (option == null || !option.IsVisible) continue;

                var rewardElement = option.GetChildAtIndex(4);
                var tooltipText = rewardElement?.Tooltip?.Text;
                if (tooltipText != null && tooltipText.Contains(preferred, StringComparison.OrdinalIgnoreCase))
                    return i;
            }

            return 3;
        }

        public void Render(BotContext ctx) { }
    }
}
