namespace IRCd.Services.Storage
{
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Services.MemoServ;

    public interface IMemoRepository
    {
        ValueTask<IReadOnlyList<Memo>> GetMemosAsync(string account, CancellationToken ct);

        ValueTask<int> GetUnreadCountAsync(string account, CancellationToken ct);

        ValueTask<bool> TryAddMemoAsync(string account, Memo memo, CancellationToken ct);

        ValueTask<bool> TryUpdateMemoAsync(string account, Memo memo, CancellationToken ct);

        ValueTask<bool> TryDeleteMemoAsync(string account, System.Guid memoId, CancellationToken ct);

        ValueTask<bool> TryClearAsync(string account, CancellationToken ct);
    }
}
