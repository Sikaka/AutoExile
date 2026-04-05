using ExileCore;
using ExileCore.PoEMemory.Elements;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using System.Numerics;

namespace AutoExile.Systems
{
    /// <summary>
    /// Handles clicking on world entities and ground item labels.
    /// Two modes:
    ///   - Range interaction: click immediately if on screen (tower building, etc.)
    ///   - Proximity interaction: navigate to entity first, then click when close enough
    /// Aware of UI overlaps — avoids clicking through labels onto wrong targets,
    /// and avoids clicking in regions blocked by game HUD panels.
    /// </summary>
    public class InteractionSystem
    {
        // Minimum time between any interaction clicks
        private const int ClickCooldownMs = 300;
        // After clicking a world entity, gate ALL bot actions for this long.
        // Gives transitions/waypoints/doors time to open UI or load screens
        // before anything else fires. Prevents rapid re-click toggling.
        private const int EntityClickGateMs = 700;
        private DateTime _lastClickTime = DateTime.MinValue;

        // Track current interaction
        private InteractionTarget? _currentTarget;
        private DateTime _interactionStartTime;
        private float _currentTimeout;
        private int _clickAttempts;
        /// <summary>Max click retry attempts. Synced from settings.</summary>
        public int MaxClickAttempts { get; set; } = 5;
        /// <summary>
        /// Minimum distance to click entities/items (grid units). Synced from InteractRadius setting.
        /// Navigation gets as close as possible; if within this range, clicks directly.
        /// </summary>
        public float InteractRadius { get; set; } = 20f;
        /// <summary>Extra seconds added to all server-response timeouts. Synced from settings.</summary>
        public float ExtraLatencySec { get; set; }
        private const float TimeoutDirect = 5f; // seconds — short timeout for range clicks
        private const float TimeoutClickBuffer = 5f; // seconds added on top of travel estimate
        private const float MinTimeoutNavigate = 10f; // minimum navigate timeout
        private const float MaxTimeoutNavigate = 60f; // hard cap
        private const float EstGridUnitsPerSecond = 25f; // conservative walk speed estimate

        // Entity cache for O(1) lookups (set by BotCore, optional fallback to linear scan)
        public EntityCache? Cache { get; set; }

        // State
        public bool IsBusy => _currentTarget != null;
        public string Status { get; private set; } = "";

        /// <summary>
        /// Reason for the last failure. Set when returning InteractionResult.Failed.
        /// Read by LootPickupTracker to pass to MarkFailed.
        /// </summary>
        public string LastFailReason { get; private set; } = "";

        /// <summary>
        /// Request to interact with a world entity (chest, shrine, transition, NPC, etc.).
        /// If requireProximity is true, will navigate to the entity first.
        /// Uses InteractRadius to determine when close enough to click.
        /// </summary>
        public bool InteractWithEntity(Entity entity, NavigationSystem? nav = null,
            bool requireProximity = true)
        {
            if (_currentTarget != null)
                return false;

            _currentTarget = new InteractionTarget
            {
                EntityId = entity.Id,
                TargetType = InteractionTargetType.WorldEntity,
                InitialState = CaptureEntityState(entity),
                RequireProximity = requireProximity,
                InteractRange = InteractRadius,
                Nav = nav,
                Phase = requireProximity ? InteractionPhase.Navigating : InteractionPhase.Clicking,
                EntityGridPos = new Vector2(entity.GridPosNum.X, entity.GridPosNum.Y),
            };
            _clickAttempts = 0;
            _interactionStartTime = DateTime.Now;
            _currentTimeout = ComputeTimeout(requireProximity, entity.DistancePlayer);
            Status = $"Interacting: {entity.RenderName ?? entity.Path}";
            return true;
        }

