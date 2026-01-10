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
    using IRCd.Services;
    using IRCd.Shared.Options;
    using IRCd.Tests.TestDoubles;

    using Microsoft.Extensions.Logging.Abstractions;
    using Microsoft.Extensions.Options;

    using Xunit;

    public sealed class CtcpServicesTests
    {
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
        public async Task CtcpVersion_ToChanServ_ReturnsVersionReply()
        {
            var state = new ServerState();
            var sessions = new FakeSessionRegistry();
            var routing = new RoutingService(sessions, new IrcFormatter());
            var silence = new SilenceService();

            var opts = Options.Create(new IrcOptions
            {
                ServerInfo = new ServerInfoOptions { Name = "srv.example", Network = "UnrealBG", Version = "UnrealBG-IRCd" },
                Links = System.Array.Empty<LinkOptions>()
            });

            // Seed pseudo users into ServerState so the CTCP handler can find them.
            ServiceUserSeeder.EnsureServiceUsers(state, opts.Value);

            state.TryAddUser(new User { ConnectionId = "u1", Nick = "alice", UserName = "a", Host = "h", IsRegistered = true });

            var s = new TestSession { ConnectionId = "u1", Nick = "alice", UserName = "a", IsRegistered = true };
            sessions.Add(s);

            var links = new ServerLinkService(
                NullLogger<ServerLinkService>.Instance,
                new OptionsMonitorStub<IrcOptions>(opts.Value),
                state,
                routing,
                sessions,
                silence,
                new WatchService(opts, routing));

            var h = new PrivMsgHandler(routing, links, new HostmaskService(), opts, silence, services: null);

            await h.HandleAsync(s, new IrcMessage(null, "PRIVMSG", new[] { "ChanServ" }, "\x01VERSION\x01"), state, CancellationToken.None);

            Assert.Contains(s.Sent, l => l.Contains(" NOTICE alice :\x01VERSION ", System.StringComparison.OrdinalIgnoreCase));
            Assert.Contains(s.Sent, l => l.Contains("services", System.StringComparison.OrdinalIgnoreCase));
        }

        private sealed class OptionsMonitorStub<T> : IOptionsMonitor<T> where T : class
        {
            private readonly T _value;

            public OptionsMonitorStub(T value) => _value = value;

            public T CurrentValue => _value;

            public T Get(string? name) => _value;

            public System.IDisposable? OnChange(System.Action<T, string?> listener) => null;
        }
    }
}
