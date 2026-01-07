namespace IRCd.Core.Commands.Handlers
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Core.Abstractions;
    using IRCd.Core.Commands.Contracts;
    using IRCd.Core.Protocol;
    using IRCd.Core.Services;
    using IRCd.Core.State;
    using IRCd.Shared.Options;

    using Microsoft.Extensions.Options;

    public sealed class SvsjoinHandler : IIrcCommandHandler
    {
        public string Command => "SVSJOIN";

        private readonly IOptions<IrcOptions> _options;
        private readonly RoutingService _routing;
        private readonly ServerLinkService _links;

        public SvsjoinHandler(IOptions<IrcOptions> options, RoutingService routing, ServerLinkService links)
        {
            _options = options;
            _routing = routing;
            _links = links;
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

            if (!state.TryGetUser(session.ConnectionId, out var oper) || oper is null || !OperCapabilityService.HasCapability(_options.Value, oper, "svsjoin"))
            {
                await session.SendAsync($":{server} 481 {me} :Permission Denied- You're not an IRC operator", ct);
                return;
            }

            if (msg.Params.Count < 2)
            {
                await session.SendAsync($":{server} 461 {me} SVSJOIN :Not enough parameters", ct);
                return;
            }

            var targetNick = (msg.Params[0] ?? string.Empty).Trim();
            var channelName = (msg.Params[1] ?? string.Empty).Trim();

            if (!IrcValidation.IsValidNick(targetNick, out _))
            {
                await session.SendAsync($":{server} 401 {me} {targetNick} :No such nick", ct);
                return;
            }

            if (!IrcValidation.IsValidChannel(channelName, out _))
            {
                await session.SendAsync($":{server} 403 {me} {channelName} :No such channel", ct);
                return;
            }

            if (!state.TryGetConnectionIdByNick(targetNick, out var targetConn) || targetConn is null || !state.TryGetUser(targetConn, out var targetUser) || targetUser is null)
            {
                await session.SendAsync($":{server} 401 {me} {targetNick} :No such nick", ct);
                return;
            }

            if (targetUser.IsService)
            {
                await session.SendAsync($":{server} NOTICE {me} :Cannot SVSJOIN services", ct);
                return;
            }

            if (targetUser.IsRemote)
            {
                if (string.IsNullOrWhiteSpace(targetUser.Uid) || string.IsNullOrWhiteSpace(targetUser.RemoteSid))
                {
                    await session.SendAsync($":{server} NOTICE {me} :Cannot route SVSJOIN", ct);
                    return;
                }

                var ok = await _links.SendSvsJoinAsync(targetUser.RemoteSid!, targetUser.Uid!, channelName, ct);
                if (!ok)
                {
                    await session.SendAsync($":{server} NOTICE {me} :SVSJOIN routing failed", ct);
                    return;
                }

                await session.SendAsync($":{server} NOTICE {me} :SVSJOIN {targetNick} {channelName}", ct);
                return;
            }

            if (!state.TryJoinChannel(targetConn, targetNick, channelName))
            {
                await session.SendAsync($":{server} NOTICE {me} :SVSJOIN no-op", ct);
                return;
            }

            if (!state.TryGetChannel(channelName, out var channel) || channel is null)
            {
                await session.SendAsync($":{server} NOTICE {me} :SVSJOIN failed", ct);
                return;
            }

            var userName = targetUser.UserName ?? "u";
            var host = state.GetHostFor(targetConn);
            var joinLine = $":{targetNick}!{userName}@{host} JOIN :{channelName}";

            await _routing.BroadcastToChannelAsync(channel, joinLine, excludeConnectionId: null, ct);

            if (!string.IsNullOrWhiteSpace(targetUser.Uid))
            {
                await _links.PropagateSvsJoinAsync(targetUser.Uid!, channelName, ct);
            }

            await session.SendAsync($":{server} NOTICE {me} :SVSJOIN {targetNick} {channelName}", ct);
        }
    }
}
