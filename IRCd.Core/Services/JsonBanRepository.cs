namespace IRCd.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Core.Abstractions;
    using IRCd.Core.State;

    using Microsoft.Extensions.Logging;

    /// <summary>
    /// JSON file-based ban repository with persistence
    /// </summary>
    public sealed class JsonBanRepository : IBanRepository
    {
        private readonly ILogger<JsonBanRepository> _logger;
        private readonly string _filePath;
        private readonly SemaphoreSlim _lock = new(1, 1);
        private readonly Dictionary<Guid, BanEntry> _bans = new();

        public JsonBanRepository(ILogger<JsonBanRepository> logger, string? filePath = null)
        {
            _logger = logger;
            _filePath = filePath ?? Path.Combine(AppContext.BaseDirectory, "bans.json");
        }

        public async Task<BanEntry> AddAsync(BanEntry entry, CancellationToken ct = default)
        {
            await _lock.WaitAsync(ct);
            try
            {
                var existing = _bans.Values.FirstOrDefault(b =>
                    b.Type == entry.Type &&
                    string.Equals(b.Mask, entry.Mask, StringComparison.OrdinalIgnoreCase) &&
                    b.IsActive);

                if (existing is not null)
                {
                    _bans.Remove(existing.Id);
                }

                _bans[entry.Id] = entry;
                await SaveAsync(ct);
                return entry;
            }
            finally
            {
                _lock.Release();
            }
        }

        public async Task<bool> RemoveByIdAsync(Guid id, CancellationToken ct = default)
        {
            await _lock.WaitAsync(ct);
            try
            {
                var removed = _bans.Remove(id);
                if (removed)
                {
                    await SaveAsync(ct);
                }
                return removed;
            }
            finally
            {
                _lock.Release();
            }
        }

        public async Task<bool> RemoveByMaskAsync(BanType type, string mask, CancellationToken ct = default)
        {
            await _lock.WaitAsync(ct);
            try
            {
                var toRemove = _bans.Values
                    .Where(b => b.Type == type && string.Equals(b.Mask, mask, StringComparison.OrdinalIgnoreCase))
                    .Select(b => b.Id)
                    .ToList();

                foreach (var id in toRemove)
                {
                    _bans.Remove(id);
                }

                if (toRemove.Count > 0)
                {
                    await SaveAsync(ct);
                }

                return toRemove.Count > 0;
            }
            finally
            {
                _lock.Release();
            }
        }

        public async Task<IReadOnlyList<BanEntry>> GetAllActiveAsync(CancellationToken ct = default)
        {
            await _lock.WaitAsync(ct);
            try
            {
                return _bans.Values.Where(b => b.IsActive).ToList();
            }
            finally
            {
                _lock.Release();
            }
        }

        public async Task<IReadOnlyList<BanEntry>> GetActiveByTypeAsync(BanType type, CancellationToken ct = default)
        {
            await _lock.WaitAsync(ct);
            try
            {
                return _bans.Values.Where(b => b.Type == type && b.IsActive).ToList();
            }
            finally
            {
                _lock.Release();
            }
        }

        public async Task<int> CleanupExpiredAsync(CancellationToken ct = default)
        {
            await _lock.WaitAsync(ct);
            try
            {
                var expired = _bans.Values.Where(b => !b.IsActive).Select(b => b.Id).ToList();
                foreach (var id in expired)
                {
                    _bans.Remove(id);
                }

                if (expired.Count > 0)
                {
                    await SaveAsync(ct);
                }

                return expired.Count;
            }
            finally
            {
                _lock.Release();
            }
        }

        public async Task ReloadAsync(CancellationToken ct = default)
        {
            await _lock.WaitAsync(ct);
            try
            {
                _bans.Clear();

                if (!File.Exists(_filePath))
                {
                    _logger.LogInformation("Ban file not found, starting fresh: {FilePath}", _filePath);
                    return;
                }

                var json = await File.ReadAllTextAsync(_filePath, ct);
                var entries = JsonSerializer.Deserialize<List<BanEntry>>(json);

                if (entries is not null)
                {
                    foreach (var entry in entries)
                    {
                        _bans[entry.Id] = entry;
                    }
                    _logger.LogInformation("Loaded {Count} bans from {FilePath}", _bans.Count, _filePath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load bans from {FilePath}", _filePath);
            }
            finally
            {
                _lock.Release();
            }
        }

        private async Task SaveAsync(CancellationToken ct)
        {
            try
            {
                var options = new JsonSerializerOptions { WriteIndented = true };
                var json = JsonSerializer.Serialize(_bans.Values.ToList(), options);
                await File.WriteAllTextAsync(_filePath, json, ct);
                _logger.LogDebug("Saved {Count} bans to {FilePath}", _bans.Count, _filePath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save bans to {FilePath}", _filePath);
            }
        }
    }
}
