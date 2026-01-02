namespace IRCd.Core.Services
{
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Core.Abstractions;
    using IRCd.Core.State;

    public sealed class RoutingService
    {
        private readonly ISessionRegistry _sessions;

        public RoutingService(ISessionRegistry sessions)
        {
            _sessions = sessions;
        }

        public async ValueTask BroadcastToChannelAsync(
            Channel channel,
            string line,
            string? excludeConnectionId,
            CancellationToken ct)
        {
            foreach (var member in channel.Members)
            {
                if (member.ConnectionId == excludeConnectionId)
                {
                    continue;
                }

                if (_sessions.TryGet(member.ConnectionId, out var session) && session is not null)
                {
                    await session.SendAsync(line, ct);
                }
            }
        }

        public async ValueTask SendToUserAsync(
            string connectionId,
            string line,
            CancellationToken ct)
        {
            if (_sessions.TryGet(connectionId, out var session) && session is not null)
            {
                await session.SendAsync(line, ct);
            }
        }
    }
}