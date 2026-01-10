namespace IRCd.Tests
{
    using System;
    using System.Net;
    using System.Security.Cryptography;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Core.Abstractions;
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

    public sealed class SaslTests
    {
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

        private sealed class TestSession : IClientSession
        {
            public string ConnectionId { get; set; } = "c1";
            public EndPoint RemoteEndPoint { get; } = new IPEndPoint(IPAddress.Loopback, 1234);
            public EndPoint LocalEndPoint { get; } = new IPEndPoint(IPAddress.Loopback, 6667);
            public bool IsSecureConnection { get; set; }

            public string? ClientCertificateSubject { get; set; }
            public string? ClientCertificateFingerprintSha256 { get; set; }

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

        [Fact]
        public async Task SaslPlain_Success_SetsAuthState_AndSends903()
        {
            var opts = Options.Create(new IrcOptions { ServerInfo = new ServerInfoOptions { Name = "srv" } });

            var repo = new InMemoryNickAccountRepository();
            await repo.TryCreateAsync(new NickAccount
            {
                Name = "alice",
                PasswordHash = HashPasswordForTest("pw"),
                IsConfirmed = true
            }, CancellationToken.None);

            var authenticator = new NickServSaslPlainAuthenticator(repo);
            var auth = new InMemoryAuthState();
            var sasl = new SaslService();

            var handler = new AuthenticateHandler(opts, sasl, auth, authenticator);

            var session = new TestSession { Nick = "alice" };
            session.EnabledCapabilities.Add("sasl");

            await handler.HandleAsync(session, new IrcMessage(null, "AUTHENTICATE", new[] { "PLAIN" }, null), new ServerState(), CancellationToken.None);

            var payload = "\0alice\0pw";
            var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload));

            await handler.HandleAsync(session, new IrcMessage(null, "AUTHENTICATE", new[] { b64 }, null), new ServerState(), CancellationToken.None);

            Assert.Contains(session.Sent, l => l == "AUTHENTICATE +");
            Assert.Contains(session.Sent, l => l.Contains(" 900 "));
            Assert.Contains(session.Sent, l => l.Contains(" 903 "));

            var identified = await auth.GetIdentifiedAccountAsync(session.ConnectionId, CancellationToken.None);
            Assert.Equal("alice", identified);
        }

        [Fact]
        public async Task Sasl_UnsupportedMechanism_Sends905()
        {
            var opts = Options.Create(new IrcOptions { ServerInfo = new ServerInfoOptions { Name = "srv" } });

            var repo = new InMemoryNickAccountRepository();
            var authenticator = new NickServSaslPlainAuthenticator(repo);
            var auth = new InMemoryAuthState();
            var sasl = new SaslService();

            var handler = new AuthenticateHandler(opts, sasl, auth, authenticator);

            var session = new TestSession { Nick = "alice" };
            session.EnabledCapabilities.Add("sasl");

            await handler.HandleAsync(session, new IrcMessage(null, "AUTHENTICATE", new[] { "SCRAM-SHA-256" }, null), new ServerState(), CancellationToken.None);

            Assert.Contains(session.Sent, l => l.Contains(" 905 "));
        }

        [Fact]
        public async Task SaslExternal_WithClientCertFingerprintMapping_Succeeds()
        {
            var o = new IrcOptions { ServerInfo = new ServerInfoOptions { Name = "srv" } };
            o.Sasl.External.Enabled = true;
            o.Sasl.External.FingerprintToAccount["AABBCC"] = "alice";

            var opts = Options.Create(o);

            var repo = new InMemoryNickAccountRepository();
            var authenticator = new NickServSaslPlainAuthenticator(repo);
            var auth = new InMemoryAuthState();
            var sasl = new SaslService();

            var handler = new AuthenticateHandler(opts, sasl, auth, authenticator);

            var session = new TestSession
            {
                Nick = "alice",
                IsSecureConnection = true,
                ClientCertificateFingerprintSha256 = "AA:BB:CC"
            };
            session.EnabledCapabilities.Add("sasl");

            await handler.HandleAsync(session, new IrcMessage(null, "AUTHENTICATE", new[] { "EXTERNAL" }, null), new ServerState(), CancellationToken.None);

            // Empty authzid
            await handler.HandleAsync(session, new IrcMessage(null, "AUTHENTICATE", new[] { "+" }, null), new ServerState(), CancellationToken.None);

            Assert.Contains(session.Sent, l => l == "AUTHENTICATE +");
            Assert.Contains(session.Sent, l => l.Contains(" 900 "));
            Assert.Contains(session.Sent, l => l.Contains(" 903 "));

            var identified = await auth.GetIdentifiedAccountAsync(session.ConnectionId, CancellationToken.None);
            Assert.Equal("alice", identified);
        }

        [Fact]
        public async Task SaslExternal_WithoutClientCert_FailsCleanly()
        {
            var o = new IrcOptions { ServerInfo = new ServerInfoOptions { Name = "srv" } };
            o.Sasl.External.Enabled = true;
            o.Sasl.External.FingerprintToAccount["AABBCC"] = "alice";

            var opts = Options.Create(o);

            var repo = new InMemoryNickAccountRepository();
            var authenticator = new NickServSaslPlainAuthenticator(repo);
            var auth = new InMemoryAuthState();
            var sasl = new SaslService();

            var handler = new AuthenticateHandler(opts, sasl, auth, authenticator);

            var session = new TestSession { Nick = "alice", IsSecureConnection = true };
            session.EnabledCapabilities.Add("sasl");

            await handler.HandleAsync(session, new IrcMessage(null, "AUTHENTICATE", new[] { "EXTERNAL" }, null), new ServerState(), CancellationToken.None);
            await handler.HandleAsync(session, new IrcMessage(null, "AUTHENTICATE", new[] { "+" }, null), new ServerState(), CancellationToken.None);

            Assert.Contains(session.Sent, l => l.Contains(" 904 "));

            var identified = await auth.GetIdentifiedAccountAsync(session.ConnectionId, CancellationToken.None);
            Assert.Null(identified);
        }

        [Fact]
        public async Task SaslPlain_BadBase64_Sends904()
        {
            var opts = Options.Create(new IrcOptions { ServerInfo = new ServerInfoOptions { Name = "srv" } });

            var repo = new InMemoryNickAccountRepository();
            await repo.TryCreateAsync(new NickAccount
            {
                Name = "alice",
                PasswordHash = HashPasswordForTest("pw"),
                IsConfirmed = true
            }, CancellationToken.None);

            var authenticator = new NickServSaslPlainAuthenticator(repo);
            var auth = new InMemoryAuthState();
            var sasl = new SaslService();

            var handler = new AuthenticateHandler(opts, sasl, auth, authenticator);

            var session = new TestSession { Nick = "alice" };
            session.EnabledCapabilities.Add("sasl");

            await handler.HandleAsync(session, new IrcMessage(null, "AUTHENTICATE", new[] { "PLAIN" }, null), new ServerState(), CancellationToken.None);
            await handler.HandleAsync(session, new IrcMessage(null, "AUTHENTICATE", new[] { "not-base64" }, null), new ServerState(), CancellationToken.None);

            Assert.Contains(session.Sent, l => l.Contains(" 904 "));
        }

        [Fact]
        public async Task SaslPlain_Chunked400PlusTerminator_Succeeds()
        {
            var opts = Options.Create(new IrcOptions { ServerInfo = new ServerInfoOptions { Name = "srv" } });

            var password = new string('p', 291);
            var repo = new InMemoryNickAccountRepository();
            await repo.TryCreateAsync(new NickAccount
            {
                Name = "alice",
                PasswordHash = HashPasswordForTest(password),
                IsConfirmed = true
            }, CancellationToken.None);

            var authenticator = new NickServSaslPlainAuthenticator(repo);
            var auth = new InMemoryAuthState();
            var sasl = new SaslService();

            var handler = new AuthenticateHandler(opts, sasl, auth, authenticator);

            var session = new TestSession { Nick = "alice" };
            session.EnabledCapabilities.Add("sasl");

            await handler.HandleAsync(session, new IrcMessage(null, "AUTHENTICATE", new[] { "PLAIN" }, null), new ServerState(), CancellationToken.None);

            var payload = "\0alice\0" + password;
            var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload));
            Assert.Equal(400, b64.Length);

            await handler.HandleAsync(session, new IrcMessage(null, "AUTHENTICATE", new[] { b64 }, null), new ServerState(), CancellationToken.None);

            // Not done yet (base64 chunk was exactly 400 chars)
            Assert.DoesNotContain(session.Sent, l => l.Contains(" 903 "));

            // Terminator
            await handler.HandleAsync(session, new IrcMessage(null, "AUTHENTICATE", new[] { "+" }, null), new ServerState(), CancellationToken.None);

            Assert.Contains(session.Sent, l => l.Contains(" 903 "));
        }
    }
}
