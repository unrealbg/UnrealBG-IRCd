namespace IRCd.Transport
{
    using System;
    using System.Collections.Concurrent;
    using System.Net;

    public sealed class LogRateLimiter
    {
        private readonly int _windowSeconds;
        private readonly int _maxEventsPerWindow;

        private readonly ConcurrentDictionary<IPAddress, Window> _windows = new();

        public LogRateLimiter(int windowSeconds, int maxEventsPerWindow)
        {
            _windowSeconds = Math.Max(1, windowSeconds);
            _maxEventsPerWindow = Math.Max(1, maxEventsPerWindow);
        }

        public bool ShouldLog(IPAddress ip)
        {
            var w = _windows.GetOrAdd(ip, _ => new Window());
            return w.Hit(_windowSeconds, _maxEventsPerWindow);
        }

        private sealed class Window
        {
            private readonly object _lock = new();
            private DateTimeOffset _windowStartUtc = DateTimeOffset.UtcNow;
            private int _count;

            public bool Hit(int windowSeconds, int maxEvents)
            {
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
                    return _count <= maxEvents;
                }
            }
        }
    }
}
