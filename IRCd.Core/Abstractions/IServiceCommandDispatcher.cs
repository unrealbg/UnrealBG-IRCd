namespace IRCd.Core.Abstractions
{
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Core.State;

    public interface IServiceCommandDispatcher
    {
        ValueTask<bool> TryHandlePrivmsgAsync(IClientSession session, string target, string text, ServerState state, CancellationToken ct);

        ValueTask<bool> TryHandleNoticeAsync(IClientSession session, string target, string text, ServerState state, CancellationToken ct);
    }
}
