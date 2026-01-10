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

    public sealed class SquitHandlerTests
    {
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
        public async Task Squit_OperWithClassWithoutCapability_Gets481()
        {
            var state = new ServerState();
            state.TryAddUser(new User { ConnectionId = "c1", Uid = "AAA", Nick = "nick", UserName = "user", IsRegistered = true, Modes = UserModes.Operator, OperClass = "helper", RemoteIp = "198.51.100.10" });

            var opts = Options.Create(new IrcOptions
            {
                ServerInfo = new ServerInfoOptions { Name = "srv" },
                Classes = new[] { new OperClassOptions { Name = "helper", Capabilities = new[] { "metrics" } } }
            });

            var sessions = new FakeSessionRegistry();
            var routing = new RoutingService(sessions, new IrcFormatter());
            var silence = new SilenceService();
            var watch = new WatchService(opts, routing);
            var links = new ServerLinkService(NullLogger<ServerLinkService>.Instance, new OptionsMonitorStub<IrcOptions>(opts.Value), state, routing, sessions, silence, watch);
            var audit = new CapturingAuditLogService();
            var h = new SquitHandler(opts, links, audit);

            var s = new TestSession { Nick = "nick", UserName = "user", IsRegistered = true };

            await h.HandleAsync(s, new IrcMessage(null, "SQUIT", new[] { "002" }, "bye"), state, CancellationToken.None);

            Assert.Contains(s.Sent, l => l.Contains(" 481 nick "));

            Assert.Equal(1, audit.CallCount);
            Assert.Equal("SQUIT", audit.Action);
            Assert.Equal("AAA", audit.ActorUid);
            Assert.Equal("nick", audit.ActorNick);
            Assert.Equal("198.51.100.10", audit.SourceIp);
            Assert.Equal("002", audit.Target);
            Assert.Equal("bye", audit.Reason);
            Assert.NotNull(audit.Extra);
            Assert.True(audit.Extra!.TryGetValue("success", out var success) && success is false);
            Assert.True(audit.Extra!.TryGetValue("error", out var err) && err is string sErr && sErr == "permission_denied");
        }

        [Fact]
        public async Task Squit_NetAdmin_RemovesRemoteTree()
        {
            var state = new ServerState();
            state.TryAddUser(new User { ConnectionId = "c1", Uid = "AAA", Nick = "nick", UserName = "user", IsRegistered = true, Modes = UserModes.Operator, OperClass = "netadmin", RemoteIp = "198.51.100.20" });

            state.TryRegisterRemoteServer(new RemoteServer { ConnectionId = "conn2", Name = "remote", Sid = "002", ParentSid = "001" });
            state.TryRegisterRemoteServer(new RemoteServer { ConnectionId = "conn2", Name = "leaf", Sid = "003", ParentSid = "002" });

            state.TryAddRemoteUser(new User { ConnectionId = "uid:002AAAAAA", Uid = "002AAAAAA", Nick = "a", UserName = "u", Host = "h", IsRegistered = true, IsRemote = true, RemoteSid = "002" });
            state.TryAddRemoteUser(new User { ConnectionId = "uid:003BBBBBB", Uid = "003BBBBBB", Nick = "b", UserName = "u", Host = "h", IsRegistered = true, IsRemote = true, RemoteSid = "003" });

            var opts = Options.Create(new IrcOptions
            {
                ServerInfo = new ServerInfoOptions { Name = "srv", Sid = "001" },
                Classes = new[] { new OperClassOptions { Name = "netadmin", Capabilities = new[] { "netadmin" } } }
            });

            var sessions = new FakeSessionRegistry();
            var routing = new RoutingService(sessions, new IrcFormatter());
            var silence = new SilenceService();
            var watch = new WatchService(opts, routing);
            var links = new ServerLinkService(NullLogger<ServerLinkService>.Instance, new OptionsMonitorStub<IrcOptions>(opts.Value), state, routing, sessions, silence, watch);
            var audit = new CapturingAuditLogService();
            var h = new SquitHandler(opts, links, audit);

            var s = new TestSession { Nick = "nick", UserName = "user", IsRegistered = true };

            await h.HandleAsync(s, new IrcMessage(null, "SQUIT", new[] { "002" }, "bye"), state, CancellationToken.None);

            Assert.Contains(s.Sent, l => l.Contains("NOTICE nick :SQUIT 002"));
            Assert.False(state.TryGetRemoteServerBySid("002", out _));
            Assert.False(state.TryGetRemoteServerBySid("003", out _));
            Assert.False(state.TryGetUserByUid("002AAAAAA", out _));
            Assert.False(state.TryGetUserByUid("003BBBBBB", out _));

            Assert.Equal(1, audit.CallCount);
            Assert.Equal("SQUIT", audit.Action);
            Assert.Equal("AAA", audit.ActorUid);
            Assert.Equal("nick", audit.ActorNick);
            Assert.Equal("198.51.100.20", audit.SourceIp);
            Assert.Equal("002", audit.Target);
            Assert.Equal("bye", audit.Reason);
            Assert.NotNull(audit.Extra);
            Assert.True(audit.Extra!.TryGetValue("success", out var success) && success is true);
            Assert.True(audit.Extra!.TryGetValue("requestedTarget", out var req) && req is string sReq && sReq == "002");
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
