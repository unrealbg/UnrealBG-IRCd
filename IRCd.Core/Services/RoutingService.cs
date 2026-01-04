namespace IRCd.Core.Services
{
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Core.Abstractions;
    using IRCd.Core.State;

    public sealed class RoutingService
    {
        private readonly ISessionRegistry _sessions;
        private readonly IRCd.Core.Protocol.IrcFormatter _formatter;

        public RoutingService(ISessionRegistry sessions, IRCd.Core.Protocol.IrcFormatter formatter)
        {
            _sessions = sessions;
            _formatter = formatter;
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
                    await session.SendAsync(_formatter.FormatFor(session, line), ct);
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
                await session.SendAsync(_formatter.FormatFor(session, line), ct);
            }
        }
    }
}