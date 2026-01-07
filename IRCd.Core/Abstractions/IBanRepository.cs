namespace IRCd.Core.Abstractions
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Core.State;

    /// <summary>
    /// Repository for persisting and querying network bans
    /// </summary>
    public interface IBanRepository
    {
        /// <summary>
        /// Add a new ban entry
        /// </summary>
        Task<BanEntry> AddAsync(BanEntry entry, CancellationToken ct = default);

        /// <summary>
        /// Remove ban by ID
        /// </summary>
        Task<bool> RemoveByIdAsync(Guid id, CancellationToken ct = default);

        /// <summary>
        /// Remove ban by type and mask
        /// </summary>
        Task<bool> RemoveByMaskAsync(BanType type, string mask, CancellationToken ct = default);

        /// <summary>
        /// Get all active bans
        /// </summary>
        Task<IReadOnlyList<BanEntry>> GetAllActiveAsync(CancellationToken ct = default);

        /// <summary>
        /// Get active bans by type
        /// </summary>
        Task<IReadOnlyList<BanEntry>> GetActiveByTypeAsync(BanType type, CancellationToken ct = default);

        /// <summary>
        /// Cleanup expired bans
        /// </summary>
        Task<int> CleanupExpiredAsync(CancellationToken ct = default);

        /// <summary>
        /// Reload bans from storage
        /// </summary>
        Task ReloadAsync(CancellationToken ct = default);
    }
}
