namespace IRCd.Tests
{
    using System;
    using System.IO;
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

    public sealed class OperServTests
    {
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

            public ValueTask CloseAsync(string reason, CancellationToken ct = default)
            {
                Closed = true;
                CloseReason = reason;
                return ValueTask.CompletedTask;
            }

            public bool Closed { get; private set; }
            public string? CloseReason { get; private set; }
        }

        [Fact]
        public async Task PrivmsgToOperServ_IsHandled_AndReturnsNotice()
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

            // Needed for OperServService.
            services.AddSingleton<RuntimeKLineService>();
            services.AddSingleton<RuntimeDLineService>();

            services.AddIrcServices();

            using var sp = services.BuildServiceProvider();

            var links = new ServerLinkService(NullLogger<ServerLinkService>.Instance, new OptionsMonitorStub<IrcOptions>(opts.Value), state, routing, sessions, silence, new WatchService(opts, routing));

            var h = new PrivMsgHandler(
                routing,
                links,
                sp.GetRequiredService<HostmaskService>(),
                opts,
                silence,
                sp.GetRequiredService<IServiceCommandDispatcher>());

            state.TryAddUser(new User { ConnectionId = "u1", Nick = "oper", UserName = "o", Host = "h", IsRegistered = true, Modes = UserModes.Operator });
            var s = new TestSession { ConnectionId = "u1", Nick = "oper", UserName = "o", IsRegistered = true };
            sessions.Add(s);

            await h.HandleAsync(s, new IrcMessage(null, "PRIVMSG", new[] { "OperServ" }, "HELP"), state, CancellationToken.None);

