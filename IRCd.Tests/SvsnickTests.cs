namespace IRCd.Tests
{
    using System;
    using System.Linq;
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

    public sealed class SvsnickTests
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
        public async Task Svsnick_LocalUser_ChangesNickAndBroadcasts()
        {
            var state = new ServerState();
            var sessions = new FakeSessionRegistry();
            var routing = new RoutingService(sessions, new IrcFormatter());
            var silence = new SilenceService();

            var opts = Options.Create(new IrcOptions
            {
                ServerInfo = new ServerInfoOptions { Name = "srv", Sid = "001", Description = "d" },
                Classes = new[] { new OperClassOptions { Name = "netadmin", Capabilities = new[] { "netadmin" } } }
            });

            var watch = new WatchService(opts, routing);
            var links = new ServerLinkService(NullLogger<ServerLinkService>.Instance, new OptionsMonitorStub<IrcOptions>(opts.Value), state, routing, sessions, silence, watch);
            var whowas = new WhowasService();

            var h = new SvsnickHandler(opts, routing, sessions, links, whowas, watch);

            state.TryAddUser(new User { ConnectionId = "oper", Nick = "oper", UserName = "o", Host = "h", IsRegistered = true, Modes = UserModes.Operator, OperClass = "netadmin" });
            state.TryAddUser(new User { ConnectionId = "victim", Nick = "victim", UserName = "v", Host = "h2", IsRegistered = true, Modes = UserModes.None, Uid = "001VIC", RemoteSid = "001", IsRemote = false });
            state.TryAddUser(new User { ConnectionId = "peer", Nick = "peer", UserName = "p", Host = "h3", IsRegistered = true });

            state.TryJoinChannel("victim", "victim", "#c");
            state.TryJoinChannel("peer", "peer", "#c");

            var operSession = new TestSession { ConnectionId = "oper", Nick = "oper", UserName = "o", IsRegistered = true };
            var victimSession = new TestSession { ConnectionId = "victim", Nick = "victim", UserName = "v", IsRegistered = true };
            var peerSession = new TestSession { ConnectionId = "peer", Nick = "peer", UserName = "p", IsRegistered = true };

            sessions.Add(operSession);
            sessions.Add(victimSession);
            sessions.Add(peerSession);

            await h.HandleAsync(operSession, new IrcMessage(null, "SVSNICK", new[] { "victim", "newnick" }, null), state, CancellationToken.None);

            Assert.True(state.TryGetConnectionIdByNick("newnick", out var c) && c == "victim");
            Assert.True(state.TryGetChannel("#c", out var ch) && ch is not null);
            Assert.Contains(ch!.Members, m => m.ConnectionId == "victim" && m.Nick == "newnick");

            Assert.Contains(peerSession.Sent, l => l.Contains(":victim!v@h2 NICK :newnick"));
            Assert.Contains(victimSession.Sent, l => l.Contains(":victim!v@h2 NICK :newnick"));
        }

        [Fact]
        public async Task Svsnick_RemoteUser_RoutesToOwningServer()
        {
            var state = new ServerState();
            var sessions = new FakeSessionRegistry();
            var routing = new RoutingService(sessions, new IrcFormatter());
            var silence = new SilenceService();

            var options = new IrcOptions
            {
                ServerInfo = new ServerInfoOptions { Name = "srv", Sid = "001", Description = "d" },
                Classes = new[] { new OperClassOptions { Name = "netadmin", Capabilities = new[] { "netadmin" } } },
                Links = new[] { new LinkOptions { Name = "remote", Sid = "002", Password = "pw", Outbound = false, UserSync = true } }
            };

            var opts = Options.Create(options);
            var watch = new WatchService(opts, routing);
            var links = new ServerLinkService(NullLogger<ServerLinkService>.Instance, new OptionsMonitorStub<IrcOptions>(options), state, routing, sessions, silence, watch);
            var whowas = new WhowasService();

            var h = new SvsnickHandler(opts, routing, sessions, links, whowas, watch);

            state.TryAddUser(new User { ConnectionId = "oper", Nick = "oper", UserName = "o", Host = "h", IsRegistered = true, Modes = UserModes.Operator, OperClass = "netadmin" });
            state.TryAddRemoteUser(new User { ConnectionId = "uid:002AAAAA", Uid = "002AAAAA", Nick = "victim", UserName = "v", Host = "h2", IsRegistered = true, IsRemote = true, RemoteSid = "002" });
            state.TrySetNextHopBySid("002", "link1");

            var link = new ControlledServerLinkSession("link1");
            link.Enqueue("PASS pw :TS 1");
            link.Enqueue("SERVER remote 002 :r");

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var linkTask = links.HandleIncomingLinkAsync(link, cts.Token);

            var start = DateTime.UtcNow;
            while (!link.IsAuthenticated && (DateTime.UtcNow - start) < TimeSpan.FromSeconds(1))
            {
                await Task.Delay(5, cts.Token);
            }

            Assert.True(link.IsAuthenticated);

            var operSession = new TestSession { ConnectionId = "oper", Nick = "oper", UserName = "o", IsRegistered = true };
            sessions.Add(operSession);

            await h.HandleAsync(operSession, new IrcMessage(null, "SVSNICK", new[] { "victim", "newnick" }, null), state, CancellationToken.None);

            Assert.Contains(link.Outgoing, l => l.StartsWith("SVSNICK ", StringComparison.OrdinalIgnoreCase) && l.Contains(" 002AAAAA ") && l.Contains(" newnick"));

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
