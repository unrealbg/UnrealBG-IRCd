namespace IRCd.Tests
{
    using System;
    using System.IO;
    using System.Net;
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Core.Abstractions;
    using IRCd.Core.Commands.Handlers;
    using IRCd.Core.Config;
    using IRCd.Core.Protocol;
    using IRCd.Core.State;
    using IRCd.Shared.Options;

    using Microsoft.Extensions.Hosting;
    using Microsoft.Extensions.Logging.Abstractions;
    using Microsoft.Extensions.Options;

    using Xunit;

    public sealed class RehashHandlerTests
    {
        private sealed class CapturingAuditLogService : IAuditLogService
        {
            public int CallCount { get; private set; }
            public string? Action { get; private set; }
            public string? SourceIp { get; private set; }
            public IReadOnlyDictionary<string, object?>? Extra { get; private set; }

            public ValueTask LogOperActionAsync(
                string action,
                IClientSession session,
                string? actorUid,
                string? actorNick,
                string? sourceIp,
                string? target,
                string? reason,
                IReadOnlyDictionary<string, object?>? extra,
                CancellationToken ct)
            {
                CallCount++;
                Action = action;
                SourceIp = sourceIp;
                Extra = extra;
                return ValueTask.CompletedTask;
            }
        }

        private sealed class TestSession : IClientSession
        {
            public string ConnectionId { get; set; } = "c1";
            public EndPoint RemoteEndPoint { get; } = new IPEndPoint(IPAddress.Loopback, 1234);
            public EndPoint LocalEndPoint { get; } = new IPEndPoint(IPAddress.Loopback, 6667);
            public bool IsSecureConnection => false;

            public ISet<string> EnabledCapabilities { get; } =
                new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);

            public string? Nick { get; set; }
            public string? UserName { get; set; }
            public bool PassAccepted { get; set; }
            public bool IsRegistered { get; set; }

            public System.DateTime LastActivityUtc { get; } = System.DateTime.UtcNow;
            public System.DateTime LastPingUtc { get; } = System.DateTime.UtcNow;
            public bool AwaitingPong { get; }
            public string? LastPingToken { get; }

            public string UserModes => string.Empty;
            public bool TryApplyUserModes(string modeString, out string appliedModes) { appliedModes = "+"; return true; }

            public void OnInboundLine() { }
            public void OnPingSent(string token) { }
            public void OnPongReceived(string? token) { }

            public readonly List<string> Sent = new();

            public ValueTask SendAsync(string line, CancellationToken ct = default)
            {
                Sent.Add(line);
                return ValueTask.CompletedTask;
            }

            public ValueTask CloseAsync(string reason, CancellationToken ct = default)
                => ValueTask.CompletedTask;
        }

        private sealed class EnvStub : IHostEnvironment
        {
            public string EnvironmentName { get; set; } = Environments.Production;
            public string ApplicationName { get; set; } = "IRCd";
            public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();
            public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
        }

        [Fact]
        public async Task Rehash_NetAdmin_ParsesConfigAndUpdatesOptions()
        {
            var tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".conf");
            try
            {
                File.WriteAllText(tmp, @"serverinfo { name = srv2; sid = 001; description = d; };
serverinfo {
    name = srv2
    sid = 001
    description = d
}
class {
    name = netadmin
    capabilities = netadmin
}
link {
    name = remote
    sid = 002
    host = 127.0.0.1
    port = 6900
    password = pw
}
");

                var optsObj = new IrcOptions
                {
                    ConfigFile = tmp,
                    ServerInfo = new ServerInfoOptions { Name = "srv", Sid = "001" },
                    Classes = new[] { new OperClassOptions { Name = "netadmin", Capabilities = new[] { "netadmin" } } }
                };

                var store = new IrcOptionsStore(optsObj);
                var env = new EnvStub();
                var config = new IrcConfigManager(store, env, Array.Empty<IRCd.Core.Abstractions.IConfigReloadListener>(), NullLogger<IrcConfigManager>.Instance);

                var state = new ServerState();
                state.TryAddUser(new User { ConnectionId = "c1", Nick = "nick", UserName = "user", IsRegistered = true, Modes = UserModes.Operator, OperClass = "netadmin" });

                var audit = new CapturingAuditLogService();
                var h = new RehashHandler(NullLogger<RehashHandler>.Instance, store, config, audit);
                var s = new TestSession { Nick = "nick", UserName = "user", IsRegistered = true };

                await h.HandleAsync(s, new IrcMessage(null, "REHASH", Array.Empty<string>(), null), state, CancellationToken.None);

                Assert.Contains(s.Sent, l => l.Contains(" 382 nick "));
                Assert.Single(store.Value.Links);
                Assert.Equal("remote", store.Value.Links[0].Name);
                Assert.Equal("002", store.Value.Links[0].Sid);

                Assert.Equal(1, audit.CallCount);
                Assert.Equal("REHASH", audit.Action);
                Assert.Equal("127.0.0.1", audit.SourceIp);
                Assert.NotNull(audit.Extra);
                Assert.True(audit.Extra!.TryGetValue("success", out var ok) && ok is true);
            }
            finally
            {
                try { File.Delete(tmp); } catch { }
            }
        }
    }
}
