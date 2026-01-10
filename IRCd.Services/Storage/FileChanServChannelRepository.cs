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

    using IRCd.Services.ChanServ;

    using Microsoft.Extensions.Logging;

    public sealed class FileChanServChannelRepository : IChanServChannelRepository
    {
        private readonly string _path;
        private readonly object _gate = new();
        private readonly ConcurrentDictionary<string, RegisteredChannel> _channels = new(StringComparer.OrdinalIgnoreCase);
        private readonly ILogger<FileChanServChannelRepository>? _logger;

        private readonly IRCd.Shared.Options.ServicesPersistenceOptions _persistence;

        private int _dirty;
        private int _saveScheduled;
        private DateTimeOffset _lastSaveUtc;

        public FileChanServChannelRepository(
            string path,
            IRCd.Shared.Options.ServicesPersistenceOptions? persistence = null,
            ILogger<FileChanServChannelRepository>? logger = null)
        {
            _path = string.IsNullOrWhiteSpace(path) ? throw new ArgumentException("Path is required", nameof(path)) : path;
            _persistence = persistence ?? new IRCd.Shared.Options.ServicesPersistenceOptions();
            _logger = logger;
            LoadBestEffort();
        }

        public ValueTask<RegisteredChannel?> GetByNameAsync(string channelName, CancellationToken ct)
        {
            _ = ct;
            if (string.IsNullOrWhiteSpace(channelName))
            {
                return ValueTask.FromResult<RegisteredChannel?>(null);
            }

            _channels.TryGetValue(channelName.Trim(), out var ch);
            return ValueTask.FromResult<RegisteredChannel?>(ch);
        }

        public IEnumerable<RegisteredChannel> All()
            => _channels.Values;

        public ValueTask<bool> TryCreateAsync(RegisteredChannel channel, CancellationToken ct)
        {
            _ = ct;
            if (channel is null || string.IsNullOrWhiteSpace(channel.Name))
            {
                return ValueTask.FromResult(false);
            }

            var ok = _channels.TryAdd(channel.Name.Trim(), channel);
            if (!ok)
            {
                return ValueTask.FromResult(false);
            }

            return ValueTask.FromResult(MarkDirtyAndMaybeSaveBestEffort());
        }

        public ValueTask<bool> TryDeleteAsync(string channelName, CancellationToken ct)
        {
            _ = ct;
            if (string.IsNullOrWhiteSpace(channelName))
            {
                return ValueTask.FromResult(false);
            }

            var ok = _channels.TryRemove(channelName.Trim(), out _);
            if (!ok)
            {
                return ValueTask.FromResult(false);
            }

            return ValueTask.FromResult(MarkDirtyAndMaybeSaveBestEffort());
        }

        public ValueTask<bool> TryUpdateAsync(RegisteredChannel updated, CancellationToken ct)
        {
            _ = ct;
            if (updated is null || string.IsNullOrWhiteSpace(updated.Name))
            {
                return ValueTask.FromResult(false);
            }

            var key = updated.Name.Trim();
            while (true)
            {
                if (!_channels.TryGetValue(key, out var existing) || existing is null)
                {
                    return ValueTask.FromResult(false);
                }

                if (_channels.TryUpdate(key, updated, existing))
                {
                    return ValueTask.FromResult(MarkDirtyAndMaybeSaveBestEffort());
                }
            }
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
                    _logger?.LogInformation("ChanServ persistence: file not found, starting with empty channel list ({Path})", full);
                    return;
                }

                var json = File.ReadAllText(full);
                var items = JsonSerializer.Deserialize<List<RegisteredChannel>>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (items is null)
                {
                    _logger?.LogWarning("ChanServ persistence: failed to deserialize, starting with empty channel list ({Path})", full);
                    return;
                }

                foreach (var ch in items.Where(ch => ch is not null && !string.IsNullOrWhiteSpace(ch.Name)))
                {
                    _channels[ch.Name.Trim()] = ch;
                }

                _logger?.LogInformation("ChanServ persistence: loaded {Count} channels from {Path}", _channels.Count, full);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "ChanServ persistence: error loading from {Path}, starting with empty channel list", _path);
            }
        }

        private bool MarkDirtyAndMaybeSaveBestEffort()
        {
            System.Threading.Interlocked.Exchange(ref _dirty, 1);

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
            if (System.Threading.Interlocked.CompareExchange(ref _saveScheduled, 1, 0) != 0)
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
                    System.Threading.Interlocked.Exchange(ref _saveScheduled, 0);
                    if (System.Threading.Volatile.Read(ref _dirty) == 1)
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

                    var snapshot = _channels.Values.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
                    var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });

                    AtomicJsonFilePersistence.WriteAtomicJsonBestEffort(full, json, _persistence, _logger);
                    _lastSaveUtc = DateTimeOffset.UtcNow;
                    System.Threading.Interlocked.Exchange(ref _dirty, 0);
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
