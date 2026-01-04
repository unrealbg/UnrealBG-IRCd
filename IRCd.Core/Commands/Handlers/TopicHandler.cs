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

        public TopicHandler(RoutingService routing, ServerLinkService links)
        {
            _routing = routing;
            _links = links;
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
            if (string.IsNullOrWhiteSpace(channelName) || !channelName.StartsWith("#", StringComparison.Ordinal))
            {
                await session.SendAsync($":server 403 {session.Nick} {channelName ?? "*"} :No such channel", ct);
                return;
            }

            if (!state.TryGetChannel(channelName, out var channel) || channel is null)
            {
                await session.SendAsync($":server 403 {session.Nick} {channelName} :No such channel", ct);
                return;
            }

            var me = session.Nick!;
            var isMember = channel.Contains(session.ConnectionId);

            if (channel.Modes.HasFlag(ChannelModes.Secret) && !isMember)
            {
                await session.SendAsync($":server 403 {me} {channelName} :No such channel", ct);
                return;
            }

            if (msg.Params.Count == 1 && msg.Trailing is null)
            {
                if (string.IsNullOrWhiteSpace(channel.Topic))
                {
                    await session.SendAsync($":server 331 {me} {channelName} :No topic is set", ct);
                }
                else
                {
                    await session.SendAsync($":server 332 {me} {channelName} :{channel.Topic}", ct);

                    if (!string.IsNullOrWhiteSpace(channel.TopicSetBy) && channel.TopicSetAtUtc.HasValue)
                    {
                        await session.SendAsync(
                            $":server 333 {me} {channelName} {channel.TopicSetBy} {channel.TopicTs}",
                            ct);
                    }
                }

                return;
            }

            if (!isMember)
            {
                await session.SendAsync($":server 442 {me} {channelName} :You're not on that channel", ct);
                return;
            }

            if (channel.Modes.HasFlag(ChannelModes.TopicOpsOnly) && !channel.HasPrivilege(session.ConnectionId, ChannelPrivilege.Op))
            {
                await session.SendAsync($":server 482 {me} {channelName} :You're not channel operator", ct);
                return;
            }

            var newTopic = msg.Params.Count > 1
                ? msg.Params[1]
                : msg.Trailing;

            newTopic = newTopic?.Trim();

            var setBy = $"{me}!{(session.UserName ?? "u")}@localhost";
            channel.TrySetTopicWithTs(newTopic, setBy, ChannelTimestamps.NowTs());

            var topicLine = $":{setBy} TOPIC {channelName} :{channel.Topic ?? string.Empty}";
            await _routing.BroadcastToChannelAsync(channel, topicLine, excludeConnectionId: null, ct);

            if (state.TryGetUser(session.ConnectionId, out var u) && u is not null && !string.IsNullOrWhiteSpace(u.Uid))
            {
                await _links.PropagateTopicAsync(u.Uid!, channelName, channel.Topic, ct);
            }
        }
    }
}
