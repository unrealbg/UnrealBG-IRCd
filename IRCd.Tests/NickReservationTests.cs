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

    using Microsoft.Extensions.Hosting;
    using Microsoft.Extensions.Logging.Abstractions;
    using Microsoft.Extensions.Options;

    using Xunit;

    public sealed class NickReservationTests
    {
        private sealed class TestMetrics : IMetrics
        {
            public void ConnectionAccepted(bool secure) { }
            public void ConnectionClosed(bool secure) { }
            public void UserRegistered() { }
            public void ChannelCreated() { }
            public void CommandProcessed(string command) { }
            public void FloodKick() { }
            public void OutboundQueueDepth(long depth) { }
            public void OutboundQueueDrop() { }
            public void OutboundQueueOverflowDisconnect() { }
            public MetricsSnapshot GetSnapshot() => new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        }

        private sealed class TestHostEnv : IHostEnvironment
        {
            public string EnvironmentName { get; set; } = Environments.Development;
            public string ApplicationName { get; set; } = "IRCd.Tests";
            public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
            public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
        }

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

        private static (ServerState State, FakeSessionRegistry Sessions, NickHandler Handler) BuildNickHarness()
        {
            var opts = Options.Create(new IrcOptions
            {
                ServerInfo = new ServerInfoOptions { Name = "srv", Network = "net", Sid = "001" },
                Motd = new MotdOptions { Lines = new[] { "hi" } },
                Links = Array.Empty<LinkOptions>()
            });

            var sessions = new FakeSessionRegistry();
            var routing = new RoutingService(sessions, new IrcFormatter());
            var watch = new WatchService(opts, routing);

            var motd = new MotdSender(new OptionsMonitorStub<IrcOptions>(opts.Value), new TestHostEnv(), NullLogger<MotdSender>.Instance);
            var banRepo = new InMemoryBanRepository();
            var banService = new BanService(banRepo, NullLogger<BanService>.Instance);
            var reg = new RegistrationService(opts, motd, new TestMetrics(), watch, banService, auth: null);

            var silence = new SilenceService();
            var links = new ServerLinkService(
                NullLogger<ServerLinkService>.Instance,
                new OptionsMonitorStub<IrcOptions>(opts.Value),
                new ServerState(),
                routing,
                sessions,
                silence,
                watch);

            var handler = new NickHandler(
                routing,
                reg,
                links,
                new HostmaskService(),
                new WhowasService(),
                watch,
                opts,
                sessions,
                NullLogger<NickHandler>.Instance,
                serviceEvents: null);

            return (new ServerState(), sessions, handler);
        }

        [Theory]
        [InlineData("ChanServ")]
        [InlineData("CS")]
        [InlineData("NickServ")]
        [InlineData("NS")]
        [InlineData("BotServ")]
        [InlineData("BS")]
        [InlineData("HostServ")]
        [InlineData("HS")]
        [InlineData("RootServ")]
        [InlineData("RS")]
        [InlineData("Global")]
        [InlineData("GS")]
        [InlineData("InfoServ")]
        [InlineData("IS")]
        [InlineData("StatServ")]
        [InlineData("AdminServ")]
        [InlineData("DevServ")]
        [InlineData("Services")]
        [InlineData("MyService")]
        [InlineData("MyServ")]
        [InlineData("whateverSeRv")]
        public async Task Nick_ReservedServiceNames_AreRejected_ForNormalUsers(string newNick)
        {
            var (state, sessions, handler) = BuildNickHarness();

            state.TryAddUser(new User { ConnectionId = "c1" });

            var s = new TestSession { ConnectionId = "c1", UserName = "user" };
            sessions.Add(s);

            await handler.HandleAsync(s, new IrcMessage(null, "NICK", new[] { newNick }, null), state, CancellationToken.None);

            Assert.True(state.TryGetUser("c1", out var u) && u is not null);
            Assert.True(string.IsNullOrWhiteSpace(u!.Nick));
            Assert.Null(s.Nick);
            Assert.Contains(s.Sent, line => line.Contains(" 433 "));
        }

        [Theory]
        [InlineData("ChanServ")]
        [InlineData("CS")]
        [InlineData("NickServ")]
        [InlineData("NS")]
        [InlineData("MyServ")]
        public void ServerState_TrySetNick_AllowsReserved_ForServiceUsers(string reserved)
        {
            var state = new ServerState();
            state.TryAddUser(new User { ConnectionId = "svc", IsService = true });

            Assert.True(state.TrySetNick("svc", reserved));
            Assert.True(state.TryGetConnectionIdByNick(reserved, out var conn));
            Assert.Equal("svc", conn);
        }

        private sealed class OptionsMonitorStub<T> : IOptionsMonitor<T>
        {
            public OptionsMonitorStub(T value) => CurrentValue = value;

            public T CurrentValue { get; }

            public T Get(string? name) => CurrentValue;

            public IDisposable? OnChange(Action<T, string?> listener) => null;
        }
    }
}
