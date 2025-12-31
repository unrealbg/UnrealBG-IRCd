namespace IRCd.Core.Commands.Handlers
{
    using IRCd.Core.Abstractions;
    using IRCd.Core.Commands.Contracts;
    using IRCd.Core.Protocol;
    using IRCd.Core.Services;
    using IRCd.Core.State;

    public sealed class TopicHandler : IIrcCommandHandler
    {
        public string Command => "TOPIC";
        private readonly RoutingService _routing;

        public TopicHandler(RoutingService routing) => _routing = routing;

        public async ValueTask HandleAsync(IClientSession session, IrcMessage msg, ServerState state, CancellationToken ct)
        {
            if (!session.IsRegistered)
            {
                await session.SendAsync($":server 451 {(session.Nick ?? "*")} :You have not registered", ct);
                return;
            }

            if (msg.Params.Count < 1)
            {
                await session.SendAsync($":server 461 {session.Nick} TOPIC :Not enough parameters", ct);
                return;
            }

            var channelName = msg.Params[0];

            if (!state.TryGetChannel(channelName, out var channel) || channel is null)
            {
                await session.SendAsync($":server 403 {session.Nick} {channelName} :No such channel", ct);
                return;
            }

            if (msg.Params.Count == 1 && msg.Trailing is null)
            {
                if (string.IsNullOrWhiteSpace(channel.Topic))
                {
                    await session.SendAsync($":server 331 {session.Nick} {channelName} :No topic is set", ct);
                }
                else
                {
                    await session.SendAsync($":server 332 {session.Nick} {channelName} :{channel.Topic}", ct);
                }

                return;
            }

            if (!channel.Contains(session.ConnectionId))
            {
                await session.SendAsync($":server 442 {session.Nick} {channelName} :You're not on that channel", ct);
                return;
            }

            if (channel.Modes.HasFlag(ChannelModes.TopicOpsOnly) &&
                !channel.HasPrivilege(session.ConnectionId, ChannelPrivilege.Op))
            {
                await session.SendAsync($":server 482 {session.Nick} {channelName} :You're not channel operator", ct);
                return;
            }

            var newTopic = msg.Trailing ?? string.Empty;
            channel.Topic = string.IsNullOrEmpty(newTopic) ? null : newTopic;

            var nick = session.Nick!;
            var user = session.UserName ?? "u";
            var line = $":{nick}!{user}@localhost TOPIC {channelName} :{newTopic}";

            await _routing.BroadcastToChannelAsync(channel, line, excludeConnectionId: null, ct);
        }
    }
}
