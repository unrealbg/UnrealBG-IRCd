namespace IRCd.Services.Dispatching
{
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Core.Abstractions;
    using IRCd.Core.State;
    using IRCd.Services.ChanServ;
    using IRCd.Services.RootServ;

    public sealed class ServicesChannelEvents : IServiceChannelEvents
    {
        private readonly ChanServService _chanServ;
        private readonly ChannelSnoopService _snoop;

        public ServicesChannelEvents(ChanServService chanServ, ChannelSnoopService snoop)
        {
            _chanServ = chanServ;
            _snoop = snoop;
        }

        public ValueTask OnUserJoinedAsync(IClientSession session, Channel channel, ServerState state, CancellationToken ct)
            => _chanServ.OnUserJoinedAsync(session, channel, state, ct);

        public ValueTask OnChannelModeChangedAsync(IClientSession session, Channel channel, ServerState state, CancellationToken ct)
            => _chanServ.OnChannelModeChangedAsync(session, channel, state, ct);

        public ValueTask OnChannelTopicChangedAsync(IClientSession session, Channel channel, string? previousTopic, ServerState state, CancellationToken ct)
            => _chanServ.OnChannelTopicChangedAsync(session, channel, previousTopic, state, ct);

        public ValueTask OnChannelMessageAsync(IClientSession session, Channel channel, string text, ServerState state, CancellationToken ct)
            => _snoop.OnChannelMessageAsync(session, channel, text, state, ct);
    }
}
