namespace IRCd.Tests
{
    using System.Net;
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Core.Abstractions;
    using IRCd.Core.Commands.Handlers;
    using IRCd.Core.Protocol;
    using IRCd.Core.State;
    using IRCd.Services;
    using IRCd.Shared.Options;
    using IRCd.Tests.TestDoubles;

    using Microsoft.Extensions.Options;

    using Xunit;

    public sealed class WhoisServicesTests
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
            public bool TryApplyUserModes(string modeString, out string appliedModes) { appliedModes = modeString; return true; }

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
        public async Task Whois_ChanServ_ReturnsNormalWhoisInfo()
        {
            var state = new ServerState();
            var sessions = new FakeSessionRegistry();

            var opts = Options.Create(new IrcOptions
            {
                ServerInfo = new ServerInfoOptions { Name = "srv.example", Network = "UnrealBG", Description = "Test IRCd" }
            });

            // Seed pseudo users (NickServ/ChanServ/OperServ)
            ServiceUserSeeder.EnsureServiceUsers(state, opts.Value);

            // Requesting user
            state.TryAddUser(new User { ConnectionId = "u1", Nick = "alice", UserName = "a", Host = "h", IsRegistered = true });

            var s = new TestSession { ConnectionId = "u1", Nick = "alice", UserName = "a", IsRegistered = true };
            sessions.Add(s);

            var h = new WhoisHandler(sessions, opts);

            await h.HandleAsync(s, new IrcMessage(null, "WHOIS", new[] { "ChanServ" }, null), state, CancellationToken.None);

            Assert.DoesNotContain(s.Sent, l => l.Contains(" 401 ", System.StringComparison.OrdinalIgnoreCase));
            Assert.Contains(s.Sent, l => l.Contains(" 311 alice ChanServ ChanServ services.", System.StringComparison.OrdinalIgnoreCase));
            Assert.Contains(s.Sent, l => l.Contains(" 312 alice ChanServ services.", System.StringComparison.OrdinalIgnoreCase)
                && l.Contains("UnrealBG network services", System.StringComparison.OrdinalIgnoreCase));
            Assert.Contains(s.Sent, l => l.Contains(" 318 alice ChanServ ", System.StringComparison.OrdinalIgnoreCase));
        }
    }
}
