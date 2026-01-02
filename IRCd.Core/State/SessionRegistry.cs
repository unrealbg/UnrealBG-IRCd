namespace IRCd.Core.State
{
    using System.Collections.Concurrent;
    using System.Collections.Generic;

    using IRCd.Core.Abstractions;

    public sealed class SessionRegistry
    {
        private readonly ConcurrentDictionary<string, IClientSession> _sessions = new();

        public bool TryGet(string connectionId, out IClientSession? session)
            => _sessions.TryGetValue(connectionId, out session);

        public bool TryAdd(IClientSession session)
            => _sessions.TryAdd(session.ConnectionId, session);

        public bool TryRemove(string connectionId, out IClientSession? session)
            => _sessions.TryRemove(connectionId, out session);

        public IEnumerable<IClientSession> All()
            => _sessions.Values;
    }
}