        /// <summary>
        /// Request to pick up a ground item.
        /// If requireProximity is true, will navigate to the item first.
        /// </summary>
        public bool PickupGroundItem(Entity itemEntity, NavigationSystem? nav = null,
            bool requireProximity = true)
        {
            if (_currentTarget != null)
                return false;

            _currentTarget = new InteractionTarget
            {
                EntityId = itemEntity.Id,
                TargetType = InteractionTargetType.GroundItem,
                RequireProximity = requireProximity,
                InteractRange = InteractRadius,
                Nav = nav,
                Phase = requireProximity ? InteractionPhase.Navigating : InteractionPhase.Clicking,
                EntityGridPos = new Vector2(itemEntity.GridPosNum.X, itemEntity.GridPosNum.Y),
            };
            _clickAttempts = 0;
            _navStoppedAt = DateTime.MinValue;
            _interactionStartTime = DateTime.Now;
            _currentTimeout = ComputeTimeout(requireProximity, itemEntity.DistancePlayer);
            Status = $"Picking up item (id={itemEntity.Id})";
            return true;
        }

        /// <summary>
        /// Cancel any pending interaction. Stops navigation if we were pathing.
        /// </summary>
        public void Cancel(GameController? gc = null)
        {
            if (_currentTarget?.Nav != null && gc != null)
                _currentTarget.Nav.Stop(gc);
            _currentTarget = null;
            BotInput.Cancel();
            Status = "";
        }

        /// <summary>
        /// Process the current interaction. Call every tick.
        /// </summary>
        public InteractionResult Tick(GameController gc)
        {
            if (_currentTarget == null)
                return InteractionResult.None;

            if ((DateTime.Now - _interactionStartTime).TotalSeconds > _currentTimeout)
            {
                Status = $"Interaction timed out ({_currentTimeout:F0}s)";
                LastFailReason = $"timeout ({_currentTimeout:F0}s, {_clickAttempts} clicks)";
                Cancel(gc);
                return InteractionResult.Failed;
            }

            // Respect click cooldown (only matters during clicking phase)
            if (_currentTarget.Phase == InteractionPhase.Clicking &&
                (DateTime.Now - _lastClickTime).TotalMilliseconds < ClickCooldownMs)
                return InteractionResult.InProgress;

            return _currentTarget.Phase switch
            {
                InteractionPhase.Navigating => TickNavigating(gc),
                InteractionPhase.Clicking => _currentTarget.TargetType == InteractionTargetType.GroundItem
                    ? TickGroundItem(gc)
                    : TickWorldEntity(gc),
                _ => InteractionResult.InProgress
            };
        }

        // --- Navigation phase ---

        private InteractionResult TickNavigating(GameController gc)
        {
            var target = _currentTarget!;
            var playerGridPos = new Vector2(gc.Player.GridPosNum.X, gc.Player.GridPosNum.Y);

            // Re-find entity to get fresh position
            var entity = FindEntity(gc, target.EntityId);
            if (entity != null)
                target.EntityGridPos = new Vector2(entity.GridPosNum.X, entity.GridPosNum.Y);

            var dist = Vector2.Distance(playerGridPos, target.EntityGridPos);

            // Close enough — switch to clicking
            if (dist <= target.InteractRange)
            {
                target.Nav?.Stop(gc);
                target.Phase = InteractionPhase.Clicking;
                Status = "In range — clicking";
                return InteractionResult.InProgress;
            }

            // Entity gone while navigating — could mean picked up, or just out of entity range
            // (ground item labels are visible beyond the ~180 grid network bubble).
            // Only count as Succeeded if we already clicked (entity vanished after click).
            // Otherwise fail so the item gets blacklisted and we don't loop forever.
            if (entity == null && target.TargetType == InteractionTargetType.GroundItem)
            {
                target.Nav?.Stop(gc);
                if (_clickAttempts > 0)
                {
                    Status = "Item gone after click — collected";
                    _currentTarget = null;
                    return InteractionResult.Succeeded;
                }
                Status = "Item not in entity list — skipping";
                LastFailReason = "entity gone before click";
                _currentTarget = null;
                return InteractionResult.Failed;
            }

            // Start or update navigation
            if (target.Nav != null)
            {
                if (!target.Nav.IsNavigating)
                {
                    // Not navigating yet, or arrived but not close enough — start path
                    var success = target.Nav.NavigateTo(gc, target.EntityGridPos);
                    if (!success)
                    {
                        Status = "No path to target";
                        LastFailReason = "no path";
                        _currentTarget = null;
                        return InteractionResult.Failed;
                    }
                    Status = $"Navigating to target (dist: {dist:F0})";
                }
                else
                {
                    // Check if destination is stale (entity moved significantly)
                    var navDest = target.Nav.Destination ?? Vector2.Zero;
                    if (Vector2.Distance(navDest, target.EntityGridPos) > target.InteractRange * 2)
                    {
                        target.Nav.NavigateTo(gc, target.EntityGridPos);
                    }
                    Status = $"Navigating (dist: {dist:F0})";
                }
            }
            else
            {
                // No nav system — can't navigate, just fail if too far
                Status = "Too far and no navigation available";
                LastFailReason = "too far, no nav";
                _currentTarget = null;
                return InteractionResult.Failed;
            }

            return InteractionResult.InProgress;
        }

