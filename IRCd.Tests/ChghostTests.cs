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

    using Microsoft.Extensions.Options;

    using Xunit;

    public sealed class ChghostTests
    {
        private sealed class CapturingAuditLogService : IAuditLogService
        {
            public int CallCount { get; private set; }
            public string? Action { get; private set; }
            public string? Target { get; private set; }
            public string? SourceIp { get; private set; }
            public IReadOnlyDictionary<string, object?>? Extra { get; private set; }

            public ValueTask LogOperActionAsync(
                string action,
                IClientSession session,
                string? actorUid,
                string? actorNick,
                string? sourceIp,
                string? target,
                string? reason,
                IReadOnlyDictionary<string, object?>? extra,
                CancellationToken ct)
            {
                CallCount++;
                Action = action;
                Target = target;
                SourceIp = sourceIp;
                Extra = extra;
                return ValueTask.CompletedTask;
            }
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
        public async Task Chghost_Netadmin_UpdatesUserAndBroadcastsToChannelPeers()
        {
            var state = new ServerState();
            var sessions = new FakeSessionRegistry();
            var routing = new RoutingService(sessions, new IrcFormatter());

            var opts = Options.Create(new IrcOptions
            {
                ServerInfo = new ServerInfoOptions { Name = "srv" },
                Classes = new[] { new OperClassOptions { Name = "netadmin", Capabilities = new[] { "netadmin" } } }
            });

            state.TryAddUser(new User { ConnectionId = "oper", Nick = "oper", UserName = "u", IsRegistered = true, Modes = UserModes.Operator, OperClass = "netadmin" });
            state.TryAddUser(new User { ConnectionId = "victim", Nick = "victim", UserName = "old", Host = "old.host", IsRegistered = true });
            state.TryAddUser(new User { ConnectionId = "peer", Nick = "peer", UserName = "p", Host = "h", IsRegistered = true });

            state.TryJoinChannel("victim", "victim", "#c");
            state.TryJoinChannel("peer", "peer", "#c");

            var operSess = new TestSession { ConnectionId = "oper", Nick = "oper", UserName = "u", IsRegistered = true };
            var victimSess = new TestSession { ConnectionId = "victim", Nick = "victim", UserName = "old", IsRegistered = true };
            var peerSess = new TestSession { ConnectionId = "peer", Nick = "peer", UserName = "p", IsRegistered = true };

            sessions.Add(operSess);
            sessions.Add(victimSess);
            sessions.Add(peerSess);

            var audit = new CapturingAuditLogService();
            var h = new ChghostHandler(opts, routing, sessions, audit);

            await h.HandleAsync(operSess, new IrcMessage(null, "CHGHOST", new[] { "victim", "newid", "new.host" }, null), state, CancellationToken.None);

            Assert.True(state.TryGetUser("victim", out var u) && u is not null);
            Assert.Equal("newid", u!.UserName);
            Assert.Equal("new.host", u.Host);

            Assert.Contains(victimSess.Sent, l => l.Contains(" CHGHOST newid new.host"));
            Assert.Contains(peerSess.Sent, l => l.Contains(":victim!old@old.host CHGHOST newid new.host"));

            Assert.Equal(1, audit.CallCount);
            Assert.Equal("CHGHOST", audit.Action);
            Assert.Equal("victim", audit.Target);
            Assert.Equal("127.0.0.1", audit.SourceIp);
            Assert.NotNull(audit.Extra);
            Assert.Equal("newid", audit.Extra!["newIdent"]);
            Assert.Equal("new.host", audit.Extra!["newHost"]);
        }

        [Fact]
        public async Task Chghost_OperWithoutCapability_Gets481()
        {
            var state = new ServerState();
            state.TryAddUser(new User { ConnectionId = "oper", Nick = "oper", UserName = "u", IsRegistered = true, Modes = UserModes.Operator, OperClass = "helper" });

            var sessions = new FakeSessionRegistry();
            var routing = new RoutingService(sessions, new IrcFormatter());

            var opts = Options.Create(new IrcOptions
            {
                ServerInfo = new ServerInfoOptions { Name = "srv" },
                Classes = new[] { new OperClassOptions { Name = "helper", Capabilities = new[] { "metrics" } } }
            });

            var operSess = new TestSession { ConnectionId = "oper", Nick = "oper", UserName = "u", IsRegistered = true };
            sessions.Add(operSess);

            var h = new ChghostHandler(opts, routing, sessions);

            await h.HandleAsync(operSess, new IrcMessage(null, "CHGHOST", new[] { "x", "y", "z" }, null), state, CancellationToken.None);

            Assert.Contains(operSess.Sent, l => l.Contains(" 481 oper "));
        }
    }
}
