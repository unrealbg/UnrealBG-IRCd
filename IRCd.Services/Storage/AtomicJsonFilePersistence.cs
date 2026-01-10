namespace IRCd.Services.Storage
{
    using System;
    using System.IO;
    using System.Security.Cryptography;
    using System.Text;
    using System.Text.Json;

    using IRCd.Shared.Options;

    using Microsoft.Extensions.Logging;

    public static class AtomicJsonFilePersistence
    {
        public static void RecoverBestEffort(string path, ServicesPersistenceOptions options, ILogger? logger = null)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            try
            {
                var tmp = path + ".tmp";

                if (File.Exists(tmp))
                {
                    var targetOk = File.Exists(path) && IsValidJsonFileBestEffort(path);
                    if (targetOk)
                    {
                        TryDelete(tmp);
                    }
                    else
                    {
                        if (IsValidJsonFileBestEffort(tmp))
                        {
                            logger?.LogWarning("Services persistence: recovering from tmp file {Tmp}", tmp);
                            WriteAtomicJsonBestEffort(path, ReadAllTextBestEffort(tmp) ?? "[]", options, logger);
                            TryDelete(tmp);
                        }
                        else
                        {
                            var bad = tmp + ".bad." + DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
                            TryMove(tmp, bad);
                        }
                    }
                }

                if (File.Exists(path) && !IsValidJsonFileBestEffort(path))
                {
                    for (var i = 1; i <= Math.Max(0, options.BackupCount); i++)
                    {
                        var bak = path + $".bak.{i}";
                        if (!File.Exists(bak))
                        {
                            continue;
                        }

                        if (IsValidJsonFileBestEffort(bak))
                        {
                            logger?.LogWarning("Services persistence: restoring from backup {Backup}", bak);
                            TryCopy(bak, path);
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Services persistence: recovery failed for {Path}", path);
            }
        }

        public static void WriteAtomicJsonBestEffort(string path, string json, ServicesPersistenceOptions options, ILogger? logger = null)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            var mutexName = BuildMutexName(path);
            using var m = new Mutex(initiallyOwned: false, name: mutexName);

            var timeoutMs = Math.Max(100, options.LockTimeoutMs);
            if (!m.WaitOne(millisecondsTimeout: timeoutMs))
            {
                throw new TimeoutException($"Timed out waiting for persistence lock for {path}");
            }

            try
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                RotateBackupsBestEffort(path, options.BackupCount);

                var tmp = path + ".tmp";

                var bytes = Encoding.UTF8.GetBytes(json);
                using (var fs = new FileStream(
                    tmp,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 16 * 1024,
                    options: FileOptions.WriteThrough))
                {
                    fs.Write(bytes, 0, bytes.Length);
                    fs.Flush(flushToDisk: true);
                }

                if (File.Exists(path))
                {
                    if (options.BackupCount > 0)
                    {
                        var bak1 = path + ".bak.1";
                        TryDelete(bak1);
                        File.Replace(tmp, path, bak1, ignoreMetadataErrors: true);
                    }
                    else
                    {
                        File.Replace(tmp, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
                    }
                }
                else
                {
                    File.Move(tmp, path);
                }

                TryDelete(tmp);
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Services persistence: atomic write failed for {Path}", path);
                throw;
            }
            finally
            {
                try { m.ReleaseMutex(); } catch { }
            }
        }

        private static void RotateBackupsBestEffort(string path, int backupCount)
        {
            backupCount = Math.Max(0, backupCount);
            if (backupCount <= 1)
            {
                return;
            }

            for (var i = backupCount - 1; i >= 1; i--)
            {
                var from = path + $".bak.{i}";
                var to = path + $".bak.{i + 1}";

                if (!File.Exists(from))
                {
                    continue;
                }

                TryDelete(to);
                TryMove(from, to);
            }
        }

        private static bool IsValidJsonFileBestEffort(string path)
        {
            try
            {
                var json = File.ReadAllText(path);
                using var _ = JsonDocument.Parse(json);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string? ReadAllTextBestEffort(string path)
        {
            try { return File.ReadAllText(path); } catch { return null; }
        }

        private static string BuildMutexName(string path)
        {
            var normalized = Path.GetFullPath(path).ToUpperInvariant();

            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
            var hex = Convert.ToHexString(hash);

            return $"Global\\IRCd.Services.Persistence.{hex}";
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        private static void TryMove(string from, string to)
        {
            try
            {
                var dir = Path.GetDirectoryName(to);
                if (!string.IsNullOrWhiteSpace(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                File.Move(from, to, overwrite: true);
            }
            catch { }
        }

        private static void TryCopy(string from, string to)
        {
            try
            {
                var dir = Path.GetDirectoryName(to);
                if (!string.IsNullOrWhiteSpace(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                File.Copy(from, to, overwrite: true);
            }
            catch { }
        }
    }
}