        // --- Clicking phase ---

        // Settle time after stopping navigation before clicking — character has movement
        // inertia and needs a few frames to fully stop, otherwise label positions shift mid-click.
        private DateTime _navStoppedAt = DateTime.MinValue;
        private const int NavSettleMs = 200;

        private InteractionResult TickGroundItem(GameController gc)
        {
            var target = _currentTarget!;

            // Stop navigation before clicking — if the player is walking, clicks miss because
            // the camera/label positions are shifting every frame. This is critical for items
            // picked up "in radius" (requireProximity=false) where no navigate phase runs.
            if (target.Nav != null && target.Nav.IsNavigating)
            {
                target.Nav.Stop(gc);
                // Only set settle timestamp once — don't reset if something else restarted nav
                if (_navStoppedAt == DateTime.MinValue)
                    _navStoppedAt = DateTime.Now;
            }

            // Wait for character to settle after stopping navigation
            if (_navStoppedAt != DateTime.MinValue &&
                (DateTime.Now - _navStoppedAt).TotalMilliseconds < NavSettleMs)
            {
                Status = "Stopping to pick up item...";
                return InteractionResult.InProgress;
            }

            var (found, labelDesc) = FindGroundItemLabel(gc, target.EntityId);
            if (!found)
            {
                // Label not in VisibleGroundItemLabels — check if the entity still exists.
                // With many ground items, labels flicker in/out as the game hides overlapping labels.
                // Only count as Succeeded if the entity is truly gone from the world.
                var entity = FindEntity(gc, target.EntityId);
                if (entity == null)
                {
                    Status = "Item collected (or gone)";
                    _currentTarget = null;
                    return InteractionResult.Succeeded;
                }

                // Entity still exists but label not visible — transient flicker.
                // If we haven't clicked yet, wait for label to reappear.
                // If we already clicked, count as success (click likely worked, label removed).
                if (_clickAttempts > 0)
                {
                    Status = "Item label gone after click — assumed collected";
                    _currentTarget = null;
                    return InteractionResult.Succeeded;
                }

                Status = "Label not visible — waiting";
                return InteractionResult.InProgress;
            }

            if (labelDesc.Label == null || !labelDesc.Label.IsVisible)
            {
                // Label found but not visible — might need to get closer
                if (target.RequireProximity && target.Nav != null)
                {
                    target.Phase = InteractionPhase.Navigating;
                    Status = "Label not visible — moving closer";
                    return InteractionResult.InProgress;
                }
                Status = "Item label not visible";
                LastFailReason = "label not visible";
                _currentTarget = null;
                return InteractionResult.Failed;
            }

            if (_clickAttempts >= MaxClickAttempts)
            {
                Status = "Failed to pick up item";
                LastFailReason = $"max clicks ({MaxClickAttempts})";
                _currentTarget = null;
                return InteractionResult.Failed;
            }

            var labelRect = labelDesc.ClientRect;
            var clickCenter = new Vector2(labelRect.X + labelRect.Width / 2, labelRect.Y + labelRect.Height / 2);

            if (IsBlockedByUI(gc, clickCenter))
            {
                Status = "Label blocked by UI";
                LastFailReason = "blocked by UI";
                _currentTarget = null;
                return InteractionResult.Failed;
            }

            // Use hover-verified click: moves cursor within label, waits for settle,
            // checks Targetable.isTargeted on the WorldItem entity before clicking.
            var worldEntity = FindEntity(gc, target.EntityId);
            if (worldEntity == null)
            {
                Status = "Item collected (or gone)";
                _currentTarget = null;
                return InteractionResult.Succeeded;
            }

            // Check if the loot item is near a portal/exit — clicking could activate
            // the portal instead of picking up the item (PoE prioritizes world entities over labels).
            if (IsNearPortal(worldEntity))
            {
                Status = "Item near portal — skipping";
                LastFailReason = "near portal";
                _currentTarget = null;
                return InteractionResult.Failed;
            }

            // Pass a rect provider that re-reads the label position at click time.
            // During cursor interpolation + settle (~100-150ms), the player character
            // continues sliding from movement momentum, shifting all screen-space positions.
            // The callback provides fresh coordinates for a final cursor snap before click.
            var entityId = target.EntityId;
            Func<SharpDX.RectangleF?> rectProvider = () =>
            {
                var (f, desc) = FindGroundItemLabel(gc, entityId);
                return f && desc?.Label?.IsVisible == true ? desc?.ClientRect : null;
            };
            var sent = BotInput.ClickLabelVerified(gc, labelRect, worldEntity, rectProvider);
            if (!sent)
            {
                Status = "Gate blocked";
                return InteractionResult.InProgress;
            }

            _lastClickTime = DateTime.Now;
            _clickAttempts++;
            Status = $"Clicking item (attempt {_clickAttempts})";

            return InteractionResult.InProgress;
        }

