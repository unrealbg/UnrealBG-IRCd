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

    public sealed class TopicHandler : IIrcCommandHandler
    {
        public string Command => "TOPIC";

        private readonly RoutingService _routing;
        private readonly ServerLinkService _links;
        private readonly IServiceChannelEvents? _channelEvents;

        public TopicHandler(RoutingService routing, ServerLinkService links, IServiceChannelEvents? channelEvents = null)
        {
            _routing = routing;
            _links = links;
            _channelEvents = channelEvents;
        }

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

            var channelName = msg.Params[0]?.Trim();
            if (!IrcValidation.IsValidChannel(channelName, out _))
            {
                await session.SendAsync($":server 403 {session.Nick} {(string.IsNullOrWhiteSpace(channelName) ? "*" : channelName)} :No such channel", ct);
                return;
            }

            var channelNameNN = channelName!;

            if (!state.TryGetChannel(channelNameNN, out var channel) || channel is null)
            {
                await session.SendAsync($":server 403 {session.Nick} {channelNameNN} :No such channel", ct);
                return;
            }

            var me = session.Nick!;
            var isMember = channel.Contains(session.ConnectionId);

            if (channel.Modes.HasFlag(ChannelModes.Secret) && !isMember)
            {
                await session.SendAsync($":server 403 {me} {channelNameNN} :No such channel", ct);
                return;
            }

            if (msg.Params.Count == 1 && msg.Trailing is null)
            {
                if (string.IsNullOrWhiteSpace(channel.Topic))
                {
                    await session.SendAsync($":server 331 {me} {channelNameNN} :No topic is set", ct);
                }
                else
                {
                    await session.SendAsync($":server 332 {me} {channelNameNN} :{channel.Topic}", ct);

                    if (!string.IsNullOrWhiteSpace(channel.TopicSetBy) && channel.TopicSetAtUtc.HasValue)
                    {
                        await session.SendAsync(
                            $":server 333 {me} {channelNameNN} {channel.TopicSetBy} {channel.TopicTs}",
                            ct);
                    }
                }

                return;
            }

            if (!isMember)
            {
                await session.SendAsync($":server 442 {me} {channelNameNN} :You're not on that channel", ct);
                return;
            }

            if (channel.Modes.HasFlag(ChannelModes.TopicOpsOnly) && !channel.HasPrivilege(session.ConnectionId, ChannelPrivilege.Op))
            {
                await session.SendAsync($":server 482 {me} {channelNameNN} :You're not channel operator", ct);
                return;
            }

            var newTopic = msg.Params.Count > 1
                ? msg.Params[1]
                : msg.Trailing;

            newTopic = newTopic?.Trim();

            var setBy = $"{me}!{(session.UserName ?? "u")}@localhost";
            var beforeTopic = channel.Topic;
            channel.TrySetTopicWithTs(newTopic, setBy, ChannelTimestamps.NowTs());

            if (_channelEvents is not null)
            {
                await _channelEvents.OnChannelTopicChangedAsync(session, channel, beforeTopic, state, ct);
            }

            var finalSetBy = channel.TopicSetBy ?? setBy;
            var topicLine = $":{finalSetBy} TOPIC {channelNameNN} :{channel.Topic ?? string.Empty}";
            await _routing.BroadcastToChannelAsync(channel, topicLine, excludeConnectionId: null, ct);

            if (state.TryGetUser(session.ConnectionId, out var u) && u is not null && !string.IsNullOrWhiteSpace(u.Uid))
            {
                await _links.PropagateTopicAsync(u.Uid!, channelNameNN, channel.Topic, ct);
            }
        }
    }
}
