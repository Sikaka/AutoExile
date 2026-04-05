using System.Numerics;

namespace AutoExile.Modes.WaveFarm
{
    /// <summary>
    /// Tracks the player's forward movement direction using EMA smoothing.
    /// Used by loot filtering and mechanic deferral to determine what's "ahead" vs "behind."
    /// </summary>
    public class DirectionTracker
    {
        /// <summary>Normalized forward direction. Zero if player hasn't moved yet.</summary>
        public Vector2 Forward { get; private set; }

        /// <summary>True once we have a stable direction (player has moved enough).</summary>
        public bool HasDirection => Forward.LengthSquared() > 0.5f;

        private Vector2 _prevPos;
        private bool _initialized;

        public void Reset()
        {
            Forward = Vector2.Zero;
            _initialized = false;
        }

        /// <summary>Call every tick with the player's current grid position.</summary>
        public void Update(Vector2 currentPos)
        {
            if (!_initialized)
            {
                _prevPos = currentPos;
                _initialized = true;
                return;
            }

            var delta = currentPos - _prevPos;
            _prevPos = currentPos;

            if (delta.LengthSquared() < 1f) return; // ignore micro-jitter / idle

            var dir = Vector2.Normalize(delta);
            Forward = Forward.LengthSquared() < 0.01f
                ? dir
                : Vector2.Normalize(Forward * 0.8f + dir * 0.2f);
        }

        /// <summary>
        /// Is targetPos ahead of playerPos relative to our forward direction?
        /// threshold=0 means forward hemisphere, 0.5 means within ~60 degrees.
        /// </summary>
        public bool IsAhead(Vector2 playerPos, Vector2 targetPos, float threshold = 0f)
        {
            if (!HasDirection) return true; // no direction yet → treat everything as ahead
            var toTarget = targetPos - playerPos;
            if (toTarget.LengthSquared() < 4f) return true; // very close → always "ahead"
            return Vector2.Dot(Vector2.Normalize(toTarget), Forward) > threshold;
        }
    }
}
