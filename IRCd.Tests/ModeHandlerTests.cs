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

    public sealed class ModeHandlerTests
    {
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

            public System.DateTime LastActivityUtc { get; } = DateTime.UtcNow;
            public System.DateTime LastPingUtc { get; } = DateTime.UtcNow;
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
        public async Task Mode_Channel_PlusP_SetsPrivateMode()
        {
            var state = new ServerState();
            var sessions = new FakeSessionRegistry();
            var routing = new RoutingService(sessions, new IrcFormatter());
            var silence = new SilenceService();

            var opts = Options.Create(new IrcOptions
            {
                ServerInfo = new ServerInfoOptions { Name = "srv", Sid = "001", Description = "d" },
                Links = Array.Empty<LinkOptions>()
            });

            var watch = new WatchService(opts, routing);

            state.TryAddUser(new User { ConnectionId = "u1", Nick = "alice", UserName = "a", Host = "h", IsRegistered = true });
            state.TryJoinChannel("u1", "alice", "#test");

            var s = new TestSession { ConnectionId = "u1", Nick = "alice", UserName = "a", IsRegistered = true };
            sessions.Add(s);

            var links = new ServerLinkService(
                NullLogger<ServerLinkService>.Instance,
                new OptionsMonitorStub<IrcOptions>(opts.Value),
                state,
                routing,
                sessions,
                silence,
                watch);

            var h = new ModeHandler(routing, links, new HostmaskService(), opts);

            await h.HandleAsync(s, new IrcMessage(null, "MODE", new[] { "#test", "+p" }, null), state, CancellationToken.None);

            Assert.True(state.TryGetChannel("#test", out var ch) && ch is not null);
            Assert.True(ch!.Modes.HasFlag(ChannelModes.Private));
            Assert.Contains(s.Sent, l => l.Contains(" MODE #test +p", StringComparison.OrdinalIgnoreCase));
            Assert.Contains("p", ch.FormatModeString());
        }

        [Fact]
        public async Task Mode_Channel_MinusO_WithoutNick_DeopsSelf()
        {
            var state = new ServerState();
            var sessions = new FakeSessionRegistry();
            var routing = new RoutingService(sessions, new IrcFormatter());
            var silence = new SilenceService();

            var opts = Options.Create(new IrcOptions
            {
                ServerInfo = new ServerInfoOptions { Name = "srv", Sid = "001", Description = "d" },
                Links = Array.Empty<LinkOptions>()
            });

            var watch = new WatchService(opts, routing);

            state.TryAddUser(new User { ConnectionId = "u1", Nick = "alice", UserName = "a", Host = "h", IsRegistered = true });
            state.TryJoinChannel("u1", "alice", "#test");

            Assert.True(state.TryGetChannel("#test", out var ch) && ch is not null);
            Assert.True(ch!.HasPrivilege("u1", ChannelPrivilege.Op));

            var s = new TestSession { ConnectionId = "u1", Nick = "alice", UserName = "a", IsRegistered = true };
            sessions.Add(s);

            var links = new ServerLinkService(
                NullLogger<ServerLinkService>.Instance,
                new OptionsMonitorStub<IrcOptions>(opts.Value),
                state,
                routing,
                sessions,
                silence,
                watch);

            var h = new ModeHandler(routing, links, new HostmaskService(), opts);

            await h.HandleAsync(s, new IrcMessage(null, "MODE", new[] { "#test", "-o" }, null), state, CancellationToken.None);

            Assert.True(state.TryGetChannel("#test", out ch) && ch is not null);
            Assert.Equal(ChannelPrivilege.Normal, ch!.GetPrivilege("u1"));
            Assert.DoesNotContain(s.Sent, l => l.Contains(" 461 ", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(s.Sent, l => l.Contains(" MODE #test -o alice", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task Mode_Channel_MultipleO_WithTooFewNicks_AppliesWhatItCan_AndDoesNotSpam461()
        {
            var state = new ServerState();
            var sessions = new FakeSessionRegistry();
            var routing = new RoutingService(sessions, new IrcFormatter());
            var silence = new SilenceService();

            var opts = Options.Create(new IrcOptions
            {
                ServerInfo = new ServerInfoOptions { Name = "srv", Sid = "001", Description = "d" },
                Links = Array.Empty<LinkOptions>()
            });

            var watch = new WatchService(opts, routing);

            state.TryAddUser(new User { ConnectionId = "u1", Nick = "alice", UserName = "a", Host = "h", IsRegistered = true });
            state.TryAddUser(new User { ConnectionId = "u2", Nick = "bob", UserName = "b", Host = "h", IsRegistered = true });

            state.TryJoinChannel("u1", "alice", "#test");
            state.TryJoinChannel("u2", "bob", "#test");

            Assert.True(state.TryGetChannel("#test", out var ch) && ch is not null);
            Assert.True(ch!.HasPrivilege("u1", ChannelPrivilege.Op));
            Assert.Equal(ChannelPrivilege.Normal, ch.GetPrivilege("u2"));

            var s = new TestSession { ConnectionId = "u1", Nick = "alice", UserName = "a", IsRegistered = true };
            sessions.Add(s);

            var links = new ServerLinkService(
                NullLogger<ServerLinkService>.Instance,
                new OptionsMonitorStub<IrcOptions>(opts.Value),
                state,
                routing,
                sessions,
                silence,
                watch);

            var h = new ModeHandler(routing, links, new HostmaskService(), opts);

            // Too few nick params for +oo: should still +o bob, then stop and return 461
            await h.HandleAsync(s, new IrcMessage(null, "MODE", new[] { "#test", "+oo", "bob" }, null), state, CancellationToken.None);

            Assert.True(state.TryGetChannel("#test", out ch) && ch is not null);
            Assert.True(ch!.HasPrivilege("u2", ChannelPrivilege.Op));
            Assert.DoesNotContain(s.Sent, l => l.Contains(" 461 ", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(s.Sent, l => l.Contains(" MODE #test +o bob", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task Mode_Channel_MixedSigns_PreservesSignTransitionsInBroadcast()
        {
            var state = new ServerState();
            var sessions = new FakeSessionRegistry();
            var routing = new RoutingService(sessions, new IrcFormatter());
            var silence = new SilenceService();

            var opts = Options.Create(new IrcOptions
            {
                ServerInfo = new ServerInfoOptions { Name = "srv", Sid = "001", Description = "d" },
                Links = Array.Empty<LinkOptions>()
            });

            var watch = new WatchService(opts, routing);

            state.TryAddUser(new User { ConnectionId = "u1", Nick = "alice", UserName = "a", Host = "h", IsRegistered = true });
            state.TryAddUser(new User { ConnectionId = "u2", Nick = "bob", UserName = "b", Host = "h", IsRegistered = true });

            state.TryJoinChannel("u1", "alice", "#test");
            state.TryJoinChannel("u2", "bob", "#test");

            var s = new TestSession { ConnectionId = "u1", Nick = "alice", UserName = "a", IsRegistered = true };
            sessions.Add(s);

            var links = new ServerLinkService(
                NullLogger<ServerLinkService>.Instance,
                new OptionsMonitorStub<IrcOptions>(opts.Value),
                state,
                routing,
                sessions,
                silence,
                watch);

            var h = new ModeHandler(routing, links, new HostmaskService(), opts);

            await h.HandleAsync(s, new IrcMessage(null, "MODE", new[] { "#test", "+v-o", "bob", "bob" }, null), state, CancellationToken.None);

            Assert.Contains(s.Sent, l => l.Contains(" MODE #test +v-o bob bob", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task Mode_Channel_CannotDeopServiceUser()
        {
            var state = new ServerState();
            var sessions = new FakeSessionRegistry();
            var routing = new RoutingService(sessions, new IrcFormatter());
            var silence = new SilenceService();

            var opts = Options.Create(new IrcOptions
            {
                ServerInfo = new ServerInfoOptions { Name = "srv", Sid = "001", Description = "d" },
                Links = Array.Empty<LinkOptions>()
            });

            var watch = new WatchService(opts, routing);

            state.TryAddUser(new User { ConnectionId = "u1", Nick = "alice", UserName = "a", Host = "h", IsRegistered = true });
            state.TryAddUser(new User { ConnectionId = "svc", Nick = "ChanServ", UserName = "services", Host = "services.local", RealName = "Channel Services", IsRegistered = true, IsService = true });

            state.TryJoinChannel("u1", "alice", "#test");
            state.TryJoinChannel("svc", "ChanServ", "#test");

            Assert.True(state.TryGetChannel("#test", out var ch) && ch is not null);
            Assert.True(ch!.TryUpdateMemberPrivilege("svc", ChannelPrivilege.Op));
            Assert.True(ch.HasPrivilege("u1", ChannelPrivilege.Op));
            Assert.True(ch.HasPrivilege("svc", ChannelPrivilege.Op));

            var s = new TestSession { ConnectionId = "u1", Nick = "alice", UserName = "a", IsRegistered = true };
            sessions.Add(s);

            var links = new ServerLinkService(
                NullLogger<ServerLinkService>.Instance,
                new OptionsMonitorStub<IrcOptions>(opts.Value),
                state,
                routing,
                sessions,
                silence,
                watch);

            var h = new ModeHandler(routing, links, new HostmaskService(), opts);

            s.Sent.Clear();
            await h.HandleAsync(s, new IrcMessage(null, "MODE", new[] { "#test", "-o", "ChanServ" }, null), state, CancellationToken.None);

            Assert.True(state.TryGetChannel("#test", out ch) && ch is not null);
            Assert.True(ch!.HasPrivilege("svc", ChannelPrivilege.Op));
            Assert.DoesNotContain(s.Sent, l => l.Contains(" MODE #test -o ChanServ", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(s.Sent, l => l.Contains(" 482 ", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task Mode_Channel_CannotDevoiceServiceUser()
        {
            var state = new ServerState();
            var sessions = new FakeSessionRegistry();
            var routing = new RoutingService(sessions, new IrcFormatter());
            var silence = new SilenceService();

            var opts = Options.Create(new IrcOptions
            {
                ServerInfo = new ServerInfoOptions { Name = "srv", Sid = "001", Description = "d" },
                Links = Array.Empty<LinkOptions>()
            });

            var watch = new WatchService(opts, routing);

            state.TryAddUser(new User { ConnectionId = "u1", Nick = "alice", UserName = "a", Host = "h", IsRegistered = true });
            state.TryAddUser(new User { ConnectionId = "svc", Nick = "ChanServ", UserName = "services", Host = "services.local", RealName = "Channel Services", IsRegistered = true, IsService = true });

            state.TryJoinChannel("u1", "alice", "#test");
            state.TryJoinChannel("svc", "ChanServ", "#test");

            Assert.True(state.TryGetChannel("#test", out var ch) && ch is not null);
            Assert.True(ch!.TryUpdateMemberPrivilege("svc", ChannelPrivilege.Voice));
            Assert.True(ch.HasPrivilege("u1", ChannelPrivilege.Op));
            Assert.Equal(ChannelPrivilege.Voice, ch.GetPrivilege("svc"));

            var s = new TestSession { ConnectionId = "u1", Nick = "alice", UserName = "a", IsRegistered = true };
            sessions.Add(s);

            var links = new ServerLinkService(
                NullLogger<ServerLinkService>.Instance,
                new OptionsMonitorStub<IrcOptions>(opts.Value),
                state,
                routing,
                sessions,
                silence,
                watch);

            var h = new ModeHandler(routing, links, new HostmaskService(), opts);

            s.Sent.Clear();
            await h.HandleAsync(s, new IrcMessage(null, "MODE", new[] { "#test", "-v", "ChanServ" }, null), state, CancellationToken.None);

            Assert.True(state.TryGetChannel("#test", out ch) && ch is not null);
            Assert.Equal(ChannelPrivilege.Voice, ch!.GetPrivilege("svc"));
            Assert.DoesNotContain(s.Sent, l => l.Contains(" MODE #test -v ChanServ", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(s.Sent, l => l.Contains(" 482 ", StringComparison.OrdinalIgnoreCase));
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
