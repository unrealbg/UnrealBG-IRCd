namespace IRCd.Tests
{
    using System.Net;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Core.Abstractions;
    using IRCd.Core.Protocol;
    using IRCd.Core.Services;
    using IRCd.Core.State;
    using IRCd.Shared.Options;
    using IRCd.Tests.TestDoubles;

    using Microsoft.Extensions.Logging.Abstractions;
    using Microsoft.Extensions.Options;

    using Xunit;

    public sealed class S2SSvsjoinTests
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
        public async Task IncomingSvsJoin_MakesRemoteUserJoinAndBroadcasts()
        {
            var state = new ServerState();
            var sessions = new FakeSessionRegistry();
            var routing = new RoutingService(sessions, new IrcFormatter());
            var silence = new SilenceService();

            var opts = Options.Create(new IrcOptions
            {
                ServerInfo = new ServerInfoOptions { Name = "local", Sid = "001", Description = "d" },
                Links = new[] { new LinkOptions { Name = "remote", Sid = "002", Password = "pw", Outbound = false, UserSync = true } }
            });

            var watch = new WatchService(opts, routing);
            var svc = new ServerLinkService(NullLogger<ServerLinkService>.Instance, new OptionsMonitorStub<IrcOptions>(opts.Value), state, routing, sessions, silence, watch);

            // local peer already in channel
            state.TryAddUser(new User { ConnectionId = "peer", Nick = "peer", UserName = "p", Host = "h", IsRegistered = true, IsRemote = false, Uid = "001PEER" });
            state.TryJoinChannel("peer", "peer", "#c");
            var peerSess = new TestSession { ConnectionId = "peer", Nick = "peer", UserName = "p", IsRegistered = true };
            sessions.Add(peerSess);

            // remote user exists
            state.TryAddRemoteUser(new User { ConnectionId = "uid:002AAAAAA", Uid = "002AAAAAA", Nick = "Nick", UserName = "u", Host = "h2", IsRegistered = true, IsRemote = true, RemoteSid = "002" });

            var link = new ControlledServerLinkSession("conn1");
            link.Enqueue("PASS pw :TS 1");
            link.Enqueue("SERVER remote 002 :r");

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var linkTask = svc.HandleIncomingLinkAsync(link, cts.Token);

            var start = DateTime.UtcNow;
            while (!link.IsAuthenticated && (DateTime.UtcNow - start) < TimeSpan.FromSeconds(1))
            {
                await Task.Delay(5, cts.Token);
            }

            Assert.True(link.IsAuthenticated);

            link.Enqueue("SVSJOIN deadbeef 002 002AAAAAA #c");

            start = DateTime.UtcNow;
            while ((DateTime.UtcNow - start) < TimeSpan.FromSeconds(1))
            {
                if (state.TryGetChannel("#c", out var ch) && ch is not null && ch.Members.Any(m => m.Nick == "Nick") && peerSess.Sent.Any(l => l.Contains(":Nick!u@h2 JOIN :#c")))
                {
                    break;
                }

                await Task.Delay(5, cts.Token);
            }

            Assert.True(state.TryGetChannel("#c", out var finalCh) && finalCh is not null);
            Assert.Contains(finalCh!.Members, m => m.Nick == "Nick");
            Assert.Contains(peerSess.Sent, l => l.Contains(":Nick!u@h2 JOIN :#c"));

            cts.Cancel();
            link.Complete();
            try { await linkTask; } catch (OperationCanceledException) { }
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
