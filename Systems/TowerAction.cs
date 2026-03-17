using ExileCore;
using ExileCore.PoEMemory;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.Elements;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using System.Numerics;

namespace AutoExile.Systems
{
    /// <summary>
    /// Self-contained state machine for a single tower build or upgrade action.
    /// Finds the best target globally (not just on-screen), navigates to it,
    /// then clicks through the menu.
    ///
    /// All internal positions are in GRID coordinates.
    /// Converts to world only for NavigateTo calls.
    ///
    /// States:
    ///   FindTarget → NavigateToTarget → ClickLabel → WaitForMenu → ClickButton → Done/Failed
    /// </summary>
    public class TowerAction
    {
        public enum ActionType { Build, Upgrade }
        public enum Phase { FindTarget, NavigateToTarget, ClickLabel, WaitForMenu, ClickButton, Done, Failed }

        public Phase CurrentPhase { get; private set; } = Phase.FindTarget;
        public string Status { get; private set; } = "";
        public bool IsComplete => (CurrentPhase == Phase.Done || CurrentPhase == Phase.Failed) && BotInput.CanAct;
        public bool Succeeded => CurrentPhase == Phase.Done;

        private readonly ActionType _type;
        private readonly BlightState _blight;
        private readonly BotSettings.BlightSettings _config;
        private readonly NavigationSystem _nav;

        // Target tracking (grid coordinates)
        private long _targetEntityId;
        private Vector2 _targetGridPos;
        public Vector2 TargetGridPos => _targetGridPos;
        private DateTime _labelClickedAt;
        private DateTime _clickLabelEnteredAt;
        private int _retries;
        private const int MaxRetries = 2;
        private const float LabelAppearWaitMs = 1500f; // grace period for label to appear after arriving

        // Timing
        private DateTime _startedAt;
        private const float TimeoutSeconds = 15f; // longer to allow navigation
        private const float MenuWaitMs = 500f;

        // Minimum standoff from the tower when approaching (grid units).
        // We don't walk directly on top — we stop this far back along the approach vector.
        private const float MinStandoff = 8f;

        // Safe click zone (screen pixels — unchanged)
        private const float SafeTop = 80f;
        private const float SafeLeft = 80f;
        private const float SafeRight = 80f;
        private const float SafeBottom = 260f;
        private const float MenuRadius = 200f;

        // Tower costs by tier: index 0 = build (tier 0→1), 1 = tier 1→2, 2 = tier 2→3, 3 = tier 3→4
        private static readonly int[] TierCosts = { 150, 300, 450, 500 };

        public TowerAction(ActionType type, BlightState blight, BotSettings.BlightSettings config, NavigationSystem nav)
        {
            _type = type;
            _blight = blight;
            _config = config;
            _nav = nav;
            _startedAt = DateTime.Now;
        }

        /// <summary>
        /// Get the currency cost for building (tier 0) or upgrading from the given tier.
        /// </summary>
        public static int GetCostForTier(int currentTier)
        {
            if (currentTier < 0 || currentTier >= TierCosts.Length) return int.MaxValue;
            return TierCosts[currentTier];
        }

        public bool Tick(GameController gc)
        {
            if (IsComplete) return false;

            if ((DateTime.Now - _startedAt).TotalSeconds > TimeoutSeconds)
            {
                Fail("Timed out");
                _nav.Stop(gc);
                return false;
            }

            return CurrentPhase switch
            {
                Phase.FindTarget => TickFindTarget(gc),
                Phase.NavigateToTarget => TickNavigateToTarget(gc),
                Phase.ClickLabel => TickClickLabel(gc),
                Phase.WaitForMenu => TickWaitForMenu(gc),
                Phase.ClickButton => TickClickButton(gc),
                _ => false,
            };
        }

        public void Cancel(GameController gc)
        {
            _nav.Stop(gc);
            Fail("Cancelled");
        }

        // --- Phase: Find best target globally (not just on-screen) ---

