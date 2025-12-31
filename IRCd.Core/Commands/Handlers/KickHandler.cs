namespace IRCd.Core.Commands.Handlers
{
    using IRCd.Core.Abstractions;
    using IRCd.Core.Commands.Contracts;
    using IRCd.Core.Protocol;
    using IRCd.Core.Services;
    using IRCd.Core.State;

    public sealed class KickHandler : IIrcCommandHandler
    {
        public string Command => "KICK";
        private readonly RoutingService _routing;

        public KickHandler(RoutingService routing) => _routing = routing;

        public async ValueTask HandleAsync(IClientSession session, IrcMessage msg, ServerState state, CancellationToken ct)
        {
            if (!session.IsRegistered)
            {
                await session.SendAsync($":server 451 {(session.Nick ?? "*")} :You have not registered", ct);
                return;
            }

            if (msg.Params.Count < 2)
            {
                await session.SendAsync($":server 461 {session.Nick} KICK :Not enough parameters", ct);
                return;
            }

            var channelName = msg.Params[0];
            var targetNick = msg.Params[1];
            var reason = msg.Trailing ?? targetNick;

            if (!state.TryGetChannel(channelName, out var channel) || channel is null)
            {
                await session.SendAsync($":server 403 {session.Nick} {channelName} :No such channel", ct);
                return;
            }

            if (!channel.Contains(session.ConnectionId))
            {
                await session.SendAsync($":server 442 {session.Nick} {channelName} :You're not on that channel", ct);
                return;
            }

            if (!channel.HasPrivilege(session.ConnectionId, ChannelPrivilege.Op))
            {
                await session.SendAsync($":server 482 {session.Nick} {channelName} :You're not channel operator", ct);
                return;
            }

            if (!state.TryGetConnectionIdByNick(targetNick, out var targetConn) || targetConn is null)
            {
                await session.SendAsync($":server 401 {session.Nick} {targetNick} :No such nick", ct);
                return;
            }

            if (!channel.Contains(targetConn))
            {
                await session.SendAsync($":server 441 {session.Nick} {targetNick} {channelName} :They aren't on that channel", ct);
                return;
            }

            if (!state.TryPartChannel(targetConn, channelName, out var updatedChannel) || updatedChannel is null)
            {
                await session.SendAsync($":server NOTICE * :KICK failed unexpectedly", ct);
                return;
            }

            var opNick = session.Nick!;
            var opUser = session.UserName ?? "u";
            var line = $":{opNick}!{opUser}@localhost KICK {channelName} {targetNick} :{reason}";

            await _routing.BroadcastToChannelAsync(updatedChannel, line, excludeConnectionId: null, ct);
            await _routing.SendToUserAsync(targetConn, line, ct);
        }
    }
}
