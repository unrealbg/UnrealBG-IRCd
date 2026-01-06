namespace IRCd.Core.Commands.Handlers
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Core.Abstractions;
    using IRCd.Core.Commands.Contracts;
    using IRCd.Core.Protocol;
    using IRCd.Core.Services;
    using IRCd.Core.State;
    using IRCd.Shared.Options;

    using Microsoft.Extensions.Options;

    public sealed class SvsnickHandler : IIrcCommandHandler
    {
        public string Command => "SVSNICK";

        private readonly IOptions<IrcOptions> _options;
        private readonly RoutingService _routing;
        private readonly ISessionRegistry _sessions;
        private readonly ServerLinkService _links;
        private readonly WhowasService _whowas;
        private readonly WatchService _watch;

        public SvsnickHandler(
            IOptions<IrcOptions> options,
            RoutingService routing,
            ISessionRegistry sessions,
            ServerLinkService links,
            WhowasService whowas,
            WatchService watch)
        {
            _options = options;
            _routing = routing;
            _sessions = sessions;
            _links = links;
            _whowas = whowas;
            _watch = watch;
        }

        public async ValueTask HandleAsync(IClientSession session, IrcMessage msg, ServerState state, CancellationToken ct)
        {
            var server = _options.Value.ServerInfo?.Name ?? "server";

            if (!session.IsRegistered)
            {
                await session.SendAsync($":{server} 451 {(session.Nick ?? "*")} :You have not registered", ct);
                return;
            }

            var me = session.Nick ?? "*";

            if (!state.TryGetUser(session.ConnectionId, out var oper) || oper is null || !OperCapabilityService.HasCapability(_options.Value, oper, "svsnick"))
            {
                await session.SendAsync($":{server} 481 {me} :Permission Denied- You're not an IRC operator", ct);
                return;
            }

            if (msg.Params.Count < 2)
            {
                await session.SendAsync($":{server} 461 {me} SVSNICK :Not enough parameters", ct);
                return;
            }

            var targetNick = (msg.Params[0] ?? string.Empty).Trim();
            var newNick = (msg.Params[1] ?? string.Empty).Trim();

            if (!IrcValidation.IsValidNick(targetNick, out _))
            {
                await session.SendAsync($":{server} 401 {me} {targetNick} :No such nick", ct);
                return;
            }

            if (!IrcValidation.IsValidNick(newNick, out _))
            {
                await session.SendAsync($":{server} 432 {me} {newNick} :Erroneous nickname", ct);
                return;
            }

            if (!state.TryGetConnectionIdByNick(targetNick, out var targetConn) || targetConn is null || !state.TryGetUser(targetConn, out var targetUser) || targetUser is null)
            {
                await session.SendAsync($":{server} 401 {me} {targetNick} :No such nick", ct);
                return;
            }

            if (string.Equals(targetNick, newNick, StringComparison.OrdinalIgnoreCase))
            {
                await session.SendAsync($":{server} NOTICE {me} :SVSNICK no-op", ct);
                return;
            }

            if (targetUser.IsRemote)
            {
                if (string.IsNullOrWhiteSpace(targetUser.Uid) || string.IsNullOrWhiteSpace(targetUser.RemoteSid))
                {
                    await session.SendAsync($":{server} NOTICE {me} :Cannot route SVSNICK", ct);
                    return;
                }

                var ok = await _links.SendSvsNickAsync(targetUser.RemoteSid!, targetUser.Uid!, newNick, ct);
                if (!ok)
                {
                    await session.SendAsync($":{server} NOTICE {me} :SVSNICK routing failed", ct);
                    return;
                }

                await session.SendAsync($":{server} NOTICE {me} :SVSNICK {targetNick} {newNick}", ct);
                return;
            }

            var oldNick = targetUser.Nick ?? targetNick;

            if (!state.TrySetNick(targetConn, newNick))
            {
                await session.SendAsync($":{server} 433 {me} {newNick} :Nickname is already in use", ct);
                return;
            }

            _whowas.Record(targetUser, explicitNick: oldNick);

            targetUser.Nick = newNick;
            targetUser.NickTs = ChannelTimestamps.NowTs();

            if (_sessions.TryGet(targetConn, out var targetSession) && targetSession is not null)
            {
                targetSession.Nick = newNick;
            }

            await _watch.NotifyNickChangeAsync(state, targetUser, oldNick, ct);

            var channels = state.UpdateNickInUserChannels(targetConn, newNick);

            var userName = targetUser.UserName ?? "u";
            var host = state.GetHostFor(targetConn);
            var nickLine = $":{oldNick}!{userName}@{host} NICK :{newNick}";

            foreach (var ch in channels)
            {
                await _routing.BroadcastToChannelAsync(ch, nickLine, excludeConnectionId: targetConn, ct);
            }

            await _routing.SendToUserAsync(targetConn, nickLine, ct);

            if (!string.IsNullOrWhiteSpace(targetUser.Uid))
            {
                await _links.PropagateSvsNickAsync(targetUser.Uid!, newNick, ct);
            }

            await session.SendAsync($":{server} NOTICE {me} :SVSNICK {oldNick} {newNick}", ct);
        }
    }
}
