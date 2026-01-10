namespace IRCd.Tests
{
    using System;
    using System.Linq;
    using System.Net;
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

    public sealed class S2SSvspartTests
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
        public async Task IncomingSvsPart_MakesUserPartAndBroadcastsAndForwards()
        {
            var state = new ServerState();
            var sessions = new FakeSessionRegistry();
            var routing = new RoutingService(sessions, new IrcFormatter());
            var silence = new SilenceService();

            var options = new IrcOptions
            {
                ServerInfo = new ServerInfoOptions { Name = "local", Sid = "001", Description = "d" },
                Links = new[]
                {
                    new LinkOptions { Name = "remote", Sid = "002", Password = "pw", Outbound = false, UserSync = true },
                    new LinkOptions { Name = "remote2", Sid = "003", Password = "pw", Outbound = false, UserSync = true }
                }
            };

            var opts = Options.Create(options);
            var watch = new WatchService(opts, routing);
            var svc = new ServerLinkService(NullLogger<ServerLinkService>.Instance, new OptionsMonitorStub<IrcOptions>(options), state, routing, sessions, silence, watch);

            // local peer in channel
            state.TryAddUser(new User { ConnectionId = "peer", Nick = "peer", UserName = "p", Host = "h", IsRegistered = true, IsRemote = false, Uid = "001PEER" });
            state.TryJoinChannel("peer", "peer", "#c");
            var peerSess = new TestSession { ConnectionId = "peer", Nick = "peer", UserName = "p", IsRegistered = true };
            sessions.Add(peerSess);

            // remote user exists and is in channel
            state.TryAddRemoteUser(new User { ConnectionId = "uid:002AAAAAA", Uid = "002AAAAAA", Nick = "Nick", UserName = "u", Host = "h2", IsRegistered = true, IsRemote = true, RemoteSid = "002" });
            state.TryJoinChannel("uid:002AAAAAA", "Nick", "#c");

            var link = new ControlledServerLinkSession("conn1");
            link.Enqueue("PASS pw :TS 1");
            link.Enqueue("SERVER remote 002 :r");

            var link2 = new ControlledServerLinkSession("conn2");
            link2.Enqueue("PASS pw :TS 1");
            link2.Enqueue("SERVER remote2 003 :r2");

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var linkTask = svc.HandleIncomingLinkAsync(link, cts.Token);
            var linkTask2 = svc.HandleIncomingLinkAsync(link2, cts.Token);

            var start = DateTime.UtcNow;
            while (!link.IsAuthenticated && (DateTime.UtcNow - start) < TimeSpan.FromSeconds(1))
            {
                await Task.Delay(5, cts.Token);
            }

            Assert.True(link.IsAuthenticated);

            start = DateTime.UtcNow;
            while (!link2.IsAuthenticated && (DateTime.UtcNow - start) < TimeSpan.FromSeconds(1))
            {
                await Task.Delay(5, cts.Token);
            }

            Assert.True(link2.IsAuthenticated);

            link.Enqueue("SVSPART abcd1234 002 002AAAAAA #c :bye");

            start = DateTime.UtcNow;
            while ((DateTime.UtcNow - start) < TimeSpan.FromMilliseconds(1500))
            {
                if (state.TryGetChannel("#c", out var ch) && ch is not null && !ch.Members.Any(m => m.ConnectionId == "uid:002AAAAAA") && peerSess.Sent.Any(l => l.Contains(":Nick!u@h2 PART #c :bye")))
                {
                    break;
                }

                await Task.Delay(5, cts.Token);
            }

            Assert.True(state.TryGetChannel("#c", out var finalCh) && finalCh is not null);
            Assert.DoesNotContain(finalCh!.Members, m => m.ConnectionId == "uid:002AAAAAA");
            Assert.Contains(peerSess.Sent, l => l.Contains(":Nick!u@h2 PART #c :bye"));

            Assert.Contains(link2.Outgoing, l => l.StartsWith("SVSPART abcd1234 002 002AAAAAA ", StringComparison.OrdinalIgnoreCase));

            cts.Cancel();
            link.Complete();
            link2.Complete();
            try { await linkTask; } catch (OperationCanceledException) { }
            try { await linkTask2; } catch (OperationCanceledException) { }
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
