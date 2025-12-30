namespace IRCd.Core.Commands.Handlers
{
    using IRCd.Core.Abstractions;
    using IRCd.Core.Commands.Contracts;
    using IRCd.Core.Protocol;
    using IRCd.Core.Services;
    using IRCd.Core.State;

    public sealed class QuitHandler : IIrcCommandHandler
    {
        public string Command => "QUIT";
        private readonly RoutingService _routing;

        public QuitHandler(RoutingService routing) => _routing = routing;

        public async ValueTask HandleAsync(IClientSession session, IrcMessage msg, ServerState state, CancellationToken ct)
        {
            var nick = session.Nick ?? "*";
            var reason = msg.Trailing ?? "Client Quit";

            var channels = state.GetUserChannels(session.ConnectionId);
            foreach (var chName in channels)
            {
                if (state.TryGetChannel(chName, out var ch) && ch is not null)
                {
                    var quitLine = $":{nick}!{session.UserName ?? "u"}@localhost QUIT :{reason}";
                    await _routing.BroadcastToChannelAsync(ch, quitLine, excludeConnectionId: session.ConnectionId, ct);
                }
            }

            state.RemoveUser(session.ConnectionId);

            await session.CloseAsync(reason, ct);
        }
    }
}
