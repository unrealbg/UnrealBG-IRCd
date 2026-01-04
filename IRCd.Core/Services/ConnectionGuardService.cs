namespace IRCd.Core.Services
{
    using System;
    using System.Collections.Concurrent;
    using System.Net;

    using IRCd.Shared.Options;

    using Microsoft.Extensions.Options;

    public sealed class ConnectionGuardService
    {
        private readonly IOptionsMonitor<IrcOptions> _options;

        private readonly ConcurrentDictionary<IPAddress, SlidingWindowCounter> _connWindows = new();

        private readonly ConcurrentDictionary<IPAddress, int> _unregisteredCounts = new();

        private readonly ConcurrentDictionary<IPAddress, int> _activeCounts = new();

        public ConnectionGuardService(IOptionsMonitor<IrcOptions> options)
        {
            _options = options;
        }

        public bool Enabled => _options.CurrentValue.ConnectionGuard.Enabled;

        public bool TryAcceptNewConnection(IPAddress ip, out string rejectReason)
        {
            rejectReason = string.Empty;

            var cfg = _options.CurrentValue.ConnectionGuard;
            if (!cfg.Enabled)
            {
                return true;
            }

            var counter = _connWindows.GetOrAdd(ip, _ => new SlidingWindowCounter());
            if (!counter.TryIncrement(cfg.WindowSeconds, cfg.MaxConnectionsPerWindowPerIp))
            {
                rejectReason = cfg.RejectMessage;
                return false;
            }

            var active = _activeCounts.AddOrUpdate(ip, 1, (_, v) => v + 1);
            if (active > cfg.MaxActiveConnectionsPerIp)
            {
                _activeCounts.AddOrUpdate(ip, 0, (_, v) => Math.Max(0, v - 1));
                rejectReason = cfg.RejectMessage;
                return false;
            }

            var current = _unregisteredCounts.AddOrUpdate(ip, 1, (_, v) => v + 1);
            if (current > cfg.MaxUnregisteredPerIp)
            {
                _unregisteredCounts.AddOrUpdate(ip, 0, (_, v) => Math.Max(0, v - 1));
                _activeCounts.AddOrUpdate(ip, 0, (_, v) => Math.Max(0, v - 1));

                rejectReason = cfg.RejectMessage;
                return false;
            }

            return true;
        }

        /// <summary>
        /// Call once a session successfully becomes registered.
        /// </summary>
        public void MarkRegistered(IPAddress ip)
        {
            if (!Enabled)
                return;

            _unregisteredCounts.AddOrUpdate(ip, 0, (_, v) => Math.Max(0, v - 1));
        }

        /// <summary>
        /// Call when a session closes/disconnects (registered or unregistered).
        /// </summary>
        public void ReleaseActive(IPAddress ip)
        {
            if (!Enabled)
                return;

            _activeCounts.AddOrUpdate(ip, 0, (_, v) => Math.Max(0, v - 1));
        }

        /// <summary>
        /// Call when a session closes/disconnects (only if it was still unregistered).
        /// </summary>
        public void ReleaseUnregistered(IPAddress ip)
        {
            if (!Enabled)
                return;

            _unregisteredCounts.AddOrUpdate(ip, 0, (_, v) => Math.Max(0, v - 1));
        }

        public int GetRegistrationTimeoutSeconds()
            => Math.Max(5, _options.CurrentValue.ConnectionGuard.RegistrationTimeoutSeconds);

        private sealed class SlidingWindowCounter
        {
            private readonly object _lock = new();
            private DateTimeOffset _windowStartUtc = DateTimeOffset.UtcNow;
            private int _count;

            public bool TryIncrement(int windowSeconds, int maxCount)
            {
                windowSeconds = Math.Max(1, windowSeconds);
                maxCount = Math.Max(1, maxCount);

                lock (_lock)
                {
                    var now = DateTimeOffset.UtcNow;
                    var window = TimeSpan.FromSeconds(windowSeconds);

                    if (now - _windowStartUtc > window)
                    {
                        _windowStartUtc = now;
                        _count = 0;
                    }

                    _count++;

                    return _count <= maxCount;
                }
            }
        }
    }
}
