namespace IRCd.Transport.Tcp
{
    using IRCd.Core.Abstractions;

    using System.Collections.Concurrent;
    using System.Collections.Generic;

    public sealed class InMemorySessionRegistry : ISessionRegistry
    {
        private readonly ConcurrentDictionary<string, IClientSession> _sessions = new();

        public bool TryAdd(IClientSession session) => _sessions.TryAdd(session.ConnectionId, session);

        public bool TryRemove(string connectionId, out IClientSession? session)
            => _sessions.TryRemove(connectionId, out session);

        public bool TryGetSession(string connectionId, out IClientSession? session)
            => _sessions.TryGetValue(connectionId, out session);

        public IReadOnlyCollection<IClientSession> GetAll() => _sessions.Values.ToArray();
    }
}
