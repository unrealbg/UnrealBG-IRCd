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
        private readonly ServerLinkService _links;

        public PartHandler(RoutingService routing, ServerLinkService links, HostmaskService hostmask)
        {
            _routing = routing;
            _links = links;
        }

        public async ValueTask HandleAsync(IClientSession session, IrcMessage msg, ServerState state, CancellationToken ct)
        {
            if (!Commands.CommandGuards.EnsureRegistered(session, ct, out var err))
            {
                await err;
                return;
            }

            var channelName = msg.Params.Count > 0 ? msg.Params[0] : msg.Trailing;
            channelName = channelName?.Trim();

            if (string.IsNullOrWhiteSpace(channelName))
            {
                await session.SendAsync($":server 461 {session.Nick} PART :Not enough parameters", ct);
                return;
            }

            if (!IrcValidation.IsValidChannel(channelName, out _))
            {
                await session.SendAsync($":server 403 {session.Nick} {channelName} :No such channel", ct);
                return;
            }

            if (!state.TryPartChannel(session.ConnectionId, channelName, out var channel) || channel is null)
            {
                await session.SendAsync($":server 442 {session.Nick} {channelName} :You're not on that channel", ct);
                return;
            }

            var nick = session.Nick!;
            var host = state.GetHostFor(session.ConnectionId);
            var partLine = $":{nick}!{session.UserName ?? "u"}@{host} PART {channelName}";
            await _routing.BroadcastToChannelAsync(channel, partLine, excludeConnectionId: null, ct);

            if (state.TryGetUser(session.ConnectionId, out var u) && u is not null && !string.IsNullOrWhiteSpace(u.Uid))
            {
                await _links.PropagatePartAsync(u.Uid!, channelName, msg.Trailing ?? "", ct);
            }
        }
    }
}
