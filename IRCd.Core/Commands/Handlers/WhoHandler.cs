namespace IRCd.Core.Commands.Handlers
{
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;

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

            if (msg.Params.Count < 1 || string.IsNullOrWhiteSpace(msg.Params[0]))
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
            var me = session.Nick!;

            if (!state.TryGetChannel(channelName, out var channel) || channel is null)
            {
                await session.SendAsync($":server 315 {me} {channelName} :End of /WHO list.", ct);
                return;
            }

            if (channel.Modes.HasFlag(ChannelModes.Secret) && !channel.Contains(session.ConnectionId))
            {
                await session.SendAsync($":server 403 {me} {channelName} :No such channel", ct);
                await session.SendAsync($":server 315 {me} {channelName} :End of /WHO list.", ct);
                return;
            }

            foreach (var m in channel.Members.OrderBy(x => x.Nick, System.StringComparer.OrdinalIgnoreCase))
            {
                if (!state.TryGetUser(m.ConnectionId, out var u) || u is null)
                {
                    continue;
                }

                var userName = u.UserName ?? "u";
                var nick = u.Nick ?? m.Nick;
                var realName = u.RealName ?? "Unknown";

                var pfx = m.Privilege.ToPrefix();
                var flags = pfx is null ? "H" : "H" + pfx;

                await session.SendAsync($":server 352 {me} {channelName} {userName} localhost server {nick} {flags} :0 {realName}", ct);
            }

            await session.SendAsync($":server 315 {me} {channelName} :End of /WHO list.", ct);
        }

        private static async ValueTask WhoNick(IClientSession session, ServerState state, string nick, CancellationToken ct)
        {
            var me = session.Nick!;

            if (!state.TryGetConnectionIdByNick(nick, out var conn) || conn is null)
            {
                await session.SendAsync($":server 315 {me} {nick} :End of /WHO list.", ct);
                return;
            }

            if (!state.TryGetUser(conn, out var user) || user is null)
            {
                await session.SendAsync($":server 315 {me} {nick} :End of /WHO list.", ct);
                return;
            }

            var channelName = state.GetUserChannels(conn)
                .FirstOrDefault(chName =>
                    state.TryGetChannel(chName, out var ch) && ch is not null &&
                    (!ch.Modes.HasFlag(ChannelModes.Secret) || ch.Contains(session.ConnectionId)))
                ?? "*";

            var flags = "H";

            await session.SendAsync(
                $":server 352 {me} {channelName} {user.UserName ?? "u"} localhost server {user.Nick ?? nick} {flags} :0 {user.RealName ?? "Unknown"}",
                ct);

            await session.SendAsync($":server 315 {me} {nick} :End of /WHO list.", ct);
        }
    }
}
