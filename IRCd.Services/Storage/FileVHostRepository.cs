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

        public FileVHostRepository(string path, ILogger<FileVHostRepository>? logger = null)
        {
            _path = string.IsNullOrWhiteSpace(path) ? throw new ArgumentException("Path is required", nameof(path)) : path;
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
            return ValueTask.FromResult(SaveBestEffort());
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

            return ValueTask.FromResult(SaveBestEffort());
        }

        private void LoadBestEffort()
        {
            try
            {
                var expanded = Environment.ExpandEnvironmentVariables(_path);
                var full = Path.IsPathRooted(expanded)
                    ? expanded
                    : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, expanded));

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

        private bool SaveBestEffort()
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

                    var tmp = full + ".tmp";
                    File.WriteAllText(tmp, json);
                    File.Copy(tmp, full, overwrite: true);
                    File.Delete(tmp);
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
