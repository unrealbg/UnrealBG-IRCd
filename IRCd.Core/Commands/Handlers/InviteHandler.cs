namespace IRCd.Core.Commands.Handlers
{
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Core.Abstractions;
    using IRCd.Core.Commands.Contracts;
    using IRCd.Core.Protocol;
    using IRCd.Core.Services;
    using IRCd.Core.State;

    public sealed class InviteHandler : IIrcCommandHandler
    {
        public string Command => "INVITE";

        private readonly RoutingService _routing;

        public InviteHandler(RoutingService routing)
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
                await session.SendAsync($":server 461 {session.Nick} INVITE :Not enough parameters", ct);
                return;
            }

            var targetNick = msg.Params[0];
            var channelName = msg.Params[1];

            if (!IrcValidation.IsValidNick(targetNick, out _))
            {
                await session.SendAsync($":server 401 {session.Nick} {targetNick} :No such nick", ct);
                return;
            }

            if (!IrcValidation.IsValidChannel(channelName, out _))
            {
                await session.SendAsync($":server 403 {session.Nick} {channelName} :No such channel", ct);
                return;
            }

            if (!state.TryGetChannel(channelName, out var ch) || ch is null)
            {
                await session.SendAsync($":server 403 {session.Nick} {channelName} :No such channel", ct);
                return;
            }

            if (!ch.Contains(session.ConnectionId))
            {
                await session.SendAsync($":server 442 {session.Nick} {channelName} :You're not on that channel", ct);
                return;
            }

            if (ch.Modes.HasFlag(ChannelModes.InviteOnly) && !ch.HasPrivilege(session.ConnectionId, ChannelPrivilege.Op))
            {
                await session.SendAsync($":server 482 {session.Nick} {channelName} :You're not channel operator", ct);
                return;
            }

            if (!state.TryGetConnectionIdByNick(targetNick, out var targetConn) || targetConn is null)
            {
                await session.SendAsync($":server 401 {session.Nick} {targetNick} :No such nick", ct);
                return;
            }

            ch.AddInvite(targetNick);

            await session.SendAsync($":server 341 {session.Nick} {targetNick} {channelName}", ct);

            var fromNick = session.Nick!;
            var fromUser = session.UserName ?? "u";
            var inviteLine = $":{fromNick}!{fromUser}@localhost INVITE {targetNick} :{channelName}";
            await _routing.SendToUserAsync(targetConn, inviteLine, ct);
        }
    }
}