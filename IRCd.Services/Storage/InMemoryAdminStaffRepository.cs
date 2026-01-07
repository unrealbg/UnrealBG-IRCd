namespace IRCd.Services.Storage
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Services.AdminServ;

    public sealed class InMemoryAdminStaffRepository : IAdminStaffRepository
    {
        private readonly ConcurrentDictionary<string, AdminStaffEntry> _items = new(StringComparer.OrdinalIgnoreCase);

        public ValueTask<AdminStaffEntry?> GetByAccountAsync(string account, CancellationToken ct)
        {
            _ = ct;
            if (string.IsNullOrWhiteSpace(account))
            {
                return ValueTask.FromResult<AdminStaffEntry?>(null);
            }

            _items.TryGetValue(account.Trim(), out var v);
            return ValueTask.FromResult<AdminStaffEntry?>(v);
        }

        public IEnumerable<AdminStaffEntry> All() => _items.Values;

        public ValueTask<bool> TryUpsertAsync(AdminStaffEntry entry, CancellationToken ct)
        {
            _ = ct;
            if (entry is null || string.IsNullOrWhiteSpace(entry.Account))
            {
                return ValueTask.FromResult(false);
            }

            _items[entry.Account.Trim()] = entry;
            return ValueTask.FromResult(true);
        }

        public ValueTask<bool> TryDeleteAsync(string account, CancellationToken ct)
        {
            _ = ct;
            if (string.IsNullOrWhiteSpace(account))
            {
                return ValueTask.FromResult(false);
            }

            return ValueTask.FromResult(_items.TryRemove(account.Trim(), out _));
        }
    }
}
