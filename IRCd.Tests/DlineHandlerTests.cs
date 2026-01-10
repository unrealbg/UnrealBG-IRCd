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
    using IRCd.Tests.TestDoubles;

    using Microsoft.Extensions.Logging.Abstractions;
    using Microsoft.Extensions.Options;

    using Xunit;

    public sealed class DlineHandlerTests
    {
        private sealed class NoopBanEnforcer : IBanEnforcer
        {
            public BanEntry? LastBan { get; private set; }

            public Task EnforceBanImmediatelyAsync(BanEntry ban, CancellationToken ct = default)
            {
                LastBan = ban;
                return Task.CompletedTask;
            }
        }

        private sealed class TestSession : IClientSession
        {
            public string ConnectionId { get; set; } = "c1";
            public EndPoint RemoteEndPoint { get; set; } = new IPEndPoint(IPAddress.Loopback, 1234);
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
            public string? ClosedReason { get; private set; }

            public ValueTask SendAsync(string line, CancellationToken ct = default)
            {
                Sent.Add(line);
                return ValueTask.CompletedTask;
            }

            public ValueTask CloseAsync(string reason, CancellationToken ct = default)
            {
                ClosedReason = reason;
                return ValueTask.CompletedTask;
            }
        }

        [Fact]
        public async Task Dline_OperWithClassWithoutCapability_Gets481()
        {
            var state = new ServerState();
            state.TryAddUser(new User { ConnectionId = "c1", Nick = "oper", UserName = "user", IsRegistered = true, Modes = UserModes.Operator, OperClass = "helper" });

            var optsObj = new IrcOptions
            {
                ServerInfo = new ServerInfoOptions { Name = "srv" },
                Classes = new[] { new OperClassOptions { Name = "helper", Capabilities = new[] { "metrics" } } }
            };

            var opts = Options.Create(optsObj);
        var repo = new InMemoryBanRepository();
        var banService = new BanService(repo, NullLogger<BanService>.Instance);
        var enforcer = new NoopBanEnforcer();
        var h = new DlineHandler(opts, banService, enforcer);

            var s = new TestSession { Nick = "oper", UserName = "user", IsRegistered = true };

            await h.HandleAsync(s, new IrcMessage(null, "DLINE", new[] { "127.0.0.*" }, "no"), state, CancellationToken.None);

            Assert.Contains(s.Sent, l => l.Contains(" 481 oper "));
        }

        [Fact]
        public async Task Dline_NetAdmin_AddsAndDisconnectsMatchingLocalUserByIp()
        {
            var state = new ServerState();
            state.TryAddUser(new User { ConnectionId = "c1", Nick = "oper", UserName = "user", IsRegistered = true, Modes = UserModes.Operator, OperClass = "netadmin" });

            state.TryAddUser(new User { ConnectionId = "u1", Nick = "victim", UserName = "v", Host = "some.host", RemoteIp = "203.0.113.9", IsRegistered = true, IsRemote = false });

            var optsObj = new IrcOptions
            {
                ServerInfo = new ServerInfoOptions { Name = "srv" },
                Classes = new[] { new OperClassOptions { Name = "netadmin", Capabilities = new[] { "netadmin", "dline" } } }
            };

            var opts = Options.Create(optsObj);
            var reg = new FakeSessionRegistry();

            var victimSession = new TestSession
            {
                ConnectionId = "u1",
                Nick = "victim",
                UserName = "v",
                IsRegistered = true,
                RemoteEndPoint = new IPEndPoint(IPAddress.Parse("203.0.113.9"), 1234)
            };
            reg.Add(victimSession);

            var repo = new InMemoryBanRepository();
            var banService = new BanService(repo, NullLogger<BanService>.Instance);
            var routing = new RoutingService(reg, new IrcFormatter());
            var enforcement = new BanEnforcementService(opts, banService, state, reg, routing, NullLogger<BanEnforcementService>.Instance);
            var h = new DlineHandler(opts, banService, enforcement);

            var operSession = new TestSession { ConnectionId = "c1", Nick = "oper", UserName = "user", IsRegistered = true };

            await h.HandleAsync(operSession, new IrcMessage(null, "DLINE", new[] { "203.0.113.*" }, "testing"), state, CancellationToken.None);

            var bans = await banService.GetActiveByTypeAsync(BanType.DLINE, CancellationToken.None);
            Assert.NotEmpty(bans);
            Assert.Contains(bans, b => b.Mask == "203.0.113.*");
            Assert.Equal("D-Lined", victimSession.ClosedReason);
            Assert.False(state.TryGetUser("u1", out _));
        }

        [Fact]
        public async Task Undline_NetAdmin_RemovesEntry()
        {
            var state = new ServerState();
            state.TryAddUser(new User { ConnectionId = "c1", Nick = "oper", UserName = "user", IsRegistered = true, Modes = UserModes.Operator, OperClass = "netadmin" });

            var optsObj = new IrcOptions
            {
                ServerInfo = new ServerInfoOptions { Name = "srv" },
                Classes = new[] { new OperClassOptions { Name = "netadmin", Capabilities = new[] { "netadmin", "dline" } } },
                DLines = new[] { new DLineOptions { Mask = "203.0.113.*", Reason = "x" } }
            };

            var opts = Options.Create(optsObj);
            var repo = new InMemoryBanRepository();
            var banService = new BanService(repo, NullLogger<BanService>.Instance);
            await banService.AddAsync(new BanEntry { Type = BanType.DLINE, Mask = "203.0.113.*", Reason = "x", SetBy = "oper" }, CancellationToken.None);
            var h = new UndlineHandler(opts, banService);

            var operSession = new TestSession { ConnectionId = "c1", Nick = "oper", UserName = "user", IsRegistered = true };

            await h.HandleAsync(operSession, new IrcMessage(null, "UNDLINE", new[] { "203.0.113.*" }, null), state, CancellationToken.None);

            var bans = await banService.GetActiveByTypeAsync(BanType.DLINE, CancellationToken.None);
            Assert.Empty(bans);
            Assert.Contains(operSession.Sent, l => l.Contains("NOTICE oper :UNDLINE removed 203.0.113.*"));
        }
    }
}
