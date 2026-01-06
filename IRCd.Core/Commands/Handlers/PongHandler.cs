namespace IRCd.Core.Commands.Handlers
{
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Core.Abstractions;
    using IRCd.Core.Commands.Contracts;
    using IRCd.Core.Protocol;
    using IRCd.Core.State;

    using Microsoft.Extensions.Logging;

    public sealed class PongHandler : IIrcCommandHandler
    {
        public string Command => "PONG";

        private readonly ILogger<PongHandler> _logger;

        public PongHandler(ILogger<PongHandler> logger)
        {
            _logger = logger;
        }

        public ValueTask HandleAsync(IClientSession session, IrcMessage msg, ServerState state, CancellationToken ct)
        {
            var token = msg.Trailing ?? (msg.Params.Count > 0 ? msg.Params[^1] : null);
            
            _logger.LogInformation("PONG received from {Nick} ({ConnectionId}), token: {Token}, was awaiting: {Awaiting}",
                session.Nick ?? "*", session.ConnectionId, token ?? "none", session.AwaitingPong);
            
            session.OnPongReceived(token);
            return ValueTask.CompletedTask;
        }
    }
}
