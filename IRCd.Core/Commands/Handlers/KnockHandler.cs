namespace IRCd.Core.Commands.Handlers
{
    using System;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Core.Abstractions;
    using IRCd.Core.Commands.Contracts;
    using IRCd.Core.Protocol;
    using IRCd.Core.Services;
    using IRCd.Core.State;

    public sealed class KnockHandler : IIrcCommandHandler
    {
        public string Command => "KNOCK";

        private readonly RoutingService _routing;
        private readonly HostmaskService _hostmask;

        public KnockHandler(RoutingService routing, HostmaskService hostmask)
        {
            _routing = routing;
            _hostmask = hostmask;
        }

        public async ValueTask HandleAsync(IClientSession session, IrcMessage msg, ServerState state, CancellationToken ct)
        {
            if (!session.IsRegistered)
            {
                await session.SendAsync($":server 451 {(session.Nick ?? "*")} :You have not registered", ct);
                return;
            }

            var me = session.Nick!;

            if (msg.Params.Count < 1 || string.IsNullOrWhiteSpace(msg.Params[0]))
            {
                await session.SendAsync($":server 461 {me} KNOCK :Not enough parameters", ct);
                return;
            }

            var channelName = (msg.Params[0] ?? string.Empty).Trim();
            var text = msg.Trailing;
            if (string.IsNullOrWhiteSpace(text) && msg.Params.Count >= 2)
            {
                text = msg.Params[1];
            }

            if (!IrcValidation.IsValidChannel(channelName, out _))
            {
                await session.SendAsync($":server 403 {me} {channelName} :No such channel", ct);
                return;
            }

            if (!state.TryGetChannel(channelName, out var ch) || ch is null)
            {
                await session.SendAsync($":server 403 {me} {channelName} :No such channel", ct);
                return;
            }

            if (ch.Contains(session.ConnectionId))
            {
                await session.SendAsync($":server NOTICE {me} :You are already on {channelName}", ct);
                return;
            }

            if (!ch.Modes.HasFlag(ChannelModes.InviteOnly))
            {
                await session.SendAsync($":server 480 {me} {channelName} :Cannot knock on channel", ct);
                return;
            }

            var user = session.UserName ?? "u";
                var host = state.GetHostFor(session.ConnectionId);
            var line = $":{me}!{user}@{host} KNOCK {channelName} :{(text ?? string.Empty)}";

            foreach (var m in ch.Members.Where(m => m.Privilege.IsAtLeast(ChannelPrivilege.Op)).ToArray())
            {
                await _routing.SendToUserAsync(m.ConnectionId, line, ct);
            }

            await session.SendAsync($":server NOTICE {me} :KNOCK sent to {channelName}", ct);
        }
    }
}
