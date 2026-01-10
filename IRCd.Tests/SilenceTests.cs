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

    public sealed class SilenceTests
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
        public async Task Silence_AddListRemove_Works()
        {
            var state = new ServerState();
            var silence = new SilenceService();
            var opts = Options.Create(new IrcOptions { ServerInfo = new ServerInfoOptions { Name = "srv" } });

            var h = new SilenceHandler(opts, silence);
            var s = new TestSession { ConnectionId = "me", Nick = "me", UserName = "u", IsRegistered = true };

            await h.HandleAsync(s, new IrcMessage(null, "SILENCE", new[] { "+bad!*@*" }, null), state, CancellationToken.None);

            s.Sent.Clear();
            await h.HandleAsync(s, new IrcMessage(null, "SILENCE", new string[0], null), state, CancellationToken.None);

            Assert.Contains(s.Sent, l => l.Contains(" 271 me bad!*@*"));
            Assert.Contains(s.Sent, l => l.Contains(" 272 me "));

            s.Sent.Clear();
            await h.HandleAsync(s, new IrcMessage(null, "SILENCE", new[] { "-bad!*@*" }, null), state, CancellationToken.None);

            s.Sent.Clear();
            await h.HandleAsync(s, new IrcMessage(null, "SILENCE", new string[0], null), state, CancellationToken.None);

            Assert.DoesNotContain(s.Sent, l => l.Contains(" 271 me "));
        }

        [Fact]
        public async Task Silence_Blocks_Privmsg_ToLocalUser()
        {
            var state = new ServerState();
            var sessions = new FakeSessionRegistry();
            var formatter = new IrcFormatter();
            var routing = new RoutingService(sessions, formatter);
            var hostmask = new HostmaskService();
            var silence = new SilenceService();

            var opts = Options.Create(new IrcOptions
            {
                ServerInfo = new ServerInfoOptions { Name = "srv", Sid = "001" },
                Links = System.Array.Empty<LinkOptions>()
            });

            // Create a ServerLinkService instance (not used for routing in this test, but required by handler ctor).
            var watch = new WatchService(opts, routing);
            var links = new ServerLinkService(NullLogger<ServerLinkService>.Instance, new OptionsMonitorStub<IrcOptions>(opts.Value), state, routing, sessions, silence, watch);

            // Users
            state.TryAddUser(new User { ConnectionId = "from", Nick = "from", UserName = "u", Host = hostmask.GetDisplayedHost(IPAddress.Loopback), IsRegistered = true });
            state.TryAddUser(new User { ConnectionId = "to", Nick = "to", UserName = "u", Host = hostmask.GetDisplayedHost(IPAddress.Loopback), IsRegistered = true });

            var fromSess = new TestSession { ConnectionId = "from", Nick = "from", UserName = "u", IsRegistered = true, RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, 1) };
            var toSess = new TestSession { ConnectionId = "to", Nick = "to", UserName = "u", IsRegistered = true, RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, 2) };

            sessions.Add(fromSess);
            sessions.Add(toSess);

            silence.TryAdd("to", "from!*@*", maxEntries: 15);

            var h = new PrivMsgHandler(routing, links, hostmask, opts, silence);

            await h.HandleAsync(fromSess, new IrcMessage(null, "PRIVMSG", new[] { "to" }, "hi"), state, CancellationToken.None);

            Assert.DoesNotContain(toSess.Sent, l => l.Contains(" PRIVMSG to :hi"));
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
