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

    using IRCd.Services.HostServ;

    using Microsoft.Extensions.Logging;

    public sealed class FileVHostRepository : IVHostRepository
    {
        private readonly string _path;
        private readonly object _gate = new();
        private readonly ConcurrentDictionary<string, VHostRecord> _items = new(StringComparer.OrdinalIgnoreCase);
        private readonly ILogger<FileVHostRepository>? _logger;

        private readonly IRCd.Shared.Options.ServicesPersistenceOptions _persistence;

        private int _dirty;
        private int _saveScheduled;
        private DateTimeOffset _lastSaveUtc;

        public FileVHostRepository(
            string path,
            IRCd.Shared.Options.ServicesPersistenceOptions? persistence = null,
            ILogger<FileVHostRepository>? logger = null)
        {
            _path = string.IsNullOrWhiteSpace(path) ? throw new ArgumentException("Path is required", nameof(path)) : path;
            _persistence = persistence ?? new IRCd.Shared.Options.ServicesPersistenceOptions();
            _logger = logger;
            LoadBestEffort();
        }

        public ValueTask<VHostRecord?> GetAsync(string nick, CancellationToken ct)
        {
            _ = ct;
            if (string.IsNullOrWhiteSpace(nick))
            {
                return ValueTask.FromResult<VHostRecord?>(null);
            }

            _items.TryGetValue(nick.Trim(), out var v);
            return ValueTask.FromResult<VHostRecord?>(v);
        }

        public IEnumerable<VHostRecord> All() => _items.Values;

        public ValueTask<bool> TryUpsertAsync(VHostRecord record, CancellationToken ct)
        {
            _ = ct;
            if (record is null || string.IsNullOrWhiteSpace(record.Nick) || string.IsNullOrWhiteSpace(record.VHost))
            {
                return ValueTask.FromResult(false);
            }

            record.UpdatedUtc = DateTimeOffset.UtcNow;
            _items[record.Nick.Trim()] = record;
            return ValueTask.FromResult(MarkDirtyAndMaybeSaveBestEffort());
        }

        public ValueTask<bool> TryDeleteAsync(string nick, CancellationToken ct)
        {
            _ = ct;
            if (string.IsNullOrWhiteSpace(nick))
            {
                return ValueTask.FromResult(false);
            }

            var ok = _items.TryRemove(nick.Trim(), out _);
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
                    _logger?.LogInformation("HostServ persistence: file not found, starting empty ({Path})", full);
                    return;
                }

                var json = File.ReadAllText(full);
                var items = JsonSerializer.Deserialize<List<VHostRecord>>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (items is null)
                {
                    _logger?.LogWarning("HostServ persistence: failed to deserialize, starting empty ({Path})", full);
                    return;
                }

                foreach (var it in items.Where(i => i is not null && !string.IsNullOrWhiteSpace(i.Nick) && !string.IsNullOrWhiteSpace(i.VHost)))
                {
                    _items[it.Nick.Trim()] = it;
                }

                _logger?.LogInformation("HostServ persistence: loaded {Count} vhost entries from {Path}", _items.Count, full);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "HostServ persistence: error loading from {Path}, starting empty", _path);
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

                    var snapshot = _items.Values.OrderBy(i => i.Nick, StringComparer.OrdinalIgnoreCase).ToList();
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
