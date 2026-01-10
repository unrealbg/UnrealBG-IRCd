namespace IRCd.Tests
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Services.NickServ;
    using IRCd.Services.Storage;
    using IRCd.Shared.Options;

    using Xunit;

    public sealed class ServicesPersistenceTests
    {
        [Fact]
        public void Recovery_WhenTmpExistsAndTargetValid_DeletesTmp()
        {
            var dir = CreateTempDir();
            var path = Path.Combine(dir, "nick.json");
            var tmp = path + ".tmp";

            File.WriteAllText(path, "[]");
            File.WriteAllText(tmp, "{");

            AtomicJsonFilePersistence.RecoverBestEffort(path, new ServicesPersistenceOptions { BackupCount = 2, RecoverTmpOnStartup = true });

            Assert.True(File.Exists(path));
            Assert.False(File.Exists(tmp));
        }

        [Fact]
        public void Recovery_WhenTargetMissingAndTmpValid_RecoversTmpToTarget()
        {
            var dir = CreateTempDir();
            var path = Path.Combine(dir, "nick.json");
            var tmp = path + ".tmp";

            var accounts = new List<NickAccount>
            {
                new()
                {
                    Name = "alice",
                    PasswordHash = "hash",
                }
            };
            var json = JsonSerializer.Serialize(accounts);
            File.WriteAllText(tmp, json);

            AtomicJsonFilePersistence.RecoverBestEffort(path, new ServicesPersistenceOptions { BackupCount = 2, RecoverTmpOnStartup = true });

            Assert.True(File.Exists(path));
            var loaded = JsonSerializer.Deserialize<List<NickAccount>>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            Assert.NotNull(loaded);
            Assert.Single(loaded!);
            Assert.Equal("alice", loaded![0].Name);
        }

        [Fact]
        public async Task DirtyTracking_CoalescesWritesWithinInterval()
        {
            var dir = CreateTempDir();
            var path = Path.Combine(dir, "nick.json");

            var opts = new ServicesPersistenceOptions
            {
                SaveIntervalSeconds = 1,
                BackupCount = 3,
                RecoverTmpOnStartup = true,
                LockTimeoutMs = 5000,
            };

            var repo = new FileNickAccountRepository(path, opts);

            // First mutation will save immediately.
            Assert.True(await repo.TryCreateAsync(new NickAccount { Name = "alice", PasswordHash = "h1" }, CancellationToken.None));

            // Rapid mutations within interval should be coalesced into (at most) one extra save.
            for (var i = 0; i < 10; i++)
            {
                Assert.True(await repo.TryUpdatePasswordHashAsync("alice", "h" + i.ToString(), CancellationToken.None));
            }

            // Give the deferred save time to run.
            await Task.Delay(TimeSpan.FromSeconds(2));

            // We expect at most one backup created from the deferred save.
            Assert.True(File.Exists(path));
            Assert.True(File.Exists(path + ".bak.1"));
            Assert.False(File.Exists(path + ".bak.2"));

            // Final JSON should be valid.
            var loaded = JsonSerializer.Deserialize<List<NickAccount>>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            Assert.NotNull(loaded);
            Assert.Single(loaded!);
            Assert.Equal("alice", loaded![0].Name);
        }

        [Fact]
        public async Task ConcurrentAtomicWrites_DoNotCorruptJson()
        {
            var dir = CreateTempDir();
            var path = Path.Combine(dir, "channels.json");

            var opts = new ServicesPersistenceOptions
            {
                BackupCount = 2,
                LockTimeoutMs = 5000,
            };

            var tasks = Enumerable.Range(0, 25)
                .Select(i => Task.Run(() =>
                {
                    var json = (i % 2 == 0) ? "[]" : "[{\"name\":\"#c\"}]";
                    AtomicJsonFilePersistence.WriteAtomicJsonBestEffort(path, json, opts);
                }));

            await Task.WhenAll(tasks);

            Assert.True(File.Exists(path));
            using var _ = JsonDocument.Parse(File.ReadAllText(path));
        }

        private static string CreateTempDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), "ircd-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}
