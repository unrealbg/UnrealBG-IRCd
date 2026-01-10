namespace IRCd.Core.Commands.Handlers
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Core.Abstractions;
    using IRCd.Core.Commands.Contracts;
    using IRCd.Core.Protocol;
    using IRCd.Core.Services;
    using IRCd.Core.State;

    public sealed class JoinHandler : IIrcCommandHandler
    {
        public string Command => "JOIN";

        private readonly RoutingService _routing;
        private readonly ServerLinkService _links;
        private readonly HostmaskService _hostmask;
        private readonly IMetrics _metrics;
        private readonly IServiceChannelEvents? _channelEvents;
        private readonly ISessionRegistry _sessions;
        private readonly IAuthState? _auth;
        private readonly BanMatcher _banMatcher;

        public JoinHandler(
            RoutingService routing,
            ServerLinkService links,
            HostmaskService hostmask,
            IMetrics metrics,
            ISessionRegistry sessions,
            IServiceChannelEvents? channelEvents = null,
            IAuthState? auth = null,
            BanMatcher? banMatcher = null)
        {
            _routing = routing;
            _links = links;
            _hostmask = hostmask;
            _metrics = metrics;
            _sessions = sessions;
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

            if (msg.Params.Count < 1)
            {
                await session.SendAsync($":server 461 {session.Nick} JOIN :Not enough parameters", ct);
                return;
            }

            var chanToken = msg.Params[0]?.Trim();
            if (string.IsNullOrWhiteSpace(chanToken))
            {
                await session.SendAsync($":server 461 {session.Nick} JOIN :Not enough parameters", ct);
                return;
            }

            if (chanToken.Equals("0", StringComparison.Ordinal))
            {
                var myChannels = state.GetUserChannels(session.ConnectionId);
                foreach (var chName in myChannels)
                {
                    var partMsg = new IrcMessage(null, "PART", new[] { chName }, null);

                    await new PartHandler(_routing, _links, _hostmask).HandleAsync(session, partMsg, state, ct);
                }

                return;
            }

            var keyToken = msg.Params.Count > 1 ? msg.Params[1]?.Trim() : null;

            var channels = chanToken.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var keys = string.IsNullOrWhiteSpace(keyToken)
                ? Array.Empty<string>()
                : keyToken.Split(',', StringSplitOptions.TrimEntries);

            for (int i = 0; i < channels.Length; i++)
            {
                var channelName = channels[i];
                var providedKey = i < keys.Length ? keys[i] : null;

                await JoinOneAsync(session, state, channelName, providedKey, ct);
            }
        }

        private async ValueTask JoinOneAsync(IClientSession session, ServerState state, string channelName, string? providedKey, CancellationToken ct)
        {
            var nick = session.Nick!;

            var existedBefore = state.TryGetChannel(channelName, out var _);

            if (!IrcValidation.IsValidChannel(channelName, out _))
            {
                await session.SendAsync($":server 479 {nick} {channelName} :Illegal channel name", ct);
                return;
            }

            if (state.TryGetChannel(channelName, out var existing) && existing is not null)
            {
                var maskUserName = session.UserName ?? "u";
                var host = state.GetHostFor(session.ConnectionId);
                var accountName = "*";
                if (_auth is not null)
                {
                    accountName = await _auth.GetIdentifiedAccountAsync(session.ConnectionId, ct) ?? "*";
                }

                var matchInput = new ChannelBanMatchInput(nick, maskUserName, host, accountName);

                var isBanned = false;
                foreach (var ban in existing.Bans)
                {
                    if (_banMatcher.IsChannelBanMatch(ban.Mask, matchInput))
                    {
                        isBanned = true;
                        break;
                    }
                }

                if (isBanned)
                {
                    var hasException = false;
                    foreach (var except in existing.ExceptBans)
                    {
                        if (_banMatcher.IsChannelExceptionMatch(except.Mask, matchInput))
                        {
                            hasException = true;
                            break;
                        }
                    }

                    if (!hasException)
                    {
                        await session.SendAsync($":server 474 {nick} {channelName} :Cannot join channel (+b)", ct);
                        return;
                    }
                }

                if (existing.Modes.HasFlag(ChannelModes.Limit) && existing.UserLimit.HasValue &&
                    existing.Members.Count >= existing.UserLimit.Value)
                {
                    await session.SendAsync($":server 471 {nick} {channelName} :Cannot join channel (+l)", ct);
                    return;
                }

                if (existing.Modes.HasFlag(ChannelModes.InviteOnly))
                {
                    var isInvited = existing.IsInvited(nick);
                    
                    if (!isInvited)
                    {
                        foreach (var inviteExcept in existing.InviteExceptions)
                        {
                            var maskValue = $"{nick}!{maskUserName}@{host}";
                            if (_banMatcher.IsWildcardMatch(inviteExcept.Mask, maskValue))
                            {
                                isInvited = true;
                                break;
                            }
                        }
                    }

                    if (!isInvited)
                    {
                        await session.SendAsync($":server 473 {nick} {channelName} :Cannot join channel (+i)", ct);
                        return;
                    }
                }

                if (existing.Modes.HasFlag(ChannelModes.Key) &&
                    !string.Equals(existing.Key, providedKey, StringComparison.Ordinal))
                {
                    await session.SendAsync($":server 475 {nick} {channelName} :Cannot join channel (+k)", ct);
                    return;
                }
            }

            if (!state.TryJoinChannel(session.ConnectionId, nick, channelName))
            {
                return;
            }

            if (!state.TryGetChannel(channelName, out var channel) || channel is null)
            {
                return;
            }

            if (!existedBefore)
            {
                _metrics.ChannelCreated();
            }

            channel.RemoveInvite(nick);

            var userName = session.UserName ?? "u";
            var host2 = state.GetHostFor(session.ConnectionId);
            var joiningAccountName = "*";
            if (_auth is not null)
            {
                joiningAccountName = await _auth.GetIdentifiedAccountAsync(session.ConnectionId, ct) ?? "*";
            }
            
            foreach (var member in channel.Members)
            {
                if (!_sessions.TryGet(member.ConnectionId, out var memberSession) || memberSession is null)
                    continue;

                if (memberSession.EnabledCapabilities.Contains("extended-join"))
                {
                    if (state.TryGetUser(session.ConnectionId, out var joiningUser) && joiningUser is not null)
                    {
                        var realName = joiningUser.RealName ?? "Unknown";
                        var extJoinLine = $":{nick}!{userName}@{host2} JOIN {channelName} {joiningAccountName} :{realName}";
                        await memberSession.SendAsync(extJoinLine, ct);
                    }
                    else
                    {
                        var extJoinLine = $":{nick}!{userName}@{host2} JOIN {channelName} {joiningAccountName} :Unknown";
                        await memberSession.SendAsync(extJoinLine, ct);
                    }
                }
                else
                {
                    var joinLine = $":{nick}!{userName}@{host2} JOIN :{channelName}";
                    await memberSession.SendAsync(joinLine, ct);
                }
            }

            var privilegeAfterServices = channel.GetPrivilege(session.ConnectionId);
            var privilegeChangedByServices = false;

            if (_channelEvents is not null)
            {
                var beforeModeString = channel.FormatModeString();
                var beforeKey = channel.Key;
                var beforeLimit = channel.UserLimit;
                var beforePrivilege = channel.GetPrivilege(session.ConnectionId);

                await _channelEvents.OnUserJoinedAsync(session, channel, state, ct);

                if (!channel.Contains(session.ConnectionId))
                {
                    return;
                }

                var afterModeString = channel.FormatModeString();
                if (!string.Equals(beforeModeString, afterModeString, StringComparison.Ordinal) ||
                    !string.Equals(beforeKey, channel.Key, StringComparison.Ordinal) ||
                    beforeLimit != channel.UserLimit)
                {
                    await _links.PropagateChannelModesAsync(channelName, channel.CreatedTs, afterModeString, ct);

                    var key = channel.Key ?? string.Empty;
                    var limit = channel.UserLimit?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
                    await _links.PropagateChannelMetaAsync(channelName, channel.CreatedTs, key, limit, ct);
                }

                privilegeAfterServices = channel.GetPrivilege(session.ConnectionId);
                privilegeChangedByServices = privilegeAfterServices != beforePrivilege;
            }

            if (state.TryGetUser(session.ConnectionId, out var u) && u is not null && !string.IsNullOrWhiteSpace(u.Uid))
            {
                await _links.PropagateJoinAsync(u.Uid!, channelName, ct);

                if (privilegeChangedByServices)
                {
                    await _links.PropagateMemberPrivilegeAsync(channelName, u.Uid!, privilegeAfterServices, ct);
                }
            }

            if (string.IsNullOrWhiteSpace(channel.Topic))
            {
                await session.SendAsync($":server 331 {nick} {channelName} :No topic is set", ct);
            }
            else
            {
                await session.SendAsync($":server 332 {nick} {channelName} :{channel.Topic}", ct);

                if (!string.IsNullOrWhiteSpace(channel.TopicSetBy) && channel.TopicSetAtUtc.HasValue)
                {
                    await session.SendAsync($":server 333 {nick} {channelName} {channel.TopicSetBy} {channel.TopicTs}", ct);
                }
            }

            await session.SendAsync($":server 329 {nick} {channelName} {channel.CreatedTs}", ct);

            await SendNamesAsync(session, channel, state, ct);
        }

        private static async ValueTask SendNamesAsync(IClientSession session, Channel channel, ServerState state, CancellationToken ct)
        {
            var me = session.Nick!;
            var channelName = channel.Name;

            var useMultiPrefix = session.EnabledCapabilities.Contains("multi-prefix");
            var useUserhostInNames = session.EnabledCapabilities.Contains("userhost-in-names");

            var names = channel.Members
                .OrderByDescending(m => m.Privilege)
                .ThenBy(m => m.Nick, StringComparer.OrdinalIgnoreCase)
                .Select(m =>
                {
                    string prefix;
                    if (useMultiPrefix)
                    {
                        prefix = m.Privilege.ToAllPrefixes();
                    }
                    else
                    {
                        var p = m.Privilege.ToPrefix();
                        prefix = p.HasValue ? p.Value.ToString() : string.Empty;
                    }
                    
                    if (useUserhostInNames)
                    {
                        var userName = "user";
                        var host = "host";
                        
                        if (state.TryGetUser(m.ConnectionId, out var memberUser) && memberUser is not null)
                        {
                            userName = memberUser.UserName ?? "user";
                            host = state.GetHostFor(m.ConnectionId);
                        }
                        
                        return $"{prefix}{m.Nick}!{userName}@{host}";
                    }
                    
                    return prefix + m.Nick;
                })
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToList();

            const int maxPayloadChars = 400;

            if (names.Count == 0)
            {
                await session.SendAsync($":server 353 {me} = {channelName} :", ct);
                await session.SendAsync($":server 366 {me} {channelName} :End of /NAMES list.", ct);
                return;
            }

            var current = new List<string>();
            var len = 0;

            foreach (var n in names)
            {
                if (current.Count == 0)
                {
                    current.Add(n);
                    len = n.Length;
                    continue;
                }

                if (len + 1 + n.Length > maxPayloadChars)
                {
                    await session.SendAsync($":server 353 {me} = {channelName} :{string.Join(' ', current)}", ct);
                    current.Clear();
                    current.Add(n);
                    len = n.Length;
                }
                else
                {
                    current.Add(n);
                    len += 1 + n.Length;
                }
            }

            if (current.Count > 0)
            {
                await session.SendAsync($":server 353 {me} = {channelName} :{string.Join(' ', current)}", ct);
            }

            await session.SendAsync($":server 366 {me} {channelName} :End of /NAMES list.", ct);
        }
    }
}
