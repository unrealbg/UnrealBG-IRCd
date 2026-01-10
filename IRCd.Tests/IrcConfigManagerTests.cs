using System;
using System.IO;

using IRCd.Core.Abstractions;
using IRCd.Core.Config;
using IRCd.Shared.Options;

using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

public sealed class IrcConfigManagerTests
{
    [Fact]
    public void Rehash_ParseFailure_DoesNotSwap()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), "UnrealBG-IRCd", "rehash", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpDir);

        var confPath = Path.Combine(tmpDir, "ircd.conf");
        File.WriteAllText(confPath, "serverinfo {\n    name = \"bad\";\n    sid = \"002\";\n");

        try
        {
            var store = new IrcOptionsStore(new IrcOptions
            {
                ServerInfo = new ServerInfoOptions { Name = "srv", Sid = "001" },
                ConfigFile = "ircd.conf",
            });

            var env = new EnvStub(tmpDir);
            var mgr = new IrcConfigManager(store, env, Array.Empty<IConfigReloadListener>(), NullLogger<IrcConfigManager>.Instance);

            var res = mgr.TryRehash(confPath);
            Assert.False(res.Success);

            Assert.Equal("srv", store.Value.ServerInfo?.Name);
            Assert.Equal("001", store.Value.ServerInfo?.Sid);
        }
        finally
        {
            try { Directory.Delete(tmpDir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void Rehash_ValidationFailure_DoesNotSwap()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), "UnrealBG-IRCd", "rehash", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpDir);

        var confPath = Path.Combine(tmpDir, "ircd.conf");
        File.WriteAllText(confPath, "serverinfo {\n    name = \"bad-sid\";\n    sid = \"000\";\n};\n");

        try
        {
            var store = new IrcOptionsStore(new IrcOptions
            {
                ServerInfo = new ServerInfoOptions { Name = "srv", Sid = "001" },
                ConfigFile = "ircd.conf",
            });

            var env = new EnvStub(tmpDir);
            var mgr = new IrcConfigManager(store, env, Array.Empty<IConfigReloadListener>(), NullLogger<IrcConfigManager>.Instance);

            var res = mgr.TryRehash(confPath);
            Assert.False(res.Success);
            Assert.NotEmpty(res.Errors);

            Assert.Equal("srv", store.Value.ServerInfo?.Name);
            Assert.Equal("001", store.Value.ServerInfo?.Sid);
        }
        finally
        {
            try { Directory.Delete(tmpDir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void Rehash_Success_SwapsSnapshot()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), "UnrealBG-IRCd", "rehash", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpDir);

        var confPath = Path.Combine(tmpDir, "ircd.conf");
        File.WriteAllText(confPath, "serverinfo {\n    name = \"rehash-srv\";\n    sid = \"002\";\n};\n");

        try
        {
            var store = new IrcOptionsStore(new IrcOptions
            {
                ServerInfo = new ServerInfoOptions { Name = "srv", Sid = "001" },
                ConfigFile = "ircd.conf",
            });

            var env = new EnvStub(tmpDir);
            var mgr = new IrcConfigManager(store, env, Array.Empty<IConfigReloadListener>(), NullLogger<IrcConfigManager>.Instance);

            var res = mgr.TryRehash(confPath);
            Assert.True(res.Success);

            Assert.Equal("rehash-srv", store.Value.ServerInfo?.Name);
            Assert.Equal("002", store.Value.ServerInfo?.Sid);
        }
        finally
        {
            try { Directory.Delete(tmpDir, recursive: true); } catch { /* best effort */ }
        }
    }

    private sealed class EnvStub : IHostEnvironment
    {
        public EnvStub(string root)
        {
            ContentRootPath = root;
            EnvironmentName = "Tests";
            ApplicationName = "IRCd.Tests";
        }

        public string EnvironmentName { get; set; }
        public string ApplicationName { get; set; }
        public string ContentRootPath { get; set; }

        public IFileProvider ContentRootFileProvider
        {
            get => throw new NotImplementedException();
            set => throw new NotImplementedException();
        }
    }
}
