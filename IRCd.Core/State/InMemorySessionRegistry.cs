namespace IRCd.Core.State
{
    using System.Collections.Concurrent;
    using System.Collections.Generic;

    using IRCd.Core.Abstractions;

    public sealed class InMemorySessionRegistry : ISessionRegistry
    {
        private readonly ConcurrentDictionary<string, IClientSession> _sessions = new();

        public void Add(IClientSession session)
            => _sessions[session.ConnectionId] = session;

        public void Remove(string connectionId)
            => _sessions.TryRemove(connectionId, out _);

        public bool TryGet(string connectionId, out IClientSession? session)
            => _sessions.TryGetValue(connectionId, out session);

        public IEnumerable<IClientSession> All()
            => _sessions.Values;
    }
}
