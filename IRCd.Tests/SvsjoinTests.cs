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

    public sealed class SvsjoinTests
    {
        private sealed class TestSession : IClientSession
        {
            public string ConnectionId { get; set; } = "c1";
            public EndPoint RemoteEndPoint { get; set; } = new IPEndPoint(IPAddress.Loopback, 1234);
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

        [Fact]
        public async Task Svsjoin_Netadmin_ForcesJoinAndBroadcasts()
        {
            var state = new ServerState();
            var sessions = new FakeSessionRegistry();
            var routing = new RoutingService(sessions, new IrcFormatter());
            var watch = new WatchService(Options.Create(new IrcOptions()), routing);
            var silence = new SilenceService();

            var opts = Options.Create(new IrcOptions
            {
                ServerInfo = new ServerInfoOptions { Name = "srv", Sid = "001", Description = "d" },
                Classes = new[] { new OperClassOptions { Name = "netadmin", Capabilities = new[] { "netadmin" } } },
                Links = Array.Empty<LinkOptions>()
            });

            state.TryAddUser(new User { ConnectionId = "oper", Nick = "oper", UserName = "u", IsRegistered = true, Modes = UserModes.Operator, OperClass = "netadmin" });
            state.TryAddUser(new User { ConnectionId = "victim", Nick = "victim", UserName = "v", Host = "h", IsRegistered = true, IsRemote = false, Uid = "001VICTIM" });
            state.TryAddUser(new User { ConnectionId = "peer", Nick = "peer", UserName = "p", Host = "h2", IsRegistered = true, IsRemote = false, Uid = "001PEER" });

            state.TryJoinChannel("peer", "peer", "#c");

            var operSess = new TestSession { ConnectionId = "oper", Nick = "oper", UserName = "u", IsRegistered = true };
            var victimSess = new TestSession { ConnectionId = "victim", Nick = "victim", UserName = "v", IsRegistered = true };
            var peerSess = new TestSession { ConnectionId = "peer", Nick = "peer", UserName = "p", IsRegistered = true };

            sessions.Add(operSess);
            sessions.Add(victimSess);
            sessions.Add(peerSess);

            var links = new ServerLinkService(NullLogger<ServerLinkService>.Instance, new OptionsMonitorStub<IrcOptions>(opts.Value), state, routing, sessions, silence, watch);
            var h = new SvsjoinHandler(opts, routing, links);

            await h.HandleAsync(operSess, new IrcMessage(null, "SVSJOIN", new[] { "victim", "#c" }, null), state, CancellationToken.None);

            Assert.True(state.TryGetChannel("#c", out var ch) && ch is not null);
            Assert.Contains(ch!.Members, m => m.ConnectionId == "victim");
            Assert.Contains(peerSess.Sent, l => l.Contains(":victim!v@h JOIN :#c"));
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
