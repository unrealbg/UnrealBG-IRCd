namespace IRCd.Tests
{
    using System.Net;
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Core.Abstractions;
    using IRCd.Core.Commands.Handlers;
    using IRCd.Core.Protocol;
    using IRCd.Core.Services;
    using IRCd.Core.State;
    using IRCd.Shared.Options;

    using Microsoft.Extensions.Options;

    using Xunit;

    public sealed class MetricsHandlerTests
    {
        private sealed class TestSession : IClientSession
        {
            public string ConnectionId { get; set; } = "c1";
            public EndPoint RemoteEndPoint { get; } = new IPEndPoint(IPAddress.Loopback, 1234);
            public EndPoint LocalEndPoint { get; } = new IPEndPoint(IPAddress.Loopback, 6667);
            public bool IsSecureConnection => false;

            public ISet<string> EnabledCapabilities { get; } =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            public string? Nick { get; set; }
            public string? UserName { get; set; }
            public bool PassAccepted { get; set; }
            public bool IsRegistered { get; set; }

            public DateTime LastActivityUtc { get; } = DateTime.UtcNow;
            public DateTime LastPingUtc { get; } = DateTime.UtcNow;
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

        [Fact]
        public async Task Metrics_NonOper_Gets481()
        {
            var state = new ServerState();
            state.TryAddUser(new User { ConnectionId = "c1", Nick = "nick", UserName = "user", IsRegistered = true });

            var opts = Options.Create(new IrcOptions { ServerInfo = new ServerInfoOptions { Name = "srv" } });
            var metrics = new DefaultMetrics();
            var h = new MetricsHandler(metrics, opts);

            var s = new TestSession { Nick = "nick", UserName = "user", IsRegistered = true };

            await h.HandleAsync(s, new IrcMessage(null, "METRICS", new string[0], null), state, CancellationToken.None);

            Assert.Contains(s.Sent, l => l.Contains(" 481 nick "));
        }

        [Fact]
        public async Task Metrics_Oper_GetsSnapshotLines()
        {
            var state = new ServerState();
            state.TryAddUser(new User { ConnectionId = "c1", Nick = "nick", UserName = "user", IsRegistered = true, Modes = UserModes.Operator });

            var opts = Options.Create(new IrcOptions { ServerInfo = new ServerInfoOptions { Name = "srv" } });
            var metrics = new DefaultMetrics();
            metrics.ConnectionAccepted(secure: false);
            metrics.UserRegistered();
            metrics.CommandProcessed("NICK");

            var h = new MetricsHandler(metrics, opts);
            var s = new TestSession { Nick = "nick", UserName = "user", IsRegistered = true };

            await h.HandleAsync(s, new IrcMessage(null, "METRICS", new string[0], null), state, CancellationToken.None);

            Assert.Contains(s.Sent, l => l.Contains("NOTICE nick :METRICS"));
            Assert.Contains(s.Sent, l => l.Contains("End of /METRICS"));
        }

        [Fact]
        public async Task Metrics_OperWithClassWithoutCapability_Gets481()
        {
            var state = new ServerState();
            state.TryAddUser(new User
            {
                ConnectionId = "c1",
                Nick = "nick",
                UserName = "user",
                IsRegistered = true,
                Modes = UserModes.Operator,
                OperClass = "helper"
            });

            var opts = Options.Create(new IrcOptions
            {
                ServerInfo = new ServerInfoOptions { Name = "srv" },
                Classes = new[]
                {
                    new OperClassOptions { Name = "helper", Capabilities = new[] { "whois" } }
                }
            });

            var metrics = new DefaultMetrics();
            var h = new MetricsHandler(metrics, opts);
            var s = new TestSession { Nick = "nick", UserName = "user", IsRegistered = true };

            await h.HandleAsync(s, new IrcMessage(null, "METRICS", new string[0], null), state, CancellationToken.None);

            Assert.Contains(s.Sent, l => l.Contains(" 481 nick "));
        }

        [Fact]
        public async Task Metrics_OperWithNetAdminCapability_GetsSnapshotLines()
        {
            var state = new ServerState();
            state.TryAddUser(new User
            {
                ConnectionId = "c1",
                Nick = "nick",
                UserName = "user",
                IsRegistered = true,
                Modes = UserModes.Operator,
                OperClass = "netadmin"
            });

            var opts = Options.Create(new IrcOptions
            {
                ServerInfo = new ServerInfoOptions { Name = "srv" },
                Classes = new[]
                {
                    new OperClassOptions { Name = "netadmin", Capabilities = new[] { "netadmin" } }
                }
            });

            var metrics = new DefaultMetrics();
            metrics.ConnectionAccepted(secure: false);
            metrics.CommandProcessed("NICK");

            var h = new MetricsHandler(metrics, opts);
            var s = new TestSession { Nick = "nick", UserName = "user", IsRegistered = true };

            await h.HandleAsync(s, new IrcMessage(null, "METRICS", new string[0], null), state, CancellationToken.None);

            Assert.Contains(s.Sent, l => l.Contains("NOTICE nick :METRICS"));
            Assert.Contains(s.Sent, l => l.Contains("End of /METRICS"));
        }
    }
}
