namespace IRCd.Core.Commands.Handlers
{
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Core.Abstractions;
    using IRCd.Core.Commands.Contracts;
    using IRCd.Core.Protocol;
    using IRCd.Core.Services;
    using IRCd.Core.State;

    using IRCd.Shared.Options;

    using Microsoft.Extensions.Options;

    public sealed class PrivMsgHandler : IIrcCommandHandler
    {
        public string Command => "PRIVMSG";

        private readonly RoutingService _routing;
        private readonly ServerLinkService _links;
        private readonly HostmaskService _hostmask;
        private readonly IOptions<IrcOptions> _options;

        public PrivMsgHandler(RoutingService routing, ServerLinkService links, HostmaskService hostmask, IOptions<IrcOptions> options)
        {
            _routing = routing;
            _links = links;
            _hostmask = hostmask;
            _options = options;
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

            var maxTargets = _options.Value.Limits?.MaxPrivmsgTargets > 0 ? _options.Value.Limits.MaxPrivmsgTargets : 4;
            var targets = target
                .Split(',', System.StringSplitOptions.RemoveEmptyEntries | System.StringSplitOptions.TrimEntries)
                .Take(maxTargets);

            var fromNick = session.Nick ?? "*";
            var fromUser = session.UserName ?? "u";
            var host = _hostmask.GetDisplayedHost((session.RemoteEndPoint as System.Net.IPEndPoint)?.Address);
            var prefix = $":{fromNick}!{fromUser}@{host}";

            foreach (var t in targets)
            {
                if (t.StartsWith('#') && !IrcValidation.IsValidChannel(t, out _))
                {
                    await session.SendAsync($":server 403 {fromNick} {t} :No such channel", ct);
                    continue;
                }

                if (!t.StartsWith('#') && !IrcValidation.IsValidNick(t, out _))
                {
                    await session.SendAsync($":server 401 {fromNick} {t} :No such nick", ct);
                    continue;
                }

                if (t.StartsWith('#'))
                {
                    if (!state.TryGetChannel(t, out var channel) || channel is null)
                    {
                        await session.SendAsync($":server 403 {fromNick} {t} :No such channel", ct);
                        continue;
                    }

                    var isMember = channel.Contains(session.ConnectionId);

                    if (channel.Modes.HasFlag(ChannelModes.NoExternalMessages) && !isMember)
                    {
                        await session.SendAsync($":server 404 {fromNick} {t} :Cannot send to channel", ct);
                        continue;
                    }

                    if (!isMember)
                    {
                        await session.SendAsync($":server 442 {fromNick} {t} :You're not on that channel", ct);
                        continue;
                    }

                    if (channel.Modes.HasFlag(ChannelModes.Moderated))
                    {
                        var priv = channel.GetPrivilege(session.ConnectionId);
                        if (!priv.IsAtLeast(ChannelPrivilege.Voice))
                        {
                            await session.SendAsync($":server 404 {fromNick} {t} :Cannot send to channel (+m)", ct);
                            continue;
                        }
                    }

                    var line = $"{prefix} PRIVMSG {t} :{text}";
                    await _routing.BroadcastToChannelAsync(channel, line, excludeConnectionId: session.ConnectionId, ct);

                    if (state.TryGetUser(session.ConnectionId, out var fromU) && fromU is not null && !string.IsNullOrWhiteSpace(fromU.Uid))
                    {
                        await _links.PropagatePrivMsgAsync(fromU.Uid!, t, text, ct);
                    }

                    continue;
                }

                if (!state.TryGetConnectionIdByNick(t, out var targetConn) || targetConn is null)
                {
                    await session.SendAsync($":server 401 {fromNick} {t} :No such nick", ct);
                    continue;
                }

                if (state.TryGetUser(targetConn, out var targetUser) && targetUser is not null && !string.IsNullOrWhiteSpace(targetUser.AwayMessage))
                {
                    await session.SendAsync($":server 301 {fromNick} {targetUser.Nick} :{targetUser.AwayMessage}", ct);
                }

                var privLine = $"{prefix} PRIVMSG {t} :{text}";
                await _routing.SendToUserAsync(targetConn, privLine, ct);

                if (state.TryGetUser(session.ConnectionId, out var fromU2) && fromU2 is not null && !string.IsNullOrWhiteSpace(fromU2.Uid))
                {
                    await _links.PropagatePrivMsgAsync(fromU2.Uid!, t, text, ct);
                }
            }
        }
    }
}