        private InteractionResult TickWorldEntity(GameController gc)
        {
            var target = _currentTarget!;

            var entity = FindEntity(gc, target.EntityId);
            if (entity == null)
            {
                Status = "Entity gone (interaction succeeded)";
                _currentTarget = null;
                return InteractionResult.Succeeded;
            }

            if (HasEntityStateChanged(entity, target.InitialState))
            {
                Status = "Interaction succeeded";
                _currentTarget = null;
                return InteractionResult.Succeeded;
            }

            if (_clickAttempts >= MaxClickAttempts)
            {
                Status = "Failed to interact";
                _currentTarget = null;
                return InteractionResult.Failed;
            }

            // Use hover-verified click: moves cursor to random position within entity bounds,
            // waits for settle, checks Targetable.isTargeted before clicking. Retries at different
            // positions if something else is on top (player, ground label, etc.).
            var sent = BotInput.ClickEntity(gc, entity);
            if (!sent)
            {
                if (!BotInput.CanAct)
                {
                    // Gate blocked — stay in Clicking phase and wait for gate to clear.
                    // Bouncing to Navigating causes a flicker loop (Nav→close→Click→gated→Nav→...)
                    Status = "Waiting for input gate";
                    return InteractionResult.InProgress;
                }

                // Entity genuinely off-screen — go back to navigating to get closer
                if (target.RequireProximity && target.Nav != null)
                {
                    target.Phase = InteractionPhase.Navigating;
                    Status = "Entity off screen — moving closer";
                    return InteractionResult.InProgress;
                }
                Status = "Entity not on screen";
                return InteractionResult.InProgress;
            }

            _lastClickTime = DateTime.Now;
            _clickAttempts++;
            Status = $"Clicking entity (attempt {_clickAttempts})";

            // Gate the entire bot after clicking a world entity.
            // Transitions (waypoints, doors, portals) need time for UI/loading screens
            // to appear. Without this, rapid re-clicks toggle UI open/closed.
            var gateUntil = DateTime.Now.AddMilliseconds(EntityClickGateMs);
            if (gateUntil > BotInput.NextActionAt)
                BotInput.NextActionAt = gateUntil;

            return InteractionResult.InProgress;
        }

        // --- Timeout calculation ---

