namespace IRCd.Core.Commands.Handlers
{
    using IRCd.Core.Abstractions;
    using IRCd.Core.Commands.Contracts;
    using IRCd.Core.Protocol;
    using IRCd.Core.Services;
    using IRCd.Core.State;

    public sealed class KickBanHandler : IIrcCommandHandler
    {
        public string Command => "KICKBAN";

        private readonly RoutingService _routing;

        public KickBanHandler(RoutingService routing)
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

            if (msg.Params.Count < 2)
            {
                await session.SendAsync($":server 461 {session.Nick} KICKBAN :Not enough parameters", ct);
                return;
            }

            var channelName = msg.Params[0];
            var targetNick = msg.Params[1];
            var reason = string.IsNullOrWhiteSpace(msg.Trailing) ? "Kicked" : msg.Trailing!;

            var opNick = session.Nick!;
            var opUser = session.UserName ?? "u";

            if (!channelName.StartsWith('#'))
            {
                await session.SendAsync($":server 403 {opNick} {channelName} :No such channel", ct);
                return;
            }

            if (!state.TryGetChannel(channelName, out var channel) || channel is null)
            {
                await session.SendAsync($":server 403 {opNick} {channelName} :No such channel", ct);
                return;
            }

            if (!channel.Contains(session.ConnectionId))
            {
                await session.SendAsync($":server 442 {opNick} {channelName} :You're not on that channel", ct);
                return;
            }

            if (!channel.HasPrivilege(session.ConnectionId, ChannelPrivilege.Op))
            {
                await session.SendAsync($":server 482 {opNick} {channelName} :You're not channel operator", ct);
                return;
            }

            if (!state.TryGetConnectionIdByNick(targetNick, out var targetConn) || targetConn is null)
            {
                await session.SendAsync($":server 401 {opNick} {targetNick} :No such nick", ct);
                return;
            }

            if (!channel.Contains(targetConn))
            {
                await session.SendAsync($":server 441 {opNick} {targetNick} {channelName} :They aren't on that channel", ct);
                return;
            }

            var banMask = $"{targetNick}!*@*";

            var banChanged = channel.AddBan(banMask, opNick);
            if (banChanged)
            {
                var modeLine = $":{opNick}!{opUser}@localhost MODE {channelName} +b {banMask}";
                await _routing.BroadcastToChannelAsync(channel, modeLine, excludeConnectionId: null, ct);
            }

            var kickLine = $":{opNick}!{opUser}@localhost KICK {channelName} {targetNick} :{reason}";

            if (!state.TryPartChannel(targetConn, channelName, out var updatedChannel) || updatedChannel is null)
            {
                await session.SendAsync($":server NOTICE * :KICKBAN failed unexpectedly", ct);
                return;
            }

            await _routing.BroadcastToChannelAsync(updatedChannel, kickLine, excludeConnectionId: null, ct);

            if (!updatedChannel.Contains(targetConn))
            {
                await _routing.SendToUserAsync(targetConn, kickLine, ct);
            }
        }
    }
}
