namespace IRCd.Core.Abstractions
{
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Core.State;

    public interface IServiceSessionEvents
    {
        ValueTask OnNickChangedAsync(IClientSession session, string? oldNick, string newNick, ServerState state, CancellationToken ct);

        ValueTask OnQuitAsync(string connectionId, CancellationToken ct);

        ValueTask<bool> IsNickRegisteredAsync(string nick, CancellationToken ct);

        ValueTask<bool> IsIdentifiedForNickAsync(string connectionId, string nick, CancellationToken ct);
    }
}
