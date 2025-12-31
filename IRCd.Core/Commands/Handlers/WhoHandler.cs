namespace IRCd.Core.Commands.Handlers
{
    using IRCd.Core.Abstractions;
    using IRCd.Core.Commands.Contracts;
    using IRCd.Core.Protocol;
    using IRCd.Core.State;

    public sealed class WhoHandler : IIrcCommandHandler
    {
        public string Command => "WHO";

        public async ValueTask HandleAsync(IClientSession session, IrcMessage msg, ServerState state, CancellationToken ct)
        {
            if (!session.IsRegistered)
            {
                await session.SendAsync($":server 451 {(session.Nick ?? "*")} :You have not registered", ct);
                return;
            }

            if (msg.Params.Count < 1)
            {
                await session.SendAsync($":server 461 {session.Nick} WHO :Not enough parameters", ct);
                return;
            }

            var target = msg.Params[0];

            if (target.StartsWith('#'))
            {
                await WhoChannel(session, state, target, ct);
                return;
            }

            await WhoNick(session, state, target, ct);
        }

        private static async ValueTask WhoChannel(IClientSession session, ServerState state, string channelName, CancellationToken ct)
        {
            if (!state.TryGetChannel(channelName, out var channel) || channel is null)
            {
                await session.SendAsync($":server 315 {session.Nick} {channelName} :End of /WHO list.", ct);
                return;
            }

            foreach (var m in channel.Members)
            {
                var nick = m.Nick;
                var user = state.TryGetConnectionIdByNick(nick, out var conn) && conn is not null
                    && state.TryGetUser(conn, out var u) && u is not null
                        ? (u.UserName ?? "u")
                        : "u";

                var flags = "H" + (m.Privilege.IsAtLeast(ChannelPrivilege.Op) ? "@" : "");
                var realName = state.TryGetConnectionIdByNick(nick, out conn) && conn is not null
                    && state.TryGetUser(conn, out var u2) && u2 is not null
                        ? (u2.RealName ?? "Unknown")
                        : "Unknown";

                await session.SendAsync($":server 352 {session.Nick} {channelName} {user} localhost server {nick} {flags} :0 {realName}", ct);
            }

            await session.SendAsync($":server 315 {session.Nick} {channelName} :End of /WHO list.", ct);
        }

        private static async ValueTask WhoNick(IClientSession session, ServerState state, string nick, CancellationToken ct)
        {
            if (!state.TryGetConnectionIdByNick(nick, out var conn) || conn is null)
            {
                await session.SendAsync($":server 315 {session.Nick} {nick} :End of /WHO list.", ct);
                return;
            }

            if (!state.TryGetUser(conn, out var user) || user is null)
            {
                await session.SendAsync($":server 315 {session.Nick} {nick} :End of /WHO list.", ct);
                return;
            }

            var channelName = state.GetUserChannels(conn).FirstOrDefault() ?? "*";
            var flags = "H";

            await session.SendAsync($":server 352 {session.Nick} {channelName} {user.UserName ?? "u"} localhost server {user.Nick ?? nick} {flags} :0 {user.RealName ?? "Unknown"}", ct);
            await session.SendAsync($":server 315 {session.Nick} {nick} :End of /WHO list.", ct);
        }
    }
}
