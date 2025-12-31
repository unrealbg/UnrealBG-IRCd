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

            if (!channelName.StartsWith('#'))
            {
                await session.SendAsync($":server 479 {session.Nick} {channelName} :Illegal channel name", ct);
                return;
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

            var user = session.UserName ?? "u";
            var joinLine = $":{nick}!{user}@localhost JOIN {channelName}";

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

            var names = channel.Members.Select(m =>
            {
                var p = m.Privilege.ToPrefix();
                return p is null ? m.Nick : $"{p}{m.Nick}";
            });

            var line = string.Join(' ', names);

            await session.SendAsync($":server 353 {me} = {channel.Name} :{line}", ct);
            await session.SendAsync($":server 366 {me} {channel.Name} :End of /NAMES list.", ct);
        }
    }
}
