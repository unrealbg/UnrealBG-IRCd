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
    using IRCd.Services.Email;
    using IRCd.Shared.Options;
    using IRCd.Tests.TestDoubles;
    using IRCd.Services.ChanServ;
    using IRCd.Services.Storage;

    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.Logging.Abstractions;
    using Microsoft.Extensions.Options;

    using Xunit;

    public sealed class NickServTests
    {
        private sealed class TestSession : IClientSession
        {
            public string ConnectionId { get; set; } = "c1";
            public EndPoint RemoteEndPoint { get; } = new IPEndPoint(IPAddress.Loopback, 1234);
            public EndPoint LocalEndPoint { get; } = new IPEndPoint(IPAddress.Loopback, 6667);
            public bool IsSecureConnection { get; set; }

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
        public async Task PrivmsgToNickServ_IsHandled_AndDoesNotReturnNoSuchNick()
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

            state.TryAddUser(new User { ConnectionId = "u1", Nick = "alice", UserName = "a", Host = "h", IsRegistered = true });

            var s = new TestSession { ConnectionId = "u1", Nick = "alice", UserName = "a", IsRegistered = true };
            sessions.Add(s);

            await h.HandleAsync(s, new IrcMessage(null, "PRIVMSG", new[] { "NickServ" }, "HELP"), state, CancellationToken.None);

            Assert.DoesNotContain(s.Sent, l => l.Contains(" 401 ", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(s.Sent, l => l.Contains("NOTICE alice", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task PrivmsgToNickServAliasNS_IsHandled_AndDoesNotReturnNoSuchNick()
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

            state.TryAddUser(new User { ConnectionId = "u1", Nick = "alice", UserName = "a", Host = "h", IsRegistered = true });

            var s = new TestSession { ConnectionId = "u1", Nick = "alice", UserName = "a", IsRegistered = true };
            sessions.Add(s);

            await h.HandleAsync(s, new IrcMessage(null, "PRIVMSG", new[] { "NS" }, "HELP"), state, CancellationToken.None);

            Assert.DoesNotContain(s.Sent, l => l.Contains(" 401 ", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(s.Sent, l => l.Contains("NOTICE alice", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task PrivmsgToWrappedNickServAlias_IsHandled_AndDoesNotReturnNoSuchNick()
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
            var h = new PrivMsgHandler(routing, links, sp.GetRequiredService<HostmaskService>(), opts, silence, sp.GetRequiredService<IServiceCommandDispatcher>());

            state.TryAddUser(new User { ConnectionId = "u1", Nick = "alice", UserName = "a", Host = "h", IsRegistered = true });
            var s = new TestSession { ConnectionId = "u1", Nick = "alice", UserName = "a", IsRegistered = true };
            sessions.Add(s);

            await h.HandleAsync(s, new IrcMessage(null, "PRIVMSG", new[] { "*NS*" }, "HELP"), state, CancellationToken.None);

            Assert.DoesNotContain(s.Sent, l => l.Contains(" 401 ", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(s.Sent, l => l.Contains("NOTICE alice", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task NickServ_SecureOption_RequiresTlsForIdentify()
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
            var h = new PrivMsgHandler(routing, links, sp.GetRequiredService<HostmaskService>(), opts, silence, sp.GetRequiredService<IServiceCommandDispatcher>());

            state.TryAddUser(new User { ConnectionId = "u1", Nick = "alice", UserName = "a", Host = "h", IsRegistered = true });
            var s = new TestSession { ConnectionId = "u1", Nick = "alice", UserName = "a", IsRegistered = true, IsSecureConnection = false };
            sessions.Add(s);

            await h.HandleAsync(s, new IrcMessage(null, "PRIVMSG", new[] { "NickServ" }, "REGISTER test@test.com secret"), state, CancellationToken.None);

            // Force SECURE on for this account.
            var repo = sp.GetRequiredService<INickAccountRepository>();
            var acc = await repo.GetByNameAsync("alice", CancellationToken.None);
            Assert.NotNull(acc);
            await repo.TryUpdateAsync(acc! with { Secure = true }, CancellationToken.None);

            // Logout to require IDENTIFY again.
            await h.HandleAsync(s, new IrcMessage(null, "PRIVMSG", new[] { "NickServ" }, "LOGOUT"), state, CancellationToken.None);

            s.Sent.Clear();
            await h.HandleAsync(s, new IrcMessage(null, "PRIVMSG", new[] { "NickServ" }, "IDENTIFY secret"), state, CancellationToken.None);
            Assert.Contains(s.Sent, l => l.Contains("SECURE is enabled", StringComparison.OrdinalIgnoreCase));

            var auth = sp.GetRequiredService<IAuthState>();
            var identified = await auth.GetIdentifiedAccountAsync(s.ConnectionId, CancellationToken.None);
            Assert.Null(identified);

            s.IsSecureConnection = true;
            s.Sent.Clear();
            await h.HandleAsync(s, new IrcMessage(null, "PRIVMSG", new[] { "NickServ" }, "IDENTIFY secret"), state, CancellationToken.None);
            Assert.Contains(s.Sent, l => l.Contains("identified", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task NickServ_RegisterThenIdentify_Works()
        {
            var state = new ServerState();
            var sessions = new FakeSessionRegistry();
            var routing = new RoutingService(sessions, new IrcFormatter());
            var silence = new SilenceService();

            var services = new ServiceCollection();
            var opts = Options.Create(new IrcOptions { ServerInfo = new ServerInfoOptions { Name = "srv", Sid = "001" } });
            services.AddSingleton<IOptions<IrcOptions>>(opts);
            services.AddSingleton<ISessionRegistry>(sessions);
            services.AddIrcServices();

            using var sp = services.BuildServiceProvider();

            var links = new ServerLinkService(NullLogger<ServerLinkService>.Instance, new OptionsMonitorStub<IrcOptions>(opts.Value), state, routing, sessions, silence, new WatchService(opts, routing));

            var h = new PrivMsgHandler(
                routing,
                links,
                new HostmaskService(),
                opts,
                silence,
                sp.GetRequiredService<IServiceCommandDispatcher>());

            state.TryAddUser(new User { ConnectionId = "u1", Nick = "alice", UserName = "a", Host = "h", IsRegistered = true });

            var s = new TestSession { ConnectionId = "u1", Nick = "alice", UserName = "a", IsRegistered = true };
            sessions.Add(s);

            await h.HandleAsync(s, new IrcMessage(null, "PRIVMSG", new[] { "NickServ" }, "REGISTER alice@test.com secret"), state, CancellationToken.None);
            Assert.Contains(s.Sent, l => l.Contains("registered", StringComparison.OrdinalIgnoreCase));

            s.Sent.Clear();

            await h.HandleAsync(s, new IrcMessage(null, "PRIVMSG", new[] { "NickServ" }, "IDENTIFY secret"), state, CancellationToken.None);
            Assert.Contains(s.Sent, l => l.Contains("identified", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task NickServ_List_ShowsRegisteredAccounts()
        {
            var state = new ServerState();
            var sessions = new FakeSessionRegistry();
            var routing = new RoutingService(sessions, new IrcFormatter());
            var silence = new SilenceService();

            var services = new ServiceCollection();
            var opts = Options.Create(new IrcOptions { ServerInfo = new ServerInfoOptions { Name = "srv", Sid = "001" } });
            services.AddSingleton<IOptions<IrcOptions>>(opts);
            services.AddSingleton<ISessionRegistry>(sessions);
            services.AddSingleton(routing);
            services.AddSingleton(silence);
            services.AddSingleton(new HostmaskService());

            services.AddIrcServices();

            using var sp = services.BuildServiceProvider();
            var links = new ServerLinkService(NullLogger<ServerLinkService>.Instance, new OptionsMonitorStub<IrcOptions>(opts.Value), state, routing, sessions, silence, new WatchService(opts, routing));
            var h = new PrivMsgHandler(routing, links, sp.GetRequiredService<HostmaskService>(), opts, silence, sp.GetRequiredService<IServiceCommandDispatcher>());

            state.TryAddUser(new User { ConnectionId = "u1", Nick = "alice", UserName = "a", Host = "h", IsRegistered = true });
            var s = new TestSession { ConnectionId = "u1", Nick = "alice", UserName = "a", IsRegistered = true, IsSecureConnection = true };
            sessions.Add(s);

            await h.HandleAsync(s, new IrcMessage(null, "PRIVMSG", new[] { "NickServ" }, "REGISTER test@test.com secret"), state, CancellationToken.None);

            // Register a second nick without using it.
            await h.HandleAsync(s, new IrcMessage(null, "PRIVMSG", new[] { "NickServ" }, "REGISTER bob bob@test.com bpass"), state, CancellationToken.None);

            s.Sent.Clear();
            await h.HandleAsync(s, new IrcMessage(null, "PRIVMSG", new[] { "NickServ" }, "LIST *"), state, CancellationToken.None);

            Assert.Contains(s.Sent, l => l.Contains("NOTICE alice", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(s.Sent, l => l.Contains(":alice", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(s.Sent, l => l.Contains(":bob", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task NickServ_Links_ListsGroupedNicks_And_LinkUnlinkAliasesWork()
        {
            var state = new ServerState();
            var sessions = new FakeSessionRegistry();
            var routing = new RoutingService(sessions, new IrcFormatter());
            var silence = new SilenceService();

            var services = new ServiceCollection();
            var opts = Options.Create(new IrcOptions { ServerInfo = new ServerInfoOptions { Name = "srv", Sid = "001" } });
            services.AddSingleton<IOptions<IrcOptions>>(opts);
            services.AddSingleton<ISessionRegistry>(sessions);
            services.AddSingleton(routing);
            services.AddSingleton(silence);
            services.AddSingleton(new HostmaskService());

            services.AddIrcServices();

            using var sp = services.BuildServiceProvider();
            var links = new ServerLinkService(NullLogger<ServerLinkService>.Instance, new OptionsMonitorStub<IrcOptions>(opts.Value), state, routing, sessions, silence, new WatchService(opts, routing));
            var h = new PrivMsgHandler(routing, links, sp.GetRequiredService<HostmaskService>(), opts, silence, sp.GetRequiredService<IServiceCommandDispatcher>());

            state.TryAddUser(new User { ConnectionId = "u1", Nick = "alice", UserName = "a", Host = "h", IsRegistered = true });
            var s = new TestSession { ConnectionId = "u1", Nick = "alice", UserName = "a", IsRegistered = true, IsSecureConnection = true };
            sessions.Add(s);

            await h.HandleAsync(s, new IrcMessage(null, "PRIVMSG", new[] { "NickServ" }, "REGISTER test@test.com secret"), state, CancellationToken.None);
            await h.HandleAsync(s, new IrcMessage(null, "PRIVMSG", new[] { "NickServ" }, "REGISTER bob bob@test.com bpass"), state, CancellationToken.None);

            // Identify for alice and group bob via LINK alias.
            await h.HandleAsync(s, new IrcMessage(null, "PRIVMSG", new[] { "NickServ" }, "IDENTIFY secret"), state, CancellationToken.None);
            s.Sent.Clear();
            await h.HandleAsync(s, new IrcMessage(null, "PRIVMSG", new[] { "NickServ" }, "LINK bob bpass"), state, CancellationToken.None);
            Assert.Contains(s.Sent, l => l.Contains("grouped", StringComparison.OrdinalIgnoreCase));

            s.Sent.Clear();
            await h.HandleAsync(s, new IrcMessage(null, "PRIVMSG", new[] { "NickServ" }, "LINKS alice"), state, CancellationToken.None);
            Assert.Contains(s.Sent, l => l.Contains(":bob", StringComparison.OrdinalIgnoreCase));

            // Unlink via UNLINK alias.
            s.Sent.Clear();
            await h.HandleAsync(s, new IrcMessage(null, "PRIVMSG", new[] { "NickServ" }, "UNLINK bob"), state, CancellationToken.None);
            Assert.Contains(s.Sent, l => l.Contains("no longer grouped", StringComparison.OrdinalIgnoreCase));

            s.Sent.Clear();
            await h.HandleAsync(s, new IrcMessage(null, "PRIVMSG", new[] { "NickServ" }, "LINKS alice"), state, CancellationToken.None);
            Assert.Contains(s.Sent, l => l.Contains("No linked nicks", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task NickServ_Access_AddListDel_And_IdentifyWithoutPassword_WhenMaskMatches()
        {
            var state = new ServerState();
            var sessions = new FakeSessionRegistry();
            var routing = new RoutingService(sessions, new IrcFormatter());
            var silence = new SilenceService();

            var services = new ServiceCollection();
            var opts = Options.Create(new IrcOptions { ServerInfo = new ServerInfoOptions { Name = "srv", Sid = "001" } });
            services.AddSingleton<IOptions<IrcOptions>>(opts);
            services.AddSingleton<ISessionRegistry>(sessions);
            services.AddSingleton(routing);
            services.AddSingleton(silence);
            services.AddSingleton(new HostmaskService());

            services.AddIrcServices();

            using var sp = services.BuildServiceProvider();
            var links = new ServerLinkService(NullLogger<ServerLinkService>.Instance, new OptionsMonitorStub<IrcOptions>(opts.Value), state, routing, sessions, silence, new WatchService(opts, routing));
            var h = new PrivMsgHandler(routing, links, sp.GetRequiredService<HostmaskService>(), opts, silence, sp.GetRequiredService<IServiceCommandDispatcher>());

            state.TryAddUser(new User { ConnectionId = "u1", Nick = "alice", UserName = "a", Host = "h", IsRegistered = true });
            var s = new TestSession { ConnectionId = "u1", Nick = "alice", UserName = "a", IsRegistered = true, IsSecureConnection = true };
            sessions.Add(s);

            await h.HandleAsync(s, new IrcMessage(null, "PRIVMSG", new[] { "NickServ" }, "REGISTER test@test.com secret"), state, CancellationToken.None);
            await h.HandleAsync(s, new IrcMessage(null, "PRIVMSG", new[] { "NickServ" }, "IDENTIFY secret"), state, CancellationToken.None);

            s.Sent.Clear();
            await h.HandleAsync(s, new IrcMessage(null, "PRIVMSG", new[] { "NickServ" }, "ACCESS ADD a@h"), state, CancellationToken.None);
            Assert.Contains(s.Sent, l => l.Contains("Mask added", StringComparison.OrdinalIgnoreCase));

            s.Sent.Clear();
            await h.HandleAsync(s, new IrcMessage(null, "PRIVMSG", new[] { "NickServ" }, "ACCESS LIST"), state, CancellationToken.None);
            Assert.Contains(s.Sent, l => l.Contains("1.", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(s.Sent, l => l.Contains("a@h", StringComparison.OrdinalIgnoreCase));

            // Logout and identify without password (hostmask matches access list).
            await h.HandleAsync(s, new IrcMessage(null, "PRIVMSG", new[] { "NickServ" }, "LOGOUT"), state, CancellationToken.None);

            s.Sent.Clear();
            await h.HandleAsync(s, new IrcMessage(null, "PRIVMSG", new[] { "NickServ" }, "ACC alice"), state, CancellationToken.None);
            Assert.Contains(s.Sent, l => l.Contains("ACC alice 4", StringComparison.OrdinalIgnoreCase));

            s.Sent.Clear();
            await h.HandleAsync(s, new IrcMessage(null, "PRIVMSG", new[] { "NickServ" }, "STATUS alice"), state, CancellationToken.None);
            Assert.Contains(s.Sent, l => l.Contains("STATUS alice 4", StringComparison.OrdinalIgnoreCase));

            s.Sent.Clear();
            await h.HandleAsync(s, new IrcMessage(null, "PRIVMSG", new[] { "NickServ" }, "IDENTIFY"), state, CancellationToken.None);
            Assert.Contains(s.Sent, l => l.Contains("identified", StringComparison.OrdinalIgnoreCase));

            // Remove mask by index.
            s.Sent.Clear();
            await h.HandleAsync(s, new IrcMessage(null, "PRIVMSG", new[] { "NickServ" }, "ACCESS DEL 1"), state, CancellationToken.None);
            Assert.Contains(s.Sent, l => l.Contains("Mask removed", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task NickServ_RegisterPendingThenConfirm_Works_WhenEnabled()
        {
            var state = new ServerState();
            var sessions = new FakeSessionRegistry();
            var routing = new RoutingService(sessions, new IrcFormatter());
            var silence = new SilenceService();

            var fakeEmail = new FakeEmailSender();

            var services = new ServiceCollection();
            var opts = Options.Create(new IrcOptions
            {
                ServerInfo = new ServerInfoOptions { Name = "srv", Sid = "001", Network = "network" },
                Services = new ServicesOptions
                {
                    NickServ = new NickServOptions
                    {
                        RequireEmailConfirmation = true,
                        PendingRegistrationExpiryHours = 24,
                        Smtp = new NickServSmtpOptions { Host = "smtp", FromAddress = "nickserv@example.test" },
                    }
                }
            });

            services.AddSingleton<IOptions<IrcOptions>>(opts);
            services.AddSingleton<ISessionRegistry>(sessions);
            services.AddSingleton<IEmailSender>(fakeEmail);
            services.AddIrcServices();

            using var sp = services.BuildServiceProvider();

            var links = new ServerLinkService(NullLogger<ServerLinkService>.Instance, new OptionsMonitorStub<IrcOptions>(opts.Value), state, routing, sessions, silence, new WatchService(opts, routing));

            var h = new PrivMsgHandler(
                routing,
                links,
                new HostmaskService(),
                opts,
                silence,
                sp.GetRequiredService<IServiceCommandDispatcher>());

            state.TryAddUser(new User { ConnectionId = "u1", Nick = "alice", UserName = "a", Host = "h", IsRegistered = true });

            var s = new TestSession { ConnectionId = "u1", Nick = "alice", UserName = "a", IsRegistered = true };
            sessions.Add(s);

            await h.HandleAsync(s, new IrcMessage(null, "PRIVMSG", new[] { "NickServ" }, "REGISTER alice@test.com secret"), state, CancellationToken.None);
            Assert.True(fakeEmail.TryDequeue(out var email));
            Assert.Contains("/NickServ CONFIRM", email.Body, StringComparison.OrdinalIgnoreCase);
            var code = ExtractConfirmCode(email.Body);
            Assert.False(string.IsNullOrWhiteSpace(code));

            s.Sent.Clear();

            await h.HandleAsync(s, new IrcMessage(null, "PRIVMSG", new[] { "NickServ" }, $"CONFIRM {code}"), state, CancellationToken.None);
            s.Sent.Clear();

            await h.HandleAsync(s, new IrcMessage(null, "PRIVMSG", new[] { "NickServ" }, "IDENTIFY secret"), state, CancellationToken.None);
            Assert.Contains(s.Sent, l => l.Contains("identified", StringComparison.OrdinalIgnoreCase));
        }

        private static string ExtractConfirmCode(string body)
        {
            // Line includes: /NickServ CONFIRM <nick> <code>
            var marker = "/NickServ CONFIRM ";
            var idx = body.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
                return string.Empty;

            idx += marker.Length;
            var lineEnd = body.IndexOfAny(new[] { '\r', '\n' }, idx);
            if (lineEnd < 0)
                lineEnd = body.Length;

            var line = body[idx..lineEnd].Trim();
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 2)
                return string.Empty;

            return parts[1];
        }

        [Fact]
        public async Task NickServ_SetPassword_RequiresIdentify_AndUpdates()
        {
            var state = new ServerState();
            var sessions = new FakeSessionRegistry();
            var routing = new RoutingService(sessions, new IrcFormatter());
            var silence = new SilenceService();

            var services = new ServiceCollection();
            var opts = Options.Create(new IrcOptions { ServerInfo = new ServerInfoOptions { Name = "srv", Sid = "001" } });
            services.AddSingleton<IOptions<IrcOptions>>(opts);
            services.AddSingleton<ISessionRegistry>(sessions);
            services.AddIrcServices();

            using var sp = services.BuildServiceProvider();

            var links = new ServerLinkService(NullLogger<ServerLinkService>.Instance, new OptionsMonitorStub<IrcOptions>(opts.Value), state, routing, sessions, silence, new WatchService(opts, routing));
            var h = new PrivMsgHandler(routing, links, new HostmaskService(), opts, silence, sp.GetRequiredService<IServiceCommandDispatcher>());

            state.TryAddUser(new User { ConnectionId = "u1", Nick = "alice", UserName = "a", Host = "h", IsRegistered = true });
            var s = new TestSession { ConnectionId = "u1", Nick = "alice", UserName = "a", IsRegistered = true };
            sessions.Add(s);

            await h.HandleAsync(s, new IrcMessage(null, "PRIVMSG", new[] { "NickServ" }, "REGISTER test@test.com secret"), state, CancellationToken.None);
            s.Sent.Clear();

            await h.HandleAsync(s, new IrcMessage(null, "PRIVMSG", new[] { "NickServ" }, "SET PASSWORD newpass"), state, CancellationToken.None);
            Assert.Contains(s.Sent, l => l.Contains("Password updated", StringComparison.OrdinalIgnoreCase));

            s.Sent.Clear();

            await h.HandleAsync(s, new IrcMessage(null, "PRIVMSG", new[] { "NickServ" }, "LOGOUT"), state, CancellationToken.None);
            await h.HandleAsync(s, new IrcMessage(null, "PRIVMSG", new[] { "NickServ" }, "IDENTIFY secret"), state, CancellationToken.None);
            Assert.Contains(s.Sent, l => l.Contains("incorrect", StringComparison.OrdinalIgnoreCase));

            s.Sent.Clear();

            await h.HandleAsync(s, new IrcMessage(null, "PRIVMSG", new[] { "NickServ" }, "IDENTIFY newpass"), state, CancellationToken.None);
            Assert.Contains(s.Sent, l => l.Contains("identified", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task NickServ_Ghost_ClosesTargetSession_WhenPasswordMatches()
        {
            var state = new ServerState();
            var sessions = new FakeSessionRegistry();
            var routing = new RoutingService(sessions, new IrcFormatter());
            var silence = new SilenceService();

            var services = new ServiceCollection();
            var opts = Options.Create(new IrcOptions { ServerInfo = new ServerInfoOptions { Name = "srv", Sid = "001" } });
            services.AddSingleton<IOptions<IrcOptions>>(opts);
            services.AddSingleton<ISessionRegistry>(sessions);
            services.AddIrcServices();

            using var sp = services.BuildServiceProvider();

            var links = new ServerLinkService(NullLogger<ServerLinkService>.Instance, new OptionsMonitorStub<IrcOptions>(opts.Value), state, routing, sessions, silence, new WatchService(opts, routing));
            var h = new PrivMsgHandler(routing, links, new HostmaskService(), opts, silence, sp.GetRequiredService<IServiceCommandDispatcher>());

            // Create an account for alice.
            var reg = new TestSession { ConnectionId = "reg", Nick = "alice", UserName = "r", IsRegistered = true };
            sessions.Add(reg);
            await h.HandleAsync(reg, new IrcMessage(null, "PRIVMSG", new[] { "NickServ" }, "REGISTER test@test.com secret"), state, CancellationToken.None);

            // Caller is a different nick.
            state.TryAddUser(new User { ConnectionId = "owner", Nick = "bob", UserName = "b", Host = "h", IsRegistered = true });
            var owner = new TestSession { ConnectionId = "owner", Nick = "bob", UserName = "b", IsRegistered = true };
            sessions.Add(owner);

            // Victim currently holds alice.
            state.TryAddUser(new User { ConnectionId = "target", Nick = "alice", UserName = "t", Host = "h2", IsRegistered = true });
            var target = new TestSession { ConnectionId = "target", Nick = "alice", UserName = "t", IsRegistered = true };
            sessions.Add(target);

            await h.HandleAsync(owner, new IrcMessage(null, "PRIVMSG", new[] { "NickServ" }, "GHOST alice secret"), state, CancellationToken.None);

            Assert.Contains(owner.Sent, l => l.Contains("Ghosted", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task NickServ_Recover_ClosesTargetSession_AndIdentifiesCaller()
        {
            var state = new ServerState();
            var sessions = new FakeSessionRegistry();
            var routing = new RoutingService(sessions, new IrcFormatter());
            var silence = new SilenceService();

            var services = new ServiceCollection();
            var opts = Options.Create(new IrcOptions { ServerInfo = new ServerInfoOptions { Name = "srv", Sid = "001" } });
            services.AddSingleton<IOptions<IrcOptions>>(opts);
            services.AddSingleton<ISessionRegistry>(sessions);
            services.AddIrcServices();

            using var sp = services.BuildServiceProvider();

            var links = new ServerLinkService(NullLogger<ServerLinkService>.Instance, new OptionsMonitorStub<IrcOptions>(opts.Value), state, routing, sessions, silence, new WatchService(opts, routing));
            var h = new PrivMsgHandler(routing, links, new HostmaskService(), opts, silence, sp.GetRequiredService<IServiceCommandDispatcher>());

            // Create an account for alice.
            var reg = new TestSession { ConnectionId = "reg", Nick = "alice", UserName = "r", IsRegistered = true };
            sessions.Add(reg);
            await h.HandleAsync(reg, new IrcMessage(null, "PRIVMSG", new[] { "NickServ" }, "REGISTER test@test.com secret"), state, CancellationToken.None);

            // Caller is a different nick.
            state.TryAddUser(new User { ConnectionId = "owner", Nick = "bob", UserName = "b", Host = "h", IsRegistered = true });
            var owner = new TestSession { ConnectionId = "owner", Nick = "bob", UserName = "b", IsRegistered = true };
            sessions.Add(owner);

            // Victim currently holds alice.
            state.TryAddUser(new User { ConnectionId = "target", Nick = "alice", UserName = "t", Host = "h2", IsRegistered = true });
            var target = new TestSession { ConnectionId = "target", Nick = "alice", UserName = "t", IsRegistered = true };
            sessions.Add(target);

            await h.HandleAsync(owner, new IrcMessage(null, "PRIVMSG", new[] { "NickServ" }, "RECOVER alice secret"), state, CancellationToken.None);

            Assert.True(target.Closed);
            Assert.Contains(owner.Sent, l => l.Contains("Recovered", StringComparison.OrdinalIgnoreCase));

            var auth = sp.GetRequiredService<IAuthState>();
            var identified = await auth.GetIdentifiedAccountAsync(owner.ConnectionId, CancellationToken.None);
            Assert.Equal("alice", identified);
        }

        [Fact]
        public async Task NickServ_Release_IdentifiesCaller_ForSpecifiedNick()
        {
            var state = new ServerState();
            var sessions = new FakeSessionRegistry();
            var routing = new RoutingService(sessions, new IrcFormatter());
            var silence = new SilenceService();

            var services = new ServiceCollection();
            var opts = Options.Create(new IrcOptions { ServerInfo = new ServerInfoOptions { Name = "srv", Sid = "001" } });
            services.AddSingleton<IOptions<IrcOptions>>(opts);
            services.AddSingleton<ISessionRegistry>(sessions);
            services.AddIrcServices();

            using var sp = services.BuildServiceProvider();

            var links = new ServerLinkService(NullLogger<ServerLinkService>.Instance, new OptionsMonitorStub<IrcOptions>(opts.Value), state, routing, sessions, silence, new WatchService(opts, routing));
            var h = new PrivMsgHandler(routing, links, new HostmaskService(), opts, silence, sp.GetRequiredService<IServiceCommandDispatcher>());

            // Create an account for alice.
            var reg = new TestSession { ConnectionId = "reg", Nick = "alice", UserName = "r", IsRegistered = true };
            sessions.Add(reg);
            await h.HandleAsync(reg, new IrcMessage(null, "PRIVMSG", new[] { "NickServ" }, "REGISTER test@test.com secret"), state, CancellationToken.None);

            // Caller is a different nick.
            state.TryAddUser(new User { ConnectionId = "owner", Nick = "bob", UserName = "b", Host = "h", IsRegistered = true });
            var owner = new TestSession { ConnectionId = "owner", Nick = "bob", UserName = "b", IsRegistered = true };
            sessions.Add(owner);

            await h.HandleAsync(owner, new IrcMessage(null, "PRIVMSG", new[] { "NickServ" }, "RELEASE alice secret"), state, CancellationToken.None);

            Assert.Contains(owner.Sent, l => l.Contains("identified", StringComparison.OrdinalIgnoreCase));

            var auth = sp.GetRequiredService<IAuthState>();
            var identified = await auth.GetIdentifiedAccountAsync(owner.ConnectionId, CancellationToken.None);
            Assert.Equal("alice", identified);
        }

        [Fact]
        public async Task NickServ_Status_ReturnsExpectedCodes()
        {
            var state = new ServerState();
            var sessions = new FakeSessionRegistry();
            var routing = new RoutingService(sessions, new IrcFormatter());
            var silence = new SilenceService();

            var services = new ServiceCollection();
            var opts = Options.Create(new IrcOptions { ServerInfo = new ServerInfoOptions { Name = "srv", Sid = "001" } });
            services.AddSingleton<IOptions<IrcOptions>>(opts);
            services.AddSingleton<ISessionRegistry>(sessions);
            services.AddIrcServices();

            using var sp = services.BuildServiceProvider();

            var links = new ServerLinkService(NullLogger<ServerLinkService>.Instance, new OptionsMonitorStub<IrcOptions>(opts.Value), state, routing, sessions, silence, new WatchService(opts, routing));
            var h = new PrivMsgHandler(routing, links, new HostmaskService(), opts, silence, sp.GetRequiredService<IServiceCommandDispatcher>());

            state.TryAddUser(new User { ConnectionId = "caller", Nick = "bob", UserName = "b", Host = "h", IsRegistered = true });
            var caller = new TestSession { ConnectionId = "caller", Nick = "bob", UserName = "b", IsRegistered = true };
            sessions.Add(caller);

            // 0 = offline
            await h.HandleAsync(caller, new IrcMessage(null, "PRIVMSG", new[] { "NickServ" }, "STATUS alice"), state, CancellationToken.None);
            Assert.True(caller.Sent.Any(l => l.IndexOf("STATUS alice 0", StringComparison.OrdinalIgnoreCase) >= 0), string.Join("\n", caller.Sent));

            caller.Sent.Clear();

            // 1 = online + unregistered
            state.TryAddUser(new User { ConnectionId = "u1", Nick = "unreg", UserName = "u", Host = "h2", IsRegistered = true });
            var unreg = new TestSession { ConnectionId = "u1", Nick = "unreg", UserName = "u", IsRegistered = true };
            sessions.Add(unreg);

            await h.HandleAsync(caller, new IrcMessage(null, "PRIVMSG", new[] { "NickServ" }, "STATUS unreg"), state, CancellationToken.None);
            Assert.True(caller.Sent.Any(l => l.IndexOf("STATUS unreg 1", StringComparison.OrdinalIgnoreCase) >= 0), string.Join("\n", caller.Sent));

            caller.Sent.Clear();

            // Register account for alice.
            var reg = new TestSession { ConnectionId = "reg", Nick = "alice", UserName = "r", IsRegistered = true };
            sessions.Add(reg);
            await h.HandleAsync(reg, new IrcMessage(null, "PRIVMSG", new[] { "NickServ" }, "REGISTER test@test.com secret"), state, CancellationToken.None);

            // 2 = online + registered (not identified)
            state.TryAddUser(new User { ConnectionId = "holder", Nick = "alice", UserName = "h", Host = "h4", IsRegistered = true });
            var holder = new TestSession { ConnectionId = "holder", Nick = "alice", UserName = "h", IsRegistered = true };
            sessions.Add(holder);

            caller.Sent.Clear();
            await h.HandleAsync(caller, new IrcMessage(null, "PRIVMSG", new[] { "NickServ" }, "STATUS alice"), state, CancellationToken.None);
            Assert.True(caller.Sent.Any(l => l.IndexOf("STATUS alice 2", StringComparison.OrdinalIgnoreCase) >= 0), string.Join("\n", caller.Sent));

            // 3 = online + identified
            await h.HandleAsync(holder, new IrcMessage(null, "PRIVMSG", new[] { "NickServ" }, "IDENTIFY secret"), state, CancellationToken.None);

            caller.Sent.Clear();
            await h.HandleAsync(caller, new IrcMessage(null, "PRIVMSG", new[] { "NickServ" }, "STATUS alice"), state, CancellationToken.None);
            var dbg = string.Join("\n", caller.Sent.Select(s => string.Concat(s.Select(ch => $"\\u{(int)ch:X4}"))));
            Assert.True(caller.Sent.Any(l => l.IndexOf("STATUS alice 3", StringComparison.OrdinalIgnoreCase) >= 0), dbg);
        }

        [Fact]
        public async Task NickServ_Acc_ReturnsExpectedCodes()
        {
            var state = new ServerState();
            var sessions = new FakeSessionRegistry();
            var routing = new RoutingService(sessions, new IrcFormatter());
            var silence = new SilenceService();

            var services = new ServiceCollection();
            var opts = Options.Create(new IrcOptions { ServerInfo = new ServerInfoOptions { Name = "srv", Sid = "001" } });
            services.AddSingleton<IOptions<IrcOptions>>(opts);
            services.AddSingleton<ISessionRegistry>(sessions);
            services.AddIrcServices();

            using var sp = services.BuildServiceProvider();

            var links = new ServerLinkService(NullLogger<ServerLinkService>.Instance, new OptionsMonitorStub<IrcOptions>(opts.Value), state, routing, sessions, silence, new WatchService(opts, routing));
            var h = new PrivMsgHandler(routing, links, new HostmaskService(), opts, silence, sp.GetRequiredService<IServiceCommandDispatcher>());

            state.TryAddUser(new User { ConnectionId = "caller", Nick = "bob", UserName = "b", Host = "h", IsRegistered = true });
            var caller = new TestSession { ConnectionId = "caller", Nick = "bob", UserName = "b", IsRegistered = true };
            sessions.Add(caller);

            // 0 = offline
            await h.HandleAsync(caller, new IrcMessage(null, "PRIVMSG", new[] { "NickServ" }, "ACC alice"), state, CancellationToken.None);
            Assert.True(caller.Sent.Any(l => l.IndexOf("ACC alice 0", StringComparison.OrdinalIgnoreCase) >= 0), string.Join("\n", caller.Sent));

            caller.Sent.Clear();

            // 1 = online + unregistered
            state.TryAddUser(new User { ConnectionId = "u1", Nick = "unreg", UserName = "u", Host = "h2", IsRegistered = true });
            var unreg = new TestSession { ConnectionId = "u1", Nick = "unreg", UserName = "u", IsRegistered = true };
            sessions.Add(unreg);

            await h.HandleAsync(caller, new IrcMessage(null, "PRIVMSG", new[] { "NickServ" }, "ACC unreg"), state, CancellationToken.None);
            Assert.True(caller.Sent.Any(l => l.IndexOf("ACC unreg 1", StringComparison.OrdinalIgnoreCase) >= 0), string.Join("\n", caller.Sent));

            caller.Sent.Clear();

            // Register account for alice.
            var reg = new TestSession { ConnectionId = "reg", Nick = "alice", UserName = "r", IsRegistered = true };
            sessions.Add(reg);
            await h.HandleAsync(reg, new IrcMessage(null, "PRIVMSG", new[] { "NickServ" }, "REGISTER test@test.com secret"), state, CancellationToken.None);

            // 2 = online + registered (not identified)
            state.TryAddUser(new User { ConnectionId = "holder", Nick = "alice", UserName = "h", Host = "h3", IsRegistered = true });
            var holder = new TestSession { ConnectionId = "holder", Nick = "alice", UserName = "h", IsRegistered = true };
            sessions.Add(holder);

            caller.Sent.Clear();
            await h.HandleAsync(caller, new IrcMessage(null, "PRIVMSG", new[] { "NickServ" }, "ACC alice"), state, CancellationToken.None);
            Assert.True(caller.Sent.Any(l => l.IndexOf("ACC alice 2", StringComparison.OrdinalIgnoreCase) >= 0), string.Join("\n", caller.Sent));

            // 3 = online + identified
            await h.HandleAsync(holder, new IrcMessage(null, "PRIVMSG", new[] { "NickServ" }, "IDENTIFY secret"), state, CancellationToken.None);

            caller.Sent.Clear();
            await h.HandleAsync(caller, new IrcMessage(null, "PRIVMSG", new[] { "NickServ" }, "ACC alice"), state, CancellationToken.None);
            Assert.True(caller.Sent.Any(l => l.IndexOf("ACC alice 3", StringComparison.OrdinalIgnoreCase) >= 0), string.Join("\n", caller.Sent));
        }

        [Fact]
        public async Task NickServ_Identify_WithNick_WorksWhenUsingDifferentNick()
        {
            var state = new ServerState();
            var sessions = new FakeSessionRegistry();
            var routing = new RoutingService(sessions, new IrcFormatter());
            var silence = new SilenceService();

            var services = new ServiceCollection();
            var opts = Options.Create(new IrcOptions { ServerInfo = new ServerInfoOptions { Name = "srv", Sid = "001" } });
            services.AddSingleton<IOptions<IrcOptions>>(opts);
            services.AddSingleton<ISessionRegistry>(sessions);
            services.AddIrcServices();

            using var sp = services.BuildServiceProvider();

            var links = new ServerLinkService(NullLogger<ServerLinkService>.Instance, new OptionsMonitorStub<IrcOptions>(opts.Value), state, routing, sessions, silence, new WatchService(opts, routing));
            var h = new PrivMsgHandler(routing, links, new HostmaskService(), opts, silence, sp.GetRequiredService<IServiceCommandDispatcher>());

            // Register account for alice.
            var reg = new TestSession { ConnectionId = "reg", Nick = "alice", UserName = "r", IsRegistered = true };
            sessions.Add(reg);
            await h.HandleAsync(reg, new IrcMessage(null, "PRIVMSG", new[] { "NickServ" }, "REGISTER test@test.com secret"), state, CancellationToken.None);

            // Caller is using a different nick.
            state.TryAddUser(new User { ConnectionId = "u1", Nick = "bob", UserName = "b", Host = "h", IsRegistered = true });
            var caller = new TestSession { ConnectionId = "u1", Nick = "bob", UserName = "b", IsRegistered = true };
            sessions.Add(caller);

            await h.HandleAsync(caller, new IrcMessage(null, "PRIVMSG", new[] { "NickServ" }, "IDENTIFY alice secret"), state, CancellationToken.None);
            Assert.Contains(caller.Sent, l => l.IndexOf("identified", StringComparison.OrdinalIgnoreCase) >= 0);

            var auth = sp.GetRequiredService<IAuthState>();
            var identified = await auth.GetIdentifiedAccountAsync(caller.ConnectionId, CancellationToken.None);
            Assert.Equal("alice", identified);
        }

        [Fact]
        public async Task NickServ_Register_WithNick_WorksWhenUsingDifferentNick()
        {
            var state = new ServerState();
            var sessions = new FakeSessionRegistry();
            var routing = new RoutingService(sessions, new IrcFormatter());
            var silence = new SilenceService();

            var services = new ServiceCollection();
            var opts = Options.Create(new IrcOptions { ServerInfo = new ServerInfoOptions { Name = "srv", Sid = "001" } });
            services.AddSingleton<IOptions<IrcOptions>>(opts);
            services.AddSingleton<ISessionRegistry>(sessions);
            services.AddIrcServices();

            using var sp = services.BuildServiceProvider();

            var links = new ServerLinkService(NullLogger<ServerLinkService>.Instance, new OptionsMonitorStub<IrcOptions>(opts.Value), state, routing, sessions, silence, new WatchService(opts, routing));
            var h = new PrivMsgHandler(routing, links, new HostmaskService(), opts, silence, sp.GetRequiredService<IServiceCommandDispatcher>());

            // Caller is using a different nick.
            state.TryAddUser(new User { ConnectionId = "u1", Nick = "bob", UserName = "b", Host = "h", IsRegistered = true });
            var caller = new TestSession { ConnectionId = "u1", Nick = "bob", UserName = "b", IsRegistered = true };
            sessions.Add(caller);

            await h.HandleAsync(caller, new IrcMessage(null, "PRIVMSG", new[] { "NickServ" }, "REGISTER alice alice@test.com secret"), state, CancellationToken.None);
            Assert.Contains(caller.Sent, l => l.IndexOf("registered and identified", StringComparison.OrdinalIgnoreCase) >= 0);

            var auth = sp.GetRequiredService<IAuthState>();
            var identified = await auth.GetIdentifiedAccountAsync(caller.ConnectionId, CancellationToken.None);
            Assert.Equal("alice", identified);

            caller.Sent.Clear();
            await h.HandleAsync(caller, new IrcMessage(null, "PRIVMSG", new[] { "NickServ" }, "INFO alice"), state, CancellationToken.None);
            Assert.Contains(caller.Sent, l => l.IndexOf("Nickname:", StringComparison.OrdinalIgnoreCase) >= 0);
            Assert.Contains(caller.Sent, l => l.IndexOf("Registered:", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        [Fact]
        public async Task NickServ_Group_And_Ungroup_ControlsIdentifyForAlias()
        {
            var state = new ServerState();
            var sessions = new FakeSessionRegistry();
            var routing = new RoutingService(sessions, new IrcFormatter());
            var silence = new SilenceService();

            var services = new ServiceCollection();
            var opts = Options.Create(new IrcOptions { ServerInfo = new ServerInfoOptions { Name = "srv", Sid = "001" } });
            services.AddSingleton<IOptions<IrcOptions>>(opts);
            services.AddSingleton<ISessionRegistry>(sessions);
            services.AddIrcServices();

            using var sp = services.BuildServiceProvider();

            var links = new ServerLinkService(NullLogger<ServerLinkService>.Instance, new OptionsMonitorStub<IrcOptions>(opts.Value), state, routing, sessions, silence, new WatchService(opts, routing));
            var h = new PrivMsgHandler(routing, links, new HostmaskService(), opts, silence, sp.GetRequiredService<IServiceCommandDispatcher>());

            // Register master 'alice'.
            state.TryAddUser(new User { ConnectionId = "u1", Nick = "alice", UserName = "a", Host = "h", IsRegistered = true });
            var alice = new TestSession { ConnectionId = "u1", Nick = "alice", UserName = "a", IsRegistered = true };
            sessions.Add(alice);
            await h.HandleAsync(alice, new IrcMessage(null, "PRIVMSG", new[] { "NickServ" }, "REGISTER test@test.com secret"), state, CancellationToken.None);

            // Register alias 'ally' with a different password.
            var bob = new TestSession { ConnectionId = "u2", Nick = "bob", UserName = "b", IsRegistered = true };
            sessions.Add(bob);
            await h.HandleAsync(bob, new IrcMessage(null, "PRIVMSG", new[] { "NickServ" }, "REGISTER ally ally@test.com otherpass"), state, CancellationToken.None);

            // Group ally into alice account.
            alice.Sent.Clear();
            await h.HandleAsync(alice, new IrcMessage(null, "PRIVMSG", new[] { "NickServ" }, "GROUP ally otherpass"), state, CancellationToken.None);
            Assert.Contains(alice.Sent, l => l.IndexOf("grouped", StringComparison.OrdinalIgnoreCase) >= 0);

            // After grouping, identifying to ally should accept alice's password.
            bob.Sent.Clear();
            await h.HandleAsync(bob, new IrcMessage(null, "PRIVMSG", new[] { "NickServ" }, "LOGOUT"), state, CancellationToken.None);
            await h.HandleAsync(bob, new IrcMessage(null, "PRIVMSG", new[] { "NickServ" }, "IDENTIFY ally secret"), state, CancellationToken.None);
            Assert.Contains(bob.Sent, l => l.IndexOf("identified", StringComparison.OrdinalIgnoreCase) >= 0);

            // Ungroup and verify ally password works again.
            await h.HandleAsync(alice, new IrcMessage(null, "PRIVMSG", new[] { "NickServ" }, "UNGROUP ally"), state, CancellationToken.None);

            bob.Sent.Clear();
            await h.HandleAsync(bob, new IrcMessage(null, "PRIVMSG", new[] { "NickServ" }, "LOGOUT"), state, CancellationToken.None);
            await h.HandleAsync(bob, new IrcMessage(null, "PRIVMSG", new[] { "NickServ" }, "IDENTIFY ally secret"), state, CancellationToken.None);
            Assert.Contains(bob.Sent, l => l.IndexOf("incorrect", StringComparison.OrdinalIgnoreCase) >= 0);

            bob.Sent.Clear();
            await h.HandleAsync(bob, new IrcMessage(null, "PRIVMSG", new[] { "NickServ" }, "IDENTIFY ally otherpass"), state, CancellationToken.None);
            Assert.Contains(bob.Sent, l => l.IndexOf("identified", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        [Fact]
        public async Task NickServ_SetEnforceOff_DisablesNickEnforcement()
        {
            var state = new ServerState();
            var sessions = new FakeSessionRegistry();
            var routing = new RoutingService(sessions, new IrcFormatter());
            var silence = new SilenceService();

            var services = new ServiceCollection();
            var opts = Options.Create(new IrcOptions
            {
                ServerInfo = new ServerInfoOptions { Name = "srv", Sid = "001" },
                Services = new ServicesOptions { NickServ = new NickServOptions { EnforceRegisteredNicks = true, EnforceDelaySeconds = 0 } }
            });

            services.AddSingleton<IOptions<IrcOptions>>(opts);
            services.AddSingleton<ISessionRegistry>(sessions);
            services.AddSingleton(routing);
            services.AddSingleton(silence);
            services.AddSingleton(new HostmaskService());
            services.AddIrcServices();

            using var sp = services.BuildServiceProvider();

            var links = new ServerLinkService(NullLogger<ServerLinkService>.Instance, new OptionsMonitorStub<IrcOptions>(opts.Value), state, routing, sessions, silence, new WatchService(opts, routing));
            var privmsg = new PrivMsgHandler(routing, links, sp.GetRequiredService<HostmaskService>(), opts, silence, sp.GetRequiredService<IServiceCommandDispatcher>());

            // Register 'alice' and disable enforcement on the account.
            state.TryAddUser(new User { ConnectionId = "reg", Nick = "alice", UserName = "r", Host = "h", IsRegistered = true });
            var reg = new TestSession { ConnectionId = "reg", Nick = "alice", UserName = "r", IsRegistered = true };
            sessions.Add(reg);
            await privmsg.HandleAsync(reg, new IrcMessage(null, "PRIVMSG", new[] { "NickServ" }, "REGISTER test@test.com secret"), state, CancellationToken.None);

            reg.Sent.Clear();
            await privmsg.HandleAsync(reg, new IrcMessage(null, "PRIVMSG", new[] { "NickServ" }, "SET ENFORCE OFF"), state, CancellationToken.None);
            Assert.Contains(reg.Sent, l => l.IndexOf("ENFORCE", StringComparison.OrdinalIgnoreCase) >= 0);

            // Intruder switches to registered nick without identifying; should NOT be closed.
            state.TryAddUser(new User { ConnectionId = "u2", Nick = "bob", UserName = "b", Host = "h2", IsRegistered = true });
            var intruder = new TestSession { ConnectionId = "u2", Nick = "bob", UserName = "b", IsRegistered = true };
            sessions.Add(intruder);

            var events = sp.GetRequiredService<IServiceSessionEvents>();
            intruder.Nick = "alice";
            await events.OnNickChangedAsync(intruder, oldNick: "bob", newNick: "alice", state, CancellationToken.None);

            Assert.False(intruder.Closed);
        }

        [Fact]
        public async Task NickServ_ListChans_ShowsFounderChannels()
        {
            var state = new ServerState();
            var sessions = new FakeSessionRegistry();
            var routing = new RoutingService(sessions, new IrcFormatter());
            var silence = new SilenceService();

            var services = new ServiceCollection();
            var opts = Options.Create(new IrcOptions { ServerInfo = new ServerInfoOptions { Name = "srv", Sid = "001" } });
            services.AddSingleton<IOptions<IrcOptions>>(opts);
            services.AddSingleton<ISessionRegistry>(sessions);
            services.AddIrcServices();

            using var sp = services.BuildServiceProvider();

            var links = new ServerLinkService(NullLogger<ServerLinkService>.Instance, new OptionsMonitorStub<IrcOptions>(opts.Value), state, routing, sessions, silence, new WatchService(opts, routing));
            var h = new PrivMsgHandler(routing, links, new HostmaskService(), opts, silence, sp.GetRequiredService<IServiceCommandDispatcher>());

            state.TryAddUser(new User { ConnectionId = "u1", Nick = "alice", UserName = "a", Host = "h", IsRegistered = true });
            var s = new TestSession { ConnectionId = "u1", Nick = "alice", UserName = "a", IsRegistered = true };
            sessions.Add(s);
            await h.HandleAsync(s, new IrcMessage(null, "PRIVMSG", new[] { "NickServ" }, "REGISTER test@test.com secret"), state, CancellationToken.None);

            // Seed a registered channel.
            var chRepo = sp.GetRequiredService<IChanServChannelRepository>();
            await chRepo.TryCreateAsync(new RegisteredChannel { Name = "#test", FounderAccount = "alice", PasswordHash = "x" }, CancellationToken.None);

            s.Sent.Clear();
            await h.HandleAsync(s, new IrcMessage(null, "PRIVMSG", new[] { "NickServ" }, "LISTCHANS"), state, CancellationToken.None);
            Assert.Contains(s.Sent, l => l.IndexOf("#test", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        [Fact]
        public async Task NickServ_Persistence_FileRepo_SavesAndReloadsAccount()
        {
            var dir = Path.Combine(Path.GetTempPath(), "UnrealBG-IRCd.Tests", Guid.NewGuid().ToString("n"));
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "nick-accounts.json");

            try
            {
                // First run: create and update account.
                {
                    var state = new ServerState();
                    var sessions = new FakeSessionRegistry();
                    var routing = new RoutingService(sessions, new IrcFormatter());
                    var silence = new SilenceService();

                    var services = new ServiceCollection();
                    var opts = Options.Create(new IrcOptions
                    {
                        ServerInfo = new ServerInfoOptions { Name = "srv", Sid = "001" },
                        Services = new ServicesOptions { NickServ = new NickServOptions { AccountsFilePath = path } }
                    });

                    services.AddSingleton<IOptions<IrcOptions>>(opts);
                    services.AddSingleton<ISessionRegistry>(sessions);
                    services.AddIrcServices();

                    using var sp = services.BuildServiceProvider();

                    var links = new ServerLinkService(NullLogger<ServerLinkService>.Instance, new OptionsMonitorStub<IrcOptions>(opts.Value), state, routing, sessions, silence, new WatchService(opts, routing));
                    var h = new PrivMsgHandler(routing, links, new HostmaskService(), opts, silence, sp.GetRequiredService<IServiceCommandDispatcher>());

                    state.TryAddUser(new User { ConnectionId = "u1", Nick = "alice", UserName = "a", Host = "h", IsRegistered = true });
                    var s = new TestSession { ConnectionId = "u1", Nick = "alice", UserName = "a", IsRegistered = true };
                    sessions.Add(s);

                    await h.HandleAsync(s, new IrcMessage(null, "PRIVMSG", new[] { "NickServ" }, "REGISTER test@test.com secret"), state, CancellationToken.None);
                    await h.HandleAsync(s, new IrcMessage(null, "PRIVMSG", new[] { "NickServ" }, "IDENTIFY secret"), state, CancellationToken.None);
                    await h.HandleAsync(s, new IrcMessage(null, "PRIVMSG", new[] { "NickServ" }, "SET ENFORCE OFF"), state, CancellationToken.None);
                    await h.HandleAsync(s, new IrcMessage(null, "PRIVMSG", new[] { "NickServ" }, "SET EMAIL alice@example.com"), state, CancellationToken.None);

                    Assert.True(File.Exists(path));
                }

                // Second run: repository loads from file.
                {
                    var services = new ServiceCollection();
                    var opts = Options.Create(new IrcOptions
                    {
                        ServerInfo = new ServerInfoOptions { Name = "srv", Sid = "001" },
                        Services = new ServicesOptions { NickServ = new NickServOptions { AccountsFilePath = path } }
                    });

                    services.AddSingleton<IOptions<IrcOptions>>(opts);
                    services.AddIrcServices();
                    using var sp = services.BuildServiceProvider();

                    var repo = sp.GetRequiredService<INickAccountRepository>();
                    var a = await repo.GetByNameAsync("alice", CancellationToken.None);

                    Assert.NotNull(a);
                    Assert.Equal("alice", a!.Name, ignoreCase: true);
                    Assert.Equal("alice@example.com", a.Email);
                    Assert.False(a.Enforce);
                    Assert.False(string.IsNullOrWhiteSpace(a.PasswordHash));
                }
            }
            finally
            {
                try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
                catch { /* ignore */ }
            }
        }

        [Fact]
        public async Task NickServ_Drop_RemovesAccount_AndClearsIdentify()
        {
            var state = new ServerState();
            var sessions = new FakeSessionRegistry();
            var routing = new RoutingService(sessions, new IrcFormatter());
            var silence = new SilenceService();

            var services = new ServiceCollection();
            var opts = Options.Create(new IrcOptions { ServerInfo = new ServerInfoOptions { Name = "srv", Sid = "001" } });
            services.AddSingleton<IOptions<IrcOptions>>(opts);
            services.AddSingleton<ISessionRegistry>(sessions);
            services.AddIrcServices();

            using var sp = services.BuildServiceProvider();

            var links = new ServerLinkService(NullLogger<ServerLinkService>.Instance, new OptionsMonitorStub<IrcOptions>(opts.Value), state, routing, sessions, silence, new WatchService(opts, routing));
            var h = new PrivMsgHandler(routing, links, new HostmaskService(), opts, silence, sp.GetRequiredService<IServiceCommandDispatcher>());

            state.TryAddUser(new User { ConnectionId = "u1", Nick = "alice", UserName = "a", Host = "h", IsRegistered = true });
            var s = new TestSession { ConnectionId = "u1", Nick = "alice", UserName = "a", IsRegistered = true };
            sessions.Add(s);

            await h.HandleAsync(s, new IrcMessage(null, "PRIVMSG", new[] { "NickServ" }, "REGISTER test@test.com secret"), state, CancellationToken.None);
            s.Sent.Clear();

            await h.HandleAsync(s, new IrcMessage(null, "PRIVMSG", new[] { "NickServ" }, "DROP secret"), state, CancellationToken.None);
            Assert.Contains(s.Sent, l => l.Contains("dropped", StringComparison.OrdinalIgnoreCase));

            s.Sent.Clear();
            await h.HandleAsync(s, new IrcMessage(null, "PRIVMSG", new[] { "NickServ" }, "INFO alice"), state, CancellationToken.None);
            Assert.Contains(s.Sent, l => l.Contains("not registered", StringComparison.OrdinalIgnoreCase));

            s.Sent.Clear();
            await h.HandleAsync(s, new IrcMessage(null, "PRIVMSG", new[] { "NickServ" }, "IDENTIFY secret"), state, CancellationToken.None);
            Assert.Contains(s.Sent, l => l.Contains("not registered", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task NickServ_Identify_UnregisteredNick_IsThrottledToAvoidSpam()
        {
            var state = new ServerState();
            var sessions = new FakeSessionRegistry();
            var routing = new RoutingService(sessions, new IrcFormatter());
            var silence = new SilenceService();

            var services = new ServiceCollection();
            var opts = Options.Create(new IrcOptions { ServerInfo = new ServerInfoOptions { Name = "srv", Sid = "001" } });
            services.AddSingleton<IOptions<IrcOptions>>(opts);
            services.AddSingleton<ISessionRegistry>(sessions);
            services.AddIrcServices();

            using var sp = services.BuildServiceProvider();

            var links = new ServerLinkService(NullLogger<ServerLinkService>.Instance, new OptionsMonitorStub<IrcOptions>(opts.Value), state, routing, sessions, silence, new WatchService(opts, routing));
            var h = new PrivMsgHandler(routing, links, new HostmaskService(), opts, silence, sp.GetRequiredService<IServiceCommandDispatcher>());

            state.TryAddUser(new User { ConnectionId = "u1", Nick = "alice", UserName = "a", Host = "h", IsRegistered = true });
            var s = new TestSession { ConnectionId = "u1", Nick = "alice", UserName = "a", IsRegistered = true };
            sessions.Add(s);

            for (var i = 0; i < 20; i++)
            {
                await h.HandleAsync(s, new IrcMessage(null, "PRIVMSG", new[] { "NickServ" }, "ID secret"), state, CancellationToken.None);
            }

            var notRegCount = s.Sent.Count(l => l.Contains("This nickname is not registered", StringComparison.OrdinalIgnoreCase));
            Assert.True(notRegCount <= 1, $"Expected <= 1 'not registered' notice, got {notRegCount}.");
        }

        [Fact]
        public async Task NickServ_EnforcesRegisteredNick_WhenNotIdentified()
        {
            var state = new ServerState();
            var sessions = new FakeSessionRegistry();
            var routing = new RoutingService(sessions, new IrcFormatter());
            var silence = new SilenceService();

            var services = new ServiceCollection();
            var opts = Options.Create(new IrcOptions
            {
                ServerInfo = new ServerInfoOptions { Name = "srv", Sid = "001" },
                Services = new ServicesOptions { NickServ = new NickServOptions { EnforceRegisteredNicks = true, EnforceDelaySeconds = 0 } }
            });

            services.AddSingleton<IOptions<IrcOptions>>(opts);
            services.AddSingleton<ISessionRegistry>(sessions);
            services.AddSingleton(routing);
            services.AddSingleton(silence);
            services.AddSingleton(new HostmaskService());
            services.AddIrcServices();

            using var sp = services.BuildServiceProvider();

            var links = new ServerLinkService(NullLogger<ServerLinkService>.Instance, new OptionsMonitorStub<IrcOptions>(opts.Value), state, routing, sessions, silence, new WatchService(opts, routing));
            var privmsg = new PrivMsgHandler(routing, links, sp.GetRequiredService<HostmaskService>(), opts, silence, sp.GetRequiredService<IServiceCommandDispatcher>());

            // Register 'alice'.
            state.TryAddUser(new User { ConnectionId = "reg", Nick = "alice", UserName = "r", Host = "h", IsRegistered = true });
            var reg = new TestSession { ConnectionId = "reg", Nick = "alice", UserName = "r", IsRegistered = true };
            sessions.Add(reg);
            await privmsg.HandleAsync(reg, new IrcMessage(null, "PRIVMSG", new[] { "NickServ" }, "REGISTER test@test.com secret"), state, CancellationToken.None);

            // Intruder switches to registered nick without identifying.
            state.TryAddUser(new User { ConnectionId = "u2", Nick = "bob", UserName = "b", Host = "h2", IsRegistered = true });
            var intruder = new TestSession { ConnectionId = "u2", Nick = "bob", UserName = "b", IsRegistered = true };
            sessions.Add(intruder);

            var events = sp.GetRequiredService<IServiceSessionEvents>();
            intruder.Nick = "alice";
            await events.OnNickChangedAsync(intruder, oldNick: "bob", newNick: "alice", state, CancellationToken.None);

            Assert.True(intruder.Closed);
            Assert.Contains("IDENTIFY", intruder.CloseReason ?? string.Empty, StringComparison.OrdinalIgnoreCase);
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
