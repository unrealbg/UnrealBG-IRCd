namespace IRCd.Core.Abstractions
{
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Core.State;

    public interface IServiceChannelEvents
    {
        ValueTask OnUserJoinedAsync(IClientSession session, Channel channel, ServerState state, CancellationToken ct);

        ValueTask OnChannelModeChangedAsync(IClientSession session, Channel channel, ServerState state, CancellationToken ct);

        ValueTask OnChannelTopicChangedAsync(IClientSession session, Channel channel, string? previousTopic, ServerState state, CancellationToken ct);

        ValueTask OnChannelMessageAsync(IClientSession session, Channel channel, string text, ServerState state, CancellationToken ct);
    }
}
