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

    public sealed class WhoisHandlerTests
    {
        private sealed class TestSession : IClientSession
        {
            public string ConnectionId { get; set; } = "c1";
            public EndPoint RemoteEndPoint { get; } = new IPEndPoint(IPAddress.Loopback, 1234);
            public EndPoint LocalEndPoint { get; } = new IPEndPoint(IPAddress.Loopback, 6667);
            public bool IsSecureConnection { get; set; }

            public ISet<string> EnabledCapabilities { get; } =
                new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);

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

        private sealed class TestServiceEvents : IServiceSessionEvents
        {
            private readonly bool _identified;

            public TestServiceEvents(bool identified)
                => _identified = identified;

            public ValueTask OnNickChangedAsync(IClientSession session, string? oldNick, string newNick, ServerState state, CancellationToken ct)
                => ValueTask.CompletedTask;

            public ValueTask OnQuitAsync(IClientSession session, string reason, ServerState state, CancellationToken ct)
                => ValueTask.CompletedTask;

            public ValueTask<bool> IsNickRegisteredAsync(string nick, CancellationToken ct)
                => ValueTask.FromResult(true);

            public ValueTask<bool> IsIdentifiedForNickAsync(string connectionId, string nick, CancellationToken ct)
                => ValueTask.FromResult(_identified);
        }

        [Fact]
        public async Task Whois_NetAdmin_Target_ShowsNetworkAdministratorLine()
        {
            var state = new ServerState();
            var sessions = new InMemorySessionRegistry();

            state.TryAddUser(new User { ConnectionId = "req", Nick = "req", UserName = "u", IsRegistered = true });
            state.TryAddUser(new User
            {
                ConnectionId = "t",
                Nick = "oper",
                UserName = "u",
                RealName = "Real",
                IsRegistered = true,
                Modes = UserModes.Operator,
                OperClass = "netadmin",
            });

            var requester = new TestSession { ConnectionId = "req", Nick = "req", UserName = "u", IsRegistered = true };
            sessions.Add(requester);

            var opts = Options.Create(new IrcOptions
            {
                ServerInfo = new ServerInfoOptions { Name = "srv", Description = "desc" },
                Classes = new[] { new OperClassOptions { Name = "netadmin", Capabilities = new[] { "netadmin" } } }
            });

            var h = new WhoisHandler(sessions, opts, new TestServiceEvents(identified: true));
            await h.HandleAsync(requester, new IrcMessage(null, "WHOIS", new[] { "oper" }, null), state, CancellationToken.None);

            Assert.Contains(requester.Sent, l => l.Contains(" 320 req oper :is a Network Administrator"));
            Assert.Contains(requester.Sent, l => l.Contains(" 313 req oper :is an IRC Operator"));
            Assert.Contains(requester.Sent, l => l.Contains(" 307 req oper :has identified for this nickname"));

            var first311 = requester.Sent.FindIndex(l => l.Contains(" 311 req oper "));
            var last318 = requester.Sent.FindLastIndex(l => l.Contains(" 318 req oper"));
            Assert.True(first311 >= 0);
            Assert.True(last318 > first311);
        }

        [Fact]
        public async Task Whois_Unidentified_DoesNotClaimIdentified()
        {
            var state = new ServerState();
            var sessions = new InMemorySessionRegistry();

            state.TryAddUser(new User { ConnectionId = "req", Nick = "req", UserName = "u", IsRegistered = true });
            state.TryAddUser(new User
            {
                ConnectionId = "t",
                Nick = "Jack",
                UserName = "u",
                RealName = "Real",
                IsRegistered = true,
            });

            var requester = new TestSession { ConnectionId = "req", Nick = "req", UserName = "u", IsRegistered = true };
            sessions.Add(requester);

            var opts = Options.Create(new IrcOptions { ServerInfo = new ServerInfoOptions { Name = "srv", Description = "desc" } });
            var h = new WhoisHandler(sessions, opts, new TestServiceEvents(identified: false));

            await h.HandleAsync(requester, new IrcMessage(null, "WHOIS", new[] { "Jack" }, null), state, CancellationToken.None);

            Assert.DoesNotContain(requester.Sent, l => l.Contains(" 307 req Jack "));
        }

        [Fact]
        public async Task Whois_CommaSeparatedTargets_ReturnsWhoisForEach()
        {
            var state = new ServerState();
            var sessions = new InMemorySessionRegistry();

            state.TryAddUser(new User { ConnectionId = "req", Nick = "req", UserName = "u", IsRegistered = true });
            state.TryAddUser(new User { ConnectionId = "t1", Nick = "oper", UserName = "u", RealName = "Real", IsRegistered = true });
            state.TryAddUser(new User { ConnectionId = "t2", Nick = "Jack", UserName = "u", RealName = "Real", IsRegistered = true });

            var requester = new TestSession { ConnectionId = "req", Nick = "req", UserName = "u", IsRegistered = true };
            sessions.Add(requester);

            var opts = Options.Create(new IrcOptions { ServerInfo = new ServerInfoOptions { Name = "srv", Description = "desc" } });
            var h = new WhoisHandler(sessions, opts);

            await h.HandleAsync(requester, new IrcMessage(null, "WHOIS", new[] { "oper,Jack" }, null), state, CancellationToken.None);

            Assert.Contains(requester.Sent, l => l.Contains(" 311 req oper "));
            Assert.Contains(requester.Sent, l => l.Contains(" 318 req oper"));
            Assert.Contains(requester.Sent, l => l.Contains(" 311 req Jack "));
            Assert.Contains(requester.Sent, l => l.Contains(" 318 req Jack"));
        }
    }
}
