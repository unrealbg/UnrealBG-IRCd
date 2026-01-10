namespace IRCd.Services.Storage
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Services.NickServ;

    using Microsoft.Extensions.Logging;

    public sealed class FileNickAccountRepository : INickAccountRepository
    {
        private readonly string _path;
        private readonly object _gate = new();
        private readonly ConcurrentDictionary<string, NickAccount> _accounts = new(StringComparer.OrdinalIgnoreCase);
        private readonly ILogger<FileNickAccountRepository>? _logger;

        private readonly IRCd.Shared.Options.ServicesPersistenceOptions _persistence;

        private int _dirty;
        private int _saveScheduled;
        private DateTimeOffset _lastSaveUtc;

        public FileNickAccountRepository(
            string path,
            IRCd.Shared.Options.ServicesPersistenceOptions? persistence = null,
            ILogger<FileNickAccountRepository>? logger = null)
        {
            _path = string.IsNullOrWhiteSpace(path) ? throw new ArgumentException("Path is required", nameof(path)) : path;
            _persistence = persistence ?? new IRCd.Shared.Options.ServicesPersistenceOptions();
            _logger = logger;
            LoadBestEffort();
        }

        public ValueTask<NickAccount?> GetByNameAsync(string name, CancellationToken ct)
        {
            _ = ct;
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
            _ = ct;
            if (account is null || string.IsNullOrWhiteSpace(account.Name))
            {
                return ValueTask.FromResult(false);
            }

            var ok = _accounts.TryAdd(account.Name.Trim(), account);
            if (!ok)
            {
                return ValueTask.FromResult(false);
            }

            return ValueTask.FromResult(MarkDirtyAndMaybeSaveBestEffort());
        }

        public ValueTask<bool> TryUpdatePasswordHashAsync(string name, string passwordHash, CancellationToken ct)
        {
            _ = ct;
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
                    return ValueTask.FromResult(MarkDirtyAndMaybeSaveBestEffort());
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
                    return ValueTask.FromResult(MarkDirtyAndMaybeSaveBestEffort());
                }
            }
        }

        public ValueTask<bool> TryDeleteAsync(string name, CancellationToken ct)
        {
            _ = ct;
            if (string.IsNullOrWhiteSpace(name))
            {
                return ValueTask.FromResult(false);
            }

            var ok = _accounts.TryRemove(name.Trim(), out _);
            if (!ok)
            {
                return ValueTask.FromResult(false);
            }

            return ValueTask.FromResult(MarkDirtyAndMaybeSaveBestEffort());
        }

        private void LoadBestEffort()
        {
            try
            {
                var expanded = Environment.ExpandEnvironmentVariables(_path);
                var full = Path.IsPathRooted(expanded)
                    ? expanded
                    : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, expanded));

                if (_persistence.RecoverTmpOnStartup)
                {
                    AtomicJsonFilePersistence.RecoverBestEffort(full, _persistence, _logger);
                }

                if (!File.Exists(full))
                {
                    _logger?.LogInformation("NickServ persistence: file not found, starting with empty account list ({Path})", full);
                    return;
                }

                var json = File.ReadAllText(full);
                var items = JsonSerializer.Deserialize<List<NickAccount>>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (items is null)
                {
                    _logger?.LogWarning("NickServ persistence: failed to deserialize, starting with empty account list ({Path})", full);
                    return;
                }

                foreach (var a in items.Where(a => a is not null && !string.IsNullOrWhiteSpace(a.Name)))
                {
                    _accounts[a.Name.Trim()] = a;
                }

                _logger?.LogInformation("NickServ persistence: loaded {Count} accounts from {Path}", _accounts.Count, full);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "NickServ persistence: error loading from {Path}, starting with empty account list", _path);
            }
        }

        private bool MarkDirtyAndMaybeSaveBestEffort()
        {
            Interlocked.Exchange(ref _dirty, 1);

            var intervalSeconds = Math.Max(0, _persistence.SaveIntervalSeconds);
            if (intervalSeconds == 0)
            {
                return SaveNowBestEffort();
            }

            if (DateTimeOffset.UtcNow - _lastSaveUtc >= TimeSpan.FromSeconds(intervalSeconds))
            {
                return SaveNowBestEffort();
            }

            ScheduleDeferredSave(intervalSeconds);
            return true;
        }

        private void ScheduleDeferredSave(int intervalSeconds)
        {
            if (Interlocked.CompareExchange(ref _saveScheduled, 1, 0) != 0)
                return;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(intervalSeconds)).ConfigureAwait(false);
                    SaveNowBestEffort();
                }
                catch { }
                finally
                {
                    Interlocked.Exchange(ref _saveScheduled, 0);
                    if (Volatile.Read(ref _dirty) == 1)
                    {
                        ScheduleDeferredSave(intervalSeconds);
                    }
                }
            });
        }

        private bool SaveNowBestEffort()
        {
            lock (_gate)
            {
                try
                {
                    var expanded = Environment.ExpandEnvironmentVariables(_path);
                    var full = Path.IsPathRooted(expanded)
                        ? expanded
                        : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, expanded));

                    var dir = Path.GetDirectoryName(full);
                    if (!string.IsNullOrWhiteSpace(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }

                    var snapshot = _accounts.Values.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
                    var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });

                    AtomicJsonFilePersistence.WriteAtomicJsonBestEffort(full, json, _persistence, _logger);
                    _lastSaveUtc = DateTimeOffset.UtcNow;
                    Interlocked.Exchange(ref _dirty, 0);
                    return true;
                }
                catch
                {
                    return false;
                }
            }
        }
    }
}
