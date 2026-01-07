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

    public sealed class NoticeHandler : IIrcCommandHandler
    {
        public string Command => "NOTICE";

        private readonly RoutingService _routing;
        private readonly ServerLinkService _links;
        private readonly HostmaskService _hostmask;
        private readonly IOptions<IrcOptions> _options;
        private readonly SilenceService _silence;
        private readonly IServiceCommandDispatcher? _services;

        public NoticeHandler(RoutingService routing, ServerLinkService links, HostmaskService hostmask, IOptions<IrcOptions> options, SilenceService silence, IServiceCommandDispatcher? services = null)
        {
            _routing = routing;
            _links = links;
            _hostmask = hostmask;
            _options = options;
            _silence = silence;
            _services = services;
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

            var maxTargets = _options.Value.Limits?.MaxNoticeTargets > 0 ? _options.Value.Limits.MaxNoticeTargets : 4;
            var targets = target
                .Split(',', System.StringSplitOptions.RemoveEmptyEntries | System.StringSplitOptions.TrimEntries)
                .Take(maxTargets);

            var fromNick = session.Nick ?? "*";
            var fromUser = session.UserName ?? "u";
            var host = state.GetHostFor(session.ConnectionId);
            var prefix = $":{fromNick}!{fromUser}@{host}";
            var fromHostmask = $"{fromNick}!{fromUser}@{host}";

            foreach (var t in targets)
            {
                if (t.StartsWith('#') && !IrcValidation.IsValidChannel(t, out _))
                {
                    continue;
                }

                if (!t.StartsWith('#') && !IrcValidation.IsValidNick(t, out _))
                {
                    continue;
                }

                if (t.StartsWith('#'))
                {
                    if (!state.TryGetChannel(t, out var channel) || channel is null)
                    {
                        continue;
                    }

                    var isMember = channel.Contains(session.ConnectionId);

                    if (channel.Modes.HasFlag(ChannelModes.NoExternalMessages) && !isMember)
                    {
                        continue;
                    }

                    if (!isMember)
                    {
                        continue;
                    }

                    if (channel.Modes.HasFlag(ChannelModes.Moderated))
                    {
                        var priv = channel.GetPrivilege(session.ConnectionId);
                        if (!priv.IsAtLeast(ChannelPrivilege.Voice))
                        {
                            continue;
                        }
                    }

                    var line = $"{prefix} NOTICE {t} :{text}";
                    await _routing.BroadcastToChannelAsync(channel, line, excludeConnectionId: session.ConnectionId, ct);

                    if (session.EnabledCapabilities.Contains("echo-message"))
                    {
                        await session.SendAsync(line, ct);
                    }

                    if (state.TryGetUser(session.ConnectionId, out var fromU) && fromU is not null && !string.IsNullOrWhiteSpace(fromU.Uid))
                    {
                        await _links.PropagateNoticeAsync(fromU.Uid!, t, text, ct);
                    }

                    continue;
                }

                if (_services is not null)
                {
                    if (await _services.TryHandleNoticeAsync(session, t, text, state, ct))
                    {
                        continue;
                    }
                }

                if (!state.TryGetConnectionIdByNick(t, out var targetConn) || targetConn is null)
                {
                    continue;
                }

                if (state.TryGetUser(targetConn, out var toU) && toU is not null && !toU.IsRemote && _silence.IsSilenced(targetConn, fromHostmask))
                {
                    continue;
                }

                var noticeLine = $"{prefix} NOTICE {t} :{text}";
                await _routing.SendToUserAsync(targetConn, noticeLine, ct);

                if (session.EnabledCapabilities.Contains("echo-message"))
                {
                    await session.SendAsync(noticeLine, ct);
                }

                if (state.TryGetUser(session.ConnectionId, out var fromU2) && fromU2 is not null && !string.IsNullOrWhiteSpace(fromU2.Uid))
                {
                    await _links.PropagateNoticeAsync(fromU2.Uid!, t, text, ct);
                }
            }
        }
    }
}
