namespace IRCd.Tests
{
    using System.Net;
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Core.Services;
    using IRCd.Core.State;
    using IRCd.Core.Protocol;
    using IRCd.Shared.Options;
    using IRCd.Tests.TestDoubles;
    using Microsoft.Extensions.Logging.Abstractions;
    using Microsoft.Extensions.Options;
    using Xunit;

    public sealed class S2SServerLinkServiceProtocolErrorTests
    {
        [Fact]
        public async Task HandleIncomingLinkAsync_InboundPeerSidMismatch_IsRejected()
        {
            var state = new ServerState();
            var sessions = new FakeSessionRegistry();
            var routing = new RoutingService(sessions, new IrcFormatter());
            var silence = new SilenceService();

            var options = new IrcOptions
            {
                ServerInfo = new ServerInfoOptions { Name = "local", Sid = "001", Description = "d" },
                Links =
                [
                    new LinkOptions { Name = "remote", Sid = "002", Password = "pw", Outbound = false, UserSync = true },
                ]
            };

            var opts = Options.Create(options);
            var watch = new WatchService(opts, routing);

            var svc = new ServerLinkService(
                NullLogger<ServerLinkService>.Instance,
                new OptionsMonitorStub<IrcOptions>(options),
                state,
                routing,
                sessions,
                silence,
                watch);

            var s = new FakeServerLinkSession("conn1", new[]
            {
                "PASS pw :TS",
                "SERVER remote 003 :r",
            });

            await svc.HandleIncomingLinkAsync(s, CancellationToken.None);

            Assert.Contains(s.Outgoing, l => l.Contains("ERROR", System.StringComparison.OrdinalIgnoreCase) && l.Contains("Unexpected SID", System.StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task HandleIncomingLinkAsync_ConnectOnlyPeer_IsRejectedAsUnknown()
        {
            var state = new ServerState();
            var sessions = new FakeSessionRegistry();
            var routing = new RoutingService(sessions, new IrcFormatter());
            var silence = new SilenceService();

            var options = new IrcOptions
            {
                ServerInfo = new ServerInfoOptions { Name = "local", Sid = "001", Description = "d" },
                Links =
                [
                    // connect {} -> outbound-only; should not allow inbound.
                    new LinkOptions { Name = "remote", Password = "pw", Outbound = true, UserSync = true },
                ]
            };

            var opts = Options.Create(options);
            var watch = new WatchService(opts, routing);

            var svc = new ServerLinkService(
                NullLogger<ServerLinkService>.Instance,
                new OptionsMonitorStub<IrcOptions>(options),
                state,
                routing,
                sessions,
                silence,
                watch);

            var s = new FakeServerLinkSession("conn1", new[]
            {
                "PASS pw :TS",
                "SERVER remote 002 :r",
            });

            await svc.HandleIncomingLinkAsync(s, CancellationToken.None);

            Assert.Contains(s.Outgoing, l => l.Contains("ERROR", System.StringComparison.OrdinalIgnoreCase) && l.Contains("Unknown server", System.StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task HandleIncomingLinkAsync_InboundPeerIpMismatch_IsRejected()
        {
            var state = new ServerState();
            var sessions = new FakeSessionRegistry();
            var routing = new RoutingService(sessions, new IrcFormatter());
            var silence = new SilenceService();

            var options = new IrcOptions
            {
                ServerInfo = new ServerInfoOptions { Name = "local", Sid = "001", Description = "d" },
                Links =
                [
                    // link {} -> inbound allowed; Host is treated as IP allowlist if it parses as IP.
                    new LinkOptions { Name = "remote", Host = "203.0.113.10", Password = "pw", Outbound = false, UserSync = true },
                ]
            };

            var opts = Options.Create(options);
            var watch = new WatchService(opts, routing);

            var svc = new ServerLinkService(
                NullLogger<ServerLinkService>.Instance,
                new OptionsMonitorStub<IrcOptions>(options),
                state,
                routing,
                sessions,
                silence,
                watch);

            var s = new FakeServerLinkSession(
                "conn1",
                new[]
                {
                    "PASS pw :TS",
                    "SERVER remote 002 :r",
                },
                new IPEndPoint(IPAddress.Loopback, 12345));

            await svc.HandleIncomingLinkAsync(s, CancellationToken.None);

            Assert.Contains(s.Outgoing, l => l.Contains("ERROR", System.StringComparison.OrdinalIgnoreCase) && l.Contains("Not authorized", System.StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task HandleIncomingLinkAsync_UserUidCollision_SendsError()
        {
            var state = new ServerState();
            var sessions = new FakeSessionRegistry();
            var routing = new RoutingService(sessions, new IrcFormatter());
            var silence = new SilenceService();

            var options = new IrcOptions
            {
                ServerInfo = new ServerInfoOptions { Name = "local", Sid = "001", Description = "d" },
                Links =
                [
                    new LinkOptions { Name = "remote", Password = "pw", Outbound = false, UserSync = true },
                ]
            };

            var opts = Options.Create(options);
            var watch = new WatchService(opts, routing);

            var svc = new ServerLinkService(
                NullLogger<ServerLinkService>.Instance,
                new OptionsMonitorStub<IrcOptions>(options),
                state,
                routing,
                sessions,
                silence,
                watch);

            // Prepare an existing remote user with UID 002AAAAAA from a different origin SID.
            state.TryAddRemoteUser(new User
            {
                ConnectionId = "uid:002AAAAAA",
                Uid = "002AAAAAA",
                Nick = "NickOld",
                UserName = "u",
                Host = "h",
                IsRegistered = true,
                IsRemote = true,
                RemoteSid = "999"
            });

            var s = new FakeServerLinkSession("conn1", new[]
            {
                "PASS pw :TS",
                "SERVER remote 002 :r",
                "USER 002AAAAAA NickNew u h 0 :Real"
            });

            await svc.HandleIncomingLinkAsync(s, CancellationToken.None);

            Assert.Contains(s.Outgoing, l => l.Contains("ERROR", System.StringComparison.OrdinalIgnoreCase) && l.Contains("UID collision", System.StringComparison.OrdinalIgnoreCase));
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
