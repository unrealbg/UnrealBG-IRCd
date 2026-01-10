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
    using IRCd.Services.Storage;
    using IRCd.Shared.Options;
    using IRCd.Tests.TestDoubles;

    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.Logging.Abstractions;
    using Microsoft.Extensions.Options;

    using Xunit;

    public sealed class AdminServTests
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

            public System.Collections.Generic.ISet<string> EnabledCapabilities { get; } =
                new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);

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

            public readonly System.Collections.Generic.List<string> Sent = new();

            public ValueTask SendAsync(string line, CancellationToken ct = default)
            {
                Sent.Add(line);
                return ValueTask.CompletedTask;
            }

            public ValueTask CloseAsync(string reason, CancellationToken ct = default) => ValueTask.CompletedTask;
        }

        [Fact]
        public async Task PrivmsgToAdminServ_IsHandled_AndReturnsNotice()
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

            state.TryAddUser(new User { ConnectionId = "u1", Nick = "oper", UserName = "o", Host = "h", IsRegistered = true, Modes = UserModes.Operator });
            var s = new TestSession { ConnectionId = "u1", Nick = "oper", UserName = "o", IsRegistered = true };
            sessions.Add(s);

            await h.HandleAsync(s, new IrcMessage(null, "PRIVMSG", new[] { "AdminServ" }, "HELP"), state, CancellationToken.None);

            Assert.DoesNotContain(s.Sent, l => l.Contains(" 401 ", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(s.Sent, l => l.Contains("NOTICE oper", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(s.Sent, l => l.Contains("AdminServ commands", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task AdminServ_OperAdd_CreatesEntry()
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

            state.TryAddUser(new User { ConnectionId = "op", Nick = "oper", UserName = "o", Host = "h", IsRegistered = true, Modes = UserModes.Operator });
            var oper = new TestSession { ConnectionId = "op", Nick = "oper", UserName = "o", IsRegistered = true };
            sessions.Add(oper);

            await h.HandleAsync(oper, new IrcMessage(null, "PRIVMSG", new[] { "AdminServ" }, "OPER ADD Alice netadmin"), state, CancellationToken.None);

            var repo = sp.GetRequiredService<IAdminStaffRepository>();
            var entry = await repo.GetByAccountAsync("Alice", CancellationToken.None);

            Assert.NotNull(entry);
            Assert.Equal("Alice", entry!.Account);
            Assert.Equal("netadmin", entry.OperClass);
        }

        [Fact]
        public async Task AdminServ_FlagsAdd_UpdatesEntry()
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

            var repo = sp.GetRequiredService<IAdminStaffRepository>();
            await repo.TryUpsertAsync(new IRCd.Services.AdminServ.AdminStaffEntry { Account = "Alice", Flags = Array.Empty<string>(), OperClass = null }, CancellationToken.None);

            var links = new ServerLinkService(NullLogger<ServerLinkService>.Instance, new OptionsMonitorStub<IrcOptions>(opts.Value), state, routing, sessions, silence, new WatchService(opts, routing));

            var h = new PrivMsgHandler(
                routing,
                links,
                sp.GetRequiredService<HostmaskService>(),
                opts,
                silence,
                sp.GetRequiredService<IServiceCommandDispatcher>());

            state.TryAddUser(new User { ConnectionId = "op", Nick = "oper", UserName = "o", Host = "h", IsRegistered = true, Modes = UserModes.Operator });
            var oper = new TestSession { ConnectionId = "op", Nick = "oper", UserName = "o", IsRegistered = true };
            sessions.Add(oper);

            await h.HandleAsync(oper, new IrcMessage(null, "PRIVMSG", new[] { "AdminServ" }, "FLAGS ADD Alice a b"), state, CancellationToken.None);

            var entry = await repo.GetByAccountAsync("Alice", CancellationToken.None);
            Assert.NotNull(entry);
            Assert.Contains(entry!.Flags, f => string.Equals(f, "a", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(entry.Flags, f => string.Equals(f, "b", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task AdminServ_Whois_Nick_ReturnsAccountAndStaffInfo()
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

            var repo = sp.GetRequiredService<IAdminStaffRepository>();
            await repo.TryUpsertAsync(new IRCd.Services.AdminServ.AdminStaffEntry { Account = "Alice", Flags = new[] { "x" }, OperClass = "netadmin" }, CancellationToken.None);

            var auth = sp.GetRequiredService<IAuthState>();

            var links = new ServerLinkService(NullLogger<ServerLinkService>.Instance, new OptionsMonitorStub<IrcOptions>(opts.Value), state, routing, sessions, silence, new WatchService(opts, routing));

            var h = new PrivMsgHandler(
                routing,
                links,
                sp.GetRequiredService<HostmaskService>(),
                opts,
                silence,
                sp.GetRequiredService<IServiceCommandDispatcher>());

            state.TryAddUser(new User { ConnectionId = "op", Nick = "oper", UserName = "o", Host = "h", IsRegistered = true, Modes = UserModes.Operator });
            var oper = new TestSession { ConnectionId = "op", Nick = "oper", UserName = "o", IsRegistered = true };
            sessions.Add(oper);

            state.TryAddUser(new User { ConnectionId = "u1", Nick = "bob", UserName = "b", Host = "h", IsRegistered = true, Modes = UserModes.None });
            var bob = new TestSession { ConnectionId = "u1", Nick = "bob", UserName = "b", IsRegistered = true };
            sessions.Add(bob);

            await auth.SetIdentifiedAccountAsync("u1", "Alice", CancellationToken.None);

            await h.HandleAsync(oper, new IrcMessage(null, "PRIVMSG", new[] { "AdminServ" }, "WHOIS bob"), state, CancellationToken.None);

            Assert.Contains(oper.Sent, l => l.Contains("Account for bob: Alice", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(oper.Sent, l => l.Contains("Staff Alice", StringComparison.OrdinalIgnoreCase));
        }
    }
}
