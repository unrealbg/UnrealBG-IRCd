namespace IRCd.Tests
{
    using System.Net;
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Core.Abstractions;
    using IRCd.Core.Commands.Handlers;
    using IRCd.Core.Protocol;
    using IRCd.Core.State;
    using IRCd.Shared.Options;

    using Microsoft.Extensions.Hosting;
    using Microsoft.Extensions.Options;

    using Xunit;

    public sealed class DieHandlerTests
    {
        private sealed class CapturingAuditLogService : IAuditLogService
        {
            public int CallCount { get; private set; }
            public string? Action { get; private set; }
            public string? SourceIp { get; private set; }

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

        private sealed class LifetimeStub : IHostApplicationLifetime
        {
            public CancellationToken ApplicationStarted => CancellationToken.None;
            public CancellationToken ApplicationStopping => CancellationToken.None;
            public CancellationToken ApplicationStopped => CancellationToken.None;

            public bool StopCalled { get; private set; }

            public void StopApplication() => StopCalled = true;
        }

        [Fact]
        public async Task Die_OperWithClassWithoutCapability_Gets481()
        {
            var state = new ServerState();
            state.TryAddUser(new User { ConnectionId = "c1", Nick = "nick", UserName = "user", IsRegistered = true, Modes = UserModes.Operator, OperClass = "helper" });

            var opts = Options.Create(new IrcOptions
            {
                ServerInfo = new ServerInfoOptions { Name = "srv" },
                Classes = new[] { new OperClassOptions { Name = "helper", Capabilities = new[] { "metrics" } } }
            });

            var life = new LifetimeStub();
            var audit = new CapturingAuditLogService();
            var h = new DieHandler(opts, life, audit);

            var s = new TestSession { Nick = "nick", UserName = "user", IsRegistered = true };

            await h.HandleAsync(s, new IrcMessage(null, "DIE", new string[0], null), state, CancellationToken.None);

            Assert.Contains(s.Sent, l => l.Contains(" 481 nick "));
            Assert.False(life.StopCalled);
            Assert.Equal(0, audit.CallCount);
        }

        [Fact]
        public async Task Die_NetAdmin_StopsApplication()
        {
            var state = new ServerState();
            state.TryAddUser(new User { ConnectionId = "c1", Nick = "nick", UserName = "user", IsRegistered = true, Modes = UserModes.Operator, OperClass = "netadmin" });

            var opts = Options.Create(new IrcOptions
            {
                ServerInfo = new ServerInfoOptions { Name = "srv" },
                Classes = new[] { new OperClassOptions { Name = "netadmin", Capabilities = new[] { "netadmin" } } }
            });

            var life = new LifetimeStub();
            var audit = new CapturingAuditLogService();
            var h = new DieHandler(opts, life, audit);

            var s = new TestSession { Nick = "nick", UserName = "user", IsRegistered = true };

            await h.HandleAsync(s, new IrcMessage(null, "DIE", new string[0], null), state, CancellationToken.None);

            Assert.True(life.StopCalled);
            Assert.Contains(s.Sent, l => l.Contains("NOTICE nick :Server is shutting down"));
            Assert.Equal(1, audit.CallCount);
            Assert.Equal("DIE", audit.Action);
            Assert.Equal("127.0.0.1", audit.SourceIp);
        }
    }
}
