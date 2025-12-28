namespace IRCd.Core.Commands.Contracts
{
    using IRCd.Core.Abstractions;
    using IRCd.Core.Protocol;
    using IRCd.Core.State;

    public interface IIrcCommandHandler
    {
        string Command { get; }

        ValueTask HandleAsync(IClientSession session, IrcMessage msg, ServerState state, CancellationToken ct);
    }
}
