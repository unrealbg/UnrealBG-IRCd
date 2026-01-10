namespace IRCd.Tests
{
    using System;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Core.Protocol;
    using IRCd.Core.Services;
    using IRCd.Core.State;
    using IRCd.Shared.Options;
    using IRCd.Tests.TestDoubles;

    using Microsoft.Extensions.Logging.Abstractions;
    using Microsoft.Extensions.Options;

    using Xunit;

    public sealed class S2SUserReplaySafetyTests
    {
        [Fact]
        public async Task IncomingLink_DuplicateUserFromSameOrigin_IsIgnored_NotFatal()
        {
            var state = new ServerState();
            var sessions = new FakeSessionRegistry();
            var routing = new RoutingService(sessions, new IrcFormatter());
            var silence = new SilenceService();

            var options = new IrcOptions
            {
                ServerInfo = new ServerInfoOptions { Name = "a", Sid = "001", Description = "A" },
                Links = new[]
                {
                    new LinkOptions { Name = "b", Sid = "002", Password = "pw", Outbound = false, UserSync = true }
                }
            };

            var watch = new WatchService(Options.Create(options), routing);
            var svc = new ServerLinkService(NullLogger<ServerLinkService>.Instance, new OptionsMonitorStub<IrcOptions>(options), state, routing, sessions, silence, watch);

            var sess = new ControlledServerLinkSession("link")
            {
                UserSyncEnabled = true
            };

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

            var linkTask = svc.HandleIncomingLinkAsync(sess, cts.Token);

            // Handshake + duplicate USER.
            sess.Enqueue("PASS pw :TS 1");
            sess.Enqueue("SERVER b 002 :B");

            var start = DateTime.UtcNow;
            while (!sess.IsAuthenticated && (DateTime.UtcNow - start) < TimeSpan.FromSeconds(1))
            {
                await Task.Delay(5, cts.Token);
            }

            Assert.True(sess.IsAuthenticated, string.Join("\n", sess.Outgoing));

            sess.Enqueue("USER abcdEF Bob b hB 0 123 :Bob User");
            sess.Enqueue("USER abcdEF Bob b hB 0 123 :Bob User");

            start = DateTime.UtcNow;
            while ((DateTime.UtcNow - start) < TimeSpan.FromMilliseconds(800))
            {
                if (state.TryGetUserByUid("abcdEF", out var maybeUser) && maybeUser is not null)
                {
                    break;
                }

                await Task.Delay(5, cts.Token);
            }

            Assert.DoesNotContain(sess.Outgoing, l => l.StartsWith("ERROR :UID collision", StringComparison.OrdinalIgnoreCase));

            Assert.True(state.TryGetUserByUid("abcdEF", out var u) && u is not null);
            Assert.True(u!.IsRemote);
            Assert.Equal("002", u.RemoteSid);

            // Should not have duplicated users.
            var matches = state.GetUsersSnapshot().Where(x => string.Equals(x.Uid, "abcdEF", StringComparison.OrdinalIgnoreCase)).ToArray();
            Assert.Single(matches);

            cts.Cancel();
            sess.Complete();
            try { await linkTask; } catch (OperationCanceledException) { }
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
