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

    using IRCd.Services.SeenServ;

    using Microsoft.Extensions.Logging;

    public sealed class FileSeenRepository : ISeenRepository
    {
        private readonly string _path;
        private readonly object _gate = new();
        private readonly ConcurrentDictionary<string, SeenRecord> _seen = new(StringComparer.OrdinalIgnoreCase);
        private readonly ILogger<FileSeenRepository>? _logger;

        private readonly IRCd.Shared.Options.ServicesPersistenceOptions _persistence;

        private int _dirty;
        private int _saveScheduled;
        private DateTimeOffset _lastSaveUtc;

        public FileSeenRepository(
            string path,
            IRCd.Shared.Options.ServicesPersistenceOptions? persistence = null,
            ILogger<FileSeenRepository>? logger = null)
        {
            _path = string.IsNullOrWhiteSpace(path) ? throw new ArgumentException("Path is required", nameof(path)) : path;
            _persistence = persistence ?? new IRCd.Shared.Options.ServicesPersistenceOptions();
            _logger = logger;
            LoadBestEffort();
        }

        public ValueTask<SeenRecord?> GetAsync(string nick, CancellationToken ct)
        {
            _ = ct;
            if (string.IsNullOrWhiteSpace(nick))
            {
                return ValueTask.FromResult<SeenRecord?>(null);
            }

            _seen.TryGetValue(nick.Trim(), out var rec);
            return ValueTask.FromResult<SeenRecord?>(rec);
        }

        public ValueTask<bool> TryUpsertAsync(SeenRecord record, CancellationToken ct)
        {
            _ = ct;
            if (record is null || string.IsNullOrWhiteSpace(record.Nick))
            {
                return ValueTask.FromResult(false);
            }

            _seen[record.Nick.Trim()] = record;
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
                    _logger?.LogInformation("SeenServ persistence: file not found, starting empty ({Path})", full);
                    return;
                }

                var json = File.ReadAllText(full);
                var items = JsonSerializer.Deserialize<List<SeenRecord>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (items is null)
                {
                    _logger?.LogWarning("SeenServ persistence: failed to deserialize, starting empty ({Path})", full);
                    return;
                }

                foreach (var r in items.Where(r => r is not null && !string.IsNullOrWhiteSpace(r.Nick)))
                {
                    _seen[r.Nick.Trim()] = r;
                }

                _logger?.LogInformation("SeenServ persistence: loaded {Count} records from {Path}", _seen.Count, full);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "SeenServ persistence: error loading from {Path}, starting empty", _path);
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

                    var snapshot = _seen.Values.OrderBy(r => r.Nick, StringComparer.OrdinalIgnoreCase).ToList();
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
