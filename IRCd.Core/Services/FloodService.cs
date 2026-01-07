namespace IRCd.Core.Services
{
    using System;
    using System.Collections.Concurrent;
    using System.Linq;

    using IRCd.Core.State;

    /// <summary>
    /// Enhanced flood control service for message, join/part, and nick change flooding
    /// </summary>
    public sealed class FloodService
    {
        private readonly ConcurrentDictionary<string, FloodTracker> _messageFlood = new();
        private readonly ConcurrentDictionary<string, FloodTracker> _joinPartFlood = new();
        private readonly ConcurrentDictionary<string, FloodTracker> _nickFlood = new();

        private readonly FloodThreshold _messageThreshold = new(5, TimeSpan.FromSeconds(10)); // 5 msgs/10sec per target
        private readonly FloodThreshold _joinPartThreshold = new(10, TimeSpan.FromSeconds(30)); // 10 join/parts/30sec
        private readonly FloodThreshold _nickThreshold = new(5, TimeSpan.FromSeconds(60)); // 5 nick changes/60sec

        /// <summary>
        /// Check if message to target is flooding
        /// </summary>
        public FloodCheckResult CheckMessageFlood(string connectionId, string target, User? user = null)
        {
            // Exempt opers and services
            if (user is not null && (!string.IsNullOrWhiteSpace(user.OperName) || user.IsService))
            {
                return new FloodCheckResult(false, 0);
            }

            var key = $"{connectionId}:{target}";
            var tracker = _messageFlood.GetOrAdd(key, _ => new FloodTracker(_messageThreshold));

            return tracker.RecordEvent();
        }

        /// <summary>
        /// Check if join/part is flooding
        /// </summary>
        public FloodCheckResult CheckJoinPartFlood(string connectionId, User? user = null)
        {
            // Exempt opers and services
            if (user is not null && (!string.IsNullOrWhiteSpace(user.OperName) || user.IsService))
            {
                return new FloodCheckResult(false, 0);
            }

            var tracker = _joinPartFlood.GetOrAdd(connectionId, _ => new FloodTracker(_joinPartThreshold));
            return tracker.RecordEvent();
        }

        /// <summary>
        /// Check if nick changes are flooding
        /// </summary>
        public FloodCheckResult CheckNickFlood(string connectionId, User? user = null)
        {
            // Exempt opers and services
            if (user is not null && (!string.IsNullOrWhiteSpace(user.OperName) || user.IsService))
            {
                return new FloodCheckResult(false, 0);
            }

            var tracker = _nickFlood.GetOrAdd(connectionId, _ => new FloodTracker(_nickThreshold));
            return tracker.RecordEvent();
        }

        /// <summary>
        /// Clear all flood tracking for a connection
        /// </summary>
        public void ClearConnection(string connectionId)
        {
            // Clear message flood entries for this connection
            var messageKeys = _messageFlood.Keys.Where(k => k.StartsWith(connectionId + ":", StringComparison.Ordinal)).ToList();
            foreach (var key in messageKeys)
            {
                _messageFlood.TryRemove(key, out _);
            }

            _joinPartFlood.TryRemove(connectionId, out _);
            _nickFlood.TryRemove(connectionId, out _);
        }

        private sealed class FloodTracker
        {
            private readonly FloodThreshold _threshold;
            private readonly object _lock = new();
            private readonly ConcurrentQueue<DateTimeOffset> _events = new();

            public FloodTracker(FloodThreshold threshold)
            {
                _threshold = threshold;
            }

            public FloodCheckResult RecordEvent()
            {
                lock (_lock)
                {
                    var now = DateTimeOffset.UtcNow;
                    var cutoff = now - _threshold.Window;

                    while (_events.TryPeek(out var oldest) && oldest < cutoff)
                    {
                        _events.TryDequeue(out _);
                    }

                    if (_events.Count >= _threshold.MaxEvents)
                    {
                        if (_events.TryPeek(out var oldestEvent))
                        {
                            var cooldown = (int)Math.Ceiling((oldestEvent.Add(_threshold.Window) - now).TotalSeconds);
                            return new FloodCheckResult(true, Math.Max(1, cooldown));
                        }

                        return new FloodCheckResult(true, (int)_threshold.Window.TotalSeconds);
                    }

                    _events.Enqueue(now);

                    var warnThreshold = (int)(_threshold.MaxEvents * 0.8);
                    if (_events.Count >= warnThreshold)
                    {
                        return new FloodCheckResult(false, 0, true);
                    }

                    return new FloodCheckResult(false, 0);
                }
            }
        }

        private readonly record struct FloodThreshold(int MaxEvents, TimeSpan Window);
    }

    /// <summary>
    /// Result of a flood check
    /// </summary>
    public readonly record struct FloodCheckResult(bool IsFlooding, int CooldownSeconds, bool ShouldWarn = false);
}
