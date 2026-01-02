namespace IRCd.Core.Commands.Handlers
{
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Core.Abstractions;
    using IRCd.Core.Commands.Contracts;
    using IRCd.Core.Protocol;
    using IRCd.Core.State;

    public sealed class PongHandler : IIrcCommandHandler
    {
        public string Command => "PONG";

        public ValueTask HandleAsync(IClientSession session, IrcMessage msg, ServerState state, CancellationToken ct)
        {
            var token = msg.Trailing ?? (msg.Params.Count > 0 ? msg.Params[^1] : null);
            session.OnPongReceived(token);
            return ValueTask.CompletedTask;
        }
    }
}
