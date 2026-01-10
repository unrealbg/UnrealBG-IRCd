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
    using IRCd.Tests.TestDoubles;

    using Microsoft.Extensions.Logging.Abstractions;
    using Microsoft.Extensions.Options;

    using Xunit;

    public sealed class InfoStatsTraceKillWallopsTests
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

        private sealed class CapturingAuditLogService : IAuditLogService
        {
            public int CallCount { get; private set; }
            public string? Action { get; private set; }
            public string? Target { get; private set; }
            public string? Reason { get; private set; }
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
                Reason = reason;
                SourceIp = sourceIp;
                Extra = extra;
                return ValueTask.CompletedTask;
            }
        }

        [Fact]
        public async Task Info_Registered_Returns371And374()
        {
            var state = new ServerState();
            var opts = Options.Create(new IrcOptions { ServerInfo = new ServerInfoOptions { Name = "srv", Version = "v" } });
            var h = new InfoHandler(opts);

            var s = new TestSession { Nick = "me", UserName = "u", IsRegistered = true };

            await h.HandleAsync(s, new IrcMessage(null, "INFO", new string[0], null), state, CancellationToken.None);

            Assert.Contains(s.Sent, l => l.Contains(" 371 me "));
            Assert.Contains(s.Sent, l => l.Contains(" 374 me "));
        }

        [Fact]
        public async Task Stats_OperWithClassWithoutCapability_Gets481()
        {
            var state = new ServerState();
            state.TryAddUser(new User { ConnectionId = "c1", Nick = "oper", UserName = "u", IsRegistered = true, Modes = UserModes.Operator, OperClass = "helper" });

            var opts = Options.Create(new IrcOptions
            {
                ServerInfo = new ServerInfoOptions { Name = "srv" },
                Classes = new[] { new OperClassOptions { Name = "helper", Capabilities = new[] { "metrics" } } }
            });

            var h = new StatsHandler(opts);
            var s = new TestSession { ConnectionId = "c1", Nick = "oper", UserName = "u", IsRegistered = true };

            await h.HandleAsync(s, new IrcMessage(null, "STATS", new[] { "l" }, null), state, CancellationToken.None);

            Assert.Contains(s.Sent, l => l.Contains(" 481 oper "));
        }

        [Fact]
        public async Task Trace_NetAdmin_Returns200And262()
        {
            var state = new ServerState();
            state.TryAddUser(new User { ConnectionId = "c1", Nick = "oper", UserName = "u", IsRegistered = true, Modes = UserModes.Operator, OperClass = "netadmin" });

            var opts = Options.Create(new IrcOptions
            {
                ServerInfo = new ServerInfoOptions { Name = "srv" },
                Classes = new[] { new OperClassOptions { Name = "netadmin", Capabilities = new[] { "netadmin" } } }
            });

            var h = new TraceHandler(opts);
            var s = new TestSession { ConnectionId = "c1", Nick = "oper", UserName = "u", IsRegistered = true };

            await h.HandleAsync(s, new IrcMessage(null, "TRACE", new string[0], null), state, CancellationToken.None);

            Assert.Contains(s.Sent, l => l.Contains(" 200 oper "));
            Assert.Contains(s.Sent, l => l.Contains(" 262 oper "));
        }

        [Fact]
        public async Task Wallops_OperWithClassWithoutCapability_Gets481()
        {
            var state = new ServerState();
            state.TryAddUser(new User { ConnectionId = "c1", Nick = "oper", UserName = "u", IsRegistered = true, Modes = UserModes.Operator, OperClass = "helper" });

            var opts = Options.Create(new IrcOptions
            {
                ServerInfo = new ServerInfoOptions { Name = "srv" },
                Classes = new[] { new OperClassOptions { Name = "helper", Capabilities = new[] { "metrics" } } }
            });

            var routing = new RoutingService(new FakeSessionRegistry(), new IrcFormatter());
            var h = new WallopsHandler(opts, routing);
            var s = new TestSession { ConnectionId = "c1", Nick = "oper", UserName = "u", IsRegistered = true };

            await h.HandleAsync(s, new IrcMessage(null, "WALLOPS", new string[0], "hi"), state, CancellationToken.None);

            Assert.Contains(s.Sent, l => l.Contains(" 481 oper "));
        }

        [Fact]
        public async Task Kill_NetAdmin_ClosesTargetAndRemovesFromState()
        {
            var state = new ServerState();
            state.TryAddUser(new User { ConnectionId = "oper", Nick = "oper", UserName = "u", IsRegistered = true, Modes = UserModes.Operator, OperClass = "netadmin" });
            state.TryAddUser(new User { ConnectionId = "victim", Nick = "victim", UserName = "v", Host = "h", IsRegistered = true, IsRemote = false, Uid = "001VICTIM", RemoteSid = "001" });

            var opts = Options.Create(new IrcOptions
            {
                ServerInfo = new ServerInfoOptions { Name = "srv" },
                Classes = new[] { new OperClassOptions { Name = "netadmin", Capabilities = new[] { "netadmin" } } }
            });

            var reg = new FakeSessionRegistry();
            var victimSession = new TestSession { ConnectionId = "victim", Nick = "victim", UserName = "v", IsRegistered = true };
            reg.Add(victimSession);

            var routing = new RoutingService(reg, new IrcFormatter());
            var silence = new SilenceService();
            var watch = new WatchService(opts, routing);
            var links = new ServerLinkService(NullLogger<ServerLinkService>.Instance, new OptionsMonitorStub<IrcOptions>(opts.Value), state, routing, reg, silence, watch);
            var audit = new CapturingAuditLogService();
            var h = new KillHandler(opts, routing, links, reg, silence, watch, audit);

            var operSession = new TestSession { ConnectionId = "oper", Nick = "oper", UserName = "u", IsRegistered = true };

            await h.HandleAsync(operSession, new IrcMessage(null, "KILL", new[] { "victim" }, "reason"), state, CancellationToken.None);

            Assert.Equal("Killed (oper: reason)", victimSession.ClosedReason);
            Assert.False(state.TryGetUser("victim", out _));

            Assert.Equal(1, audit.CallCount);
            Assert.Equal("KILL", audit.Action);
            Assert.Equal("victim", audit.Target);
            Assert.Equal("reason", audit.Reason);
            Assert.Equal("127.0.0.1", audit.SourceIp);
            Assert.NotNull(audit.Extra);
            Assert.True(audit.Extra!.TryGetValue("success", out var ok) && ok is true);
            Assert.True(audit.Extra!.TryGetValue("remote", out var remote) && remote is false);
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
