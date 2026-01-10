namespace IRCd.Tests
{
    using System;
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

    public sealed class S2SKillTests
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
        public async Task IncomingS2SKill_ForLocalUser_ClosesAndPropagatesQuit()
        {
            var state = new ServerState();
            var sessions = new FakeSessionRegistry();
            var routing = new RoutingService(sessions, new IrcFormatter());
            var silence = new SilenceService();

            state.TryAddUser(new User { ConnectionId = "victim", Nick = "victim", UserName = "v", Host = "h", IsRegistered = true, IsRemote = false, Uid = "001VICTIM" });
            var victimSession = new TestSession { ConnectionId = "victim", Nick = "victim", UserName = "v", IsRegistered = true };
            sessions.Add(victimSession);

            var options = new IrcOptions
            {
                ServerInfo = new ServerInfoOptions { Name = "local", Sid = "001", Description = "d" },
                Links =
                [
                    new LinkOptions { Name = "remote", Sid = "002", Password = "pw", Outbound = false, UserSync = true },
                ]
            };

            var svc = new ServerLinkService(
                NullLogger<ServerLinkService>.Instance,
                new OptionsMonitorStub<IrcOptions>(options),
                state,
                routing,
                sessions,
                silence,
                new WatchService(Options.Create(options), routing));

            var killQuit = "Killed (oper: reason)";

            var s2s = new FakeServerLinkSession("conn1", new[]
            {
                "PASS pw :TS 1",
                "SERVER remote 002 :r",
                $"KILL deadbeef 002 001VICTIM :{killQuit}",
                null
            });

            await svc.HandleIncomingLinkAsync(s2s, CancellationToken.None);

            Assert.Equal(killQuit, victimSession.ClosedReason);
            Assert.False(state.TryGetUser("victim", out _));
            Assert.Contains(s2s.Outgoing, l => l.StartsWith("QUIT ", StringComparison.OrdinalIgnoreCase) && l.Contains(" 001VICTIM ") && l.Contains(killQuit));
        }

        [Fact]
        public async Task KillHandler_RemoteUser_SendsS2SKillToOwningServer()
        {
            var state = new ServerState();
            state.TryAddUser(new User { ConnectionId = "oper", Nick = "oper", UserName = "u", IsRegistered = true, Modes = UserModes.Operator, OperClass = "netadmin" });
            state.TryAddUser(new User { ConnectionId = "uid:002AAAAA", Nick = "victim", UserName = "v", Host = "h", IsRegistered = true, IsRemote = true, Uid = "002AAAAA", RemoteSid = "002" });
            state.TrySetNextHopBySid("002", "link1");

            var opts = Options.Create(new IrcOptions
            {
                ServerInfo = new ServerInfoOptions { Name = "srv", Sid = "001", Description = "d" },
                Classes = new[] { new OperClassOptions { Name = "netadmin", Capabilities = new[] { "netadmin" } } },
                Links =
                [
                    new LinkOptions { Name = "remote", Sid = "002", Password = "pw", Outbound = false, UserSync = true },
                ]
            });

            var sessions = new FakeSessionRegistry();
            var routing = new RoutingService(sessions, new IrcFormatter());
            var silence = new SilenceService();

            var watch = new WatchService(opts, routing);
            var svc = new ServerLinkService(NullLogger<ServerLinkService>.Instance, new OptionsMonitorStub<IrcOptions>(opts.Value), state, routing, sessions, silence, watch);

            var link = new ControlledServerLinkSession("link1");
            link.Enqueue("PASS pw :TS 1");
            link.Enqueue("SERVER remote 002 :r");

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var linkTask = svc.HandleIncomingLinkAsync(link, cts.Token);

            // Wait until the link is authenticated/registered.
            var start = DateTime.UtcNow;
            while (!link.IsAuthenticated && (DateTime.UtcNow - start) < TimeSpan.FromSeconds(1))
            {
                await Task.Delay(5, cts.Token);
            }

            Assert.True(link.IsAuthenticated);

            var h = new KillHandler(opts, routing, svc, sessions, silence, watch);
            var operSession = new TestSession { ConnectionId = "oper", Nick = "oper", UserName = "u", IsRegistered = true };

            await h.HandleAsync(operSession, new IrcMessage(null, "KILL", new[] { "victim" }, "reason"), state, CancellationToken.None);

            Assert.Contains(link.Outgoing, l => l.StartsWith("KILL ", StringComparison.OrdinalIgnoreCase) && l.Contains(" 002AAAAA ") && l.Contains("Killed (oper: reason)"));

            cts.Cancel();
            try { await linkTask; } catch { }
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
