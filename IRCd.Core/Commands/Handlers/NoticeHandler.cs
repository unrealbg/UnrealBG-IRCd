namespace IRCd.Core.Commands.Handlers
{
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Core.Abstractions;
    using IRCd.Core.Commands.Contracts;
    using IRCd.Core.Protocol;
    using IRCd.Core.Services;
    using IRCd.Core.State;

    public sealed class NoticeHandler : IIrcCommandHandler
    {
        public string Command => "NOTICE";

        private readonly RoutingService _routing;
        private readonly ServerLinkService _links;
        private readonly HostmaskService _hostmask;

        public NoticeHandler(RoutingService routing, ServerLinkService links, HostmaskService hostmask)
        {
            _routing = routing;
            _links = links;
            _hostmask = hostmask;
        }

        public async ValueTask HandleAsync(IClientSession session, IrcMessage msg, ServerState state, CancellationToken ct)
        {
            if (!session.IsRegistered)
            {
                return;
            }

            if (msg.Params.Count < 1 || string.IsNullOrWhiteSpace(msg.Trailing))
            {
                return;
            }

            var target = msg.Params[0];
            var text = msg.Trailing!;

            var fromNick = session.Nick ?? "*";
            var fromUser = session.UserName ?? "u";
            var host = _hostmask.GetDisplayedHost((session.RemoteEndPoint as System.Net.IPEndPoint)?.Address);
            var prefix = $":{fromNick}!{fromUser}@{host}";

            if (target.StartsWith('#'))
            {
                if (!state.TryGetChannel(target, out var channel) || channel is null)
                {
                    return;
                }

                var isMember = channel.Contains(session.ConnectionId);

                if (channel.Modes.HasFlag(ChannelModes.NoExternalMessages) && !isMember)
                {
                    return;
                }

                if (!isMember)
                {
                    return;
                }

                if (channel.Modes.HasFlag(ChannelModes.Moderated))
                {
                    var priv = channel.GetPrivilege(session.ConnectionId);
                    if (!priv.IsAtLeast(ChannelPrivilege.Voice))
                    {
                        return;
                    }
                }

                var line = $"{prefix} NOTICE {target} :{text}";
                await _routing.BroadcastToChannelAsync(channel, line, excludeConnectionId: session.ConnectionId, ct);

                if (state.TryGetUser(session.ConnectionId, out var fromU) && fromU is not null && !string.IsNullOrWhiteSpace(fromU.Uid))
                {
                    await _links.PropagateNoticeAsync(fromU.Uid!, target, text, ct);
                }

                return;
            }

            if (!state.TryGetConnectionIdByNick(target, out var targetConn) || targetConn is null)
            {
                return;
            }

            var noticeLine = $"{prefix} NOTICE {target} :{text}";
            await _routing.SendToUserAsync(targetConn, noticeLine, ct);

            if (state.TryGetUser(session.ConnectionId, out var fromU2) && fromU2 is not null && !string.IsNullOrWhiteSpace(fromU2.Uid))
            {
                await _links.PropagateNoticeAsync(fromU2.Uid!, target, text, ct);
            }
        }
    }
}
