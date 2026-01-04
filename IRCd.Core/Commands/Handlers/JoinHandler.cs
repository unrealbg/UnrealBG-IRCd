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

        public JoinHandler(RoutingService routing, ServerLinkService links, HostmaskService hostmask)
        {
            _routing = routing;
            _links = links;
            _hostmask = hostmask;
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

            if (!IrcValidation.IsValidChannel(channelName, out _))
            {
                await session.SendAsync($":server 479 {nick} {channelName} :Illegal channel name", ct);
                return;
            }

            if (state.TryGetChannel(channelName, out var existing) && existing is not null)
            {
                var maskUserName = session.UserName ?? "u";
                var host = _hostmask.GetDisplayedHost((session.RemoteEndPoint as System.Net.IPEndPoint)?.Address);
                var maskValue = $"{nick}!{maskUserName}@{host}";

                foreach (var ban in existing.Bans)
                {
                    if (MaskMatcher.IsMatch(ban.Mask, maskValue))
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

                if (existing.Modes.HasFlag(ChannelModes.InviteOnly) &&
                    !existing.IsInvited(nick))
                {
                    await session.SendAsync($":server 473 {nick} {channelName} :Cannot join channel (+i)", ct);
                    return;
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

            channel.RemoveInvite(nick);

            var userName = session.UserName ?? "u";
            var host2 = _hostmask.GetDisplayedHost((session.RemoteEndPoint as System.Net.IPEndPoint)?.Address);
            var joinLine = $":{nick}!{userName}@{host2} JOIN :{channelName}";
            await _routing.BroadcastToChannelAsync(channel, joinLine, excludeConnectionId: null, ct);

            if (state.TryGetUser(session.ConnectionId, out var u) && u is not null && !string.IsNullOrWhiteSpace(u.Uid))
            {
                await _links.PropagateJoinAsync(u.Uid!, channelName, ct);
            }

            if (string.IsNullOrWhiteSpace(channel.Topic))
            {
                await session.SendAsync($":server 331 {nick} {channelName} :No topic is set", ct);
            }
            else
            {
                await session.SendAsync($":server 332 {nick} {channelName} :{channel.Topic}", ct);
            }

            await SendNamesAsync(session, channel, ct);
        }

        private static async ValueTask SendNamesAsync(IClientSession session, Channel channel, CancellationToken ct)
        {
            var me = session.Nick!;
            var channelName = channel.Name;

            var names = channel.Members
                .OrderByDescending(m => m.Privilege)
                .ThenBy(m => m.Nick, StringComparer.OrdinalIgnoreCase)
                .Select(m =>
                {
                    var p = m.Privilege.ToPrefix();
                    return p is null ? m.Nick : $"{p}{m.Nick}";
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
