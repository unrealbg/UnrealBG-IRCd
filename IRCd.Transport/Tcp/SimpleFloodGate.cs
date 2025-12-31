namespace IRCd.Transport.Tcp
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Text;

    public sealed class SimpleFloodGate
    {
        private readonly ConcurrentDictionary<string, SlidingWindow> _windows = new();
        private readonly int _maxLines;
        private readonly TimeSpan _window;

        public SimpleFloodGate(int maxLines = 10, TimeSpan? window = null)
        {
            _maxLines = maxLines;
            _window = window ?? TimeSpan.FromSeconds(10);
        }

        public bool Allow(string connectionId)
        {
            var w = _windows.GetOrAdd(connectionId, _ => new SlidingWindow(_window));
            return w.Hit(_maxLines);
        }

        public void Remove(string connectionId) => _windows.TryRemove(connectionId, out _);

        private sealed class SlidingWindow
        {
            private readonly TimeSpan _window;
            private readonly Queue<DateTimeOffset> _hits = new();
            private readonly object _lock = new();

            public SlidingWindow(TimeSpan window) => _window = window;

            public bool Hit(int max)
            {
                lock (_lock)
                {
                    var now = DateTimeOffset.UtcNow;

                    while (_hits.Count > 0 && (now - _hits.Peek()) > _window)
                        _hits.Dequeue();

                    if (_hits.Count >= max)
                        return false;

                    _hits.Enqueue(now);
                    return true;
                }
            }
        }
    }
}
