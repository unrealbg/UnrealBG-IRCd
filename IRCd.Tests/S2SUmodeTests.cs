namespace IRCd.Tests
{
    using System;
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

    public sealed class S2SUmodeTests
    {
        [Fact]
        public async Task IncomingUMode_SetsInvisibleOnRemoteUser()
        {
            var state = new ServerState();
            var sessions = new FakeSessionRegistry();
            var routing = new RoutingService(sessions, new IrcFormatter());
            var silence = new SilenceService();

            var opts = Options.Create(new IrcOptions
            {
                ServerInfo = new ServerInfoOptions { Name = "local", Sid = "001", Description = "d" },
                Links = new[] { new LinkOptions { Name = "remote", Sid = "002", Password = "pw", Outbound = false, UserSync = true } }
            });

            var watch = new WatchService(opts, routing);
            var svc = new ServerLinkService(NullLogger<ServerLinkService>.Instance, new OptionsMonitorStub<IrcOptions>(opts.Value), state, routing, sessions, silence, watch);

            state.TryAddRemoteUser(new User { ConnectionId = "uid:002AAAAAA", Uid = "002AAAAAA", Nick = "Nick", UserName = "u", Host = "h", IsRegistered = true, IsRemote = true, RemoteSid = "002" });

            var link = new ControlledServerLinkSession("conn1");
            link.Enqueue("PASS pw :TS 1");
            link.Enqueue("SERVER remote 002 :r");

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var linkTask = svc.HandleIncomingLinkAsync(link, cts.Token);

            var start = DateTime.UtcNow;
            while (!link.IsAuthenticated && (DateTime.UtcNow - start) < TimeSpan.FromSeconds(1))
            {
                await Task.Delay(5, cts.Token);
            }

            Assert.True(link.IsAuthenticated);

            link.Enqueue("UMODE deadbeef 002 002AAAAAA +i");

            start = DateTime.UtcNow;
            while ((DateTime.UtcNow - start) < TimeSpan.FromSeconds(1))
            {
                if (state.TryGetUserByUid("002AAAAAA", out var u) && u is not null && u.Modes.HasFlag(UserModes.Invisible))
                {
                    break;
                }

                await Task.Delay(5, cts.Token);
            }

            Assert.True(state.TryGetUserByUid("002AAAAAA", out var finalUser) && finalUser is not null);
            Assert.True(finalUser!.Modes.HasFlag(UserModes.Invisible));

            cts.Cancel();
            link.Complete();
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
