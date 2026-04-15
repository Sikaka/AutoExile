namespace AutoExile.Systems
{
    /// <summary>
    /// Tracks how long the bot has been *actively* running this session.
    ///
    /// Time spent paused (Settings.Running.Value == false) does NOT count toward
    /// active duration. Pausing and resuming preserves accumulated active time —
    /// only an explicit Reset() zeros the counters.
    ///
    /// Lifecycle:
    ///   - Created at plugin load → sessionStart = now
    ///   - <see cref="Tick"/> called every frame with the current "is bot running"
    ///     flag; pause edges (running ↔ stopped) update internal accounting
    ///   - <see cref="ActiveDuration"/> is the source of truth for "how long has
    ///     the bot been working"
    ///   - <see cref="IsExpired"/> compares ActiveDuration against the user's
    ///     configured max runtime; consumer (BotCore) flips Running off when true
    ///   - <see cref="Reset"/> is the only way to zero the counters — invoked from
    ///     the web UI button and on profile switch
    /// </summary>
    public class RuntimeTracker
    {
        private DateTime _sessionStart = DateTime.Now;
        private DateTime? _pausedAt;
        private TimeSpan _accumulatedPause = TimeSpan.Zero;
        private bool _wasRunning;

        /// <summary>True the very first tick — used to suppress the "wave 0 → first" pause edge on startup.</summary>
        private bool _firstTick = true;

        /// <summary>
        /// Wall-clock time the session started (or was last reset). Stored as
        /// a fixed point so consumers can show "Started at HH:MM" if desired.
        /// </summary>
        public DateTime SessionStart => _sessionStart;

        /// <summary>
        /// Total time spent in the running state since session start (or last
        /// reset). Excludes any time the bot was paused.
        /// </summary>
        public TimeSpan ActiveDuration
        {
            get
            {
                var raw = DateTime.Now - _sessionStart;
                var pendingPause = _pausedAt.HasValue ? DateTime.Now - _pausedAt.Value : TimeSpan.Zero;
                var total = raw - _accumulatedPause - pendingPause;
                return total < TimeSpan.Zero ? TimeSpan.Zero : total;
            }
        }

        /// <summary>True when the tracker is currently in the paused state.</summary>
        public bool IsPaused => _pausedAt.HasValue;

        /// <summary>
        /// Update pause/run state. Call once per frame from BotCore. The flag
        /// should reflect "is the bot allowed to take actions" — e.g.
        /// <c>Settings.Running.Value</c>. Edge transitions update accounting.
        /// </summary>
        public void Tick(bool isRunning)
        {
            if (_firstTick)
            {
                _firstTick = false;
                _wasRunning = isRunning;
                if (!isRunning)
                    _pausedAt = DateTime.Now; // started paused
                return;
            }

            if (isRunning == _wasRunning) return;

            if (isRunning)
            {
                // Resuming — close the open pause window
                if (_pausedAt.HasValue)
                {
                    _accumulatedPause += DateTime.Now - _pausedAt.Value;
                    _pausedAt = null;
                }
            }
            else
            {
                // Pausing — open a pause window
                _pausedAt = DateTime.Now;
            }
            _wasRunning = isRunning;
        }

        /// <summary>
        /// True when the user-configured cap (in minutes) has been exceeded by
        /// active runtime. Returns false when <paramref name="maxMinutes"/> is
        /// zero or negative (= no limit).
        /// </summary>
        public bool IsExpired(int maxMinutes)
        {
            if (maxMinutes <= 0) return false;
            return ActiveDuration >= TimeSpan.FromMinutes(maxMinutes);
        }

        /// <summary>
        /// Time remaining before <see cref="IsExpired"/> returns true. Returns
        /// <see cref="TimeSpan.Zero"/> if already expired, or
        /// <see cref="TimeSpan.MaxValue"/> if no limit is configured.
        /// </summary>
        public TimeSpan Remaining(int maxMinutes)
        {
            if (maxMinutes <= 0) return TimeSpan.MaxValue;
            var max = TimeSpan.FromMinutes(maxMinutes);
            var left = max - ActiveDuration;
            return left < TimeSpan.Zero ? TimeSpan.Zero : left;
        }

        /// <summary>
        /// Zero the timer. Session start moves to "now" and pause accounting
        /// clears. If the bot is currently paused, a fresh pause window opens
        /// at the new session start so we don't accumulate phantom active time
        /// before the user resumes.
        /// </summary>
        public void Reset()
        {
            _sessionStart = DateTime.Now;
            _accumulatedPause = TimeSpan.Zero;
            _pausedAt = _wasRunning ? null : DateTime.Now;
        }
    }
}