        /// <summary>
        /// Compute timeout based on distance. For navigation: travel time estimate + click buffer.
        /// For direct clicks: flat short timeout.
        /// </summary>
        private float ComputeTimeout(bool requireProximity, float gridDistance)
        {
            if (!requireProximity)
                return TimeoutDirect + ExtraLatencySec;

            // Travel time: distance / speed + buffer for clicking + stuck recovery
            var travelEstimate = gridDistance / EstGridUnitsPerSecond;
            var timeout = travelEstimate + TimeoutClickBuffer + ExtraLatencySec;
            return Math.Clamp(timeout, MinTimeoutNavigate, MaxTimeoutNavigate + ExtraLatencySec);
        }

        // --- Overlap / UI helpers ---

        private bool IsGroundLabelOverlapping(GameController gc, Vector2 screenPos)
        {
            try
            {
                var labels = gc.IngameState.IngameUi.ItemsOnGroundLabelElement.VisibleGroundItemLabels;
                foreach (var label in labels)
                {
                    if (label.Label == null || !label.Label.IsVisible)
                        continue;
                    var rect = label.ClientRect;
                    if (screenPos.X >= rect.X && screenPos.X <= rect.X + rect.Width &&
                        screenPos.Y >= rect.Y && screenPos.Y <= rect.Y + rect.Height)
                        return true;
                }
            }
            catch { }
            return false;
        }

        private Vector2? FindClearClickPosition(GameController gc, Entity entity, Vector2 screenPos)
        {
            var offsets = new Vector2[]
            {
                new(0, -30), new(0, 30), new(-40, 0), new(40, 0),
                new(0, -50), new(0, 50),
            };

            var windowRect = gc.Window.GetWindowRectangle();
            foreach (var offset in offsets)
            {
                var testPos = screenPos + offset;
                if (testPos.X < 10 || testPos.X > windowRect.Width - 10 ||
                    testPos.Y < 10 || testPos.Y > windowRect.Height - 10)
                    continue;

                if (!IsGroundLabelOverlapping(gc, testPos) && !IsBlockedByUI(gc, testPos))
                    return testPos;
            }

            return null;
        }

        private bool IsBlockedByUI(GameController gc, Vector2 screenPos)
        {
            var windowRect = gc.Window.GetWindowRectangle();
            var w = windowRect.Width;
            var h = windowRect.Height;

            if (screenPos.Y > h * 0.88f && screenPos.X > w * 0.2f && screenPos.X < w * 0.8f)
                return true;
            if (screenPos.Y > h * 0.78f && screenPos.X < w * 0.12f)
                return true;
            if (screenPos.Y > h * 0.78f && screenPos.X > w * 0.88f)
                return true;
            if (screenPos.Y < h * 0.25f && screenPos.X > w * 0.75f)
                return true;

            try
            {
                var ui = gc.IngameState.IngameUi;
                if (ui.InventoryPanel.IsVisible && IsPointInRect(screenPos, ui.InventoryPanel.GetClientRect()))
                    return true;
                if (ui.StashElement.IsVisible && IsPointInRect(screenPos, ui.StashElement.GetClientRect()))
                    return true;
                // Ritual shop — if open, everything behind it is blocked
                if (ui.RitualWindow?.IsVisible == true)
                    return true;
            }
            catch { }

            return false;
        }

        /// <summary>
        /// Grid positions that are dangerous to click near (portals, exit entities).
        /// Modes populate this to prevent accidental portal activation when clicking loot.
        /// Checked during ground item pickup — items near these positions are skipped.
        /// </summary>
        public List<Vector2> DangerZones { get; } = new(4);

        /// <summary>
        /// Check if a loot entity is physically close to a portal, area-transition,
        /// or mode-specified danger zone. Prevents accidental portal activation.
        /// </summary>
        public bool IsNearPortal(Entity lootEntity, float radius = 15f)
        {
            var lootPos = lootEntity.GridPosNum;
            float radiusSq = radius * radius;

            // Check mode-specified danger zones (e.g., SekhemaPortal position)
            foreach (var dz in DangerZones)
            {
                if (Vector2.DistanceSquared(lootPos, dz) < radiusSq)
                    return true;
            }

            // Check cached portals and area transitions
            if (Cache == null) return false;

            foreach (var portal in Cache.Portals)
            {
                if (Vector2.DistanceSquared(lootPos, portal.GridPosNum) < radiusSq)
                    return true;
            }
            foreach (var transition in Cache.AreaTransitions)
            {
                if (Vector2.DistanceSquared(lootPos, transition.GridPosNum) < radiusSq)
                    return true;
            }

            return false;
        }