            Assert.DoesNotContain(s.Sent, l => l.Contains(" 401 ", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(s.Sent, l => l.Contains("NOTICE oper", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task OperServ_Kline_ClosesMatchingLocalUser_AndAddsEntry()
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

            services.AddSingleton<RuntimeKLineService>();
            services.AddSingleton<RuntimeDLineService>();

            services.AddIrcServices();

            using var sp = services.BuildServiceProvider();

            var links = new ServerLinkService(NullLogger<ServerLinkService>.Instance, new OptionsMonitorStub<IrcOptions>(opts.Value), state, routing, sessions, silence, new WatchService(opts, routing));

            var h = new PrivMsgHandler(
                routing,
                links,
                sp.GetRequiredService<HostmaskService>(),
                opts,
                silence,
                sp.GetRequiredService<IServiceCommandDispatcher>());

            // Operator.
            state.TryAddUser(new User { ConnectionId = "op", Nick = "oper", UserName = "o", Host = "ok.host", IsRegistered = true, Modes = UserModes.Operator });
            var oper = new TestSession { ConnectionId = "op", Nick = "oper", UserName = "o", IsRegistered = true };
            sessions.Add(oper);

            // Victim.
            state.TryAddUser(new User { ConnectionId = "u2", Nick = "bad", UserName = "b", Host = "bad.host", IsRegistered = true, Modes = UserModes.None, IsRemote = false });
            var bad = new TestSession { ConnectionId = "u2", Nick = "bad", UserName = "b", IsRegistered = true };
            sessions.Add(bad);

            await h.HandleAsync(oper, new IrcMessage(null, "PRIVMSG", new[] { "OperServ" }, "KLINE *@bad.host testing"), state, CancellationToken.None);

            Assert.True(bad.Closed);
            var bans = await sp.GetRequiredService<BanService>().GetActiveByTypeAsync(BanType.KLINE, CancellationToken.None);
            Assert.Contains(bans, b => string.Equals(b.Mask, "*@bad.host", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task OperServ_Dline_ClosesMatchingLocalUser_AndAddsEntry()
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
                sp.GetRequiredService<IServiceCommandDispatcher>());

            // Operator.
            state.TryAddUser(new User { ConnectionId = "op", Nick = "oper", UserName = "o", Host = "ok.host", IsRegistered = true, Modes = UserModes.Operator });
            var oper = new TestSession { ConnectionId = "op", Nick = "oper", UserName = "o", IsRegistered = true };
            sessions.Add(oper);

            // Victim.
            state.TryAddUser(new User { ConnectionId = "u2", Nick = "bad", UserName = "b", Host = "bad.host", RemoteIp = "10.0.0.1", IsRegistered = true, Modes = UserModes.None, IsRemote = false });
            var bad = new TestSession { ConnectionId = "u2", Nick = "bad", UserName = "b", IsRegistered = true };
            sessions.Add(bad);

            await h.HandleAsync(oper, new IrcMessage(null, "PRIVMSG", new[] { "OperServ" }, "DLINE 10.0.0.1 testing"), state, CancellationToken.None);

            Assert.True(bad.Closed);
            var bans = await sp.GetRequiredService<BanService>().GetActiveByTypeAsync(BanType.DLINE, CancellationToken.None);
            Assert.Contains(bans, b => string.Equals(b.Mask, "10.0.0.1", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task OperServ_Akill_ClosesMatchingLocalUser_AndAddsEntry()
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
                sp.GetRequiredService<IServiceCommandDispatcher>());

            // Operator.
            state.TryAddUser(new User { ConnectionId = "op", Nick = "oper", UserName = "o", Host = "ok.host", IsRegistered = true, Modes = UserModes.Operator });
            var oper = new TestSession { ConnectionId = "op", Nick = "oper", UserName = "o", IsRegistered = true };
            sessions.Add(oper);

            // Victim.
            state.TryAddUser(new User { ConnectionId = "u2", Nick = "bad", UserName = "b", Host = "bad.host", IsRegistered = true, Modes = UserModes.None, IsRemote = false });
            var bad = new TestSession { ConnectionId = "u2", Nick = "bad", UserName = "b", IsRegistered = true };
            sessions.Add(bad);

            await h.HandleAsync(oper, new IrcMessage(null, "PRIVMSG", new[] { "OperServ" }, "AKILL *@bad.host testing"), state, CancellationToken.None);

            Assert.True(bad.Closed);
            var bans = await sp.GetRequiredService<BanService>().GetActiveByTypeAsync(BanType.KLINE, CancellationToken.None);
            Assert.Contains(bans, b => string.Equals(b.Mask, "*@bad.host", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task OperServ_Zline_ClosesMatchingLocalUser_AndAddsEntry()
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
                sp.GetRequiredService<IServiceCommandDispatcher>());

            // Operator.
            state.TryAddUser(new User { ConnectionId = "op", Nick = "oper", UserName = "o", Host = "ok.host", IsRegistered = true, Modes = UserModes.Operator });
            var oper = new TestSession { ConnectionId = "op", Nick = "oper", UserName = "o", IsRegistered = true };
            sessions.Add(oper);

            // Victim.
            state.TryAddUser(new User { ConnectionId = "u2", Nick = "bad", UserName = "b", Host = "bad.host", RemoteIp = "10.0.0.1", IsRegistered = true, Modes = UserModes.None, IsRemote = false });
            var bad = new TestSession { ConnectionId = "u2", Nick = "bad", UserName = "b", IsRegistered = true };
            sessions.Add(bad);

            await h.HandleAsync(oper, new IrcMessage(null, "PRIVMSG", new[] { "OperServ" }, "ZLINE 10.0.0.1 testing"), state, CancellationToken.None);

            Assert.True(bad.Closed);
            var bans = await sp.GetRequiredService<BanService>().GetActiveByTypeAsync(BanType.ZLINE, CancellationToken.None);
            Assert.Contains(bans, b => string.Equals(b.Mask, "10.0.0.1", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task OperServ_Global_SendsNoticeToAllUsers()
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
                sp.GetRequiredService<IServiceCommandDispatcher>());

            state.TryAddUser(new User { ConnectionId = "op", Nick = "oper", UserName = "o", Host = "ok.host", IsRegistered = true, Modes = UserModes.Operator });
            var oper = new TestSession { ConnectionId = "op", Nick = "oper", UserName = "o", IsRegistered = true };
            sessions.Add(oper);

            state.TryAddUser(new User { ConnectionId = "u1", Nick = "alice", UserName = "a", Host = "h", IsRegistered = true });
            var alice = new TestSession { ConnectionId = "u1", Nick = "alice", UserName = "a", IsRegistered = true };
            sessions.Add(alice);

            state.TryAddUser(new User { ConnectionId = "u2", Nick = "bob", UserName = "b", Host = "h", IsRegistered = true });
            var bob = new TestSession { ConnectionId = "u2", Nick = "bob", UserName = "b", IsRegistered = true };
            sessions.Add(bob);

            await h.HandleAsync(oper, new IrcMessage(null, "PRIVMSG", new[] { "OperServ" }, "GLOBAL maintenance in 5"), state, CancellationToken.None);

            Assert.Contains(alice.Sent, l => l.Contains("NOTICE alice", StringComparison.OrdinalIgnoreCase) && l.Contains("maintenance in 5", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(bob.Sent, l => l.Contains("NOTICE bob", StringComparison.OrdinalIgnoreCase) && l.Contains("maintenance in 5", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task OperServ_Fjoin_ForcesLocalUserToJoinChannel()
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
                sp.GetRequiredService<IServiceCommandDispatcher>());

            state.TryAddUser(new User { ConnectionId = "op", Nick = "oper", UserName = "o", Host = "ok.host", IsRegistered = true, Modes = UserModes.Operator });
            var oper = new TestSession { ConnectionId = "op", Nick = "oper", UserName = "o", IsRegistered = true };
            sessions.Add(oper);

            state.TryAddUser(new User { ConnectionId = "u1", Nick = "alice", UserName = "a", Host = "h", IsRegistered = true });
            var alice = new TestSession { ConnectionId = "u1", Nick = "alice", UserName = "a", IsRegistered = true };
            sessions.Add(alice);

            await h.HandleAsync(oper, new IrcMessage(null, "PRIVMSG", new[] { "OperServ" }, "FJOIN alice #chan"), state, CancellationToken.None);

            Assert.Contains(state.GetUserChannels("u1"), ch => string.Equals(ch, "#chan", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(alice.Sent, l => l.Contains(" JOIN :#chan", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task OperServ_Fpart_ForcesLocalUserToPartChannel()
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
                sp.GetRequiredService<IServiceCommandDispatcher>());

            state.TryAddUser(new User { ConnectionId = "op", Nick = "oper", UserName = "o", Host = "ok.host", IsRegistered = true, Modes = UserModes.Operator });
            var oper = new TestSession { ConnectionId = "op", Nick = "oper", UserName = "o", IsRegistered = true };
            sessions.Add(oper);

            state.TryAddUser(new User { ConnectionId = "u1", Nick = "alice", UserName = "a", Host = "h", IsRegistered = true });
            var alice = new TestSession { ConnectionId = "u1", Nick = "alice", UserName = "a", IsRegistered = true };
            sessions.Add(alice);

            state.TryJoinChannel("u1", "alice", "#chan");
            Assert.Contains(state.GetUserChannels("u1"), ch => string.Equals(ch, "#chan", StringComparison.OrdinalIgnoreCase));

            await h.HandleAsync(oper, new IrcMessage(null, "PRIVMSG", new[] { "OperServ" }, "FPART alice #chan bye"), state, CancellationToken.None);

            Assert.DoesNotContain(state.GetUserChannels("u1"), ch => string.Equals(ch, "#chan", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(alice.Sent, l => l.Contains(" PART #chan", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task OperServ_ClearAll_ClearsRuntimeLists()
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
                sp.GetRequiredService<IServiceCommandDispatcher>());

            state.TryAddUser(new User { ConnectionId = "op", Nick = "oper", UserName = "o", Host = "ok.host", IsRegistered = true, Modes = UserModes.Operator });
            var oper = new TestSession { ConnectionId = "op", Nick = "oper", UserName = "o", IsRegistered = true };
            sessions.Add(oper);

            await h.HandleAsync(oper, new IrcMessage(null, "PRIVMSG", new[] { "OperServ" }, "KLINE *@bad.host testing"), state, CancellationToken.None);
            await h.HandleAsync(oper, new IrcMessage(null, "PRIVMSG", new[] { "OperServ" }, "DLINE 10.0.0.1 testing"), state, CancellationToken.None);
            await h.HandleAsync(oper, new IrcMessage(null, "PRIVMSG", new[] { "OperServ" }, "DENY badnick testing"), state, CancellationToken.None);
            await h.HandleAsync(oper, new IrcMessage(null, "PRIVMSG", new[] { "OperServ" }, "WARN *@* test"), state, CancellationToken.None);
            await h.HandleAsync(oper, new IrcMessage(null, "PRIVMSG", new[] { "OperServ" }, "TRIGGER ping pong"), state, CancellationToken.None);

            var banSvc = sp.GetRequiredService<BanService>();
            Assert.NotEmpty(await banSvc.GetActiveByTypeAsync(BanType.KLINE, CancellationToken.None));
            Assert.NotEmpty(await banSvc.GetActiveByTypeAsync(BanType.DLINE, CancellationToken.None));
            Assert.NotEmpty(opts.Value.Denies);
            Assert.NotEmpty(opts.Value.Warns);
            Assert.NotEmpty(opts.Value.Triggers);

            await h.HandleAsync(oper, new IrcMessage(null, "PRIVMSG", new[] { "OperServ" }, "CLEAR ALL"), state, CancellationToken.None);

            Assert.Empty(await banSvc.GetActiveByTypeAsync(BanType.KLINE, CancellationToken.None));
            Assert.Empty(await banSvc.GetActiveByTypeAsync(BanType.DLINE, CancellationToken.None));
            Assert.Empty(await banSvc.GetActiveByTypeAsync(BanType.ZLINE, CancellationToken.None));
            Assert.Empty(opts.Value.Denies);
            Assert.Empty(opts.Value.Warns);
            Assert.Empty(opts.Value.Triggers);
        }

        [Fact]
        public async Task OperServ_Unkline_RemovesExistingEntry()
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
                sp.GetRequiredService<IServiceCommandDispatcher>());

            state.TryAddUser(new User { ConnectionId = "op", Nick = "oper", UserName = "o", Host = "ok.host", IsRegistered = true, Modes = UserModes.Operator });
            var oper = new TestSession { ConnectionId = "op", Nick = "oper", UserName = "o", IsRegistered = true };
            sessions.Add(oper);

            await h.HandleAsync(oper, new IrcMessage(null, "PRIVMSG", new[] { "OperServ" }, "KLINE *@bad.host testing"), state, CancellationToken.None);
            var banSvc = sp.GetRequiredService<BanService>();
            Assert.Contains(await banSvc.GetActiveByTypeAsync(BanType.KLINE, CancellationToken.None), b => string.Equals(b.Mask, "*@bad.host", StringComparison.OrdinalIgnoreCase));

            await h.HandleAsync(oper, new IrcMessage(null, "PRIVMSG", new[] { "OperServ" }, "KLINE -*@bad.host"), state, CancellationToken.None);

            Assert.DoesNotContain(await banSvc.GetActiveByTypeAsync(BanType.KLINE, CancellationToken.None), b => string.Equals(b.Mask, "*@bad.host", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(oper.Sent, l => l.Contains("UNKLINE removed", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task OperServ_Undline_RemovesExistingEntry()
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
                sp.GetRequiredService<IServiceCommandDispatcher>());

            state.TryAddUser(new User { ConnectionId = "op", Nick = "oper", UserName = "o", Host = "ok.host", IsRegistered = true, Modes = UserModes.Operator });
            var oper = new TestSession { ConnectionId = "op", Nick = "oper", UserName = "o", IsRegistered = true };
            sessions.Add(oper);

            await h.HandleAsync(oper, new IrcMessage(null, "PRIVMSG", new[] { "OperServ" }, "DLINE 10.0.0.1 testing"), state, CancellationToken.None);
            var banSvc = sp.GetRequiredService<BanService>();
            Assert.Contains(await banSvc.GetActiveByTypeAsync(BanType.DLINE, CancellationToken.None), b => string.Equals(b.Mask, "10.0.0.1", StringComparison.OrdinalIgnoreCase));

            await h.HandleAsync(oper, new IrcMessage(null, "PRIVMSG", new[] { "OperServ" }, "DLINE -10.0.0.1"), state, CancellationToken.None);

            Assert.DoesNotContain(await banSvc.GetActiveByTypeAsync(BanType.DLINE, CancellationToken.None), b => string.Equals(b.Mask, "10.0.0.1", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(oper.Sent, l => l.Contains("UNDLINE removed", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task OperServ_Kline_PermissionDenied_ForNonOper()
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
                sp.GetRequiredService<IServiceCommandDispatcher>());

            state.TryAddUser(new User { ConnectionId = "u1", Nick = "alice", UserName = "a", Host = "h", IsRegistered = true, Modes = UserModes.None });
            var s = new TestSession { ConnectionId = "u1", Nick = "alice", UserName = "a", IsRegistered = true };
            sessions.Add(s);

            await h.HandleAsync(s, new IrcMessage(null, "PRIVMSG", new[] { "OperServ" }, "KLINE *@bad.host nope"), state, CancellationToken.None);

            Assert.Contains(s.Sent, l => l.Contains("Permission denied", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(opts.Value.KLines, k => string.Equals(k?.Mask, "*@bad.host", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task OperServ_Rehash_PermissionDenied_ForNonOper()
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
                sp.GetRequiredService<IServiceCommandDispatcher>());

            state.TryAddUser(new User { ConnectionId = "u1", Nick = "alice", UserName = "a", Host = "h", IsRegistered = true, Modes = UserModes.None });
            var s = new TestSession { ConnectionId = "u1", Nick = "alice", UserName = "a", IsRegistered = true };
            sessions.Add(s);

            await h.HandleAsync(s, new IrcMessage(null, "PRIVMSG", new[] { "OperServ" }, "REHASH"), state, CancellationToken.None);

            Assert.Contains(s.Sent, l => l.Contains("Permission denied", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task OperServ_Rehash_Fails_WhenConfigMissing()
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

            var env = new TestHostEnvironment { ContentRootPath = Directory.GetCurrentDirectory() };
            services.AddSingleton<Microsoft.Extensions.Hosting.IHostEnvironment>(env);

            var store = new IRCd.Core.Config.IrcOptionsStore(new IrcOptions
            {
                ServerInfo = new ServerInfoOptions { Name = "srv", Sid = "001" },
                ConfigFile = "definitely-missing-ircd.conf",
            });
            services.AddSingleton(store);
            services.AddSingleton<IOptions<IrcOptions>>(store);
            services.AddSingleton<IOptionsMonitor<IrcOptions>>(store);

            services.AddSingleton(new IRCd.Core.Config.IrcConfigManager(
                store,
                env,
                Array.Empty<IRCd.Core.Abstractions.IConfigReloadListener>(),
                NullLogger<IRCd.Core.Config.IrcConfigManager>.Instance));

            services.AddIrcServices();

            using var sp = services.BuildServiceProvider();

            var links = new ServerLinkService(NullLogger<ServerLinkService>.Instance, new OptionsMonitorStub<IrcOptions>(store.Value), state, routing, sessions, silence, new WatchService(store, routing));

            var h = new PrivMsgHandler(
                routing,
                links,
                sp.GetRequiredService<HostmaskService>(),
                store,
                silence,
                sp.GetRequiredService<IServiceCommandDispatcher>());

            state.TryAddUser(new User { ConnectionId = "op", Nick = "oper", UserName = "o", Host = "ok.host", IsRegistered = true, Modes = UserModes.Operator });
            var oper = new TestSession { ConnectionId = "op", Nick = "oper", UserName = "o", IsRegistered = true };
            sessions.Add(oper);

            await h.HandleAsync(oper, new IrcMessage(null, "PRIVMSG", new[] { "OperServ" }, "REHASH"), state, CancellationToken.None);

            Assert.Contains(oper.Sent, l => l.Contains("REHASH failed: config file not found", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task OperServ_Rehash_Succeeds_AndUpdatesOptions()
        {
            var tmpDir = Path.Combine(Path.GetTempPath(), "UnrealBG-IRCd", "operserv", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmpDir);

            var confPath = Path.Combine(tmpDir, "ircd.conf");
            // Note: The config tokenizer treats ';' as "comment until end-of-line".
            // Keep each statement on its own line (like the real ircd.conf) so parsing succeeds.
            File.WriteAllText(confPath, "serverinfo {\n    name = \"rehash-srv\";\n    sid = \"002\";\n};\n");

            try
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

                var env = new TestHostEnvironment { ContentRootPath = tmpDir };
                services.AddSingleton<Microsoft.Extensions.Hosting.IHostEnvironment>(env);

                var store = new IRCd.Core.Config.IrcOptionsStore(new IrcOptions
                {
                    ServerInfo = new ServerInfoOptions { Name = "srv", Sid = "001" },
                    ConfigFile = "ircd.conf",
                });
                services.AddSingleton(store);
                services.AddSingleton<IOptions<IrcOptions>>(store);
                services.AddSingleton<IOptionsMonitor<IrcOptions>>(store);

                services.AddSingleton(new IRCd.Core.Config.IrcConfigManager(
                    store,
                    env,
                    Array.Empty<IRCd.Core.Abstractions.IConfigReloadListener>(),
                    NullLogger<IRCd.Core.Config.IrcConfigManager>.Instance));

                services.AddIrcServices();

                using var sp = services.BuildServiceProvider();

                var links = new ServerLinkService(NullLogger<ServerLinkService>.Instance, new OptionsMonitorStub<IrcOptions>(store.Value), state, routing, sessions, silence, new WatchService(store, routing));

                var h = new PrivMsgHandler(
                    routing,
                    links,
                    sp.GetRequiredService<HostmaskService>(),
                    store,
                    silence,
                    sp.GetRequiredService<IServiceCommandDispatcher>());

                state.TryAddUser(new User { ConnectionId = "op", Nick = "oper", UserName = "o", Host = "ok.host", IsRegistered = true, Modes = UserModes.Operator });
                var oper = new TestSession { ConnectionId = "op", Nick = "oper", UserName = "o", IsRegistered = true };
                sessions.Add(oper);

                await h.HandleAsync(oper, new IrcMessage(null, "PRIVMSG", new[] { "OperServ" }, "REHASH"), state, CancellationToken.None);

                Assert.Contains(oper.Sent, l => l.Contains("Rehashing", StringComparison.OrdinalIgnoreCase));
                Assert.Equal("rehash-srv", store.Value.ServerInfo?.Name);
                Assert.Equal("002", store.Value.ServerInfo?.Sid);
            }
            finally
            {
                try { Directory.Delete(tmpDir, recursive: true); } catch { /* best effort */ }
            }
        }

        [Fact]
        public async Task OperServ_Die_ReportsNotAvailable_WhenNoHostLifetime()
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
                sp.GetRequiredService<IServiceCommandDispatcher>());

            state.TryAddUser(new User { ConnectionId = "op", Nick = "oper", UserName = "o", Host = "ok.host", IsRegistered = true, Modes = UserModes.Operator });
            var oper = new TestSession { ConnectionId = "op", Nick = "oper", UserName = "o", IsRegistered = true };
            sessions.Add(oper);

            await h.HandleAsync(oper, new IrcMessage(null, "PRIVMSG", new[] { "OperServ" }, "DIE"), state, CancellationToken.None);

            Assert.Contains(oper.Sent, l => l.Contains("DIE is not available", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task OperServ_Restart_ReportsNotAvailable_WhenNoHostLifetime()
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
                sp.GetRequiredService<IServiceCommandDispatcher>());

            state.TryAddUser(new User { ConnectionId = "op", Nick = "oper", UserName = "o", Host = "ok.host", IsRegistered = true, Modes = UserModes.Operator });
            var oper = new TestSession { ConnectionId = "op", Nick = "oper", UserName = "o", IsRegistered = true };
            sessions.Add(oper);

            await h.HandleAsync(oper, new IrcMessage(null, "PRIVMSG", new[] { "OperServ" }, "RESTART"), state, CancellationToken.None);

            Assert.Contains(oper.Sent, l => l.Contains("RESTART is not available", StringComparison.OrdinalIgnoreCase));
        }

        private sealed class TestHostEnvironment : Microsoft.Extensions.Hosting.IHostEnvironment
        {
            public string EnvironmentName { get; set; } = "Development";
            public string ApplicationName { get; set; } = "IRCd.Tests";
            public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
            public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
        }
    }
}
