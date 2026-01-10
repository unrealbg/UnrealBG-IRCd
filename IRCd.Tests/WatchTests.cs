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

    public sealed class WatchTests
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
        public async Task Watch_AddOnlineNick_SendsNowOn()
        {
            var state = new ServerState();
            var sessions = new FakeSessionRegistry();
            var routing = new RoutingService(sessions, new IrcFormatter());

            var opts = Options.Create(new IrcOptions { ServerInfo = new ServerInfoOptions { Name = "srv" } });
            var limits = Options.Create(new CommandLimitsOptions { MaxWatchEntries = 128 });

            var watch = new WatchService(opts, routing);
            var h = new WatchHandler(opts, limits, watch);

            state.TryAddUser(new User { ConnectionId = "watcher", Nick = "me", UserName = "u", IsRegistered = true });
            state.TryAddUser(new User { ConnectionId = "target", Nick = "bob", UserName = "u", Host = "h", IsRegistered = true });

            var watcherSess = new TestSession { ConnectionId = "watcher", Nick = "me", UserName = "u", IsRegistered = true };
            sessions.Add(watcherSess);

            await h.HandleAsync(watcherSess, new IrcMessage(null, "WATCH", new[] { "+bob" }, null), state, CancellationToken.None);

            Assert.Contains(watcherSess.Sent, l => l.Contains(" 604 me bob ") && l.Contains(" :is online"));
        }

        [Fact]
        public async Task Watch_Logoff_NotifiesWatcherOnQuit()
        {
            var state = new ServerState();
            var sessions = new FakeSessionRegistry();
            var routing = new RoutingService(sessions, new IrcFormatter());
            var silence = new SilenceService();

            var opts = Options.Create(new IrcOptions { ServerInfo = new ServerInfoOptions { Name = "srv", Sid = "001" }, Links = System.Array.Empty<LinkOptions>() });
            var limits = Options.Create(new CommandLimitsOptions { MaxWatchEntries = 128 });

            var watch = new WatchService(opts, routing);
            var watchHandler = new WatchHandler(opts, limits, watch);

            var links = new ServerLinkService(NullLogger<ServerLinkService>.Instance, new OptionsMonitorStub<IrcOptions>(opts.Value), state, routing, sessions, silence, watch);
            var quitHandler = new QuitHandler(routing, links, new WhowasService(), silence, watch);

            state.TryAddUser(new User { ConnectionId = "watcher", Nick = "me", UserName = "u", IsRegistered = true });
            state.TryAddUser(new User { ConnectionId = "target", Nick = "bob", UserName = "u", Host = "h", IsRegistered = true, IsRemote = false });

            var watcherSess = new TestSession { ConnectionId = "watcher", Nick = "me", UserName = "u", IsRegistered = true };
            var targetSess = new TestSession { ConnectionId = "target", Nick = "bob", UserName = "u", IsRegistered = true };

            sessions.Add(watcherSess);
            sessions.Add(targetSess);

            await watchHandler.HandleAsync(watcherSess, new IrcMessage(null, "WATCH", new[] { "+bob" }, null), state, CancellationToken.None);
            watcherSess.Sent.Clear();

            await quitHandler.HandleAsync(targetSess, new IrcMessage(null, "QUIT", new string[0], "bye"), state, CancellationToken.None);

            Assert.Contains(watcherSess.Sent, l => l.Contains(" 601 me bob ") && l.Contains(" :logged off"));
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
