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

        public ValueTask BroadcastToChannelAsync(
            Channel channel,
            string line,
            string? excludeConnectionId,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            foreach (var member in channel.Members)
            {
                if (member.ConnectionId == excludeConnectionId)
                {
                    continue;
                }

                ct.ThrowIfCancellationRequested();

                if (_sessions.TryGet(member.ConnectionId, out var session) && session is not null)
                {
                    session.SendAsync(_formatter.FormatFor(session, line), ct);
                }
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask SendToUserAsync(
            string connectionId,
            string line,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            if (_sessions.TryGet(connectionId, out var session) && session is not null)
            {
                session.SendAsync(_formatter.FormatFor(session, line), ct);
            }

            return ValueTask.CompletedTask;
        }
    }
}