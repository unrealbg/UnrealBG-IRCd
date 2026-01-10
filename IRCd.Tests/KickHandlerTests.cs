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
    using IRCd.Tests.TestDoubles;

    using Xunit;

    public sealed class KickHandlerTests
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
        public async Task Kick_CannotKickServiceUser()
        {
            var state = new ServerState();
            var sessions = new FakeSessionRegistry();
            var routing = new RoutingService(sessions, new IrcFormatter());

            state.TryAddUser(new User { ConnectionId = "u1", Nick = "alice", UserName = "a", Host = "h", IsRegistered = true });
            state.TryAddUser(new User { ConnectionId = "svc", Nick = "ChanServ", UserName = "services", Host = "services.local", RealName = "Channel Services", IsRegistered = true, IsService = true });

            state.TryJoinChannel("u1", "alice", "#test");
            state.TryJoinChannel("svc", "ChanServ", "#test");

            Assert.True(state.TryGetChannel("#test", out var ch) && ch is not null);
            Assert.True(ch!.HasPrivilege("u1", ChannelPrivilege.Op));
            Assert.True(ch.Contains("svc"));

            var alice = new TestSession { ConnectionId = "u1", Nick = "alice", UserName = "a", IsRegistered = true };
            var chanserv = new TestSession { ConnectionId = "svc", Nick = "ChanServ", UserName = "services", IsRegistered = true };
            sessions.Add(alice);
            sessions.Add(chanserv);

            var h = new KickHandler(routing);

            alice.Sent.Clear();
            chanserv.Sent.Clear();

            await h.HandleAsync(alice, new IrcMessage(null, "KICK", new[] { "#test", "ChanServ" }, "bye"), state, CancellationToken.None);

            Assert.True(state.TryGetChannel("#test", out ch) && ch is not null);
            Assert.True(ch!.Contains("svc"));

            Assert.Contains(alice.Sent, l => l.Contains("NOTICE alice :Cannot KICK services"));
            Assert.DoesNotContain(alice.Sent, l => l.Contains(" KICK #test ChanServ "));
            Assert.DoesNotContain(chanserv.Sent, l => l.Contains(" KICK #test ChanServ "));
        }
    }
}
