namespace IRCd.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Net;
    using System.Security.Cryptography;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Core.Abstractions;
    using IRCd.Core.Commands;
    using IRCd.Core.Commands.Contracts;
    using IRCd.Core.Commands.Handlers;
    using IRCd.Core.Protocol;
    using IRCd.Core.Services;
    using IRCd.Core.State;
    using IRCd.Services.Auth;
    using IRCd.Services.NickServ;
    using IRCd.Services.Storage;
    using IRCd.Shared.Options;

    using Microsoft.Extensions.Options;

    using Xunit;

    public sealed class SaslIntegrationTests
    {
        private sealed class TestSession : IClientSession
        {
            public string ConnectionId { get; set; } = "c1";
            public EndPoint RemoteEndPoint { get; } = new IPEndPoint(IPAddress.Loopback, 1234);
            public EndPoint LocalEndPoint { get; } = new IPEndPoint(IPAddress.Loopback, 6667);
            public bool IsSecureConnection { get; set; }

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

            public ValueTask CloseAsync(string reason, CancellationToken ct = default) => ValueTask.CompletedTask;
        }

        private sealed class TestMetrics : IMetrics
        {
            public void ConnectionAccepted(bool secure) { }
            public void ConnectionClosed(bool secure) { }
            public void UserRegistered() { }
            public void ChannelCreated() { }
            public void CommandProcessed(string command) { }
            public void FloodKick() { }
            public void OutboundQueueDepth(long depth) { }
            public void OutboundQueueDrop() { }
            public void OutboundQueueOverflowDisconnect() { }
            public MetricsSnapshot GetSnapshot() => new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        }

        private static string HashPasswordForTest(string password)
        {
            const string version = "v1";
            const int saltSize = 16;
            const int keySize = 32;
            const int iterations = 100_000;

            var salt = new byte[saltSize];
            RandomNumberGenerator.Fill(salt);

            var key = Rfc2898DeriveBytes.Pbkdf2(
                password,
                salt,
                iterations,
                HashAlgorithmName.SHA256,
                keySize);

            return $"{version}:{iterations}:{Convert.ToBase64String(salt)}:{Convert.ToBase64String(key)}";
        }

        [Fact]
        public async Task FullSaslFlow_CapLsReqAuthenticateCapEnd_Succeeds()
        {
            var opts = Options.Create(new IrcOptions { ServerInfo = new ServerInfoOptions { Name = "srv" } });

            var repo = new InMemoryNickAccountRepository();
            await repo.TryCreateAsync(new NickAccount
            {
                Name = "alice",
                PasswordHash = HashPasswordForTest("mypassword"),
                IsConfirmed = true
            }, CancellationToken.None);

            var authenticator = new NickServSaslPlainAuthenticator(repo);
            var auth = new InMemoryAuthState();
            var sasl = new SaslService();

            var capHandler = new CapHandler(opts);
            var authHandler = new AuthenticateHandler(opts, sasl, auth, authenticator);

            var handlers = new IIrcCommandHandler[] { capHandler, authHandler };
            var rateLimit = new RateLimitService(new OptionsMonitorStub<IrcOptions>(new IrcOptions()));
            var dispatcher = new CommandDispatcher(handlers, rateLimit, new TestMetrics());

            var session = new TestSession { Nick = "alice" };
            var state = new ServerState();

            // 1. CAP LS
            await dispatcher.DispatchAsync(session, new IrcMessage(null, "CAP", new[] { "LS" }, null), state, CancellationToken.None);
            Assert.Contains(session.Sent, l => l.Contains("CAP") && l.Contains("sasl"));

            // 2. CAP REQ :sasl
            await dispatcher.DispatchAsync(session, new IrcMessage(null, "CAP", new[] { "REQ", "sasl" }, null), state, CancellationToken.None);
            Assert.Contains(session.Sent, l => l.Contains("CAP") && l.Contains("ACK") && l.Contains("sasl"));
            Assert.True(session.EnabledCapabilities.Contains("sasl"));

            // 3. AUTHENTICATE PLAIN
            await dispatcher.DispatchAsync(session, new IrcMessage(null, "AUTHENTICATE", new[] { "PLAIN" }, null), state, CancellationToken.None);
            Assert.Contains(session.Sent, l => l == "AUTHENTICATE +");

            // 4. AUTHENTICATE <base64>
            var payload = "\0alice\0mypassword";
            var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload));
            await dispatcher.DispatchAsync(session, new IrcMessage(null, "AUTHENTICATE", new[] { b64 }, null), state, CancellationToken.None);

            // 5. Check numerics
            Assert.Contains(session.Sent, l => l.Contains(" 900 "));
            Assert.Contains(session.Sent, l => l.Contains(" 903 "));

            // 6. Verify auth state
            var identified = await auth.GetIdentifiedAccountAsync(session.ConnectionId, CancellationToken.None);
            Assert.Equal("alice", identified);

            // 7. CAP END
            await dispatcher.DispatchAsync(session, new IrcMessage(null, "CAP", new[] { "END" }, null), state, CancellationToken.None);
        }

        [Fact]
        public async Task SaslAbort_WithAsterisk_Sends906()
        {
            var opts = Options.Create(new IrcOptions { ServerInfo = new ServerInfoOptions { Name = "srv" } });

            var repo = new InMemoryNickAccountRepository();
            var authenticator = new NickServSaslPlainAuthenticator(repo);
            var auth = new InMemoryAuthState();
            var sasl = new SaslService();

            var capHandler = new CapHandler(opts);
            var authHandler = new AuthenticateHandler(opts, sasl, auth, authenticator);

            var handlers = new IIrcCommandHandler[] { capHandler, authHandler };
            var rateLimit = new RateLimitService(new OptionsMonitorStub<IrcOptions>(new IrcOptions()));
            var dispatcher = new CommandDispatcher(handlers, rateLimit, new TestMetrics());

            var session = new TestSession { Nick = "alice" };
            var state = new ServerState();

            // Enable sasl
            await dispatcher.DispatchAsync(session, new IrcMessage(null, "CAP", new[] { "REQ", "sasl" }, null), state, CancellationToken.None);

            // Start PLAIN
            await dispatcher.DispatchAsync(session, new IrcMessage(null, "AUTHENTICATE", new[] { "PLAIN" }, null), state, CancellationToken.None);
            Assert.Contains(session.Sent, l => l == "AUTHENTICATE +");

            // Abort with *
            await dispatcher.DispatchAsync(session, new IrcMessage(null, "AUTHENTICATE", new[] { "*" }, null), state, CancellationToken.None);
            Assert.Contains(session.Sent, l => l.Contains(" 906 "));

            // Verify auth state not set
            var identified = await auth.GetIdentifiedAccountAsync(session.ConnectionId, CancellationToken.None);
            Assert.Null(identified);
        }

        [Fact]
        public async Task SaslWrongPassword_Sends904()
        {
            var opts = Options.Create(new IrcOptions { ServerInfo = new ServerInfoOptions { Name = "srv" } });

            var repo = new InMemoryNickAccountRepository();
            await repo.TryCreateAsync(new NickAccount
            {
                Name = "alice",
                PasswordHash = HashPasswordForTest("correctpassword"),
                IsConfirmed = true
            }, CancellationToken.None);

            var authenticator = new NickServSaslPlainAuthenticator(repo);
            var auth = new InMemoryAuthState();
            var sasl = new SaslService();

            var capHandler = new CapHandler(opts);
            var authHandler = new AuthenticateHandler(opts, sasl, auth, authenticator);

            var handlers = new IIrcCommandHandler[] { capHandler, authHandler };
            var rateLimit = new RateLimitService(new OptionsMonitorStub<IrcOptions>(new IrcOptions()));
            var dispatcher = new CommandDispatcher(handlers, rateLimit, new TestMetrics());

            var session = new TestSession { Nick = "alice" };
            var state = new ServerState();

            await dispatcher.DispatchAsync(session, new IrcMessage(null, "CAP", new[] { "REQ", "sasl" }, null), state, CancellationToken.None);
            await dispatcher.DispatchAsync(session, new IrcMessage(null, "AUTHENTICATE", new[] { "PLAIN" }, null), state, CancellationToken.None);

            var payload = "\0alice\0wrongpassword";
            var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload));
            await dispatcher.DispatchAsync(session, new IrcMessage(null, "AUTHENTICATE", new[] { b64 }, null), state, CancellationToken.None);

            Assert.Contains(session.Sent, l => l.Contains(" 904 "));

            var identified = await auth.GetIdentifiedAccountAsync(session.ConnectionId, CancellationToken.None);
            Assert.Null(identified);
        }

        [Fact]
        public async Task SaslWithoutCapSasl_Sends904()
        {
            var opts = Options.Create(new IrcOptions { ServerInfo = new ServerInfoOptions { Name = "srv" } });

            var repo = new InMemoryNickAccountRepository();
            var authenticator = new NickServSaslPlainAuthenticator(repo);
            var auth = new InMemoryAuthState();
            var sasl = new SaslService();

            var authHandler = new AuthenticateHandler(opts, sasl, auth, authenticator);

            var handlers = new IIrcCommandHandler[] { authHandler };
            var rateLimit = new RateLimitService(new OptionsMonitorStub<IrcOptions>(new IrcOptions()));
            var dispatcher = new CommandDispatcher(handlers, rateLimit, new TestMetrics());

            var session = new TestSession { Nick = "alice" };
            var state = new ServerState();

            // Try to authenticate without CAP REQ :sasl
            await dispatcher.DispatchAsync(session, new IrcMessage(null, "AUTHENTICATE", new[] { "PLAIN" }, null), state, CancellationToken.None);

            Assert.Contains(session.Sent, l => l.Contains(" 904 "));
        }

        private sealed class OptionsMonitorStub<T> : IOptionsMonitor<T>
        {
            public OptionsMonitorStub(T value) => CurrentValue = value;

            public T CurrentValue { get; }

            public T Get(string? name) => CurrentValue;

            public IDisposable? OnChange(Action<T, string?> listener) => null;
        }
    }
}
