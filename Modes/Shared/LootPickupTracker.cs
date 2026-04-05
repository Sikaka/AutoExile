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
        public double PendingChaosValue => _pendingValue;
        public int PickupCount => _pickupCount;


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
        }

        /// <summary>
        /// Reset pickup count only (e.g. between map runs while preserving pending state).
        /// </summary>
        public void ResetCount() => _pickupCount = 0;
    }
}
