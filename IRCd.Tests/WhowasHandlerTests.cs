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

    using Microsoft.Extensions.Options;

    using Xunit;

    public sealed class WhowasHandlerTests
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

            public ValueTask SendAsync(string line, CancellationToken ct = default)
            {
                Sent.Add(line);
                return ValueTask.CompletedTask;
            }

            public ValueTask CloseAsync(string reason, CancellationToken ct = default)
                => ValueTask.CompletedTask;
        }

        [Fact]
        public async Task Whowas_NoSuchNick_Gets406And369()
        {
            var state = new ServerState();
            var opts = Options.Create(new IrcOptions { ServerInfo = new ServerInfoOptions { Name = "srv" } });
            var whowas = new WhowasService();
            var h = new WhowasHandler(opts, whowas);

            var s = new TestSession { Nick = "me", UserName = "u", IsRegistered = true };

            await h.HandleAsync(s, new IrcMessage(null, "WHOWAS", new[] { "nobody" }, null), state, CancellationToken.None);

            Assert.Contains(s.Sent, l => l.Contains(" 406 me nobody "));
            Assert.Contains(s.Sent, l => l.Contains(" 369 me nobody "));
        }

        [Fact]
        public async Task Whowas_Found_Returns314And369()
        {
            var state = new ServerState();
            var opts = Options.Create(new IrcOptions { ServerInfo = new ServerInfoOptions { Name = "srv" } });
            var whowas = new WhowasService();

            whowas.Record(new User { Nick = "old", UserName = "user", Host = "h", RealName = "r" }, explicitNick: "old", signoff: "bye");

            var h = new WhowasHandler(opts, whowas);
            var s = new TestSession { Nick = "me", UserName = "u", IsRegistered = true };

            await h.HandleAsync(s, new IrcMessage(null, "WHOWAS", new[] { "old" }, null), state, CancellationToken.None);

            Assert.Contains(s.Sent, l => l.Contains(" 314 me old user h "));
            Assert.Contains(s.Sent, l => l.Contains(" 369 me old "));
        }
    }
}
