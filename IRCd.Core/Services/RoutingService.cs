namespace IRCd.Core.Services
{
    using IRCd.Core.Abstractions;
    using IRCd.Core.State;

    public sealed class RoutingService
    {
        private readonly ISessionRegistry _sessions;

        public RoutingService(ISessionRegistry sessions)
        {
            _sessions = sessions;
        }

        public async ValueTask SendToUserAsync(string connectionId, string line, CancellationToken ct)
        {
            if (_sessions.TryGetSession(connectionId, out var s) && s is not null)
                await s.SendAsync(line, ct);
        }

        public async ValueTask BroadcastToChannelAsync(Channel channel, string line, string? excludeConnectionId, CancellationToken ct)
        {
            foreach (var member in channel.Members)
            {
                if (excludeConnectionId is not null && member.ConnectionId == excludeConnectionId)
                    continue;

                if (_sessions.TryGetSession(member.ConnectionId, out var s) && s is not null)
                    await s.SendAsync(line, ct);
            }
        }
    }
}
