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

    using IRCd.Services.AdminServ;

    using Microsoft.Extensions.Logging;

    public sealed class FileAdminStaffRepository : IAdminStaffRepository
    {
        private readonly string _path;
        private readonly object _gate = new();
        private readonly ConcurrentDictionary<string, AdminStaffEntry> _items = new(StringComparer.OrdinalIgnoreCase);
        private readonly ILogger<FileAdminStaffRepository>? _logger;

        public FileAdminStaffRepository(string path, ILogger<FileAdminStaffRepository>? logger = null)
        {
            _path = string.IsNullOrWhiteSpace(path) ? throw new ArgumentException("Path is required", nameof(path)) : path;
            _logger = logger;
            LoadBestEffort();
        }

        public ValueTask<AdminStaffEntry?> GetByAccountAsync(string account, CancellationToken ct)
        {
            _ = ct;
            if (string.IsNullOrWhiteSpace(account))
            {
                return ValueTask.FromResult<AdminStaffEntry?>(null);
            }

            _items.TryGetValue(account.Trim(), out var v);
            return ValueTask.FromResult<AdminStaffEntry?>(v);
        }

        public IEnumerable<AdminStaffEntry> All() => _items.Values;

        public ValueTask<bool> TryUpsertAsync(AdminStaffEntry entry, CancellationToken ct)
        {
            _ = ct;
            if (entry is null || string.IsNullOrWhiteSpace(entry.Account))
            {
                return ValueTask.FromResult(false);
            }

            _items[entry.Account.Trim()] = entry;
            return ValueTask.FromResult(SaveBestEffort());
        }

        public ValueTask<bool> TryDeleteAsync(string account, CancellationToken ct)
        {
            _ = ct;
            if (string.IsNullOrWhiteSpace(account))
            {
                return ValueTask.FromResult(false);
            }

            var ok = _items.TryRemove(account.Trim(), out _);
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
                    _logger?.LogInformation("AdminServ persistence: file not found, starting empty ({Path})", full);
                    return;
                }

                var json = File.ReadAllText(full);
                var items = JsonSerializer.Deserialize<List<AdminStaffEntry>>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (items is null)
                {
                    _logger?.LogWarning("AdminServ persistence: failed to deserialize, starting empty ({Path})", full);
                    return;
                }

                foreach (var it in items.Where(i => i is not null && !string.IsNullOrWhiteSpace(i.Account)))
                {
                    _items[it.Account.Trim()] = it;
                }

                _logger?.LogInformation("AdminServ persistence: loaded {Count} staff entries from {Path}", _items.Count, full);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "AdminServ persistence: error loading from {Path}, starting empty", _path);
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

                    var snapshot = _items.Values.OrderBy(i => i.Account, StringComparer.OrdinalIgnoreCase).ToList();
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
