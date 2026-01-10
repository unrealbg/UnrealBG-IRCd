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

    public sealed class KlineHandlerTests
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
        public async Task Kline_OperWithClassWithoutCapability_Gets481()
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
        var h = new KlineHandler(opts, banService, enforcer);

            var s = new TestSession { Nick = "oper", UserName = "user", IsRegistered = true };

            await h.HandleAsync(s, new IrcMessage(null, "KLINE", new[] { "bad.host" }, "no"), state, CancellationToken.None);

            Assert.Contains(s.Sent, l => l.Contains(" 481 oper "));
        }

        [Fact]
        public async Task Kline_NetAdmin_AddsAndDisconnectsMatchingLocalUser()
        {
            var state = new ServerState();
            state.TryAddUser(new User { ConnectionId = "c1", Nick = "oper", UserName = "user", IsRegistered = true, Modes = UserModes.Operator, OperClass = "netadmin" });

            state.TryAddUser(new User { ConnectionId = "u1", Nick = "victim", UserName = "v", Host = "bad.host", IsRegistered = true, IsRemote = false });

            var optsObj = new IrcOptions
            {
                ServerInfo = new ServerInfoOptions { Name = "srv" },
                Classes = new[] { new OperClassOptions { Name = "netadmin", Capabilities = new[] { "netadmin", "kline" } } }
            };

            var opts = Options.Create(optsObj);
        var reg = new FakeSessionRegistry();
        var victimSession = new TestSession { ConnectionId = "u1", Nick = "victim", UserName = "v", IsRegistered = true };
        reg.Add(victimSession);

        var repo = new InMemoryBanRepository();
        var banService = new BanService(repo, NullLogger<BanService>.Instance);
        var routing = new RoutingService(reg, new IrcFormatter());
        var enforcement = new BanEnforcementService(opts, banService, state, reg, routing, NullLogger<BanEnforcementService>.Instance);
        var h = new KlineHandler(opts, banService, enforcement);

            var operSession = new TestSession { ConnectionId = "c1", Nick = "oper", UserName = "user", IsRegistered = true };

            await h.HandleAsync(operSession, new IrcMessage(null, "KLINE", new[] { "bad.host" }, "testing"), state, CancellationToken.None);

            var bans = await banService.GetActiveByTypeAsync(BanType.KLINE, CancellationToken.None);
            Assert.NotEmpty(bans);
            Assert.Contains(bans, b => b.Mask == "bad.host");
            Assert.Equal("K-Lined", victimSession.ClosedReason);
            Assert.False(state.TryGetUser("u1", out _));
        }

        [Fact]
        public async Task Unkline_NetAdmin_RemovesEntry()
        {
            var state = new ServerState();
            state.TryAddUser(new User { ConnectionId = "c1", Nick = "oper", UserName = "user", IsRegistered = true, Modes = UserModes.Operator, OperClass = "netadmin" });

            var optsObj = new IrcOptions
            {
                ServerInfo = new ServerInfoOptions { Name = "srv" },
                Classes = new[] { new OperClassOptions { Name = "netadmin", Capabilities = new[] { "netadmin", "kline" } } },
                KLines = new[] { new KLineOptions { Mask = "bad.host", Reason = "x" } }
            };

            var opts = Options.Create(optsObj);
            var repo = new InMemoryBanRepository();
            var banService = new BanService(repo, NullLogger<BanService>.Instance);
            await banService.AddAsync(new BanEntry { Type = BanType.KLINE, Mask = "bad.host", Reason = "x", SetBy = "oper" }, CancellationToken.None);
            var h = new UnklineHandler(opts, banService);

            var operSession = new TestSession { ConnectionId = "c1", Nick = "oper", UserName = "user", IsRegistered = true };

            await h.HandleAsync(operSession, new IrcMessage(null, "UNKLINE", new[] { "bad.host" }, null), state, CancellationToken.None);

            var bans = await banService.GetActiveByTypeAsync(BanType.KLINE, CancellationToken.None);
            Assert.Empty(bans);
            Assert.Contains(operSession.Sent, l => l.Contains("NOTICE oper :UNKLINE removed bad.host"));
        }
    }
}
