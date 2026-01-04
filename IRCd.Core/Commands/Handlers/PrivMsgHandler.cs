namespace IRCd.Core.Commands.Handlers
{
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Core.Abstractions;
    using IRCd.Core.Commands.Contracts;
    using IRCd.Core.Protocol;
    using IRCd.Core.Services;
    using IRCd.Core.State;

    public sealed class PrivMsgHandler : IIrcCommandHandler
    {
        public string Command => "PRIVMSG";

        private readonly RoutingService _routing;
        private readonly ServerLinkService _links;

        public PrivMsgHandler(RoutingService routing, ServerLinkService links)
        {
            _routing = routing;
            _links = links;
        }

        public async ValueTask HandleAsync(IClientSession session, IrcMessage msg, ServerState state, CancellationToken ct)
        {
            if (!session.IsRegistered)
            {
                await session.SendAsync($":server 451 {(session.Nick ?? "*")} :You have not registered", ct);
                return;
            }

            if (msg.Params.Count < 1 || string.IsNullOrWhiteSpace(msg.Trailing))
            {
                await session.SendAsync($":server 461 {session.Nick} PRIVMSG :Not enough parameters", ct);
                return;
            }

            var target = msg.Params[0];
            var text = msg.Trailing!;

            var fromNick = session.Nick ?? "*";
            var fromUser = session.UserName ?? "u";
            var prefix = $":{fromNick}!{fromUser}@localhost";

            if (target.StartsWith('#'))
            {
                if (!state.TryGetChannel(target, out var channel) || channel is null)
                {
                    await session.SendAsync($":server 403 {fromNick} {target} :No such channel", ct);
                    return;
                }

                var isMember = channel.Contains(session.ConnectionId);

                if (channel.Modes.HasFlag(ChannelModes.NoExternalMessages) && !isMember)
                {
                    await session.SendAsync($":server 404 {fromNick} {target} :Cannot send to channel", ct);
                    return;
                }

                if (!isMember)
                {
                    await session.SendAsync($":server 442 {fromNick} {target} :You're not on that channel", ct);
                    return;
                }

                if (channel.Modes.HasFlag(ChannelModes.Moderated))
                {
                    var priv = channel.GetPrivilege(session.ConnectionId);
                    if (!priv.IsAtLeast(ChannelPrivilege.Voice))
                    {
                        await session.SendAsync($":server 404 {fromNick} {target} :Cannot send to channel (+m)", ct);
                        return;
                    }
                }

                var line = $"{prefix} PRIVMSG {target} :{text}";
                await _routing.BroadcastToChannelAsync(channel, line, excludeConnectionId: session.ConnectionId, ct);

                if (state.TryGetUser(session.ConnectionId, out var fromU) && fromU is not null && !string.IsNullOrWhiteSpace(fromU.Uid))
                {
                    await _links.PropagatePrivMsgAsync(fromU.Uid!, target, text, ct);
                }
                return;
            }

            if (!state.TryGetConnectionIdByNick(target, out var targetConn) || targetConn is null)
            {
                await session.SendAsync($":server 401 {fromNick} {target} :No such nick", ct);
                return;
            }

            var privLine = $"{prefix} PRIVMSG {target} :{text}";
            await _routing.SendToUserAsync(targetConn, privLine, ct);

            if (state.TryGetUser(session.ConnectionId, out var fromU2) && fromU2 is not null && !string.IsNullOrWhiteSpace(fromU2.Uid))
            {
                await _links.PropagatePrivMsgAsync(fromU2.Uid!, target, text, ct);
            }
        }
    }
}
