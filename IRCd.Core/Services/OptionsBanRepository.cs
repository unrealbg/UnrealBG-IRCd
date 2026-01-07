namespace IRCd.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Core.Abstractions;
    using IRCd.Core.State;
    using IRCd.Shared.Options;

    using Microsoft.Extensions.Options;

    /// <summary>
    /// Read-only ban repository that surfaces configured KLINE/DLINE entries from <see cref="IrcOptions"/>.
    /// </summary>
    public sealed class OptionsBanRepository : IBanRepository
    {
        private readonly IOptionsMonitor<IrcOptions> _options;

        public OptionsBanRepository(IOptionsMonitor<IrcOptions> options)
        {
            _options = options;
        }

        public Task<BanEntry> AddAsync(BanEntry entry, CancellationToken ct = default)
            => throw new NotSupportedException("OptionsBanRepository is read-only.");

        public Task<bool> RemoveByIdAsync(Guid id, CancellationToken ct = default)
            => throw new NotSupportedException("OptionsBanRepository is read-only.");

        public Task<bool> RemoveByMaskAsync(BanType type, string mask, CancellationToken ct = default)
            => throw new NotSupportedException("OptionsBanRepository is read-only.");

        public Task<IReadOnlyList<BanEntry>> GetAllActiveAsync(CancellationToken ct = default)
        {
            var o = _options.CurrentValue;
            var bans = new List<BanEntry>();

            bans.AddRange(o.KLines
                .Where(k => k is not null && !string.IsNullOrWhiteSpace(k.Mask))
                .Select(k => new BanEntry
                {
                    Type = BanType.KLINE,
                    Mask = k.Mask!.Trim(),
                    Reason = string.IsNullOrWhiteSpace(k.Reason) ? "Banned" : k.Reason!,
                    SetBy = "config",
                    CreatedAt = DateTimeOffset.MinValue,
                    ExpiresAt = null
                }));

            bans.AddRange(o.DLines
                .Where(d => d is not null && !string.IsNullOrWhiteSpace(d.Mask))
                .Select(d => new BanEntry
                {
                    Type = BanType.DLINE,
                    Mask = d.Mask!.Trim(),
                    Reason = string.IsNullOrWhiteSpace(d.Reason) ? "Banned" : d.Reason!,
                    SetBy = "config",
                    CreatedAt = DateTimeOffset.MinValue,
                    ExpiresAt = null
                }));

            return Task.FromResult((IReadOnlyList<BanEntry>)bans);
        }

        public async Task<IReadOnlyList<BanEntry>> GetActiveByTypeAsync(BanType type, CancellationToken ct = default)
        {
            var all = await GetAllActiveAsync(ct);
            return all.Where(b => b.Type == type && b.IsActive).ToList();
        }

        public Task<int> CleanupExpiredAsync(CancellationToken ct = default)
            => Task.FromResult(0);

        public Task ReloadAsync(CancellationToken ct = default)
            => Task.CompletedTask;
    }
}
