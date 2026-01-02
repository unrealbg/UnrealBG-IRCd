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

        public NickHandler(RoutingService routing, RegistrationService registration)
        {
            _routing = routing;
            _registration = registration;
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
        }
    }
}
