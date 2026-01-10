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

    public sealed class JoinEnforcementTests
    {
        private sealed class TestAuthState : IAuthState
        {
            private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string?> _accounts = new(System.StringComparer.OrdinalIgnoreCase);

            public ValueTask<string?> GetIdentifiedAccountAsync(string connectionId, CancellationToken ct)
            {
                _accounts.TryGetValue(connectionId, out var v);
                return ValueTask.FromResult(v);
            }

            public ValueTask SetIdentifiedAccountAsync(string connectionId, string? accountName, CancellationToken ct)
            {
                _accounts[connectionId] = accountName;
                return ValueTask.CompletedTask;
            }

            public ValueTask ClearAsync(string connectionId, CancellationToken ct)
            {
                _accounts.TryRemove(connectionId, out _);
                return ValueTask.CompletedTask;
            }
        }

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

        private sealed class TestSession : IClientSession
        {
            public string ConnectionId { get; set; } = "c1";
            public EndPoint RemoteEndPoint { get; } = new IPEndPoint(IPAddress.Loopback, 1234);
            public EndPoint LocalEndPoint { get; } = new IPEndPoint(IPAddress.Loopback, 6667);
            public bool IsSecureConnection => false;

            public System.Collections.Generic.ISet<string> EnabledCapabilities { get; } =
                new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);

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

            public readonly System.Collections.Generic.List<string> Sent = new();

            public ValueTask SendAsync(string line, CancellationToken ct = default)
            {
                Sent.Add(line);
                return ValueTask.CompletedTask;
            }

            public ValueTask CloseAsync(string reason, CancellationToken ct = default)
                => ValueTask.CompletedTask;
        }

        [Fact]
        public async Task Join_BannedUser_WithException_CanJoin()
        {
            var state = new ServerState();
            var sessions = new FakeSessionRegistry();
            var routing = new RoutingService(sessions, new IrcFormatter());
            var silence = new SilenceService();

            var opts = Options.Create(new IrcOptions
            {
                ServerInfo = new ServerInfoOptions { Name = "srv", Sid = "001", Description = "d" },
                Links = System.Array.Empty<LinkOptions>()
            });

            var watch = new WatchService(opts, routing);

            // Alice is channel operator
            state.TryAddUser(new User { ConnectionId = "u1", Nick = "alice", UserName = "a", Host = "h", IsRegistered = true });
            state.TryJoinChannel("u1", "alice", "#test");

            // Bob will be banned
            state.TryAddUser(new User { ConnectionId = "u2", Nick = "bob", UserName = "bob", Host = "evil.host", IsRegistered = true });

            var aliceSession = new TestSession { ConnectionId = "u1", Nick = "alice", UserName = "a", IsRegistered = true };
            var bobSession = new TestSession { ConnectionId = "u2", Nick = "bob", UserName = "bob", IsRegistered = true };
            sessions.Add(aliceSession);
            sessions.Add(bobSession);

            var links = new ServerLinkService(
                NullLogger<ServerLinkService>.Instance,
                new OptionsMonitorStub<IrcOptions>(opts.Value),
                state,
                routing,
                sessions,
                silence,
                watch);

            var hostmask = new HostmaskService();
            var modeHandler = new ModeHandler(routing, links, hostmask, opts);

            // Alice sets +b on bob's hostmask
            await modeHandler.HandleAsync(aliceSession, new IrcMessage(null, "MODE", new[] { "#test", "+b", "*!*@evil.host" }, null), state, CancellationToken.None);

            // Alice also adds exception for bob specifically
            await modeHandler.HandleAsync(aliceSession, new IrcMessage(null, "MODE", new[] { "#test", "+e", "bob!*@evil.host" }, null), state, CancellationToken.None);

            var metrics = new TestMetrics();
            var joinHandler = new JoinHandler(routing, links, hostmask, metrics, sessions);

            bobSession.Sent.Clear();
            await joinHandler.HandleAsync(bobSession, new IrcMessage(null, "JOIN", new[] { "#test" }, null), state, CancellationToken.None);

            // Bob should successfully join because of the exception
            Assert.True(state.TryGetChannel("#test", out var ch) && ch is not null);
            Assert.True(ch!.Contains("u2"));
            Assert.Contains(bobSession.Sent, l => l.Contains(" JOIN :", System.StringComparison.OrdinalIgnoreCase) && l.Contains("#test"));
            Assert.DoesNotContain(bobSession.Sent, l => l.Contains(" 474 ", System.StringComparison.OrdinalIgnoreCase)); // ERR_BANNEDFROMCHAN
        }

        [Fact]
        public async Task Join_BannedUser_WithoutException_CannotJoin()
        {
            var state = new ServerState();
            var sessions = new FakeSessionRegistry();
            var routing = new RoutingService(sessions, new IrcFormatter());
            var silence = new SilenceService();

            var opts = Options.Create(new IrcOptions
            {
                ServerInfo = new ServerInfoOptions { Name = "srv", Sid = "001", Description = "d" },
                Links = System.Array.Empty<LinkOptions>()
            });

            var watch = new WatchService(opts, routing);

            // Alice is channel operator
            state.TryAddUser(new User { ConnectionId = "u1", Nick = "alice", UserName = "a", Host = "h", IsRegistered = true });
            state.TryJoinChannel("u1", "alice", "#test");

            // Bob will be banned
            state.TryAddUser(new User { ConnectionId = "u2", Nick = "bob", UserName = "bob", Host = "evil.host", IsRegistered = true });

            var aliceSession = new TestSession { ConnectionId = "u1", Nick = "alice", UserName = "a", IsRegistered = true };
            var bobSession = new TestSession { ConnectionId = "u2", Nick = "bob", UserName = "bob", IsRegistered = true };
            sessions.Add(aliceSession);
            sessions.Add(bobSession);

            var links = new ServerLinkService(
                NullLogger<ServerLinkService>.Instance,
                new OptionsMonitorStub<IrcOptions>(opts.Value),
                state,
                routing,
                sessions,
                silence,
                watch);

            var hostmask = new HostmaskService();
            var modeHandler = new ModeHandler(routing, links, hostmask, opts);

            // Alice sets +b on bob's hostmask
            await modeHandler.HandleAsync(aliceSession, new IrcMessage(null, "MODE", new[] { "#test", "+b", "*!*@evil.host" }, null), state, CancellationToken.None);

            var metrics = new TestMetrics();
            var joinHandler = new JoinHandler(routing, links, hostmask, metrics, sessions);

            bobSession.Sent.Clear();
            await joinHandler.HandleAsync(bobSession, new IrcMessage(null, "JOIN", new[] { "#test" }, null), state, CancellationToken.None);

            // Bob should be blocked from joining
            Assert.True(state.TryGetChannel("#test", out var ch) && ch is not null);
            Assert.False(ch!.Contains("u2"));
            Assert.DoesNotContain(bobSession.Sent, l => l.Contains(" JOIN :", System.StringComparison.OrdinalIgnoreCase));
            Assert.Contains(bobSession.Sent, l => l.Contains(" 474 ", System.StringComparison.OrdinalIgnoreCase)); // ERR_BANNEDFROMCHAN
        }

        [Fact]
        public async Task Join_BannedAccountExtban_WithoutException_CannotJoin()
        {
            var state = new ServerState();
            var sessions = new FakeSessionRegistry();
            var routing = new RoutingService(sessions, new IrcFormatter());
            var silence = new SilenceService();

            var opts = Options.Create(new IrcOptions
            {
                ServerInfo = new ServerInfoOptions { Name = "srv", Sid = "001", Description = "d" },
                Links = System.Array.Empty<LinkOptions>()
            });

            var watch = new WatchService(opts, routing);

            // Alice is channel operator
            state.TryAddUser(new User { ConnectionId = "u1", Nick = "alice", UserName = "a", Host = "h", IsRegistered = true });
            state.TryJoinChannel("u1", "alice", "#test");

            // Bob will be banned by account
            state.TryAddUser(new User { ConnectionId = "u2", Nick = "bob", UserName = "bob", Host = "friendly.host", IsRegistered = true });

            var aliceSession = new TestSession { ConnectionId = "u1", Nick = "alice", UserName = "a", IsRegistered = true };
            var bobSession = new TestSession { ConnectionId = "u2", Nick = "bob", UserName = "bob", IsRegistered = true };
            sessions.Add(aliceSession);
            sessions.Add(bobSession);

            var links = new ServerLinkService(
                NullLogger<ServerLinkService>.Instance,
                new OptionsMonitorStub<IrcOptions>(opts.Value),
                state,
                routing,
                sessions,
                silence,
                watch);

            var hostmask = new HostmaskService();
            var modeHandler = new ModeHandler(routing, links, hostmask, opts);

            // Alice sets +b on account "acc1"
            await modeHandler.HandleAsync(aliceSession, new IrcMessage(null, "MODE", new[] { "#test", "+b", "~a:acc1" }, null), state, CancellationToken.None);

            var auth = new TestAuthState();
            await auth.SetIdentifiedAccountAsync("u2", "acc1", CancellationToken.None);

            var metrics = new TestMetrics();
            var joinHandler = new JoinHandler(routing, links, hostmask, metrics, sessions, auth: auth, banMatcher: new BanMatcher());

            bobSession.Sent.Clear();
            await joinHandler.HandleAsync(bobSession, new IrcMessage(null, "JOIN", new[] { "#test" }, null), state, CancellationToken.None);

            Assert.True(state.TryGetChannel("#test", out var ch) && ch is not null);
            Assert.False(ch!.Contains("u2"));
            Assert.Contains(bobSession.Sent, l => l.Contains(" 474 ", System.StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task Join_BannedAccountExtban_WithException_CanJoin()
        {
            var state = new ServerState();
            var sessions = new FakeSessionRegistry();
            var routing = new RoutingService(sessions, new IrcFormatter());
            var silence = new SilenceService();

            var opts = Options.Create(new IrcOptions
            {
                ServerInfo = new ServerInfoOptions { Name = "srv", Sid = "001", Description = "d" },
                Links = System.Array.Empty<LinkOptions>()
            });

            var watch = new WatchService(opts, routing);

            // Alice is channel operator
            state.TryAddUser(new User { ConnectionId = "u1", Nick = "alice", UserName = "a", Host = "h", IsRegistered = true });
            state.TryJoinChannel("u1", "alice", "#test");

            // Bob will be banned by account
            state.TryAddUser(new User { ConnectionId = "u2", Nick = "bob", UserName = "bob", Host = "friendly.host", IsRegistered = true });

            var aliceSession = new TestSession { ConnectionId = "u1", Nick = "alice", UserName = "a", IsRegistered = true };
            var bobSession = new TestSession { ConnectionId = "u2", Nick = "bob", UserName = "bob", IsRegistered = true };
            sessions.Add(aliceSession);
            sessions.Add(bobSession);

            var links = new ServerLinkService(
                NullLogger<ServerLinkService>.Instance,
                new OptionsMonitorStub<IrcOptions>(opts.Value),
                state,
                routing,
                sessions,
                silence,
                watch);

            var hostmask = new HostmaskService();
            var modeHandler = new ModeHandler(routing, links, hostmask, opts);

            await modeHandler.HandleAsync(aliceSession, new IrcMessage(null, "MODE", new[] { "#test", "+b", "~a:acc1" }, null), state, CancellationToken.None);
            await modeHandler.HandleAsync(aliceSession, new IrcMessage(null, "MODE", new[] { "#test", "+e", "~a:acc1" }, null), state, CancellationToken.None);

            var auth = new TestAuthState();
            await auth.SetIdentifiedAccountAsync("u2", "acc1", CancellationToken.None);

            var metrics = new TestMetrics();
            var joinHandler = new JoinHandler(routing, links, hostmask, metrics, sessions, auth: auth, banMatcher: new BanMatcher());

            bobSession.Sent.Clear();
            await joinHandler.HandleAsync(bobSession, new IrcMessage(null, "JOIN", new[] { "#test" }, null), state, CancellationToken.None);

            Assert.True(state.TryGetChannel("#test", out var ch) && ch is not null);
            Assert.True(ch!.Contains("u2"));
            Assert.DoesNotContain(bobSession.Sent, l => l.Contains(" 474 ", System.StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task PrivMsg_BannedAccountExtban_CannotSpeak()
        {
            var state = new ServerState();
            var sessions = new FakeSessionRegistry();
            var routing = new RoutingService(sessions, new IrcFormatter());
            var silence = new SilenceService();

            var opts = Options.Create(new IrcOptions
            {
                ServerInfo = new ServerInfoOptions { Name = "srv", Sid = "001", Description = "d" },
                Links = System.Array.Empty<LinkOptions>()
            });

            var watch = new WatchService(opts, routing);

            state.TryAddUser(new User { ConnectionId = "u1", Nick = "alice", UserName = "a", Host = "h", IsRegistered = true });
            state.TryAddUser(new User { ConnectionId = "u2", Nick = "bob", UserName = "bob", Host = "friendly.host", IsRegistered = true });

            state.TryJoinChannel("u1", "alice", "#test");
            state.TryJoinChannel("u2", "bob", "#test");

            var aliceSession = new TestSession { ConnectionId = "u1", Nick = "alice", UserName = "a", IsRegistered = true };
            var bobSession = new TestSession { ConnectionId = "u2", Nick = "bob", UserName = "bob", IsRegistered = true };
            sessions.Add(aliceSession);
            sessions.Add(bobSession);

            var links = new ServerLinkService(
                NullLogger<ServerLinkService>.Instance,
                new OptionsMonitorStub<IrcOptions>(opts.Value),
                state,
                routing,
                sessions,
                silence,
                watch);

            var hostmask = new HostmaskService();
            var modeHandler = new ModeHandler(routing, links, hostmask, opts);

            // Ban bob's account after he's already in-channel
            await modeHandler.HandleAsync(aliceSession, new IrcMessage(null, "MODE", new[] { "#test", "+b", "~a:acc1" }, null), state, CancellationToken.None);

            var auth = new TestAuthState();
            await auth.SetIdentifiedAccountAsync("u2", "acc1", CancellationToken.None);

            var privMsgHandler = new PrivMsgHandler(routing, links, hostmask, opts, silence, auth: auth, banMatcher: new BanMatcher());

            aliceSession.Sent.Clear();
            bobSession.Sent.Clear();

            await privMsgHandler.HandleAsync(bobSession, new IrcMessage(null, "PRIVMSG", new[] { "#test" }, "hi"), state, CancellationToken.None);

            Assert.Contains(bobSession.Sent, l => l.Contains(" 404 ", System.StringComparison.OrdinalIgnoreCase) && l.Contains("(+b)", System.StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(aliceSession.Sent, l => l.Contains("PRIVMSG #test", System.StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task Join_InviteOnlyChannel_WithInviteException_CanJoin()
        {
            var state = new ServerState();
            var sessions = new FakeSessionRegistry();
            var routing = new RoutingService(sessions, new IrcFormatter());
            var silence = new SilenceService();

            var opts = Options.Create(new IrcOptions
            {
                ServerInfo = new ServerInfoOptions { Name = "srv", Sid = "001", Description = "d" },
                Links = System.Array.Empty<LinkOptions>()
            });

            var watch = new WatchService(opts, routing);

            // Alice is channel operator
            state.TryAddUser(new User { ConnectionId = "u1", Nick = "alice", UserName = "a", Host = "h", IsRegistered = true });
            state.TryJoinChannel("u1", "alice", "#test");

            // Bob will try to join invite-only channel
            state.TryAddUser(new User { ConnectionId = "u2", Nick = "bob", UserName = "bob", Host = "friendly.host", IsRegistered = true });

            var aliceSession = new TestSession { ConnectionId = "u1", Nick = "alice", UserName = "a", IsRegistered = true };
            var bobSession = new TestSession { ConnectionId = "u2", Nick = "bob", UserName = "bob", IsRegistered = true };
            sessions.Add(aliceSession);
            sessions.Add(bobSession);

            var links = new ServerLinkService(
                NullLogger<ServerLinkService>.Instance,
                new OptionsMonitorStub<IrcOptions>(opts.Value),
                state,
                routing,
                sessions,
                silence,
                watch);

            var hostmask = new HostmaskService();
            var modeHandler = new ModeHandler(routing, links, hostmask, opts);

            // Alice sets +i (invite only)
            await modeHandler.HandleAsync(aliceSession, new IrcMessage(null, "MODE", new[] { "#test", "+i" }, null), state, CancellationToken.None);

            // Alice adds bob to invite exception list
            await modeHandler.HandleAsync(aliceSession, new IrcMessage(null, "MODE", new[] { "#test", "+I", "bob!*@friendly.host" }, null), state, CancellationToken.None);

            var metrics = new TestMetrics();
            var joinHandler = new JoinHandler(routing, links, hostmask, metrics, sessions);

            bobSession.Sent.Clear();
            await joinHandler.HandleAsync(bobSession, new IrcMessage(null, "JOIN", new[] { "#test" }, null), state, CancellationToken.None);

            // Bob should successfully join because of the invite exception
            Assert.True(state.TryGetChannel("#test", out var ch) && ch is not null);
            Assert.True(ch!.Contains("u2"));
            Assert.Contains(bobSession.Sent, l => l.Contains(" JOIN :", System.StringComparison.OrdinalIgnoreCase) && l.Contains("#test"));
            Assert.DoesNotContain(bobSession.Sent, l => l.Contains(" 473 ", System.StringComparison.OrdinalIgnoreCase)); // ERR_INVITEONLYCHAN
        }

        [Fact]
        public async Task Join_InviteOnlyChannel_WithoutInviteException_CannotJoin()
        {
            var state = new ServerState();
            var sessions = new FakeSessionRegistry();
            var routing = new RoutingService(sessions, new IrcFormatter());
            var silence = new SilenceService();

            var opts = Options.Create(new IrcOptions
            {
                ServerInfo = new ServerInfoOptions { Name = "srv", Sid = "001", Description = "d" },
                Links = System.Array.Empty<LinkOptions>()
            });

            var watch = new WatchService(opts, routing);

            // Alice is channel operator
            state.TryAddUser(new User { ConnectionId = "u1", Nick = "alice", UserName = "a", Host = "h", IsRegistered = true });
            state.TryJoinChannel("u1", "alice", "#test");

            // Bob will try to join invite-only channel
            state.TryAddUser(new User { ConnectionId = "u2", Nick = "bob", UserName = "bob", Host = "friendly.host", IsRegistered = true });

            var aliceSession = new TestSession { ConnectionId = "u1", Nick = "alice", UserName = "a", IsRegistered = true };
            var bobSession = new TestSession { ConnectionId = "u2", Nick = "bob", UserName = "bob", IsRegistered = true };
            sessions.Add(aliceSession);
            sessions.Add(bobSession);

            var links = new ServerLinkService(
                NullLogger<ServerLinkService>.Instance,
                new OptionsMonitorStub<IrcOptions>(opts.Value),
                state,
                routing,
                sessions,
                silence,
                watch);

            var hostmask = new HostmaskService();
            var modeHandler = new ModeHandler(routing, links, hostmask, opts);

            // Alice sets +i (invite only)
            await modeHandler.HandleAsync(aliceSession, new IrcMessage(null, "MODE", new[] { "#test", "+i" }, null), state, CancellationToken.None);

            var metrics = new TestMetrics();
            var joinHandler = new JoinHandler(routing, links, hostmask, metrics, sessions);

            bobSession.Sent.Clear();
            await joinHandler.HandleAsync(bobSession, new IrcMessage(null, "JOIN", new[] { "#test" }, null), state, CancellationToken.None);

            // Bob should be blocked from joining
            Assert.True(state.TryGetChannel("#test", out var ch) && ch is not null);
            Assert.False(ch!.Contains("u2"));
            Assert.DoesNotContain(bobSession.Sent, l => l.Contains(" JOIN :", System.StringComparison.OrdinalIgnoreCase));
            Assert.Contains(bobSession.Sent, l => l.Contains(" 473 ", System.StringComparison.OrdinalIgnoreCase)); // ERR_INVITEONLYCHAN
        }

        [Fact]
        public async Task Mode_MaxList_RejectsExcessiveBans()
        {
            var state = new ServerState();
            var sessions = new FakeSessionRegistry();
            var routing = new RoutingService(sessions, new IrcFormatter());
            var silence = new SilenceService();

            var opts = Options.Create(new IrcOptions
            {
                ServerInfo = new ServerInfoOptions { Name = "srv", Sid = "001", Description = "d" },
                Links = System.Array.Empty<LinkOptions>(),
                Limits = new CommandLimitsOptions { MaxListModes = 3 } // Low limit for testing
            });

            var watch = new WatchService(opts, routing);

            // Alice is channel operator
            state.TryAddUser(new User { ConnectionId = "u1", Nick = "alice", UserName = "a", Host = "h", IsRegistered = true });
            state.TryJoinChannel("u1", "alice", "#test");

            var aliceSession = new TestSession { ConnectionId = "u1", Nick = "alice", UserName = "a", IsRegistered = true };
            sessions.Add(aliceSession);

            var links = new ServerLinkService(
                NullLogger<ServerLinkService>.Instance,
                new OptionsMonitorStub<IrcOptions>(opts.Value),
                state,
                routing,
                sessions,
                silence,
                watch);

            var hostmask = new HostmaskService();
            var modeHandler = new ModeHandler(routing, links, hostmask, opts);

            // Add 3 bans (up to the limit)
            await modeHandler.HandleAsync(aliceSession, new IrcMessage(null, "MODE", new[] { "#test", "+b", "*!*@host1" }, null), state, CancellationToken.None);
            await modeHandler.HandleAsync(aliceSession, new IrcMessage(null, "MODE", new[] { "#test", "+b", "*!*@host2" }, null), state, CancellationToken.None);
            await modeHandler.HandleAsync(aliceSession, new IrcMessage(null, "MODE", new[] { "#test", "+b", "*!*@host3" }, null), state, CancellationToken.None);

            Assert.True(state.TryGetChannel("#test", out var ch) && ch is not null);
            Assert.Equal(3, ch!.Bans.Count);

            aliceSession.Sent.Clear();

            // Try to add a 4th ban (should fail)
            await modeHandler.HandleAsync(aliceSession, new IrcMessage(null, "MODE", new[] { "#test", "+b", "*!*@host4" }, null), state, CancellationToken.None);

            Assert.Equal(3, ch.Bans.Count); // Still 3 bans
            Assert.Contains(aliceSession.Sent, l => l.Contains(" 478 ", System.StringComparison.OrdinalIgnoreCase)); // ERR_BANLISTFULL
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
