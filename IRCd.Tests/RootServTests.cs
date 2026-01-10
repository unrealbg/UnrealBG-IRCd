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

    public sealed class RootServTests
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

        private sealed class OptionsMonitorStub<T> : IOptionsMonitor<T> where T : class
        {
            private readonly T _value;

            public OptionsMonitorStub(T value) => _value = value;

            public T CurrentValue => _value;

            public T Get(string? name) => _value;

            public IDisposable? OnChange(Action<T, string?> listener) => null;
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

            public ValueTask CloseAsync(string reason, CancellationToken ct = default) => ValueTask.CompletedTask;
        }

        [Fact]
        public async Task RootServ_Help_Works_ForRegisteredUser()
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

            var h = new PrivMsgHandler(
                routing,
                links,
                sp.GetRequiredService<HostmaskService>(),
                opts,
                silence,
                sp.GetRequiredService<IServiceCommandDispatcher>(),
                sp.GetRequiredService<IServiceChannelEvents>());

            state.TryAddUser(new User { ConnectionId = "u1", Nick = "u", UserName = "u", Host = "h", IsRegistered = true, Modes = UserModes.None });
            var s = new TestSession { ConnectionId = "u1", Nick = "u", UserName = "u", IsRegistered = true };
            sessions.Add(s);

            await h.HandleAsync(s, new IrcMessage(null, "PRIVMSG", new[] { "RootServ" }, "HELP"), state, CancellationToken.None);

            Assert.Contains(s.Sent, l => l.Contains("RootServ", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(s.Sent, l => l.Contains("NOTICE u", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task RootServ_Raw_Denied_ForNonRootOper()
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

            var h = new PrivMsgHandler(
                routing,
                links,
                sp.GetRequiredService<HostmaskService>(),
                opts,
                silence,
                sp.GetRequiredService<IServiceCommandDispatcher>(),
                sp.GetRequiredService<IServiceChannelEvents>());

            state.TryAddUser(new User { ConnectionId = "op", Nick = "oper", UserName = "o", Host = "h", IsRegistered = true, Modes = UserModes.Operator, OperClass = "localop" });
            var oper = new TestSession { ConnectionId = "op", Nick = "oper", UserName = "o", IsRegistered = true };
            sessions.Add(oper);

            var victim = new TestSession { ConnectionId = "u2", Nick = "victim", UserName = "v", IsRegistered = true };
            sessions.Add(victim);

            await h.HandleAsync(oper, new IrcMessage(null, "PRIVMSG", new[] { "RootServ" }, "RAW :srv NOTICE victim :hi"), state, CancellationToken.None);

            Assert.DoesNotContain(victim.Sent, l => l.Contains("NOTICE victim :hi", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(oper.Sent, l => l.Contains("Permission denied", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task RootServ_Raw_SendsLine_WhenRootCapabilityPresent()
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
                ServerInfo = new ServerInfoOptions { Name = "srv", Sid = "001" },
                Classes = new[]
                {
                    new OperClassOptions { Name = "root", Capabilities = new[] { "rootserv" } }
                }
            });
            services.AddSingleton<IOptions<IrcOptions>>(opts);

            services.AddIrcServices();

            using var sp = services.BuildServiceProvider();

            var links = new ServerLinkService(NullLogger<ServerLinkService>.Instance, new OptionsMonitorStub<IrcOptions>(opts.Value), state, routing, sessions, silence, new WatchService(opts, routing));

            var h = new PrivMsgHandler(
                routing,
                links,
                sp.GetRequiredService<HostmaskService>(),
                opts,
                silence,
                sp.GetRequiredService<IServiceCommandDispatcher>(),
                sp.GetRequiredService<IServiceChannelEvents>());

            state.TryAddUser(new User { ConnectionId = "op", Nick = "oper", UserName = "o", Host = "h", IsRegistered = true, Modes = UserModes.Operator, OperClass = "root" });
            var oper = new TestSession { ConnectionId = "op", Nick = "oper", UserName = "o", IsRegistered = true };
            sessions.Add(oper);

            var victim = new TestSession { ConnectionId = "u2", Nick = "victim", UserName = "v", IsRegistered = true };
            sessions.Add(victim);

            await h.HandleAsync(oper, new IrcMessage(null, "PRIVMSG", new[] { "RootServ" }, "RAW :srv NOTICE victim :hi"), state, CancellationToken.None);

            Assert.Contains(victim.Sent, l => l.Contains("NOTICE victim :hi", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(oper.Sent, l => l.Contains("RAW sent", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task RootServ_ChanSnoop_MirrorsChannelMessages_ToWatcher()
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
                ServerInfo = new ServerInfoOptions { Name = "srv", Sid = "001" },
                Classes = new[]
                {
                    new OperClassOptions { Name = "root", Capabilities = new[] { "rootserv" } }
                }
            });
            services.AddSingleton<IOptions<IrcOptions>>(opts);

            services.AddIrcServices();

            using var sp = services.BuildServiceProvider();

            var links = new ServerLinkService(NullLogger<ServerLinkService>.Instance, new OptionsMonitorStub<IrcOptions>(opts.Value), state, routing, sessions, silence, new WatchService(opts, routing));

            var h = new PrivMsgHandler(
                routing,
                links,
                sp.GetRequiredService<HostmaskService>(),
                opts,
                silence,
                sp.GetRequiredService<IServiceCommandDispatcher>(),
                sp.GetRequiredService<IServiceChannelEvents>());

            // Root watcher.
            state.TryAddUser(new User { ConnectionId = "op", Nick = "oper", UserName = "o", Host = "h", IsRegistered = true, Modes = UserModes.Operator, OperClass = "root" });
            var oper = new TestSession { ConnectionId = "op", Nick = "oper", UserName = "o", IsRegistered = true };
            sessions.Add(oper);

            // Talker.
            state.TryAddUser(new User { ConnectionId = "u2", Nick = "bob", UserName = "b", Host = "h", IsRegistered = true, Modes = UserModes.None });
            var bob = new TestSession { ConnectionId = "u2", Nick = "bob", UserName = "b", IsRegistered = true };
            sessions.Add(bob);

            // Create/join channel.
            var join = new JoinHandler(routing, links, sp.GetRequiredService<HostmaskService>(), new TestMetrics(), sp.GetRequiredService<ISessionRegistry>(), sp.GetRequiredService<IServiceChannelEvents>());
            await join.HandleAsync(oper, new IrcMessage(null, "JOIN", new[] { "#x" }, null), state, CancellationToken.None);
            await join.HandleAsync(bob, new IrcMessage(null, "JOIN", new[] { "#x" }, null), state, CancellationToken.None);

            await h.HandleAsync(oper, new IrcMessage(null, "PRIVMSG", new[] { "RootServ" }, "CHANSNOOP ON #x"), state, CancellationToken.None);
            await h.HandleAsync(bob, new IrcMessage(null, "PRIVMSG", new[] { "#x" }, "hello"), state, CancellationToken.None);

            Assert.Contains(oper.Sent, l => l.Contains("snoop #x", StringComparison.OrdinalIgnoreCase) && l.Contains("<bob>", StringComparison.OrdinalIgnoreCase));
        }
    }
}
