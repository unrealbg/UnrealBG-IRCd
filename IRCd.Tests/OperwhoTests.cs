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

    public sealed class OperwhoTests
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
        public async Task Operwho_Netadmin_ListsOpers()
        {
            var state = new ServerState();

            state.TryAddUser(new User { ConnectionId = "oper", Nick = "oper", UserName = "u", IsRegistered = true, Modes = UserModes.Operator, OperName = "root", OperClass = "netadmin" });
            state.TryAddUser(new User { ConnectionId = "oper2", Nick = "oper2", UserName = "u", IsRegistered = true, Modes = UserModes.Operator, OperName = "helper", OperClass = "helper" });

            var opts = Options.Create(new IrcOptions
            {
                ServerInfo = new ServerInfoOptions { Name = "srv" },
                Classes = new[]
                {
                    new OperClassOptions { Name = "netadmin", Capabilities = new[] { "netadmin" } },
                    new OperClassOptions { Name = "helper", Capabilities = new[] { "operwho" } },
                }
            });

            var h = new OperwhoHandler(opts);
            var s = new TestSession { ConnectionId = "oper", Nick = "oper", UserName = "u", IsRegistered = true };

            await h.HandleAsync(s, new IrcMessage(null, "OPERWHO", new string[0], null), state, CancellationToken.None);

            Assert.Contains(s.Sent, l => l.Contains("NOTICE oper") && l.Contains("OPER oper "));
            Assert.Contains(s.Sent, l => l.Contains("NOTICE oper") && l.Contains("OPER oper2 "));
            Assert.Contains(s.Sent, l => l.Contains("End of /OPERWHO"));
        }

        [Fact]
        public async Task Operwhois_Netadmin_ShowsTargetOper()
        {
            var state = new ServerState();

            state.TryAddUser(new User { ConnectionId = "oper", Nick = "oper", UserName = "u", IsRegistered = true, Modes = UserModes.Operator, OperName = "root", OperClass = "netadmin" });
            state.TryAddUser(new User { ConnectionId = "oper2", Nick = "oper2", UserName = "u", IsRegistered = true, Modes = UserModes.Operator, OperName = "helper", OperClass = "helper" });

            var opts = Options.Create(new IrcOptions
            {
                ServerInfo = new ServerInfoOptions { Name = "srv" },
                Classes = new[] { new OperClassOptions { Name = "netadmin", Capabilities = new[] { "netadmin" } } }
            });

            var h = new OperwhoisHandler(opts);
            var s = new TestSession { ConnectionId = "oper", Nick = "oper", UserName = "u", IsRegistered = true };

            await h.HandleAsync(s, new IrcMessage(null, "OPERWHOIS", new[] { "oper2" }, null), state, CancellationToken.None);

            Assert.Contains(s.Sent, l => l.Contains("NOTICE oper") && l.Contains("OPERWHOIS oper2 helper helper"));
        }
    }
}
