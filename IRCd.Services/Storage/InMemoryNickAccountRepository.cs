namespace IRCd.Services.Storage
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Services.NickServ;

    public sealed class InMemoryNickAccountRepository : INickAccountRepository
    {
        private readonly ConcurrentDictionary<string, NickAccount> _accounts =
            new(StringComparer.OrdinalIgnoreCase);

        public ValueTask<NickAccount?> GetByNameAsync(string name, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return ValueTask.FromResult<NickAccount?>(null);
            }

            _accounts.TryGetValue(name.Trim(), out var acc);
            return ValueTask.FromResult<NickAccount?>(acc);
        }

        public IEnumerable<NickAccount> All()
            => _accounts.Values;

        public ValueTask<bool> TryCreateAsync(NickAccount account, CancellationToken ct)
        {
            if (account is null || string.IsNullOrWhiteSpace(account.Name))
            {
                return ValueTask.FromResult(false);
            }

            var ok = _accounts.TryAdd(account.Name.Trim(), account);
            return ValueTask.FromResult(ok);
        }

        public ValueTask<bool> TryUpdatePasswordHashAsync(string name, string passwordHash, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(passwordHash))
            {
                return ValueTask.FromResult(false);
            }

            var key = name.Trim();

            while (true)
            {
                if (!_accounts.TryGetValue(key, out var existing) || existing is null)
                {
                    return ValueTask.FromResult(false);
                }

                var updated = existing with { PasswordHash = passwordHash };

                if (_accounts.TryUpdate(key, updated, existing))
                {
                    return ValueTask.FromResult(true);
                }
            }
        }

        public ValueTask<bool> TryUpdateAsync(NickAccount updated, CancellationToken ct)
        {
            _ = ct;
            if (updated is null || string.IsNullOrWhiteSpace(updated.Name))
            {
                return ValueTask.FromResult(false);
            }

            var key = updated.Name.Trim();
            while (true)
            {
                if (!_accounts.TryGetValue(key, out var existing) || existing is null)
                {
                    return ValueTask.FromResult(false);
                }

                if (_accounts.TryUpdate(key, updated, existing))
                {
                    return ValueTask.FromResult(true);
                }
            }
        }

        public ValueTask<bool> TryDeleteAsync(string name, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return ValueTask.FromResult(false);
            }

            var ok = _accounts.TryRemove(name.Trim(), out _);
            return ValueTask.FromResult(ok);
        }
    }
}
