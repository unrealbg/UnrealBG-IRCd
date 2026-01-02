namespace IRCd.Core.Commands.Handlers
{
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Core.Abstractions;
    using IRCd.Core.Commands.Contracts;
    using IRCd.Core.Protocol;
    using IRCd.Core.State;

    public sealed class PingHandler : IIrcCommandHandler
    {
        public string Command => "PING";

        public ValueTask HandleAsync(IClientSession session, IrcMessage msg, ServerState state, CancellationToken ct)
        {
            var token = msg.Trailing ?? (msg.Params.Count > 0 ? msg.Params[0] : "server");
            return session.SendAsync($"PONG :{token}", ct);
        }
    }
}
