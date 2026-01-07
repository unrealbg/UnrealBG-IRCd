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

    using IRCd.Services.MemoServ;

    using Microsoft.Extensions.Logging;

    public sealed class FileMemoRepository : IMemoRepository
    {
        private sealed record MemoBox(string Account, List<Memo> Memos);

        private readonly string _path;
        private readonly object _gate = new();
        private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, Memo>> _memosByAccount = new(StringComparer.OrdinalIgnoreCase);
        private readonly ILogger<FileMemoRepository>? _logger;

        public FileMemoRepository(string path, ILogger<FileMemoRepository>? logger = null)
        {
            _path = string.IsNullOrWhiteSpace(path) ? throw new ArgumentException("Path is required", nameof(path)) : path;
            _logger = logger;
            LoadBestEffort();
        }

        public ValueTask<IReadOnlyList<Memo>> GetMemosAsync(string account, CancellationToken ct)
        {
            _ = ct;
            if (string.IsNullOrWhiteSpace(account))
            {
                return ValueTask.FromResult<IReadOnlyList<Memo>>(Array.Empty<Memo>());
            }

            if (!_memosByAccount.TryGetValue(account.Trim(), out var box) || box is null)
            {
                return ValueTask.FromResult<IReadOnlyList<Memo>>(Array.Empty<Memo>());
            }

            var list = box.Values.OrderBy(m => m.SentAtUtc).ToList();
            return ValueTask.FromResult<IReadOnlyList<Memo>>(list);
        }

        public ValueTask<int> GetUnreadCountAsync(string account, CancellationToken ct)
        {
            _ = ct;
            if (string.IsNullOrWhiteSpace(account))
            {
                return ValueTask.FromResult(0);
            }

            if (!_memosByAccount.TryGetValue(account.Trim(), out var box) || box is null)
            {
                return ValueTask.FromResult(0);
            }

            var n = box.Values.Count(m => !m.IsRead);
            return ValueTask.FromResult(n);
        }

        public ValueTask<bool> TryAddMemoAsync(string account, Memo memo, CancellationToken ct)
        {
            _ = ct;
            if (string.IsNullOrWhiteSpace(account) || memo is null || memo.Id == Guid.Empty)
            {
                return ValueTask.FromResult(false);
            }

            var box = _memosByAccount.GetOrAdd(account.Trim(), _ => new ConcurrentDictionary<Guid, Memo>());
            var ok = box.TryAdd(memo.Id, memo);
            if (!ok)
            {
                return ValueTask.FromResult(false);
            }

            return ValueTask.FromResult(SaveBestEffort());
        }

        public ValueTask<bool> TryUpdateMemoAsync(string account, Memo memo, CancellationToken ct)
        {
            _ = ct;
            if (string.IsNullOrWhiteSpace(account) || memo is null || memo.Id == Guid.Empty)
            {
                return ValueTask.FromResult(false);
            }

            if (!_memosByAccount.TryGetValue(account.Trim(), out var box) || box is null)
            {
                return ValueTask.FromResult(false);
            }

            while (true)
            {
                if (!box.TryGetValue(memo.Id, out var existing) || existing is null)
                {
                    return ValueTask.FromResult(false);
                }

                if (box.TryUpdate(memo.Id, memo, existing))
                {
                    return ValueTask.FromResult(SaveBestEffort());
                }
            }
        }

        public ValueTask<bool> TryDeleteMemoAsync(string account, Guid memoId, CancellationToken ct)
        {
            _ = ct;
            if (string.IsNullOrWhiteSpace(account) || memoId == Guid.Empty)
            {
                return ValueTask.FromResult(false);
            }

            if (!_memosByAccount.TryGetValue(account.Trim(), out var box) || box is null)
            {
                return ValueTask.FromResult(false);
            }

            var ok = box.TryRemove(memoId, out _);
            if (!ok)
            {
                return ValueTask.FromResult(false);
            }

            return ValueTask.FromResult(SaveBestEffort());
        }

        public ValueTask<bool> TryClearAsync(string account, CancellationToken ct)
        {
            _ = ct;
            if (string.IsNullOrWhiteSpace(account))
            {
                return ValueTask.FromResult(false);
            }

            var ok = _memosByAccount.TryRemove(account.Trim(), out _);
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
                    _logger?.LogInformation("MemoServ persistence: file not found, starting empty ({Path})", full);
                    return;
                }

                var json = File.ReadAllText(full);
                var boxes = JsonSerializer.Deserialize<List<MemoBox>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (boxes is null)
                {
                    _logger?.LogWarning("MemoServ persistence: failed to deserialize, starting empty ({Path})", full);
                    return;
                }

                foreach (var b in boxes.Where(b => b is not null && !string.IsNullOrWhiteSpace(b.Account)))
                {
                    var dict = new ConcurrentDictionary<Guid, Memo>();
                    foreach (var m in (b.Memos ?? new List<Memo>()).Where(m => m is not null && m.Id != Guid.Empty))
                    {
                        dict[m.Id] = m;
                    }
                    _memosByAccount[b.Account.Trim()] = dict;
                }

                _logger?.LogInformation("MemoServ persistence: loaded {Count} accounts from {Path}", _memosByAccount.Count, full);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "MemoServ persistence: error loading from {Path}, starting empty", _path);
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

                    var snapshot = _memosByAccount
                        .OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase)
                        .Select(k => new MemoBox(k.Key, k.Value.Values.OrderBy(m => m.SentAtUtc).ToList()))
                        .ToList();

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
