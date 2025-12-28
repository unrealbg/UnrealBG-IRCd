namespace IRCd.Core.Commands
{
    using IRCd.Core.Abstractions;
    using IRCd.Core.Commands.Contracts;
    using IRCd.Core.Protocol;
    using IRCd.Core.State;

    public sealed class CommandDispatcher
    {
        private readonly Dictionary<string, IIrcCommandHandler> _handlers;

        public CommandDispatcher(IEnumerable<IIrcCommandHandler> handlers)
        {
            _handlers = handlers.ToDictionary(h => h.Command, StringComparer.OrdinalIgnoreCase);
        }

        public async ValueTask DispatchAsync(IClientSession session, IrcMessage msg, ServerState state, CancellationToken ct)
        {
            if (_handlers.TryGetValue(msg.Command, out var handler))
            {
                await handler.HandleAsync(session, msg, state, ct);
                return;
            }

            var nick = session.Nick ?? "*";
            await session.SendAsync($":server 421 {nick} {msg.Command} :Unknown command", ct);
        }
    }
}