        private bool TickFindTarget(GameController gc)
        {
            if (_type == ActionType.Build)
                _targetEntityId = FindBestFoundation(gc);
            else
                _targetEntityId = FindBestUpgradeTarget(gc);

            if (_targetEntityId == 0)
            {
                Fail(_type == ActionType.Build ? "No buildable foundation" : "No upgradeable tower");
                return false;
            }

            var playerGridPos = gc.Player.GridPosNum;
            var dist = Vector2.Distance(playerGridPos, _targetGridPos);
            var approachDist = _config.TowerApproachDistance.Value;

            if (dist <= approachDist)
            {
                _nav.Stop(gc);
                CurrentPhase = Phase.ClickLabel;
                _clickLabelEnteredAt = DateTime.Now;
                Status = $"Target nearby (dist: {dist:F0})";
            }
            else
            {
                // Navigate to approach point — close enough per config but not on top of the tower
                var approachGridPos = GetApproachPosition(playerGridPos, _targetGridPos, approachDist);
                var success = _nav.NavigateTo(gc, BlightState.ToWorld(approachGridPos));
                if (!success)
                {
                    Fail("No path to target");
                    return false;
                }
                CurrentPhase = Phase.NavigateToTarget;
                Status = $"Navigating to target (dist: {dist:F0}, approach: {approachDist:F0})";
            }
            return false;
        }

        // --- Phase: Navigate to approach point, then wait for nav to finish ---

        private bool TickNavigateToTarget(GameController gc)
        {
            // Wait for navigation to fully complete — don't click while still moving
            if (_nav.IsNavigating)
            {
                var playerGridPos = gc.Player.GridPosNum;
                var dist = Vector2.Distance(playerGridPos, _targetGridPos);
                Status = $"Navigating (dist: {dist:F0})";
                return false;
            }

            // Navigation finished — check if we're close enough
            var finalGridPos = gc.Player.GridPosNum;
            var finalDist = Vector2.Distance(finalGridPos, _targetGridPos);
            var approachDist = _config.TowerApproachDistance.Value;

            if (finalDist <= approachDist + 10f) // small tolerance for path ending
            {
                CurrentPhase = Phase.ClickLabel;
                _clickLabelEnteredAt = DateTime.Now;
                Status = $"Arrived — ready to click (dist: {finalDist:F0})";
                return false;
            }

            // Stopped too far — fail
            Fail($"Navigation ended too far (dist: {finalDist:F0}, need: {approachDist:F0})");
            return false;
        }

        /// <summary>
        /// Compute a safe approach position along the line from target toward the player.
        /// Stays at least MinStandoff away from the target (don't walk on top of it),
        /// but gets within approachDist so the label/menu is centered on screen.
        /// All in grid coordinates.
        /// </summary>
        private static Vector2 GetApproachPosition(Vector2 playerGridPos, Vector2 targetGridPos, float approachDist)
        {
            var dir = playerGridPos - targetGridPos;
            var len = dir.Length();
            if (len < 1f) return targetGridPos;

            // Stand back from the target by the larger of MinStandoff or half the approach distance,
            // so we're close but not directly on top.
            float standoff = Math.Max(MinStandoff, approachDist * 0.5f);
            return targetGridPos + dir / len * standoff;
        }

        // --- Phase: Click the label to open menu ---

        private bool TickClickLabel(GameController gc)
        {
            // Don't click while still navigating — stop and wait for movement to settle
            if (_nav.IsNavigating)
            {
                _nav.Stop(gc);
                Status = "Stopping navigation before click";
                return false;
            }

            if (!CanClick()) return false;

            // Validate target entity still exists before waiting for label
            var targetEntity = gc.EntityListWrapper.OnlyValidEntities
                .FirstOrDefault(e => e.Id == _targetEntityId);
            if (targetEntity == null)
            {
                Fail($"Target entity no longer exists (id={_targetEntityId})");
                return false;
            }

            var label = FindTargetLabel(gc);
            if (label == null)
            {
                // Grace period — label may take a moment to appear after entity enters render range
                var waited = (DateTime.Now - _clickLabelEnteredAt).TotalMilliseconds;
                if (waited < LabelAppearWaitMs)
                {
                    Status = $"Waiting for label ({waited:F0}ms)";
                    return false;
                }
                // Fallback: try clicking via WorldToScreen position
                if (TryClickWorldPosition(gc, targetEntity))
                    return true;

                var playerDist = Vector2.Distance(gc.Player.GridPosNum, _targetGridPos);
                Fail($"Label not visible (id={_targetEntityId}, dist={playerDist:F0})");
                return false;
            }

            if (!IsElementClickable(gc, label.Label))
            {
                // Also give a grace period — label might be off-screen edge temporarily
                var waited = (DateTime.Now - _clickLabelEnteredAt).TotalMilliseconds;
                if (waited < LabelAppearWaitMs)
                {
                    Status = $"Label not clickable yet ({waited:F0}ms)";
                    return false;
                }
                var labelRect = label.Label.GetClientRect();
                Fail($"Label not clickable (center={labelRect.Center.X:F0},{labelRect.Center.Y:F0})");
                return false;
            }

            var rect = label.Label.GetClientRect();
            var center = new Vector2(rect.Center.X, rect.Center.Y);
            if (!ClickRelative(gc, center)) return false;

            _labelClickedAt = DateTime.Now;
            CurrentPhase = Phase.WaitForMenu;
            Status = "Clicked label — waiting for menu";
            return true;
        }

