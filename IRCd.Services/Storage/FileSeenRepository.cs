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

        public FileSeenRepository(string path, ILogger<FileSeenRepository>? logger = null)
        {
            _path = string.IsNullOrWhiteSpace(path) ? throw new ArgumentException("Path is required", nameof(path)) : path;
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

                    var snapshot = _seen.Values.OrderBy(r => r.Nick, StringComparer.OrdinalIgnoreCase).ToList();
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
