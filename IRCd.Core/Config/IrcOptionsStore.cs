namespace IRCd.Core.Config;

using System;
using System.Collections.Generic;
using System.Threading;

using IRCd.Shared.Options;

using Microsoft.Extensions.Options;

public sealed class IrcOptionsStore : IOptions<IrcOptions>, IOptionsMonitor<IrcOptions>
{
    private IrcOptions _current;

    private readonly object _lock = new();
    private readonly List<Action<IrcOptions, string>> _listeners = new();

    public IrcOptionsStore(IrcOptions initial)
    {
        _current = initial ?? throw new ArgumentNullException(nameof(initial));
    }

    public IrcOptions CurrentValue => Value;

    public IrcOptions Value => Volatile.Read(ref _current);

    public IrcOptions Get(string? name) => Value;

    public IDisposable OnChange(Action<IrcOptions, string> listener)
    {
        if (listener is null) throw new ArgumentNullException(nameof(listener));

        lock (_lock)
        {
            _listeners.Add(listener);
        }

        return new Unsubscriber(this, listener);
    }

    internal void Swap(IrcOptions next)
    {
        if (next is null) throw new ArgumentNullException(nameof(next));

        Interlocked.Exchange(ref _current, next);

        Action<IrcOptions, string>[] listeners;
        lock (_lock)
        {
            listeners = _listeners.ToArray();
        }

        foreach (var l in listeners)
        {
            try { l(next, Options.DefaultName); } catch { /* best effort */ }
        }
    }

    private sealed class Unsubscriber : IDisposable
    {
        private readonly IrcOptionsStore _owner;
        private readonly Action<IrcOptions, string> _listener;
        private int _disposed;

        public Unsubscriber(IrcOptionsStore owner, Action<IrcOptions, string> listener)
        {
            _owner = owner;
            _listener = listener;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            lock (_owner._lock)
            {
                _owner._listeners.Remove(_listener);
            }
        }
    }
}
