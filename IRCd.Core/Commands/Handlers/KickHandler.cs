namespace IRCd.Core.Commands.Handlers
{
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Core.Abstractions;
    using IRCd.Core.Commands.Contracts;
    using IRCd.Core.Protocol;
    using IRCd.Core.Services;
    using IRCd.Core.State;

    public sealed class KickHandler : IIrcCommandHandler
    {
        public string Command => "KICK";

        private readonly RoutingService _routing;

        public KickHandler(RoutingService routing)
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
                await session.SendAsync($":server 461 {session.Nick} KICK :Not enough parameters", ct);
                return;
            }

            var channelName = msg.Params[0];
            var targetNick = msg.Params[1];
            var reason = string.IsNullOrWhiteSpace(msg.Trailing) ? targetNick : msg.Trailing!;

            var meNick = session.Nick!;
            var meUser = session.UserName ?? "u";

            if (!IrcValidation.IsValidChannel(channelName, out _))
            {
                await session.SendAsync($":server 403 {meNick} {channelName} :No such channel", ct);
                return;
            }

            if (!IrcValidation.IsValidNick(targetNick, out _))
            {
                await session.SendAsync($":server 401 {meNick} {targetNick} :No such nick", ct);
                return;
            }

            if (!state.TryGetChannel(channelName, out var channel) || channel is null)
            {
                await session.SendAsync($":server 403 {meNick} {channelName} :No such channel", ct);
                return;
            }

            if (!channel.Contains(session.ConnectionId))
            {
                await session.SendAsync($":server 442 {meNick} {channelName} :You're not on that channel", ct);
                return;
            }

            if (!channel.HasPrivilege(session.ConnectionId, ChannelPrivilege.Op))
            {
                await session.SendAsync($":server 482 {meNick} {channelName} :You're not channel operator", ct);
                return;
            }

            if (!state.TryGetConnectionIdByNick(targetNick, out var targetConn) || targetConn is null)
            {
                await session.SendAsync($":server 401 {meNick} {targetNick} :No such nick", ct);
                return;
            }

            if (state.TryGetUser(targetConn, out var targetUser) && targetUser is not null && targetUser.IsService)
            {
                await session.SendAsync($":server NOTICE {meNick} :Cannot KICK services", ct);
                return;
            }

            if (!channel.Contains(targetConn))
            {
                await session.SendAsync($":server 441 {meNick} {targetNick} {channelName} :They aren't on that channel", ct);
                return;
            }

            var line = $":{meNick}!{meUser}@localhost KICK {channelName} {targetNick} :{reason}";

            if (!state.TryPartChannel(targetConn, channelName, out var updatedChannel) || updatedChannel is null)
            {
                await session.SendAsync($":server NOTICE * :KICK failed unexpectedly", ct);
                return;
            }

            await _routing.BroadcastToChannelAsync(updatedChannel, line, excludeConnectionId: null, ct);

            if (!updatedChannel.Contains(targetConn))
            {
                await _routing.SendToUserAsync(targetConn, line, ct);
            }
        }
    }
}
