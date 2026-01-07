namespace IRCd.Core.Services
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Core.Abstractions;
    using IRCd.Core.State;

    /// <summary>
    /// In-memory ban repository (non-persistent)
    /// </summary>
    public sealed class InMemoryBanRepository : IBanRepository
    {
        private readonly ConcurrentDictionary<Guid, BanEntry> _bans = new();

        public Task<BanEntry> AddAsync(BanEntry entry, CancellationToken ct = default)
        {
            var existing = _bans.Values.FirstOrDefault(b => 
                b.Type == entry.Type && 
                string.Equals(b.Mask, entry.Mask, StringComparison.OrdinalIgnoreCase) &&
                b.IsActive);

            if (existing is not null)
            {
                _bans.TryRemove(existing.Id, out _);
            }

            _bans[entry.Id] = entry;
            return Task.FromResult(entry);
        }

        public Task<bool> RemoveByIdAsync(Guid id, CancellationToken ct = default)
        {
            return Task.FromResult(_bans.TryRemove(id, out _));
        }

        public Task<bool> RemoveByMaskAsync(BanType type, string mask, CancellationToken ct = default)
        {
            var toRemove = _bans.Values
                .Where(b => b.Type == type && string.Equals(b.Mask, mask, StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var ban in toRemove)
            {
                _bans.TryRemove(ban.Id, out _);
            }

            return Task.FromResult(toRemove.Count > 0);
        }

        public Task<IReadOnlyList<BanEntry>> GetAllActiveAsync(CancellationToken ct = default)
        {
            var active = _bans.Values.Where(b => b.IsActive).ToList();
            return Task.FromResult<IReadOnlyList<BanEntry>>(active);
        }

        public Task<IReadOnlyList<BanEntry>> GetActiveByTypeAsync(BanType type, CancellationToken ct = default)
        {
            var active = _bans.Values.Where(b => b.Type == type && b.IsActive).ToList();
            return Task.FromResult<IReadOnlyList<BanEntry>>(active);
        }

        public Task<int> CleanupExpiredAsync(CancellationToken ct = default)
        {
            var expired = _bans.Values.Where(b => !b.IsActive).ToList();
            foreach (var ban in expired)
            {
                _bans.TryRemove(ban.Id, out _);
            }

            return Task.FromResult(expired.Count);
        }

        public Task ReloadAsync(CancellationToken ct = default)
        {
            return Task.CompletedTask;
        }
    }
}