        // --- Phase: Wait for menu to appear ---

        private bool TickWaitForMenu(GameController gc)
        {
            if ((DateTime.Now - _labelClickedAt).TotalMilliseconds < MenuWaitMs)
                return false;

            var label = FindTargetLabel(gc);
            if (label == null)
            {
                Fail("Target disappeared while waiting for menu");
                return false;
            }

            var menu = GetMenu(label.Label);
            if (menu != null && menu.ChildCount > 0)
            {
                CurrentPhase = Phase.ClickButton;
                Status = $"Menu visible ({menu.ChildCount} buttons)";
                return false;
            }

            _retries++;
            if (_retries > MaxRetries)
            {
                Fail("Menu never appeared");
                return false;
            }

            CurrentPhase = Phase.ClickLabel;
            Status = $"Menu not visible — retry {_retries}";
            return false;
        }

        // --- Phase: Click the appropriate button ---

        private bool TickClickButton(GameController gc)
        {
            if (!CanClick()) return false;

            var label = FindTargetLabel(gc);
            if (label == null)
            {
                Fail("Target disappeared before button click");
                return false;
            }

            var menu = GetMenu(label.Label);
            if (menu == null || menu.ChildCount == 0)
            {
                Fail("Menu closed before button click");
                return false;
            }

            int buttonIndex;
            if (_type == ActionType.Build)
            {
                buttonIndex = ChooseBuildTowerIndex(gc, label.ItemOnGround);
                if (buttonIndex < 0)
                {
                    Fail("No valid tower type for this location (all filtered by spread/nearby rules)");
                    return false;
                }
            }
            else
            {
                buttonIndex = ChooseUpgradeIndex(gc, label.ItemOnGround, menu);
                if (buttonIndex < 0)
                {
                    _blight.FullyUpgradedTowerIds.Add(_targetEntityId);
                    Fail("No valid upgrade option");
                    return false;
                }
            }

            if (buttonIndex >= menu.ChildCount)
            {
                Fail($"Button index {buttonIndex} out of range (menu has {menu.ChildCount})");
                return false;
            }

            var button = menu.GetChildAtIndex(buttonIndex);
            if (button == null || !button.IsVisible)
            {
                Fail("Button not visible");
                return false;
            }

            // Menu buttons float on top of the game UI — only check they're within the window
            var winRect = gc.Window.GetWindowRectangle();
            var btnCheck = button.GetClientRect();
            if (btnCheck.Center.X < winRect.Left || btnCheck.Center.X > winRect.Right ||
                btnCheck.Center.Y < winRect.Top || btnCheck.Center.Y > winRect.Bottom)
            {
                Fail("Button outside window bounds");
                return false;
            }

            var btnRect = button.GetClientRect();
            var btnCenter = new Vector2(btnRect.Center.X, btnRect.Center.Y);
            if (!ClickRelative(gc, btnCenter)) return false;

            CurrentPhase = Phase.Done;
            if (_type == ActionType.Build)
            {
                _blight.LastTowerBuildAt = DateTime.Now;
                Status = "Tower built";
            }
            else
            {
                _blight.LastTowerUpgradeAt = DateTime.Now;
                Status = "Tower upgraded";
            }
            return true;
        }

