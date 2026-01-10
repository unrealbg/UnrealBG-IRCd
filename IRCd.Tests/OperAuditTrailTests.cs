namespace IRCd.Tests
{
    using System.Collections.Generic;
    using System.Net;
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Core.Abstractions;
    using IRCd.Core.Commands.Handlers;
    using IRCd.Core.Protocol;
    using IRCd.Core.Services;
    using IRCd.Core.State;
    using IRCd.Shared.Options;

    using Microsoft.Extensions.Logging.Abstractions;
    using Microsoft.Extensions.Options;

    using Xunit;

    public sealed class OperAuditTrailTests
    {
        private sealed class NoopBanEnforcer : IBanEnforcer
        {
            public Task EnforceBanImmediatelyAsync(BanEntry ban, CancellationToken ct = default) => Task.CompletedTask;
        }

        private sealed class CapturingAuditLogService : IAuditLogService
        {
            public int CallCount { get; private set; }
            public string? Action { get; private set; }
            public string? ActorUid { get; private set; }
            public string? ActorNick { get; private set; }
            public string? SourceIp { get; private set; }
            public string? Target { get; private set; }
            public string? Reason { get; private set; }
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
                ActorUid = actorUid;
                ActorNick = actorNick;
                SourceIp = sourceIp;
                Target = target;
                Reason = reason;
                Extra = extra;
                return ValueTask.CompletedTask;
            }
        }

        private sealed class TestSession : IClientSession
        {
            public string ConnectionId { get; set; } = "c1";
            public EndPoint RemoteEndPoint { get; set; } = new IPEndPoint(IPAddress.Loopback, 1234);
            public EndPoint LocalEndPoint { get; } = new IPEndPoint(IPAddress.Loopback, 6667);
            public bool IsSecureConnection => false;

            public ISet<string> EnabledCapabilities { get; } = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);

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
            public string? ClosedReason { get; private set; }

            public ValueTask SendAsync(string line, CancellationToken ct = default)
            {
                Sent.Add(line);
                return ValueTask.CompletedTask;
            }

            public ValueTask CloseAsync(string reason, CancellationToken ct = default)
            {
                ClosedReason = reason;
                return ValueTask.CompletedTask;
            }
        }

        [Fact]
        public async Task Kline_EmitsAuditEvent()
        {
            var state = new ServerState();
            state.TryAddUser(new User
            {
                ConnectionId = "c1",
                Uid = "AAA",
                Nick = "oper",
                UserName = "user",
                IsRegistered = true,
                Modes = UserModes.Operator,
                OperClass = "netadmin",
                RemoteIp = "198.51.100.10"
            });

            var opts = Options.Create(new IrcOptions
            {
                ServerInfo = new ServerInfoOptions { Name = "srv" },
                Classes = new[] { new OperClassOptions { Name = "netadmin", Capabilities = new[] { "kline" } } }
            });

            var repo = new InMemoryBanRepository();
            var banService = new BanService(repo, NullLogger<BanService>.Instance);
            var enforcer = new NoopBanEnforcer();
            var audit = new CapturingAuditLogService();
            var handler = new KlineHandler(opts, banService, enforcer, audit);

            var session = new TestSession { ConnectionId = "c1", Nick = "oper", UserName = "user", IsRegistered = true };

            await handler.HandleAsync(session, new IrcMessage(null, "KLINE", new[] { "bad.host" }, "testing"), state, CancellationToken.None);

            Assert.Equal(1, audit.CallCount);
            Assert.Equal("KLINE", audit.Action);
            Assert.Equal("AAA", audit.ActorUid);
            Assert.Equal("oper", audit.ActorNick);
            Assert.Equal("198.51.100.10", audit.SourceIp);
            Assert.Equal("bad.host", audit.Target);
            Assert.Equal("testing", audit.Reason);
        }

        [Fact]
        public async Task Dline_EmitsAuditEvent()
        {
            var state = new ServerState();
            state.TryAddUser(new User
            {
                ConnectionId = "c1",
                Uid = "AAA",
                Nick = "oper",
                UserName = "user",
                IsRegistered = true,
                Modes = UserModes.Operator,
                OperClass = "netadmin",
                RemoteIp = "198.51.100.11"
            });

            var opts = Options.Create(new IrcOptions
            {
                ServerInfo = new ServerInfoOptions { Name = "srv" },
                Classes = new[] { new OperClassOptions { Name = "netadmin", Capabilities = new[] { "dline" } } }
            });

            var repo = new InMemoryBanRepository();
            var banService = new BanService(repo, NullLogger<BanService>.Instance);
            var enforcer = new NoopBanEnforcer();
            var audit = new CapturingAuditLogService();
            var handler = new DlineHandler(opts, banService, enforcer, audit);

            var session = new TestSession { ConnectionId = "c1", Nick = "oper", UserName = "user", IsRegistered = true };

            await handler.HandleAsync(session, new IrcMessage(null, "DLINE", new[] { "203.0.113.*" }, "testing"), state, CancellationToken.None);

            Assert.Equal(1, audit.CallCount);
            Assert.Equal("DLINE", audit.Action);
            Assert.Equal("AAA", audit.ActorUid);
            Assert.Equal("oper", audit.ActorNick);
            Assert.Equal("198.51.100.11", audit.SourceIp);
            Assert.Equal("203.0.113.*", audit.Target);
            Assert.Equal("testing", audit.Reason);
        }

        [Fact]
        public async Task Qline_EmitsAuditEvent()
        {
            var state = new ServerState();
            state.TryAddUser(new User
            {
                ConnectionId = "c1",
                Uid = "AAA",
                Nick = "oper",
                UserName = "user",
                IsRegistered = true,
                Modes = UserModes.Operator,
                OperClass = "netadmin",
                RemoteIp = "198.51.100.12"
            });

            var opts = Options.Create(new IrcOptions
            {
                ServerInfo = new ServerInfoOptions { Name = "srv" },
                Classes = new[] { new OperClassOptions { Name = "netadmin", Capabilities = new[] { "qline" } } }
            });

            var repo = new InMemoryBanRepository();
            var banService = new BanService(repo, NullLogger<BanService>.Instance);
            var enforcer = new NoopBanEnforcer();
            var audit = new CapturingAuditLogService();
            var handler = new QlineHandler(opts, banService, enforcer, audit);

            var session = new TestSession { ConnectionId = "c1", Nick = "oper", UserName = "user", IsRegistered = true };

            await handler.HandleAsync(session, new IrcMessage(null, "QLINE", new[] { "BadNick*" }, "reserved"), state, CancellationToken.None);

            Assert.Equal(1, audit.CallCount);
            Assert.Equal("QLINE", audit.Action);
            Assert.Equal("AAA", audit.ActorUid);
            Assert.Equal("oper", audit.ActorNick);
            Assert.Equal("198.51.100.12", audit.SourceIp);
            Assert.Equal("BadNick*", audit.Target);
            Assert.Equal("reserved", audit.Reason);
        }
    }
}
