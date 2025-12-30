namespace IRCd.Core.Commands.Handlers
{
    using IRCd.Core.Abstractions;
    using IRCd.Core.Commands.Contracts;
    using IRCd.Core.Protocol;
    using IRCd.Core.Services;
    using IRCd.Core.State;

    public sealed class JoinHandler : IIrcCommandHandler
    {
        public string Command => "JOIN";
        private readonly RoutingService _routing;

        public JoinHandler(RoutingService routing) => _routing = routing;

        public async ValueTask HandleAsync(IClientSession session, IrcMessage msg, ServerState state, CancellationToken ct)
        {
            if (!Commands.CommandGuards.EnsureRegistered(session, ct, out var err)) { await err; return; }

            var channelName = msg.Params.Count > 0 ? msg.Params[0] : msg.Trailing;
            channelName = channelName?.Trim();

            if (string.IsNullOrWhiteSpace(channelName) || !channelName.StartsWith('#'))
            {
                await session.SendAsync($":server 479 {session.Nick} {channelName ?? "*"} :Illegal channel name", ct);
                return;
            }

            var nick = session.Nick!;
            var joined = state.TryJoinChannel(session.ConnectionId, nick, channelName);
            var channel = state.GetOrCreateChannel(channelName);

            if (!joined)
            {
                return;
            }

            var joinLine = $":{nick}!{session.UserName ?? "u"}@localhost JOIN {channelName}";
            await _routing.BroadcastToChannelAsync(channel, joinLine, excludeConnectionId: null, ct);

            var names = string.Join(' ', channel.Members.Select(m => m.Nick));
            await session.SendAsync($":server 353 {nick} = {channelName} :{names}", ct);
            await session.SendAsync($":server 366 {nick} {channelName} :End of /NAMES list.", ct);
        }
    }
}
