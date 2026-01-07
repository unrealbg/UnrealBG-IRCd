namespace IRCd.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Core.Abstractions;
    using IRCd.Core.State;

    /// <summary>
    /// Combines a primary mutable repository (e.g. JSON) with a secondary read-only repository (e.g. config).
    /// Mutations apply to the primary repository only.
    /// </summary>
    public sealed class CompositeBanRepository : IBanRepository
    {
        private readonly IBanRepository _primary;
        private readonly IBanRepository _secondary;

        public CompositeBanRepository(IBanRepository primary, IBanRepository secondary)
        {
            _primary = primary;
            _secondary = secondary;
        }

        public Task<BanEntry> AddAsync(BanEntry entry, CancellationToken ct = default)
            => _primary.AddAsync(entry, ct);

        public Task<bool> RemoveByIdAsync(Guid id, CancellationToken ct = default)
            => _primary.RemoveByIdAsync(id, ct);

        public Task<bool> RemoveByMaskAsync(BanType type, string mask, CancellationToken ct = default)
            => _primary.RemoveByMaskAsync(type, mask, ct);

        public async Task<IReadOnlyList<BanEntry>> GetAllActiveAsync(CancellationToken ct = default)
        {
            var primary = await _primary.GetAllActiveAsync(ct);
            var secondary = await _secondary.GetAllActiveAsync(ct);
            return Merge(primary, secondary);
        }

        public async Task<IReadOnlyList<BanEntry>> GetActiveByTypeAsync(BanType type, CancellationToken ct = default)
        {
            var primary = await _primary.GetActiveByTypeAsync(type, ct);
            var secondary = await _secondary.GetActiveByTypeAsync(type, ct);
            return Merge(primary, secondary);
        }

        public Task<int> CleanupExpiredAsync(CancellationToken ct = default)
            => _primary.CleanupExpiredAsync(ct);

        public Task ReloadAsync(CancellationToken ct = default)
            => _primary.ReloadAsync(ct);

        private static IReadOnlyList<BanEntry> Merge(IReadOnlyList<BanEntry> primary, IReadOnlyList<BanEntry> secondary)
        {
            static string Key(BanEntry b) => $"{(int)b.Type}:{b.Mask?.Trim().ToUpperInvariant()}";

            var map = new Dictionary<string, BanEntry>(StringComparer.Ordinal);

            foreach (var b in secondary.Where(b => b is not null && b.IsActive))
            {
                map[Key(b)] = b;
            }

            foreach (var b in primary.Where(b => b is not null && b.IsActive))
            {
                map[Key(b)] = b;
            }

            return map.Values.ToList();
        }
    }
}
