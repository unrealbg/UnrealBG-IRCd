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

    public sealed class NamesHandlerTests
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
        public async Task Names_SecretChannel_NonMember_DoesNotLeakNames()
        {
            var state = new ServerState();
            state.TryAddUser(new User { ConnectionId = "a", Nick = "alice", UserName = "a", Host = "h", IsRegistered = true });
            state.TryJoinChannel("a", "alice", "#s");

            Assert.True(state.TryGetChannel("#s", out var ch) && ch is not null);
            ch!.ApplyModeChange(ChannelModes.Secret, enable: true);

            state.TryAddUser(new User { ConnectionId = "b", Nick = "bob", UserName = "b", Host = "h2", IsRegistered = true });

            var bob = new TestSession { ConnectionId = "b", Nick = "bob", UserName = "b", IsRegistered = true };

            var opts = Options.Create(new IrcOptions());
            var h = new NamesHandler(opts);

            await h.HandleAsync(bob, new IrcMessage(null, "NAMES", new[] { "#s" }, null), state, CancellationToken.None);

            Assert.DoesNotContain(bob.Sent, l => l.Contains(" 353 bob = #s "));
            Assert.Contains(bob.Sent, l => l.Contains(" 366 bob #s :End of /NAMES list."));
        }

        [Fact]
        public async Task Names_LongList_IsChunkedAcrossMultiple353Lines()
        {
            var state = new ServerState();

            // requester is a member so secret rules don't block
            state.TryAddUser(new User { ConnectionId = "req", Nick = "req", UserName = "u", Host = "h", IsRegistered = true });
            state.TryJoinChannel("req", "req", "#test");

            // add many members with long nicknames
            for (var i = 0; i < 60; i++)
            {
                var conn = "u" + i;
                var nick = "user" + i.ToString(System.Globalization.CultureInfo.InvariantCulture) + new string('x', 8);
                state.TryAddUser(new User { ConnectionId = conn, Nick = nick, UserName = "u", Host = "h", IsRegistered = true });
                state.TryJoinChannel(conn, nick, "#test");
            }

            var s = new TestSession { ConnectionId = "req", Nick = "req", UserName = "u", IsRegistered = true };
            var opts = Options.Create(new IrcOptions());
            var h = new NamesHandler(opts);

            await h.HandleAsync(s, new IrcMessage(null, "NAMES", new[] { "#test" }, null), state, CancellationToken.None);

            var count353 = 0;
            foreach (var line in s.Sent)
            {
                if (line.Contains(" 353 req = #test ")) count353++;
            }

            Assert.True(count353 >= 2);
            Assert.Contains(s.Sent, l => l.Contains(" 366 req #test :End of /NAMES list."));
        }
    }
}
