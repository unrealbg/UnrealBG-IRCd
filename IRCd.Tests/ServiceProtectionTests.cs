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

    public sealed class ServiceProtectionTests
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

        private static (ServerState State, FakeSessionRegistry Sessions, RoutingService Routing, ServerLinkService Links, IOptions<IrcOptions> Options) BuildCoreHarness()
        {
            var state = new ServerState();
            var sessions = new FakeSessionRegistry();
            var routing = new RoutingService(sessions, new IrcFormatter());

            var opts = Options.Create(new IrcOptions
            {
                ServerInfo = new ServerInfoOptions { Name = "srv", Sid = "001", Description = "d" },
                Links = System.Array.Empty<LinkOptions>()
            });

            var silence = new SilenceService();
            var watch = new WatchService(opts, routing);

            var links = new ServerLinkService(
                NullLogger<ServerLinkService>.Instance,
                new OptionsMonitorStub<IrcOptions>(opts.Value),
                state,
                routing,
                sessions,
                silence,
                watch);

            return (state, sessions, routing, links, opts);
        }

        [Fact]
        public async Task KickBan_CannotTargetServiceUser()
        {
            var (state, sessions, routing, _, _) = BuildCoreHarness();

            state.TryAddUser(new User { ConnectionId = "u1", Nick = "alice", UserName = "a", Host = "h", IsRegistered = true });
            state.TryAddUser(new User { ConnectionId = "svc", Nick = "ChanServ", UserName = "services", Host = "services.local", IsRegistered = true, IsService = true });

            state.TryJoinChannel("u1", "alice", "#test");
            state.TryJoinChannel("svc", "ChanServ", "#test");

            Assert.True(state.TryGetChannel("#test", out var ch) && ch is not null);
            Assert.True(ch!.HasPrivilege("u1", ChannelPrivilege.Op));

            var alice = new TestSession { ConnectionId = "u1", Nick = "alice", UserName = "a", IsRegistered = true };
            var chanserv = new TestSession { ConnectionId = "svc", Nick = "ChanServ", UserName = "services", IsRegistered = true };
            sessions.Add(alice);
            sessions.Add(chanserv);

            var h = new KickBanHandler(routing);

            await h.HandleAsync(alice, new IrcMessage(null, "KICKBAN", new[] { "#test", "ChanServ" }, "bye"), state, CancellationToken.None);

            Assert.True(state.TryGetChannel("#test", out ch) && ch is not null);
            Assert.True(ch!.Contains("svc"));
            Assert.Contains(alice.Sent, l => l.Contains("NOTICE alice :Cannot KICKBAN services"));
            Assert.DoesNotContain(alice.Sent, l => l.Contains(" MODE #test +b "));
            Assert.DoesNotContain(alice.Sent, l => l.Contains(" KICK #test ChanServ "));
        }

        [Fact]
        public async Task Invite_CannotTargetServiceUser()
        {
            var (state, sessions, routing, _, _) = BuildCoreHarness();

            state.TryAddUser(new User { ConnectionId = "u1", Nick = "alice", UserName = "a", Host = "h", IsRegistered = true });
            state.TryAddUser(new User { ConnectionId = "svc", Nick = "ChanServ", UserName = "services", Host = "services.local", IsRegistered = true, IsService = true });

            state.TryJoinChannel("u1", "alice", "#test");

            var alice = new TestSession { ConnectionId = "u1", Nick = "alice", UserName = "a", IsRegistered = true };
            var chanserv = new TestSession { ConnectionId = "svc", Nick = "ChanServ", UserName = "services", IsRegistered = true };
            sessions.Add(alice);
            sessions.Add(chanserv);

            var h = new InviteHandler(routing);

            await h.HandleAsync(alice, new IrcMessage(null, "INVITE", new[] { "ChanServ", "#test" }, null), state, CancellationToken.None);

            Assert.Contains(alice.Sent, l => l.Contains("NOTICE alice :Cannot INVITE services"));
            Assert.DoesNotContain(alice.Sent, l => l.Contains(" 341 "));
            Assert.DoesNotContain(chanserv.Sent, l => l.Contains(" INVITE ChanServ "));
        }

        [Fact]
        public async Task Chghost_CannotTargetServiceUser()
        {
            var (state, sessions, routing, _, opts) = BuildCoreHarness();

            state.TryAddUser(new User { ConnectionId = "oper", Nick = "oper", UserName = "o", Host = "h", IsRegistered = true, Modes = UserModes.Operator });
            state.TryAddUser(new User { ConnectionId = "svc", Nick = "ChanServ", UserName = "services", Host = "services.local", IsRegistered = true, IsService = true });

            var operSess = new TestSession { ConnectionId = "oper", Nick = "oper", UserName = "o", IsRegistered = true };
            sessions.Add(operSess);

            var h = new ChghostHandler(opts, routing, sessions);

            await h.HandleAsync(operSess, new IrcMessage(null, "CHGHOST", new[] { "ChanServ", "x", "evil.host" }, null), state, CancellationToken.None);

            Assert.Contains(operSess.Sent, l => l.Contains("NOTICE oper :Cannot CHGHOST services"));
            Assert.True(state.TryGetUser("svc", out var u) && u is not null);
            Assert.Equal("services", u!.UserName);
            Assert.Equal("services.local", u.Host);
        }

        [Fact]
        public async Task Svsnick_CannotTargetServiceUser()
        {
            var (state, sessions, routing, links, opts) = BuildCoreHarness();

            state.TryAddUser(new User { ConnectionId = "oper", Nick = "oper", UserName = "o", Host = "h", IsRegistered = true, Modes = UserModes.Operator });
            state.TryAddUser(new User { ConnectionId = "svc", Nick = "ChanServ", UserName = "services", Host = "services.local", IsRegistered = true, IsService = true });

            var operSess = new TestSession { ConnectionId = "oper", Nick = "oper", UserName = "o", IsRegistered = true };
            sessions.Add(operSess);

            var h = new SvsnickHandler(opts, routing, sessions, links, new WhowasService(), new WatchService(opts, routing));

            await h.HandleAsync(operSess, new IrcMessage(null, "SVSNICK", new[] { "ChanServ", "CS" }, null), state, CancellationToken.None);

            Assert.Contains(operSess.Sent, l => l.Contains("NOTICE oper :Cannot SVSNICK services"));
            Assert.True(state.TryGetUser("svc", out var u) && u is not null);
            Assert.Equal("ChanServ", u!.Nick);
        }

        [Fact]
        public async Task Svspart_CannotTargetServiceUser()
        {
            var (state, sessions, routing, links, opts) = BuildCoreHarness();

            state.TryAddUser(new User { ConnectionId = "oper", Nick = "oper", UserName = "o", Host = "h", IsRegistered = true, Modes = UserModes.Operator });
            state.TryAddUser(new User { ConnectionId = "svc", Nick = "ChanServ", UserName = "services", Host = "services.local", IsRegistered = true, IsService = true });

            state.TryJoinChannel("svc", "ChanServ", "#test");

            var operSess = new TestSession { ConnectionId = "oper", Nick = "oper", UserName = "o", IsRegistered = true };
            sessions.Add(operSess);

            var h = new SvspartHandler(opts, routing, links);

            await h.HandleAsync(operSess, new IrcMessage(null, "SVSPART", new[] { "ChanServ", "#test" }, "bye"), state, CancellationToken.None);

            Assert.Contains(operSess.Sent, l => l.Contains("NOTICE oper :Cannot SVSPART services"));
            Assert.True(state.TryGetChannel("#test", out var ch) && ch is not null);
            Assert.True(ch!.Contains("svc"));
        }

        [Fact]
        public async Task Svsjoin_CannotTargetServiceUser()
        {
            var (state, sessions, routing, links, opts) = BuildCoreHarness();

            state.TryAddUser(new User { ConnectionId = "oper", Nick = "oper", UserName = "o", Host = "h", IsRegistered = true, Modes = UserModes.Operator });
            state.TryAddUser(new User { ConnectionId = "svc", Nick = "ChanServ", UserName = "services", Host = "services.local", IsRegistered = true, IsService = true });

            var operSess = new TestSession { ConnectionId = "oper", Nick = "oper", UserName = "o", IsRegistered = true };
            sessions.Add(operSess);

            var h = new SvsjoinHandler(opts, routing, links);

            await h.HandleAsync(operSess, new IrcMessage(null, "SVSJOIN", new[] { "ChanServ", "#test" }, null), state, CancellationToken.None);

            Assert.Contains(operSess.Sent, l => l.Contains("NOTICE oper :Cannot SVSJOIN services"));
            Assert.False(state.TryGetChannel("#test", out _));
        }

        [Fact]
        public async Task Svs2mode_CannotTargetServiceUser()
        {
            var (state, sessions, _, links, opts) = BuildCoreHarness();

            state.TryAddUser(new User { ConnectionId = "oper", Nick = "oper", UserName = "o", Host = "h", IsRegistered = true, Modes = UserModes.Operator });
            state.TryAddUser(new User { ConnectionId = "svc", Nick = "ChanServ", UserName = "services", Host = "services.local", IsRegistered = true, IsService = true });

            var operSess = new TestSession { ConnectionId = "oper", Nick = "oper", UserName = "o", IsRegistered = true };
            sessions.Add(operSess);

            var h = new Svs2modeHandler(opts, sessions, links);

            await h.HandleAsync(operSess, new IrcMessage(null, "SVS2MODE", new[] { "ChanServ", "+i" }, null), state, CancellationToken.None);

            Assert.Contains(operSess.Sent, l => l.Contains("NOTICE oper :Cannot SVS2MODE services"));
            Assert.True(state.TryGetUser("svc", out var u) && u is not null);
            Assert.False(u!.Modes.HasFlag(UserModes.Invisible));
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
