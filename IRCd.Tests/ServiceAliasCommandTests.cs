namespace IRCd.Tests
{
    using System;
    using System.Net;
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Core.Abstractions;
    using IRCd.Core.Commands.Handlers;
    using IRCd.Core.Protocol;
    using IRCd.Core.Services;
    using IRCd.Core.State;
    using IRCd.Services.DependencyInjection;
    using IRCd.Shared.Options;
    using IRCd.Tests.TestDoubles;

    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.Logging.Abstractions;
    using Microsoft.Extensions.Options;

    using Xunit;

    public sealed class ServiceAliasCommandTests
    {
        private sealed class TestSession : IClientSession
        {
            public string ConnectionId { get; set; } = "u1";
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
        public async Task NS_Command_RoutesToNickServ()
        {
            var state = new ServerState();
            var sessions = new FakeSessionRegistry();
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
            using var sp = services.BuildServiceProvider();

            var links = new ServerLinkService(NullLogger<ServerLinkService>.Instance, new OptionsMonitorStub<IrcOptions>(opts.Value), state, routing, sessions, silence, new WatchService(opts, routing));
            _ = links;

            state.TryAddUser(new User { ConnectionId = "u1", Nick = "alice", UserName = "a", Host = "h", IsRegistered = true });

            var s = new TestSession { ConnectionId = "u1", Nick = "alice", UserName = "a", IsRegistered = true };
            sessions.Add(s);

            var h = new NsHandler(sp.GetRequiredService<IServiceCommandDispatcher>());
            await h.HandleAsync(s, new IrcMessage(null, "NS", new[] { "HELP" }, null), state, CancellationToken.None);

            Assert.DoesNotContain(s.Sent, l => l.Contains(" 421 ", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(s.Sent, l => l.Contains("NOTICE alice", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task IS_Command_RoutesToInfoServ()
        {
            var state = new ServerState();
            var sessions = new FakeSessionRegistry();
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
            using var sp = services.BuildServiceProvider();

            var links = new ServerLinkService(NullLogger<ServerLinkService>.Instance, new OptionsMonitorStub<IrcOptions>(opts.Value), state, routing, sessions, silence, new WatchService(opts, routing));
            _ = links;

            state.TryAddUser(new User { ConnectionId = "u1", Nick = "alice", UserName = "a", Host = "h", IsRegistered = true });

            var s = new TestSession { ConnectionId = "u1", Nick = "alice", UserName = "a", IsRegistered = true };
            sessions.Add(s);

            var h = new IsHandler(sp.GetRequiredService<IServiceCommandDispatcher>());
            await h.HandleAsync(s, new IrcMessage(null, "IS", new[] { "HELP" }, null), state, CancellationToken.None);

            Assert.DoesNotContain(s.Sent, l => l.Contains(" 421 ", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(s.Sent, l => l.Contains("NOTICE alice", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task InfoServ_Command_RoutesToInfoServ()
        {
            var state = new ServerState();
            var sessions = new FakeSessionRegistry();
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
            using var sp = services.BuildServiceProvider();

            var links = new ServerLinkService(NullLogger<ServerLinkService>.Instance, new OptionsMonitorStub<IrcOptions>(opts.Value), state, routing, sessions, silence, new WatchService(opts, routing));
            _ = links;

            state.TryAddUser(new User { ConnectionId = "u1", Nick = "alice", UserName = "a", Host = "h", IsRegistered = true });

            var s = new TestSession { ConnectionId = "u1", Nick = "alice", UserName = "a", IsRegistered = true };
            sessions.Add(s);

            var h = new InfoServCommandHandler(sp.GetRequiredService<IServiceCommandDispatcher>());
            await h.HandleAsync(s, new IrcMessage(null, "INFOSERV", new[] { "HELP" }, null), state, CancellationToken.None);

            Assert.DoesNotContain(s.Sent, l => l.Contains(" 421 ", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(s.Sent, l => l.Contains("NOTICE alice", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task StatServ_Command_RoutesToStatServ()
        {
            var state = new ServerState();
            var sessions = new FakeSessionRegistry();
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
            using var sp = services.BuildServiceProvider();

            var links = new ServerLinkService(NullLogger<ServerLinkService>.Instance, new OptionsMonitorStub<IrcOptions>(opts.Value), state, routing, sessions, silence, new WatchService(opts, routing));
            _ = links;

            state.TryAddUser(new User { ConnectionId = "u1", Nick = "alice", UserName = "a", Host = "h", IsRegistered = true });

            var s = new TestSession { ConnectionId = "u1", Nick = "alice", UserName = "a", IsRegistered = true };
            sessions.Add(s);

            var h = new StatServCommandHandler(sp.GetRequiredService<IServiceCommandDispatcher>());
            await h.HandleAsync(s, new IrcMessage(null, "STATSERV", new[] { "HELP" }, null), state, CancellationToken.None);

            Assert.DoesNotContain(s.Sent, l => l.Contains(" 421 ", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(s.Sent, l => l.Contains("NOTICE alice", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task AdminServ_Command_RoutesToAdminServ()
        {
            var state = new ServerState();
            var sessions = new FakeSessionRegistry();
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
            using var sp = services.BuildServiceProvider();

            var links = new ServerLinkService(NullLogger<ServerLinkService>.Instance, new OptionsMonitorStub<IrcOptions>(opts.Value), state, routing, sessions, silence, new WatchService(opts, routing));
            _ = links;

            state.TryAddUser(new User { ConnectionId = "u1", Nick = "alice", UserName = "a", Host = "h", IsRegistered = true });

            var s = new TestSession { ConnectionId = "u1", Nick = "alice", UserName = "a", IsRegistered = true };
            sessions.Add(s);

            var h = new AdminServCommandHandler(sp.GetRequiredService<IServiceCommandDispatcher>());
            await h.HandleAsync(s, new IrcMessage(null, "ADMINSERV", new[] { "HELP" }, null), state, CancellationToken.None);

            Assert.DoesNotContain(s.Sent, l => l.Contains(" 421 ", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(s.Sent, l => l.Contains("NOTICE alice", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task DevServ_Command_RoutesToDevServ()
        {
            var state = new ServerState();
            var sessions = new FakeSessionRegistry();
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
            using var sp = services.BuildServiceProvider();

            var links = new ServerLinkService(NullLogger<ServerLinkService>.Instance, new OptionsMonitorStub<IrcOptions>(opts.Value), state, routing, sessions, silence, new WatchService(opts, routing));
            _ = links;

            state.TryAddUser(new User { ConnectionId = "u1", Nick = "alice", UserName = "a", Host = "h", IsRegistered = true });

            var s = new TestSession { ConnectionId = "u1", Nick = "alice", UserName = "a", IsRegistered = true };
            sessions.Add(s);

            var h = new DevServCommandHandler(sp.GetRequiredService<IServiceCommandDispatcher>());
            await h.HandleAsync(s, new IrcMessage(null, "DEVSERV", new[] { "HELP" }, null), state, CancellationToken.None);

            Assert.DoesNotContain(s.Sent, l => l.Contains(" 421 ", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(s.Sent, l => l.Contains("NOTICE alice", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task InfoServ_Info_ShowsNetworkAndServer()
        {
            var state = new ServerState();
            var sessions = new FakeSessionRegistry();
            var routing = new RoutingService(sessions, new IrcFormatter());
            var silence = new SilenceService();

            var services = new ServiceCollection();
            services.AddSingleton<ISessionRegistry>(sessions);
            services.AddSingleton(routing);
            services.AddSingleton(silence);
            services.AddSingleton(new HostmaskService());

            var opts = Options.Create(new IrcOptions
            {
                ServerInfo = new ServerInfoOptions { Name = "srv", Network = "net", Sid = "001", Description = "desc", Version = "v" }
            });
            services.AddSingleton<IOptions<IrcOptions>>(opts);

            services.AddIrcServices();
            using var sp = services.BuildServiceProvider();

            var links = new ServerLinkService(NullLogger<ServerLinkService>.Instance, new OptionsMonitorStub<IrcOptions>(opts.Value), state, routing, sessions, silence, new WatchService(opts, routing));
            _ = links;

            state.TryAddUser(new User { ConnectionId = "u1", Nick = "alice", UserName = "a", Host = "h", IsRegistered = true });

            var s = new TestSession { ConnectionId = "u1", Nick = "alice", UserName = "a", IsRegistered = true };
            sessions.Add(s);

            var h = new InfoServCommandHandler(sp.GetRequiredService<IServiceCommandDispatcher>());
            await h.HandleAsync(s, new IrcMessage(null, "INFOSERV", new[] { "INFO" }, null), state, CancellationToken.None);

            Assert.Contains(s.Sent, l => l.Contains("NOTICE alice :Network: net", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(s.Sent, l => l.Contains("NOTICE alice :Server: srv", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task StatServ_Stats_ShowsUserAndChannelCounts()
        {
            var state = new ServerState();
            var sessions = new FakeSessionRegistry();
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
            using var sp = services.BuildServiceProvider();

            var links = new ServerLinkService(NullLogger<ServerLinkService>.Instance, new OptionsMonitorStub<IrcOptions>(opts.Value), state, routing, sessions, silence, new WatchService(opts, routing));
            _ = links;

            state.TryAddUser(new User { ConnectionId = "u1", Nick = "alice", UserName = "a", Host = "h", IsRegistered = true });
            state.TryAddUser(new User { ConnectionId = "u2", Nick = "bob", UserName = "b", Host = "h", IsRegistered = true, Modes = UserModes.Operator });
            state.TryJoinChannel("u1", "alice", "#c");

            var s = new TestSession { ConnectionId = "u1", Nick = "alice", UserName = "a", IsRegistered = true };
            sessions.Add(s);

            var h = new StatServCommandHandler(sp.GetRequiredService<IServiceCommandDispatcher>());
            await h.HandleAsync(s, new IrcMessage(null, "STATSERV", new[] { "STATS" }, null), state, CancellationToken.None);

            Assert.Contains(s.Sent, l => l.Contains("NOTICE alice :Users:", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(s.Sent, l => l.Contains("NOTICE alice :Channels: 1", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task HelpServ_Help_ShowsServiceList()
        {
            var state = new ServerState();
            var sessions = new FakeSessionRegistry();
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
            using var sp = services.BuildServiceProvider();

            var links = new ServerLinkService(NullLogger<ServerLinkService>.Instance, new OptionsMonitorStub<IrcOptions>(opts.Value), state, routing, sessions, silence, new WatchService(opts, routing));
            _ = links;

            state.TryAddUser(new User { ConnectionId = "u1", Nick = "alice", UserName = "a", Host = "h", IsRegistered = true });

            var s = new TestSession { ConnectionId = "u1", Nick = "alice", UserName = "a", IsRegistered = true };
            sessions.Add(s);

            var h = new HelpServCommandHandler(sp.GetRequiredService<IServiceCommandDispatcher>());
            await h.HandleAsync(s, new IrcMessage(null, "HELPSERV", new[] { "HELP" }, null), state, CancellationToken.None);

            Assert.DoesNotContain(s.Sent, l => l.Contains(" 421 ", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(s.Sent, l => l.Contains("NOTICE alice", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(s.Sent, l => l.Contains("Services:", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(s.Sent, l => l.Contains("NickServ", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task HelpServ_Help_NickServ_ShowsNickServHelpIntro()
        {
            var state = new ServerState();
            var sessions = new FakeSessionRegistry();
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
            using var sp = services.BuildServiceProvider();

            var links = new ServerLinkService(NullLogger<ServerLinkService>.Instance, new OptionsMonitorStub<IrcOptions>(opts.Value), state, routing, sessions, silence, new WatchService(opts, routing));
            _ = links;

            state.TryAddUser(new User { ConnectionId = "u1", Nick = "alice", UserName = "a", Host = "h", IsRegistered = true });

            var s = new TestSession { ConnectionId = "u1", Nick = "alice", UserName = "a", IsRegistered = true };
            sessions.Add(s);

            var h = new HelpServCommandHandler(sp.GetRequiredService<IServiceCommandDispatcher>());
            await h.HandleAsync(s, new IrcMessage(null, "HELPSERV", new[] { "HELP", "NickServ" }, null), state, CancellationToken.None);

            Assert.Contains(s.Sent, l => l.Contains("NickServ commands:", StringComparison.OrdinalIgnoreCase));
        }

        private sealed class OptionsMonitorStub<T> : IOptionsMonitor<T> where T : class
        {
            private readonly T _value;

            public OptionsMonitorStub(T value) => _value = value;

            public T CurrentValue => _value;

            public T Get(string? name) => _value;

            public IDisposable? OnChange(Action<T, string?> listener) => null;
        }
    }
}
