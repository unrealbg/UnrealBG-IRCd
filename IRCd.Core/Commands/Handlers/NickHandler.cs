namespace IRCd.Core.Commands.Handlers
{
    using IRCd.Core.Abstractions;
    using IRCd.Core.Commands.Contracts;
    using IRCd.Core.Protocol;
    using IRCd.Core.Services;
    using IRCd.Core.State;

    public sealed class NickHandler : IIrcCommandHandler
    {
        public string Command => "NICK";
        private readonly RoutingService _routing;
        private readonly RegistrationService _registration;
        private readonly ServerLinkService _links;
        private readonly Microsoft.Extensions.Options.IOptions<IRCd.Shared.Options.IrcOptions> _options;

        public NickHandler(RoutingService routing, RegistrationService registration, ServerLinkService links, Microsoft.Extensions.Options.IOptions<IRCd.Shared.Options.IrcOptions> options)
        {
            _routing = routing;
            _registration = registration;
            _links = links;
            _options = options;
        }

        public async ValueTask HandleAsync(IClientSession session, IrcMessage msg, ServerState state, CancellationToken ct)
        {
            var newNick = msg.Params.Count > 0 ? msg.Params[0] : msg.Trailing;
            newNick = newNick?.Trim();

            if (string.IsNullOrWhiteSpace(newNick))
            {
                await session.SendAsync(":server 431 * :No nickname given", ct);
                return;
            }

            if (newNick.Length > 20 || newNick.Contains(' ') || newNick.Contains(':'))
            {
                await session.SendAsync($":server 432 * {newNick} :Erroneous nickname", ct);
                return;
            }

            var oldNick = session.Nick;

            if (oldNick is not null && string.Equals(oldNick, newNick, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (!state.TrySetNick(session.ConnectionId, newNick))
            {
                await session.SendAsync($":server 433 * {newNick} :Nickname is already in use", ct);
                return;
            }

            session.Nick = newNick;
            await _registration.TryCompleteRegistrationAsync(session, state, ct);

            if (state.TryGetUser(session.ConnectionId, out var u) && u is not null && string.IsNullOrWhiteSpace(u.Uid))
            {
                var sid = _options.Value.ServerInfo?.Sid ?? "001";
                u.Uid = $"{sid}{session.ConnectionId[..Math.Min(6, session.ConnectionId.Length)].ToUpperInvariant()}";
                u.RemoteSid = sid;
                u.IsRemote = false;
            }

            if (string.IsNullOrWhiteSpace(oldNick))
            {
                return;
            }

            var channels = state.UpdateNickInUserChannels(session.ConnectionId, newNick);

            var user = session.UserName ?? "u";
            var nickLine = $":{oldNick}!{user}@localhost NICK :{newNick}";

            foreach (var ch in channels)
            {
                await _routing.BroadcastToChannelAsync(ch, nickLine, excludeConnectionId: session.ConnectionId, ct);
            }

            await session.SendAsync(nickLine, ct);

            if (state.TryGetUser(session.ConnectionId, out var meUser) && meUser is not null && !string.IsNullOrWhiteSpace(meUser.Uid))
            {
                meUser.NickTs = ChannelTimestamps.NowTs();
                await _links.PropagateNickAsync(meUser.Uid!, newNick, ct);
            }
        }
    }
}
