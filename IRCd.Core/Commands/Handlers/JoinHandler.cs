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

    public sealed class JoinHandler : IIrcCommandHandler
    {
        public string Command => "JOIN";

        private readonly RoutingService _routing;

        public JoinHandler(RoutingService routing)
        {
            _routing = routing;
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

            var channelName = msg.Params[0];
            var providedKey = msg.Params.Count > 1 ? msg.Params[1] : null;

            if (!channelName.StartsWith('#'))
            {
                await session.SendAsync($":server 479 {session.Nick} {channelName} :Illegal channel name", ct);
                return;
            }

            if (state.TryGetChannel(channelName, out var existing) && existing is not null)
            {
                var maskUserName = session.UserName ?? "u";
                var host = "localhost";
                var maskValue = $"{session.Nick}!{maskUserName}@{host}";

                foreach (var ban in existing.Bans)
                {
                    if (MaskMatcher.IsMatch(ban.Mask, maskValue))
                    {
                        await session.SendAsync($":server 474 {session.Nick} {channelName} :Cannot join channel (+b)", ct);
                        return;
                    }
                }

                if (existing.Modes.HasFlag(ChannelModes.Limit) && existing.UserLimit.HasValue &&
                    existing.Members.Count >= existing.UserLimit.Value)
                {
                    await session.SendAsync($":server 471 {session.Nick} {channelName} :Cannot join channel (+l)", ct);
                    return;
                }

                if (existing.Modes.HasFlag(ChannelModes.InviteOnly) &&
                    !existing.IsInvited(session.Nick!))
                {
                    await session.SendAsync($":server 473 {session.Nick} {channelName} :Cannot join channel (+i)", ct);
                    return;
                }

                if (existing.Modes.HasFlag(ChannelModes.Key) &&
                    !string.Equals(existing.Key, providedKey))
                {
                    await session.SendAsync($":server 475 {session.Nick} {channelName} :Cannot join channel (+k)", ct);
                    return;
                }
            }

            var nick = session.Nick!;
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
            var joinLine = $":{nick}!{userName}@localhost JOIN {channelName}";

            await _routing.BroadcastToChannelAsync(channel, joinLine, excludeConnectionId: null, ct);

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

            var names = channel.Members
                .OrderByDescending(m => m.Privilege)
                .ThenBy(m => m.Nick, System.StringComparer.OrdinalIgnoreCase)
                .Select(m =>
                {
                    var p = m.Privilege.ToPrefix();
                    return p is null ? m.Nick : $"{p}{m.Nick}";
                });

            await session.SendAsync($":server 353 {me} = {channel.Name} :{string.Join(' ', names)}", ct);
            await session.SendAsync($":server 366 {me} {channel.Name} :End of /NAMES list.", ct);
        }
    }
}
