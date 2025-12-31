namespace IRCd.Core.Commands.Handlers
{
    using IRCd.Core.Abstractions;
    using IRCd.Core.Commands.Contracts;
    using IRCd.Core.Protocol;
    using IRCd.Core.Services;
    using IRCd.Core.State;

    public sealed class PartHandler : IIrcCommandHandler
    {
        public string Command => "PART";
        private readonly RoutingService _routing;

        public PartHandler(RoutingService routing) => _routing = routing;

        public async ValueTask HandleAsync(IClientSession session, IrcMessage msg, ServerState state, CancellationToken ct)
        {
            if (!Commands.CommandGuards.EnsureRegistered(session, ct, out var err)) { await err; return; }

            var channelName = msg.Params.Count > 0 ? msg.Params[0] : msg.Trailing;
            channelName = channelName?.Trim();

            if (string.IsNullOrWhiteSpace(channelName))
            {
                await session.SendAsync($":server 461 {session.Nick} PART :Not enough parameters", ct);
                return;
            }

            if (!state.TryPartChannel(session.ConnectionId, channelName, out var channel) || channel is null)
            {
                await session.SendAsync($":server 442 {session.Nick} {channelName} :You're not on that channel", ct);
                return;
            }

            var nick = session.Nick!;
            var partLine = $":{nick}!{session.UserName ?? "u"}@localhost PART {channelName}";
            await _routing.BroadcastToChannelAsync(channel, partLine, excludeConnectionId: null, ct);
        }
    }
}
