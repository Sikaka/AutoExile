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
        private DateTime _lastClickTime = DateTime.MinValue;

        // Track current interaction
        private InteractionTarget? _currentTarget;
        private DateTime _interactionStartTime;
        private float _currentTimeout;
        private int _clickAttempts;
        private const int MaxClickAttempts = 3;
        private const float DefaultInteractRange = 8f; // grid units — must be this close to click
        private const float TimeoutDirect = 5f; // seconds — short timeout for range clicks
        private const float TimeoutClickBuffer = 5f; // seconds added on top of travel estimate
        private const float MinTimeoutNavigate = 10f; // minimum navigate timeout
        private const float MaxTimeoutNavigate = 60f; // hard cap
        private const float EstGridUnitsPerSecond = 25f; // conservative walk speed estimate

        // State
        public bool IsBusy => _currentTarget != null;
        public string Status { get; private set; } = "";

        /// <summary>
        /// Request to interact with a world entity (chest, shrine, transition, NPC, etc.).
        /// If requireProximity is true, will navigate to the entity first.
        /// interactRange controls how close we need to be before clicking (grid units).
        /// </summary>
        public bool InteractWithEntity(Entity entity, NavigationSystem? nav = null,
            bool requireProximity = true, float interactRange = DefaultInteractRange)
        {
            if (_currentTarget != null)
                return false;

            _currentTarget = new InteractionTarget
            {
                EntityId = entity.Id,
                TargetType = InteractionTargetType.WorldEntity,
                InitialState = CaptureEntityState(entity),
                RequireProximity = requireProximity,
                InteractRange = interactRange,
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
            bool requireProximity = true, float interactRange = DefaultInteractRange)
        {
            if (_currentTarget != null)
                return false;

            _currentTarget = new InteractionTarget
            {
                EntityId = itemEntity.Id,
                TargetType = InteractionTargetType.GroundItem,
                RequireProximity = requireProximity,
                InteractRange = interactRange,
                Nav = nav,
                Phase = requireProximity ? InteractionPhase.Navigating : InteractionPhase.Clicking,
                EntityGridPos = new Vector2(itemEntity.GridPosNum.X, itemEntity.GridPosNum.Y),
            };
            _clickAttempts = 0;
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

            // Entity gone while navigating
            if (entity == null && target.TargetType == InteractionTargetType.GroundItem)
            {
                target.Nav?.Stop(gc);
                Status = "Item gone while navigating";
                _currentTarget = null;
                return InteractionResult.Succeeded;
            }

            // Start or update navigation — convert grid to world for NavigateTo
            if (target.Nav != null)
            {
                var worldTarget = target.EntityGridPos * Pathfinding.GridToWorld;
                if (!target.Nav.IsNavigating)
                {
                    // Not navigating yet, or arrived but not close enough — start path
                    var success = target.Nav.NavigateTo(gc, worldTarget);
                    if (!success)
                    {
                        Status = "No path to target";
                        _currentTarget = null;
                        return InteractionResult.Failed;
                    }
                    Status = $"Navigating to target (dist: {dist:F0})";
                }
                else
                {
                    // Check if destination is stale (entity moved significantly)
                    var navDest = target.Nav.Destination ?? Vector2.Zero;
                    if (Vector2.Distance(navDest, worldTarget) > target.InteractRange * Pathfinding.GridToWorld * 2)
                    {
                        target.Nav.NavigateTo(gc, worldTarget);
                    }
                    Status = $"Navigating (dist: {dist:F0})";
                }
            }
            else
            {
                // No nav system — can't navigate, just fail if too far
                Status = "Too far and no navigation available";
                _currentTarget = null;
                return InteractionResult.Failed;
            }

            return InteractionResult.InProgress;
        }

        // --- Clicking phase ---

        private InteractionResult TickGroundItem(GameController gc)
        {
            var target = _currentTarget!;

            var (found, labelDesc) = FindGroundItemLabel(gc, target.EntityId);
            if (!found)
            {
                Status = "Item collected (or gone)";
                _currentTarget = null;
                return InteractionResult.Succeeded;
            }

            if (labelDesc.Label == null || !labelDesc.Label.IsVisible)
            {
                // Label not visible — might need to get closer
                if (target.RequireProximity && target.Nav != null)
                {
                    target.Phase = InteractionPhase.Navigating;
                    Status = "Label not visible — moving closer";
                    return InteractionResult.InProgress;
                }
                Status = "Item label not visible";
                _currentTarget = null;
                return InteractionResult.Failed;
            }

            if (_clickAttempts >= MaxClickAttempts)
            {
                Status = "Failed to pick up item";
                _currentTarget = null;
                return InteractionResult.Failed;
            }

            var labelRect = labelDesc.ClientRect;
            var clickPos = new Vector2(
                labelRect.X + labelRect.Width / 2f,
                labelRect.Y + labelRect.Height / 2f);

            var windowRect = gc.Window.GetWindowRectangle();

            if (IsBlockedByUI(gc, clickPos))
            {
                Status = "Label blocked by UI";
                _currentTarget = null;
                return InteractionResult.Failed;
            }

            var absPos = new Vector2(windowRect.X + clickPos.X, windowRect.Y + clickPos.Y);
            BotInput.Click(absPos);
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

            var screenPos = gc.IngameState.Camera.WorldToScreen(entity.BoundsCenterPosNum);
            var windowRect = gc.Window.GetWindowRectangle();

            if (screenPos.X < 0 || screenPos.X > windowRect.Width ||
                screenPos.Y < 0 || screenPos.Y > windowRect.Height)
            {
                // Off-screen — go back to navigating if we can
                if (target.RequireProximity && target.Nav != null)
                {
                    target.Phase = InteractionPhase.Navigating;
                    Status = "Entity off screen — moving closer";
                    return InteractionResult.InProgress;
                }
                Status = "Entity not on screen";
                return InteractionResult.InProgress;
            }

            // Check if a ground item label is covering our click target
            if (IsGroundLabelOverlapping(gc, screenPos))
            {
                var offsetPos = FindClearClickPosition(gc, entity, screenPos);
                if (offsetPos == null)
                {
                    Status = "Blocked by item label — waiting";
                    return InteractionResult.InProgress;
                }
                screenPos = offsetPos.Value;
            }

            if (IsBlockedByUI(gc, screenPos))
            {
                Status = "Blocked by UI panel";
                return InteractionResult.InProgress;
            }

            var absPos = new Vector2(windowRect.X + screenPos.X, windowRect.Y + screenPos.Y);
            BotInput.Click(absPos);
            _lastClickTime = DateTime.Now;
            _clickAttempts++;
            Status = $"Clicking entity (attempt {_clickAttempts})";

            return InteractionResult.InProgress;
        }

        // --- Timeout calculation ---

        /// <summary>
        /// Compute timeout based on distance. For navigation: travel time estimate + click buffer.
        /// For direct clicks: flat short timeout.
        /// </summary>
        private static float ComputeTimeout(bool requireProximity, float gridDistance)
        {
            if (!requireProximity)
                return TimeoutDirect;

            // Travel time: distance / speed + buffer for clicking + stuck recovery
            var travelEstimate = gridDistance / EstGridUnitsPerSecond;
            var timeout = travelEstimate + TimeoutClickBuffer;
            return Math.Clamp(timeout, MinTimeoutNavigate, MaxTimeoutNavigate);
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
            }
            catch { }

            return false;
        }

        private static bool IsPointInRect(Vector2 point, SharpDX.RectangleF rect)
        {
            return point.X >= rect.X && point.X <= rect.X + rect.Width &&
                   point.Y >= rect.Y && point.Y <= rect.Y + rect.Height;
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
