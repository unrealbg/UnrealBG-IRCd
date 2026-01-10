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
    using IRCd.Services.DependencyInjection;
    using IRCd.Shared.Options;

    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.Options;

    using Xunit;

    public sealed class SeenServTests
    {
        private sealed class TestSession : IClientSession
        {
            public string ConnectionId { get; set; } = "c1";
            public EndPoint RemoteEndPoint { get; } = new IPEndPoint(IPAddress.Loopback, 1234);
            public EndPoint LocalEndPoint { get; } = new IPEndPoint(IPAddress.Loopback, 6667);
            public bool IsSecureConnection { get; set; }

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

        private static (ServerState State, InMemorySessionRegistry Sessions, PrivMsgHandler Handler, IServiceSessionEvents Events) BuildHarness()
        {
            var state = new ServerState();
            var sessions = new InMemorySessionRegistry();
            var routing = new RoutingService(sessions, new IrcFormatter());
            var silence = new SilenceService();

            var services = new ServiceCollection();
            services.AddSingleton<ISessionRegistry>(sessions);
            services.AddSingleton(routing);
            services.AddSingleton(silence);
            services.AddSingleton(new HostmaskService());

            var opts = Options.Create(new IrcOptions { ServerInfo = new ServerInfoOptions { Name = "srv", Sid = "001" } });
            services.AddSingleton<IOptions<IrcOptions>>(opts);
            services.AddIrcServices();

            var sp = services.BuildServiceProvider();

            var links = new ServerLinkService(
                Microsoft.Extensions.Logging.Abstractions.NullLogger<ServerLinkService>.Instance,
                new OptionsMonitorStub<IrcOptions>(opts.Value),
                state,
                routing,
                sessions,
                silence,
                new WatchService(opts, routing));

            var h = new PrivMsgHandler(
                routing,
                links,
                sp.GetRequiredService<HostmaskService>(),
                opts,
                silence,
                sp.GetRequiredService<IServiceCommandDispatcher>());

            return (state, sessions, h, sp.GetRequiredService<IServiceSessionEvents>());
        }

        private sealed class OptionsMonitorStub<T> : IOptionsMonitor<T> where T : class
        {
            private readonly T _value;

            public OptionsMonitorStub(T value) => _value = value;

            public T CurrentValue => _value;

            public T Get(string? name) => _value;

            public System.IDisposable? OnChange(System.Action<T, string?> listener) => null;
        }

        [Fact]
        public async Task SeenServ_OnlineUser_SaysOnline()
        {
            var (state, sessions, h, _) = BuildHarness();

            ServiceUserSeeder.EnsureServiceUsers(state, new IrcOptions { ServerInfo = new ServerInfoOptions { Name = "srv", Network = "UnrealBG" } });

            state.TryAddUser(new User { ConnectionId = "b", Nick = "bob", UserName = "b", Host = "h2", IsRegistered = true });
            var bob = new TestSession { ConnectionId = "b", Nick = "bob", UserName = "b", IsRegistered = true };
            sessions.Add(bob);

            state.TryAddUser(new User { ConnectionId = "a", Nick = "alice", UserName = "a", Host = "h", IsRegistered = true });
            var alice = new TestSession { ConnectionId = "a", Nick = "alice", UserName = "a", IsRegistered = true };
            sessions.Add(alice);

            alice.Sent.Clear();
            await h.HandleAsync(alice, new IrcMessage(null, "PRIVMSG", new[] { "SeenServ" }, "bob"), state, CancellationToken.None);
            Assert.Contains(alice.Sent, l => l.Contains("bob is currently online", System.StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task SeenServ_Quit_RecordsAndReports()
        {
            var (state, sessions, h, events) = BuildHarness();

            ServiceUserSeeder.EnsureServiceUsers(state, new IrcOptions { ServerInfo = new ServerInfoOptions { Name = "srv", Network = "UnrealBG" } });

            state.TryAddUser(new User { ConnectionId = "b", Nick = "bob", UserName = "b", Host = "h2", IsRegistered = true });
            var bob = new TestSession { ConnectionId = "b", Nick = "bob", UserName = "b", IsRegistered = true };
            sessions.Add(bob);

            await events.OnQuitAsync(bob, "Client Quit", state, CancellationToken.None);
            state.RemoveUser("b");

            state.TryAddUser(new User { ConnectionId = "a", Nick = "alice", UserName = "a", Host = "h", IsRegistered = true });
            var alice = new TestSession { ConnectionId = "a", Nick = "alice", UserName = "a", IsRegistered = true };
            sessions.Add(alice);

            alice.Sent.Clear();
            await h.HandleAsync(alice, new IrcMessage(null, "PRIVMSG", new[] { "SeenServ" }, "bob"), state, CancellationToken.None);
            Assert.Contains(alice.Sent, l => l.Contains("was last seen", System.StringComparison.OrdinalIgnoreCase));
            Assert.Contains(alice.Sent, l => l.Contains("quit:", System.StringComparison.OrdinalIgnoreCase));
        }
    }
}
