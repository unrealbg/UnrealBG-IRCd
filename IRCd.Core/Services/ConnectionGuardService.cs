namespace IRCd.Core.Services
{
    using System;
    using System.Collections.Concurrent;
    using System.Net;

    using IRCd.Core.Abstractions;
    using IRCd.Shared.Options;

    using Microsoft.Extensions.Options;

    public sealed class ConnectionGuardService
    {
        private readonly IOptionsMonitor<IrcOptions> _options;

        private readonly IServerClock _clock;

        private readonly ConcurrentDictionary<IPAddress, SlidingWindowCounter> _plainConnWindows = new();

        private readonly ConcurrentDictionary<IPAddress, SlidingWindowCounter> _tlsConnWindows = new();

        private readonly ConcurrentDictionary<IPAddress, SlidingWindowCounter> _tlsHandshakeWindows = new();

        private readonly ConcurrentDictionary<IPAddress, int> _unregisteredCounts = new();

        private readonly ConcurrentDictionary<IPAddress, int> _activeCounts = new();

        private int _globalActiveCount;

        public ConnectionGuardService(IOptionsMonitor<IrcOptions> options, IServerClock clock)
        {
            _options = options;
            _clock = clock;
        }

        public bool Enabled => _options.CurrentValue.ConnectionGuard.Enabled;

        public bool TryAcceptNewConnection(IPAddress ip, out string rejectReason)
            => TryAcceptNewConnection(ip, secure: false, out rejectReason);

        public bool TryAcceptNewConnection(IPAddress ip, bool secure, out string rejectReason)
        {
            rejectReason = string.Empty;

            var cfg = _options.CurrentValue.ConnectionGuard;
            if (!cfg.Enabled)
            {
                return true;
            }

            var maxPerIp = secure ? cfg.MaxConnectionsPerWindowPerIpTls : cfg.MaxConnectionsPerWindowPerIp;
            var windows = secure ? _tlsConnWindows : _plainConnWindows;

            var counter = windows.GetOrAdd(ip, _ => new SlidingWindowCounter(_clock));
            if (!counter.TryIncrement(cfg.WindowSeconds, maxPerIp))
            {
                rejectReason = cfg.RejectMessage;
                return false;
            }

            var global = System.Threading.Interlocked.Increment(ref _globalActiveCount);
            if (cfg.GlobalMaxActiveConnections > 0 && global > cfg.GlobalMaxActiveConnections)
            {
                DecrementNonNegative(ref _globalActiveCount);
                rejectReason = cfg.RejectMessage;
                return false;
            }

            var active = _activeCounts.AddOrUpdate(ip, 1, (_, v) => v + 1);
            if (active > cfg.MaxActiveConnectionsPerIp)
            {
                _activeCounts.AddOrUpdate(ip, 0, (_, v) => Math.Max(0, v - 1));
                DecrementNonNegative(ref _globalActiveCount);
                rejectReason = cfg.RejectMessage;
                return false;
            }

            var current = _unregisteredCounts.AddOrUpdate(ip, 1, (_, v) => v + 1);
            if (current > cfg.MaxUnregisteredPerIp)
            {
                _unregisteredCounts.AddOrUpdate(ip, 0, (_, v) => Math.Max(0, v - 1));
                _activeCounts.AddOrUpdate(ip, 0, (_, v) => Math.Max(0, v - 1));
                DecrementNonNegative(ref _globalActiveCount);

                rejectReason = cfg.RejectMessage;
                return false;
            }

            return true;
        }

        public bool TryStartTlsHandshake(IPAddress ip, out string rejectReason)
        {
            rejectReason = string.Empty;

            var cfg = _options.CurrentValue.ConnectionGuard;
            if (!cfg.Enabled)
            {
                return true;
            }

            var counter = _tlsHandshakeWindows.GetOrAdd(ip, _ => new SlidingWindowCounter(_clock));
            if (!counter.TryIncrement(cfg.WindowSeconds, cfg.MaxTlsHandshakesPerWindowPerIp))
            {
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
            _unregisteredCounts.AddOrUpdate(ip, 0, (_, v) => Math.Max(0, v - 1));
        }

        /// <summary>
        /// Call when a session closes/disconnects (registered or unregistered).
        /// </summary>
        public void ReleaseActive(IPAddress ip)
        {
            _activeCounts.AddOrUpdate(ip, 0, (_, v) => Math.Max(0, v - 1));

            DecrementNonNegative(ref _globalActiveCount);
        }

        /// <summary>
        /// Call when a session closes/disconnects (only if it was still unregistered).
        /// </summary>
        public void ReleaseUnregistered(IPAddress ip)
        {
            _unregisteredCounts.AddOrUpdate(ip, 0, (_, v) => Math.Max(0, v - 1));
        }

        public int GetRegistrationTimeoutSeconds()
            => Math.Max(5, _options.CurrentValue.ConnectionGuard.RegistrationTimeoutSeconds);

        public int GetTlsHandshakeTimeoutSeconds()
            => Math.Max(1, _options.CurrentValue.ConnectionGuard.TlsHandshakeTimeoutSeconds);

        private static void DecrementNonNegative(ref int value)
        {
            while (true)
            {
                var current = Volatile.Read(ref value);
                if (current <= 0)
                {
                    return;
                }

                if (Interlocked.CompareExchange(ref value, current - 1, current) == current)
                {
                    return;
                }
            }
        }

        private sealed class SlidingWindowCounter
        {
            private readonly object _lock = new();
            private readonly IServerClock _clock;
            private DateTimeOffset _windowStartUtc;
            private int _count;

            public SlidingWindowCounter(IServerClock clock)
            {
                _clock = clock;
                _windowStartUtc = clock.UtcNow;
            }

            public bool TryIncrement(int windowSeconds, int maxCount)
            {
                windowSeconds = Math.Max(1, windowSeconds);
                maxCount = Math.Max(1, maxCount);

                lock (_lock)
                {
                    var now = _clock.UtcNow;
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
