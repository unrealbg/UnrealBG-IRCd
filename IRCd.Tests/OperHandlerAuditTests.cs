namespace IRCd.Tests
{
    using System.Collections.Generic;
    using System.Net;
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Core.Abstractions;
    using IRCd.Core.Commands.Handlers;
    using IRCd.Core.Protocol;
    using IRCd.Core.Security;
    using IRCd.Core.State;
    using IRCd.Shared.Options;

    using Microsoft.Extensions.Options;

    using Xunit;

    public sealed class OperHandlerAuditTests
    {
        private sealed class CapturingAuditLogService : IAuditLogService
        {
            public int CallCount { get; private set; }
            public string? Action { get; private set; }
            public string? ActorUid { get; private set; }
            public string? ActorNick { get; private set; }
            public string? SourceIp { get; private set; }
            public string? Target { get; private set; }
            public IReadOnlyDictionary<string, object?>? Extra { get; private set; }

            public ValueTask LogOperActionAsync(
                string action,
                IClientSession session,
                string? actorUid,
                string? actorNick,
                string? sourceIp,
                string? target,
                string? reason,
                IReadOnlyDictionary<string, object?>? extra,
                CancellationToken ct)
            {
                CallCount++;
                Action = action;
                ActorUid = actorUid;
                ActorNick = actorNick;
                SourceIp = sourceIp;
                Target = target;
                Extra = extra;
                return ValueTask.CompletedTask;
            }
        }

        private sealed class FakeOperPasswordVerifier : IOperPasswordVerifier
        {
            private readonly OperPasswordVerifyResult _result;

            public FakeOperPasswordVerifier(OperPasswordVerifyResult result) => _result = result;

            public OperPasswordVerifyResult Verify(string providedPassword, string storedPassword, bool requireHashed) => _result;
        }

        private sealed class TestSession : IClientSession
        {
            public string ConnectionId { get; set; } = "c1";
            public EndPoint RemoteEndPoint { get; set; } = new IPEndPoint(IPAddress.Loopback, 1234);
            public EndPoint LocalEndPoint { get; } = new IPEndPoint(IPAddress.Loopback, 6667);
            public bool IsSecureConnection => false;

            public ISet<string> EnabledCapabilities { get; } = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);

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

            public ValueTask CloseAsync(string reason, CancellationToken ct = default) => ValueTask.CompletedTask;
        }

        [Fact]
        public async Task Oper_Success_EmitsAuditEvent()
        {
            var state = new ServerState();
            state.TryAddUser(new User
            {
                ConnectionId = "c1",
                Uid = "AAA",
                Nick = "nick",
                UserName = "user",
                IsRegistered = true,
                RemoteIp = "198.51.100.30"
            });

            var opts = Options.Create(new IrcOptions
            {
                Opers = new[]
                {
                    new OperOptions { Name = "netadmin", Password = "does-not-matter", Class = "netadmin" }
                }
            });

            var audit = new CapturingAuditLogService();
            var handler = new OperHandler(opts, new FakeOperPasswordVerifier(OperPasswordVerifyResult.Ok()), audit);

            var session = new TestSession { ConnectionId = "c1", Nick = "nick", UserName = "user", IsRegistered = true };

            await handler.HandleAsync(session, new IrcMessage(null, "OPER", new[] { "netadmin", "supersecret" }, null), state, CancellationToken.None);

            Assert.Contains(session.Sent, l => l.Contains(" 381 nick "));

            Assert.Equal(1, audit.CallCount);
            Assert.Equal("OPER", audit.Action);
            Assert.Equal("AAA", audit.ActorUid);
            Assert.Equal("nick", audit.ActorNick);
            Assert.Equal("198.51.100.30", audit.SourceIp);
            Assert.Equal("netadmin", audit.Target);
            Assert.NotNull(audit.Extra);
            Assert.True(audit.Extra!.TryGetValue("success", out var success) && success is true);
            Assert.True(audit.Extra!.TryGetValue("operClass", out var cls) && cls is string sCls && sCls == "netadmin");
        }

        [Fact]
        public async Task Oper_IncorrectPassword_EmitsAuditEvent()
        {
            var state = new ServerState();
            state.TryAddUser(new User
            {
                ConnectionId = "c1",
                Uid = "AAA",
                Nick = "nick",
                UserName = "user",
                IsRegistered = true,
                RemoteIp = "198.51.100.31"
            });

            var opts = Options.Create(new IrcOptions
            {
                Opers = new[]
                {
                    new OperOptions { Name = "netadmin", Password = "stored", Class = "netadmin" }
                }
            });

            var audit = new CapturingAuditLogService();
            var handler = new OperHandler(opts, new FakeOperPasswordVerifier(OperPasswordVerifyResult.Fail(OperPasswordVerifyFailure.Incorrect)), audit);

            var session = new TestSession { ConnectionId = "c1", Nick = "nick", UserName = "user", IsRegistered = true };

            await handler.HandleAsync(session, new IrcMessage(null, "OPER", new[] { "netadmin", "wrong" }, null), state, CancellationToken.None);

            Assert.Contains(session.Sent, l => l.Contains(" 464 nick "));

            Assert.Equal(1, audit.CallCount);
            Assert.Equal("OPER", audit.Action);
            Assert.Equal("netadmin", audit.Target);
            Assert.NotNull(audit.Extra);
            Assert.True(audit.Extra!.TryGetValue("success", out var success) && success is false);
            Assert.True(audit.Extra!.TryGetValue("error", out var err) && err is string sErr && sErr == "password_incorrect");
        }

        [Fact]
        public async Task Oper_PlaintextDisallowed_EmitsAuditEvent()
        {
            var state = new ServerState();
            state.TryAddUser(new User
            {
                ConnectionId = "c1",
                Uid = "AAA",
                Nick = "nick",
                UserName = "user",
                IsRegistered = true,
                RemoteIp = "198.51.100.32"
            });

            var opts = Options.Create(new IrcOptions
            {
                OperSecurity = new OperSecurityOptions { RequireHashedPasswords = true },
                Opers = new[]
                {
                    new OperOptions { Name = "netadmin", Password = "plaintext", Class = "netadmin" }
                }
            });

            var audit = new CapturingAuditLogService();
            var handler = new OperHandler(opts, new FakeOperPasswordVerifier(OperPasswordVerifyResult.Fail(OperPasswordVerifyFailure.PlaintextDisallowed)), audit);

            var session = new TestSession { ConnectionId = "c1", Nick = "nick", UserName = "user", IsRegistered = true };

            await handler.HandleAsync(session, new IrcMessage(null, "OPER", new[] { "netadmin", "plaintext" }, null), state, CancellationToken.None);

            Assert.Contains(session.Sent, l => l.Contains("Plaintext oper passwords are disabled"));

            Assert.Equal(1, audit.CallCount);
            Assert.Equal("OPER", audit.Action);
            Assert.Equal("netadmin", audit.Target);
            Assert.NotNull(audit.Extra);
            Assert.True(audit.Extra!.TryGetValue("success", out var success) && success is false);
            Assert.True(audit.Extra!.TryGetValue("error", out var err) && err is string sErr && sErr == "plaintext_disallowed");
        }
    }
}
