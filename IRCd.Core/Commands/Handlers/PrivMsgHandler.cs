namespace IRCd.Core.Commands.Handlers
{
    using IRCd.Core.Abstractions;
    using IRCd.Core.Commands.Contracts;
    using IRCd.Core.Protocol;
    using IRCd.Core.Services;
    using IRCd.Core.State;

    public sealed class PrivMsgHandler : IIrcCommandHandler
    {
        public string Command => "PRIVMSG";
        private readonly RoutingService _routing;

        public PrivMsgHandler(RoutingService routing) => _routing = routing;

        public async ValueTask HandleAsync(IClientSession session, IrcMessage msg, ServerState state, CancellationToken ct)
        {
            if (!Commands.CommandGuards.EnsureRegistered(session, ct, out var err)) { await err; return; }

            if (msg.Params.Count < 1)
            {
                await session.SendAsync($":server 411 {session.Nick} :No recipient given (PRIVMSG)", ct);
                return;
            }

            var target = msg.Params[0];
            var text = msg.Trailing;

            if (string.IsNullOrWhiteSpace(text))
            {
                await session.SendAsync($":server 412 {session.Nick} :No text to send", ct);
                return;
            }

            var fromNick = session.Nick!;
            var line = $":{fromNick}!{session.UserName ?? "u"}@localhost PRIVMSG {target} :{text}";

            if (target.StartsWith('#'))
            {
                if (!state.TryGetChannel(target, out var channel) || channel is null)
                {
                    await session.SendAsync($":server 403 {fromNick} {target} :No such channel", ct);
                    return;
                }

                if (!channel.Contains(session.ConnectionId))
                {
                    await session.SendAsync($":server 404 {fromNick} {target} :Cannot send to channel", ct);
                    return;
                }

                await _routing.BroadcastToChannelAsync(channel, line, excludeConnectionId: session.ConnectionId, ct);
                return;
            }

            if (!state.TryGetConnectionIdByNick(target, out var toConn) || toConn is null)
            {
                await session.SendAsync($":server 401 {fromNick} {target} :No such nick", ct);
                return;
            }

            await _routing.SendToUserAsync(toConn, line, ct);
        }
    }
}