        private static bool IsPointInRect(Vector2 point, SharpDX.RectangleF rect)
        {
            return point.X >= rect.X && point.X <= rect.X + rect.Width &&
                   point.Y >= rect.Y && point.Y <= rect.Y + rect.Height;
        }

        /// <summary>
        /// Check if another visible ground label's rect overlaps the target label.
        /// Returns the overlapping entity if found, null if clear.
        /// Only checks labels from different entities (not the target itself).
        /// </summary>
        private Entity? FindOverlappingLabel(GameController gc, long targetEntityId, SharpDX.RectangleF targetRect)
        {
            try
            {
                foreach (var label in gc.IngameState.IngameUi.ItemsOnGroundLabelElement.VisibleGroundItemLabels)
                {
                    if (label.Label == null || !label.Label.IsVisible) continue;
                    if (label.Entity?.Id == targetEntityId) continue; // skip self

                    var otherRect = label.ClientRect;

                    // Rect intersection test
                    bool overlaps = targetRect.X < otherRect.X + otherRect.Width &&
                                    targetRect.X + targetRect.Width > otherRect.X &&
                                    targetRect.Y < otherRect.Y + otherRect.Height &&
                                    targetRect.Y + targetRect.Height > otherRect.Y;

                    if (overlaps && label.Entity != null)
                        return label.Entity;
                }
            }
            catch { }

            return null;
        }

        // --- Entity lookup helpers ---

        private (bool Found, ItemsOnGroundLabelElement.VisibleGroundItemDescription? Label) FindGroundItemLabel(
            GameController gc, long entityId)
        {
            try
            {
                foreach (var label in gc.IngameState.IngameUi.ItemsOnGroundLabelElement.VisibleGroundItemLabels)
                {
                    if (label.Entity?.Id == entityId)
                        return (true, label);
                }
            }
            catch { }
            return (false, default);
        }

        private Entity? FindEntity(GameController gc, long entityId)
        {
            // O(1) lookup via entity cache, fall through to linear scan on miss.
            // Cache misses happen for entities that spawn after EntityCache.Rebuild()
            // (e.g., portals loading late after area transitions).
            if (Cache != null)
            {
                var cached = Cache.Get(entityId);
                if (cached != null)
                    return cached;
            }

            // Fallback: linear scan of OnlyValidEntities
            foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
            {
                if (entity.Id == entityId)
                    return entity;
            }
            return null;
        }

        private EntityState CaptureEntityState(Entity entity)
        {
            return new EntityState
            {
                IsTargetable = entity.IsTargetable,
                IsOpened = entity.IsOpened,
            };
        }

        private bool HasEntityStateChanged(Entity entity, EntityState initialState)
        {
            if (!initialState.IsOpened && entity.IsOpened)
                return true;
            if (initialState.IsTargetable && !entity.IsTargetable)
                return true;
            return false;
        }
    }

    public enum InteractionResult
    {
        None,
        InProgress,
        Succeeded,
        Failed,
    }

    internal enum InteractionTargetType
    {
        WorldEntity,
        GroundItem,
    }

    internal enum InteractionPhase
    {
        Navigating, // Moving to entity
        Clicking,   // Close enough, trying to click
    }

    internal class InteractionTarget
    {
        public long EntityId;
        public InteractionTargetType TargetType;
        public EntityState InitialState;
        public bool RequireProximity;
        public float InteractRange;
        public NavigationSystem? Nav;
        public InteractionPhase Phase;
        public Vector2 EntityGridPos;
    }

    internal struct EntityState
    {
        public bool IsTargetable;
        public bool IsOpened;
    }
}
