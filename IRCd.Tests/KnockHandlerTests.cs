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

    public sealed class KnockHandlerTests
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
            public bool TryApplyUserModes(string modeString, out string appliedModes) { appliedModes = "+"; return true; }

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
        public async Task Knock_InviteOnlyChannel_SendsToOps()
        {
            var reg = new FakeSessionRegistry();
            var formatter = new IrcFormatter();
            var routing = new RoutingService(reg, formatter);
            var hostmask = new HostmaskService();

            var state = new ServerState();
            var ch = state.GetOrCreateChannel("#a");
            ch.ApplyModeChange(ChannelModes.InviteOnly, enable: true);

            // Create op on channel.
            state.TryAddUser(new User { ConnectionId = "op", Nick = "op", UserName = "u", Host = "h", IsRegistered = true });
            state.TryJoinChannel("op", "op", "#a");
            ch.TryUpdateMemberPrivilege("op", ChannelPrivilege.Op);

            var opSession = new TestSession { ConnectionId = "op", Nick = "op", UserName = "u", IsRegistered = true };
            reg.Add(opSession);

            // Knocker not on channel.
            state.TryAddUser(new User { ConnectionId = "k", Nick = "k", UserName = "u", Host = "h", IsRegistered = true });
            var knocker = new TestSession { ConnectionId = "k", Nick = "k", UserName = "u", IsRegistered = true };
            reg.Add(knocker);

            var h = new KnockHandler(routing, hostmask);

            await h.HandleAsync(knocker, new IrcMessage(null, "KNOCK", new[] { "#a" }, "let me in"), state, CancellationToken.None);

            Assert.Contains(opSession.Sent, l => l.Contains(" KNOCK #a :let me in"));
            Assert.Contains(knocker.Sent, l => l.Contains("NOTICE k :KNOCK sent"));
        }

        [Fact]
        public async Task Knock_NonInviteOnly_Returns480()
        {
            var reg = new FakeSessionRegistry();
            var routing = new RoutingService(reg, new IrcFormatter());
            var hostmask = new HostmaskService();

            var state = new ServerState();
            state.GetOrCreateChannel("#a");

            state.TryAddUser(new User { ConnectionId = "k", Nick = "k", UserName = "u", Host = "h", IsRegistered = true });
            var knocker = new TestSession { ConnectionId = "k", Nick = "k", UserName = "u", IsRegistered = true };

            var h = new KnockHandler(routing, hostmask);

            await h.HandleAsync(knocker, new IrcMessage(null, "KNOCK", new[] { "#a" }, "x"), state, CancellationToken.None);

            Assert.Contains(knocker.Sent, l => l.Contains(" 480 k #a "));
        }
    }
}
