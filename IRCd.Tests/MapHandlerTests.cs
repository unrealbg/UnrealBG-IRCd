namespace IRCd.Tests
{
    using System.Net;
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Core.Abstractions;
    using IRCd.Core.Commands.Handlers;
    using IRCd.Core.Protocol;
    using IRCd.Core.State;
    using IRCd.Shared.Options;

    using Microsoft.Extensions.Options;

    using Xunit;

    public sealed class MapHandlerTests
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

            public ValueTask SendAsync(string line, CancellationToken ct = default)
            {
                Sent.Add(line);
                return ValueTask.CompletedTask;
            }

            public ValueTask CloseAsync(string reason, CancellationToken ct = default)
                => ValueTask.CompletedTask;
        }

        [Fact]
        public async Task Map_ListsRemoteServersInTree()
        {
            var state = new ServerState();

            state.TryRegisterRemoteServer(new RemoteServer
            {
                ConnectionId = "link",
                Name = "remote",
                Sid = "002",
                Description = "r",
                ParentSid = "001"
            });

            state.TryRegisterRemoteServer(new RemoteServer
            {
                ConnectionId = "link",
                Name = "leaf",
                Sid = "003",
                Description = "l",
                ParentSid = "002"
            });

            var opts = Options.Create(new IrcOptions
            {
                ServerInfo = new ServerInfoOptions { Name = "local", Sid = "001" }
            });

            var h = new MapHandler(opts);
            var s = new TestSession { ConnectionId = "c1", Nick = "me", UserName = "u", IsRegistered = true };

            await h.HandleAsync(s, new IrcMessage(null, "MAP", new string[0], null), state, CancellationToken.None);

            Assert.Contains(s.Sent, l => l.Contains(" 006 me ") && l.Contains("local [001]"));
            Assert.Contains(s.Sent, l => l.Contains(" 006 me ") && l.Contains("remote [002]") && l.Contains(":r"));
            Assert.Contains(s.Sent, l => l.Contains(" 006 me ") && l.Contains("leaf [003]") && l.Contains(":l"));
            Assert.Contains(s.Sent, l => l.Contains(" 007 me "));
        }
    }
}