        // --- Target finding (uses cached data — works for off-screen entities) ---

        private long FindBestFoundation(GameController gc)
        {
            // Building a new tower costs 150
            if (!_config.IgnoreCurrency.Value && _blight.Currency < GetCostForTier(0))
                return 0;

            long bestId = 0;
            float bestScore = float.MinValue;
            var pumpPos = _blight.PumpPosition ?? Vector2.Zero;
            var playerGridPos = gc.Player.GridPosNum;
            var priorityOrder = _config.GetPriorityOrder();
            if (priorityOrder.Count == 0) return 0;
            float buildRadius = _config.TowerBuildRadius.Value;

            foreach (var cf in _blight.CachedFoundations.Values)
            {
                if (cf.IsBuilt) continue;

                // Must be within build radius of pump (grid units)
                if (pumpPos != Vector2.Zero && Vector2.Distance(cf.Position, pumpPos) > buildRadius)
                    continue;

                // Skip foundations where no tower type is viable (all filtered by spread/nearby rules)
                if (!HasViableTowerType(cf.Position, priorityOrder))
                    continue;

                // Score by lane coverage using the best viable tower's radius
                var bestViableType = GetBestViableTowerType(cf.Position, priorityOrder);
                float radius = _blight.LaneTracker.GetTowerRadius(_blight.CachedTowers.Values, bestViableType);
                float score = _blight.LaneTracker.HasLaneData
                    ? _blight.LaneTracker.ScoreFoundation(cf.Position, radius)
                    : 1f;

                score += ScoreUncoveredLaneBonus(cf.Position, radius);

                // Distance penalty from player (grid units — scale factor adjusted)
                float distToPlayer = Vector2.Distance(playerGridPos, cf.Position);
                score -= distToPlayer * 0.02f;

                if (score > bestScore)
                {
                    bestScore = score;
                    bestId = cf.EntityId;
                    _targetGridPos = cf.Position;
                }
            }
            return bestId;
        }

