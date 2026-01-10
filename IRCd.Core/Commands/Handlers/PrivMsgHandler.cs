namespace IRCd.Core.Commands.Handlers
{
    using System.Linq;
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
        private readonly SilenceService _silence;
        private readonly IServiceCommandDispatcher? _services;
        private readonly IServiceChannelEvents? _channelEvents;
        private readonly IAuthState? _auth;
        private readonly BanMatcher _banMatcher;

        public PrivMsgHandler(
            RoutingService routing,
            ServerLinkService links,
            HostmaskService hostmask,
            IOptions<IrcOptions> options,
            SilenceService silence,
            IServiceCommandDispatcher? services = null,
            IServiceChannelEvents? channelEvents = null,
            IAuthState? auth = null,
            BanMatcher? banMatcher = null)
        {
            _routing = routing;
            _links = links;
            _hostmask = hostmask;
            _options = options;
            _silence = silence;
            _services = services;
            _channelEvents = channelEvents;
            _auth = auth;
            _banMatcher = banMatcher ?? BanMatcher.Shared;
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
            var host = state.GetHostFor(session.ConnectionId);
            var prefix = $":{fromNick}!{fromUser}@{host}";
            var fromHostmask = $"{fromNick}!{fromUser}@{host}";

            foreach (var t in targets)
            {
                if (!t.StartsWith('#')
                    && (!state.TryGetConnectionIdByNick(t, out var existingConn) || existingConn is null)
                    && _services is not null
                    && await _services.TryHandlePrivmsgAsync(session, t, text, state, ct))
                {
                    continue;
                }

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

                    var accountName = "*";
                    if (_auth is not null)
                    {
                        accountName = await _auth.GetIdentifiedAccountAsync(session.ConnectionId, ct) ?? "*";
                    }

                    var banInput = new ChannelBanMatchInput(fromNick, fromUser, host, accountName);
                    var isBanned = channel.Bans.Any(b => _banMatcher.IsChannelBanMatch(b.Mask, banInput));
                    if (isBanned)
                    {
                        var isExcepted = channel.ExceptBans.Any(e => _banMatcher.IsChannelExceptionMatch(e.Mask, banInput));
                        if (!isExcepted)
                        {
                            await session.SendAsync($":server 404 {fromNick} {t} :Cannot send to channel (+b)", ct);
                            continue;
                        }
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

                    if (session.EnabledCapabilities.Contains("echo-message"))
                    {
                        await session.SendAsync(line, ct);
                    }

                    if (_channelEvents is not null)
                    {
                        await _channelEvents.OnChannelMessageAsync(session, channel, text, state, ct);
                    }

                    if (state.TryGetUser(session.ConnectionId, out var fromU) && fromU is not null && !string.IsNullOrWhiteSpace(fromU.Uid))
                    {
                        await _links.PropagatePrivMsgAsync(fromU.Uid!, t, text, ct);
                    }

                    continue;
                }

                if (IsCtcpVersion(text) && state.TryGetConnectionIdByNick(t, out var ctcpConn) && ctcpConn is not null
                    && state.TryGetUser(ctcpConn, out var ctcpUser) && ctcpUser is not null && ctcpUser.IsService)
                {
                    var serverName = _options.Value.ServerInfo?.Name ?? "server";
                    var version = _options.Value.ServerInfo?.Version ?? "UnrealBG-IRCd";
                    var reply = $":{t}!services@{state.GetHostFor(ctcpConn)} NOTICE {fromNick} :\x01VERSION {serverName} services {version}\x01";
                    await session.SendAsync(reply, ct);
                    continue;
                }

                if (_services is not null)
                {
                    if (await _services.TryHandlePrivmsgAsync(session, t, text, state, ct))
                    {
                        continue;
                    }
                }

                if (!state.TryGetConnectionIdByNick(t, out var targetConn) || targetConn is null)
                {
                    await session.SendAsync($":server 401 {fromNick} {t} :No such nick", ct);
                    continue;
                }

                if (state.TryGetUser(targetConn, out var toU) && toU is not null && !toU.IsRemote && _silence.IsSilenced(targetConn, fromHostmask))
                {
                    continue;
                }

                if (state.TryGetUser(targetConn, out var targetUser) && targetUser is not null && !string.IsNullOrWhiteSpace(targetUser.AwayMessage))
                {
                    await session.SendAsync($":server 301 {fromNick} {targetUser.Nick} :{targetUser.AwayMessage}", ct);
                }

                var privLine = $"{prefix} PRIVMSG {t} :{text}";
                await _routing.SendToUserAsync(targetConn, privLine, ct);

                if (session.EnabledCapabilities.Contains("echo-message"))
                {
                    await session.SendAsync(privLine, ct);
                }

                if (state.TryGetUser(session.ConnectionId, out var fromU2) && fromU2 is not null && !string.IsNullOrWhiteSpace(fromU2.Uid))
                {
                    await _links.PropagatePrivMsgAsync(fromU2.Uid!, t, text, ct);
                }
            }
        }

        private static bool IsCtcpVersion(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            if (text.Length < 2 || text[0] != '\x01' || text[^1] != '\x01')
            {
                return false;
            }

            var inner = text[1..^1].Trim();
            return inner.Equals("VERSION", System.StringComparison.OrdinalIgnoreCase)
                || inner.StartsWith("VERSION ", System.StringComparison.OrdinalIgnoreCase);
        }
    }
}
