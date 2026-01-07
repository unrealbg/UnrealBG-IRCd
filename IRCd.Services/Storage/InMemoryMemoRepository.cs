namespace IRCd.Services.Storage
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Services.MemoServ;

    public sealed class InMemoryMemoRepository : IMemoRepository
    {
        private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, Memo>> _memosByAccount = new(StringComparer.OrdinalIgnoreCase);

        public ValueTask<IReadOnlyList<Memo>> GetMemosAsync(string account, CancellationToken ct)
        {
            _ = ct;
            if (string.IsNullOrWhiteSpace(account))
            {
                return ValueTask.FromResult<IReadOnlyList<Memo>>(Array.Empty<Memo>());
            }

            if (!_memosByAccount.TryGetValue(account.Trim(), out var box) || box is null)
            {
                return ValueTask.FromResult<IReadOnlyList<Memo>>(Array.Empty<Memo>());
            }

            var list = box.Values.OrderBy(m => m.SentAtUtc).ToList();
            return ValueTask.FromResult<IReadOnlyList<Memo>>(list);
        }

        public ValueTask<int> GetUnreadCountAsync(string account, CancellationToken ct)
        {
            _ = ct;
            if (string.IsNullOrWhiteSpace(account))
            {
                return ValueTask.FromResult(0);
            }

            if (!_memosByAccount.TryGetValue(account.Trim(), out var box) || box is null)
            {
                return ValueTask.FromResult(0);
            }

            var n = box.Values.Count(m => !m.IsRead);
            return ValueTask.FromResult(n);
        }

        public ValueTask<bool> TryAddMemoAsync(string account, Memo memo, CancellationToken ct)
        {
            _ = ct;
            if (string.IsNullOrWhiteSpace(account) || memo is null || memo.Id == Guid.Empty)
            {
                return ValueTask.FromResult(false);
            }

            var box = _memosByAccount.GetOrAdd(account.Trim(), _ => new ConcurrentDictionary<Guid, Memo>());
            return ValueTask.FromResult(box.TryAdd(memo.Id, memo));
        }

        public ValueTask<bool> TryUpdateMemoAsync(string account, Memo memo, CancellationToken ct)
        {
            _ = ct;
            if (string.IsNullOrWhiteSpace(account) || memo is null || memo.Id == Guid.Empty)
            {
                return ValueTask.FromResult(false);
            }

            if (!_memosByAccount.TryGetValue(account.Trim(), out var box) || box is null)
            {
                return ValueTask.FromResult(false);
            }

            while (true)
            {
                if (!box.TryGetValue(memo.Id, out var existing) || existing is null)
                {
                    return ValueTask.FromResult(false);
                }

                if (box.TryUpdate(memo.Id, memo, existing))
                {
                    return ValueTask.FromResult(true);
                }
            }
        }

        public ValueTask<bool> TryDeleteMemoAsync(string account, Guid memoId, CancellationToken ct)
        {
            _ = ct;
            if (string.IsNullOrWhiteSpace(account) || memoId == Guid.Empty)
            {
                return ValueTask.FromResult(false);
            }

            if (!_memosByAccount.TryGetValue(account.Trim(), out var box) || box is null)
            {
                return ValueTask.FromResult(false);
            }

            return ValueTask.FromResult(box.TryRemove(memoId, out _));
        }

        public ValueTask<bool> TryClearAsync(string account, CancellationToken ct)
        {
            _ = ct;
            if (string.IsNullOrWhiteSpace(account))
            {
                return ValueTask.FromResult(false);
            }

            return ValueTask.FromResult(_memosByAccount.TryRemove(account.Trim(), out _));
        }
    }
}
