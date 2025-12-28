namespace IRCd.Core.Commands.Handlers
{
    using IRCd.Core.Abstractions;
    using IRCd.Core.Commands.Contracts;
    using IRCd.Core.Protocol;
    using IRCd.Core.State;

    public sealed class NickHandler : IIrcCommandHandler
    {
        public string Command => "NICK";

        public async ValueTask HandleAsync(IClientSession session, IrcMessage msg, ServerState state, CancellationToken ct)
        {
            var nick = msg.Params.Count > 0 ? msg.Params[0] : msg.Trailing;
            nick = nick?.Trim();

            if (string.IsNullOrWhiteSpace(nick))
            {
                await session.SendAsync($":server 431 * :No nickname given", ct);
                return;
            }

            if (nick.Length > 20)
            {
                await session.SendAsync($":server 432 * {nick} :Erroneous nickname", ct);
                return;
            }

            if (!state.TrySetNick(session.ConnectionId, nick))
            {
                await session.SendAsync($":server 433 * {nick} :Nickname is already in use", ct);
                return;
            }

            session.Nick = nick;

            await session.SendAsync($":server NOTICE * :NICK set to {nick}", ct);
        }
    }
}
