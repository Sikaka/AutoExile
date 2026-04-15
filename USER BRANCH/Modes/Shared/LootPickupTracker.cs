using AutoExile.Systems;

namespace AutoExile.Modes.Shared
{
    /// <summary>
    /// Tracks pending loot pickup state and handles confirmed/failed results.
    /// Replaces duplicated _pendingLootEntityId/_pendingLootName/_pendingLootValue fields
    /// and HandleLootResult logic across modes.
    /// </summary>
    public class LootPickupTracker
    {
        private long _pendingEntityId;
        private string _pendingItemName = "";
        private double _pendingValue;
        private int _pickupCount;

        public bool HasPending => _pendingEntityId != 0;
        public long PendingEntityId => _pendingEntityId;
        public string PendingItemName => _pendingItemName;
        public int PickupCount => _pickupCount;

        /// <summary>
        /// True if the most recent completed pickup failed (item unreachable, blocked, etc.).
        /// Reset to false on the next successful pickup or explicitly via ResetLastFailed().
        /// Used by Simulacrum mode to trigger an early stash cycle when a pickup fails mid-sweep.
        /// </summary>
        public bool LastPickupFailed { get; private set; }


        /// <summary>
        /// Called after starting a pickup via InteractionSystem.
        /// </summary>
        public void SetPending(long entityId, string itemName, double chaosValue)
        {
            _pendingEntityId = entityId;
            _pendingItemName = itemName;
            _pendingValue = chaosValue;
        }

        /// <summary>
        /// Handle the interaction result. On Succeeded: records to LootTracker + increments count.
        /// On Failed: marks failed in LootSystem. Clears pending on either outcome.
        /// </summary>
        public void HandleResult(InteractionResult result, BotContext ctx)
        {
            if (_pendingEntityId == 0) return;

            if (result == InteractionResult.Succeeded)
            {
                ctx.LootTracker.RecordItem(_pendingItemName, _pendingValue, _pendingEntityId);
                _pickupCount++;
                LastPickupFailed = false;
                // Blacklist the entity to prevent re-pickup from entity flicker
                // (item may briefly reappear on ground after successful pickup)
                ctx.Loot.MarkFailed(_pendingEntityId, "picked up");
            }
            else if (result == InteractionResult.Failed)
            {
                var failReason = ctx.Interaction.LastFailReason;
                ctx.Loot.MarkFailed(_pendingEntityId, failReason);
                ctx.Loot.LogSkipEvent(_pendingEntityId, _pendingItemName,
                    $"pickup failed: {failReason}", _pendingValue);
                LastPickupFailed = true;
            }

            if (result == InteractionResult.Succeeded || result == InteractionResult.Failed)
            {
                _pendingEntityId = 0;
                _pendingItemName = "";
                _pendingValue = 0;
            }
        }

        /// <summary>
        /// Clear all state (e.g. on area change or phase reset).
        /// </summary>
        public void Reset()
        {
            _pendingEntityId = 0;
            _pendingItemName = "";
            _pendingValue = 0;
            _pickupCount = 0;
            LastPickupFailed = false;
        }

        /// <summary>Clear the last-failed flag — call after stashing to resume normal pickup flow.</summary>
        public void ResetLastFailed() => LastPickupFailed = false;

        /// <summary>
        /// Reset pickup count only (e.g. between map runs while preserving pending state).
        /// </summary>
        public void ResetCount() => _pickupCount = 0;
    }
}