        /// <summary>
        /// Check if at least one tower type from the priority list can be built at this position.
        /// </summary>
        private bool HasViableTowerType(Vector2 gridPos, List<(string Name, BotSettings.TowerTypeSettings Config)> priorityOrder)
        {
            foreach (var (name, cfg) in priorityOrder)
            {
                float effectRadius = _blight.LaneTracker.GetTowerRadius(_blight.CachedTowers.Values, name);
                if (!cfg.CanStack.Value && IsTowerTypeNearby(gridPos, name, effectRadius))
                    continue;
                if (cfg.RequiresNearbyTower.Value && !HasAnyTowerNearby(gridPos, effectRadius))
                    continue;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Returns the name of the highest-priority viable tower type at this position.
        /// Assumes HasViableTowerType was already checked.
        /// </summary>
        private string GetBestViableTowerType(Vector2 gridPos, List<(string Name, BotSettings.TowerTypeSettings Config)> priorityOrder)
        {
            foreach (var (name, cfg) in priorityOrder)
            {
                float effectRadius = _blight.LaneTracker.GetTowerRadius(_blight.CachedTowers.Values, name);
                if (!cfg.CanStack.Value && IsTowerTypeNearby(gridPos, name, effectRadius))
                    continue;
                if (cfg.RequiresNearbyTower.Value && !HasAnyTowerNearby(gridPos, effectRadius))
                    continue;
                return name;
            }
            return priorityOrder[0].Name; // fallback (shouldn't reach here if HasViableTowerType was checked)
        }

        private long FindBestUpgradeTarget(GameController gc)
        {
            long bestId = 0;
            float bestScore = -1;
            var pumpPos = _blight.PumpPosition ?? Vector2.Zero;
            var playerGridPos = gc.Player.GridPosNum;

            foreach (var ct in _blight.CachedTowers.Values)
            {
                if (_blight.FullyUpgradedTowerIds.Contains(ct.EntityId)) continue;

                // Skip towers with no identifiable type (can't upgrade what we can't identify)
                if (ct.TowerType == null)
                    continue;

                if (ct.Tier >= 4)
                {
                    _blight.FullyUpgradedTowerIds.Add(ct.EntityId);
                    continue;
                }

                // Check tier-3 branch config
                if (ct.Tier >= 3)
                {
                    var typeConfig = _config.GetTowerConfig(ct.TowerType);
                    if (typeConfig.Tier3Branch.Value == "None")
                    {
                        _blight.FullyUpgradedTowerIds.Add(ct.EntityId);
                        continue;
                    }
                }

                // Check if we have enough currency for this upgrade
                if (!_config.IgnoreCurrency.Value && _blight.Currency < GetCostForTier(ct.Tier))
                    continue;

                // Must be within build radius of pump (grid units)
                if (pumpPos != Vector2.Zero && Vector2.Distance(ct.Position, pumpPos) > _config.TowerBuildRadius.Value)
                    continue;

                // Score: higher danger lanes + lower tier = higher priority
                int closestLane = _blight.LaneTracker.FindClosestLane(ct.Position, out _);
                float laneDanger = closestLane >= 0 && closestLane < _blight.LaneTracker.LaneDanger.Length
                    ? _blight.LaneTracker.LaneDanger[closestLane] : 0;

                float score = (laneDanger + 1f) * (4 - ct.Tier);

                // CC towers on dangerous lanes get bonus
                if (laneDanger > 3f && ct.TowerType is "Chilling" or "Seismic" or "ShockNova")
                    score *= 2f;

                float distToPlayer = Vector2.Distance(playerGridPos, ct.Position);
                score -= distToPlayer * 0.02f;

                if (score > bestScore)
                {
                    bestScore = score;
                    bestId = ct.EntityId;
                    _targetGridPos = ct.Position;
                }
            }
            return bestId;
        }

        /// <summary>
        /// Bonus score for foundations near lanes that have zero tower coverage.
        /// </summary>
        private float ScoreUncoveredLaneBonus(Vector2 pos, float radius)
        {
            if (!_blight.LaneTracker.HasLaneData) return 0;

            float bonus = 0;
            float radiusSq = radius * radius;
            var lanes = _blight.LaneTracker.Lanes;
            var coverage = _blight.LaneTracker.LaneCoverage;

            for (int li = 0; li < lanes.Count && li < coverage.Length; li++)
            {
                if (coverage[li] > 0) continue; // already covered

                bool coversLane = false;
                foreach (var wp in lanes[li])
                {
                    if (Vector2.DistanceSquared(wp, pos) <= radiusSq)
                    {
                        coversLane = true;
                        break;
                    }
                }
                if (coversLane)
                    bonus += 100f; // big bonus per uncovered lane
            }
            return bonus;
        }

        // --- Tower type / upgrade selection (config-driven) ---

        private int ChooseBuildTowerIndex(GameController gc, Entity foundation)
        {
            var pos = foundation.GridPosNum;
            var laneTracker = _blight.LaneTracker;
            var priorityOrder = _config.GetPriorityOrder();

            if (!laneTracker.HasLaneData || priorityOrder.Count == 0)
                return priorityOrder.Count > 0 ? GetMenuIndex(priorityOrder[0].Name) : 0;

            foreach (var (name, cfg) in priorityOrder)
            {
                float effectRadius = _blight.LaneTracker.GetTowerRadius(_blight.CachedTowers.Values, name);

                // Spread check: skip if same type already in range (unless stacking allowed)
                if (!cfg.CanStack.Value && IsTowerTypeNearby(pos, name, effectRadius))
                    continue;

                // Requires nearby tower: skip if no other towers within effect radius
                if (cfg.RequiresNearbyTower.Value && !HasAnyTowerNearby(pos, effectRadius))
                    continue;

                return GetMenuIndex(name);
            }

            // All types filtered out at this location — no valid tower to build
            return -1;
        }

        private int ChooseUpgradeIndex(GameController gc, Entity tower, Element menu)
        {
            var btId = BlightLaneTracker.GetBlightTowerId(tower);
            int tier = btId != null ? BlightLaneTracker.GetTierFromBlightTowerId(btId) : 1;

            if (menu.ChildCount == 1)
                return 0; // single upgrade button

            if (menu.ChildCount >= 2 && tier >= 3)
            {
                // Tier 3 branching — check config
                var towerType = btId != null ? BlightLaneTracker.GetTypeFromBlightTowerId(btId) : null;
                if (towerType != null)
                {
                    var cfg = _config.GetTowerConfig(towerType);
                    return cfg.Tier3Branch.Value switch
                    {
                        "Left" => 1,
                        "Right" => 0,
                        _ => -1, // None = don't upgrade
                    };
                }
                return -1;
            }

            // Fallback: first visible button
            for (int i = 0; i < menu.ChildCount; i++)
            {
                var child = menu.GetChildAtIndex(i);
                if (child != null && child.IsVisible) return i;
            }
            return -1;
        }

        // --- Helpers ---

        private static int GetMenuIndex(string towerType)
        {
            return BlightLaneTracker.TowerNameToIndex.TryGetValue(towerType, out var idx) ? idx : 0;
        }

        private bool IsTowerTypeNearby(Vector2 gridPos, string towerType, float spreadRadius)
        {
            foreach (var ct in _blight.CachedTowers.Values)
            {
                if (Vector2.Distance(ct.Position, gridPos) > spreadRadius) continue;
                if (string.Equals(ct.TowerType, towerType, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private bool HasAnyTowerNearby(Vector2 gridPos, float radius)
        {
            foreach (var ct in _blight.CachedTowers.Values)
            {
                if (Vector2.Distance(ct.Position, gridPos) <= radius)
                    return true;
            }
            return false;
        }

        private bool TryClickWorldPosition(GameController gc, Entity targetEntity)
        {
            var cam = gc.IngameState.Camera;
            var worldPos = targetEntity.PosNum;
            var screenPos = cam.WorldToScreen(worldPos);

            // Check if the screen position is within the safe click area
            var safe = GetSafeRect(gc);
            if (screenPos.X < safe.Left || screenPos.X > safe.Right ||
                screenPos.Y < safe.Top || screenPos.Y > safe.Bottom)
                return false;

            if (!ClickRelative(gc, screenPos)) return false;

            _labelClickedAt = DateTime.Now;
            CurrentPhase = Phase.WaitForMenu;
            Status = "Clicked world position — waiting for menu";
            return true;
        }

        private LabelOnGround? FindTargetLabel(GameController gc)
        {
            var labels = gc.IngameState.IngameUi.ItemsOnGroundLabelElement.LabelsOnGround;
            if (labels == null) return null;

            foreach (var label in labels)
            {
                if (label?.ItemOnGround?.Id == _targetEntityId)
                    return label;
            }
            return null;
        }

        // --- UI element helpers ---

        private static Element? GetMenu(Element? label)
        {
            if (label?.Children == null || label.Children.Count == 0) return null;
            var container = label.Children[0];
            if (container?.Children == null || container.Children.Count <= 3) return null;
            var menu = container.Children[3];
            return menu?.IsVisible == true ? menu : null;
        }

        private static SharpDX.RectangleF GetSafeRect(GameController gc)
        {
            var w = gc.Window.GetWindowRectangle();
            return new SharpDX.RectangleF(
                w.Left + SafeLeft, w.Top + SafeTop,
                w.Width - SafeLeft - SafeRight, w.Height - SafeTop - SafeBottom);
        }

        private static bool IsElementClickable(GameController gc, Element? element)
        {
            if (element == null || !element.IsVisible) return false;
            var rect = element.GetClientRect();
            var center = new Vector2(rect.Center.X, rect.Center.Y);
            var safe = GetSafeRect(gc);
            return center.X > safe.Left && center.X < safe.Right &&
                   center.Y > safe.Top && center.Y < safe.Bottom;
        }

        private bool CanClick()
        {
            return BotInput.CanAct;
        }

        private bool ClickRelative(GameController gc, Vector2 windowRelativePos)
        {
            if (!CanClick()) return false;
            var windowRect = gc.Window.GetWindowRectangle();
            var absPos = new Vector2(windowRect.X + windowRelativePos.X, windowRect.Y + windowRelativePos.Y);
            BotInput.Click(absPos);
            return true;
        }

        private void Fail(string reason)
        {
            CurrentPhase = Phase.Failed;
            Status = reason;
        }
    }
}
