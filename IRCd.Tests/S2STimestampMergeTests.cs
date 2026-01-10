namespace IRCd.Tests
{
    using System;
    using System.Net;
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

    public sealed class S2STimestampMergeTests
    {
        [Fact]
        public async Task IncomingMemberPrivilege_WithHigherChannelTs_IsIgnored()
        {
            var state = new ServerState();
            var sessions = new FakeSessionRegistry();
            var routing = new RoutingService(sessions, new IrcFormatter());
            var silence = new SilenceService();

            var options = new IrcOptions
            {
                ServerInfo = new ServerInfoOptions { Name = "local", Sid = "001", Description = "d" },
                Links = new[]
                {
                    new LinkOptions { Name = "remote", Sid = "002", Password = "pw", Outbound = false, UserSync = true }
                }
            };

            var watch = new WatchService(Options.Create(options), routing);
            var svc = new ServerLinkService(NullLogger<ServerLinkService>.Instance, new OptionsMonitorStub<IrcOptions>(options), state, routing, sessions, silence, watch);

            state.TryAddRemoteUser(new User
            {
                ConnectionId = "uid:002AAAAAA",
                Uid = "002AAAAAA",
                Nick = "Nick",
                UserName = "u",
                Host = "h",
                IsRegistered = true,
                IsRemote = true,
                RemoteSid = "002",
                NickTs = 100
            });

            var ch = state.GetOrCreateChannel("#c");
            ch.CreatedTs = 100;
            state.TryJoinChannel("uid:002AAAAAA", "Nick", "#c");
            ch.TryUpdateMemberPrivilege("uid:002AAAAAA", ChannelPrivilege.Normal);

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

            // New-format MEMBER: MEMBER <channel> <ts> <uid> <priv>
            link.Enqueue("MEMBER #c 200 002AAAAAA 3");

            start = DateTime.UtcNow;
            while ((DateTime.UtcNow - start) < TimeSpan.FromMilliseconds(500))
            {
                if (ch.GetPrivilege("uid:002AAAAAA") == ChannelPrivilege.Normal)
                {
                    break;
                }

                await Task.Delay(5, cts.Token);
            }

            Assert.Equal(ChannelPrivilege.Normal, ch.GetPrivilege("uid:002AAAAAA"));

            cts.Cancel();
            link.Complete();
            try { await linkTask; } catch (OperationCanceledException) { }
        }

        [Fact]
        public async Task IncomingMemberPrivilege_WithLowerChannelTs_ResetsAndApplies()
        {
            var state = new ServerState();
            var sessions = new FakeSessionRegistry();
            var routing = new RoutingService(sessions, new IrcFormatter());
            var silence = new SilenceService();

            var options = new IrcOptions
            {
                ServerInfo = new ServerInfoOptions { Name = "local", Sid = "001", Description = "d" },
                Links = new[]
                {
                    new LinkOptions { Name = "remote", Sid = "002", Password = "pw", Outbound = false, UserSync = true }
                }
            };

            var watch = new WatchService(Options.Create(options), routing);
            var svc = new ServerLinkService(NullLogger<ServerLinkService>.Instance, new OptionsMonitorStub<IrcOptions>(options), state, routing, sessions, silence, watch);

            state.TryAddRemoteUser(new User
            {
                ConnectionId = "uid:002AAAAAA",
                Uid = "002AAAAAA",
                Nick = "Nick",
                UserName = "u",
                Host = "h",
                IsRegistered = true,
                IsRemote = true,
                RemoteSid = "002",
                NickTs = 200
            });

            var ch = state.GetOrCreateChannel("#c");
            ch.CreatedTs = 200;
            state.TryJoinChannel("uid:002AAAAAA", "Nick", "#c");
            ch.TryUpdateMemberPrivilege("uid:002AAAAAA", ChannelPrivilege.Op);

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

            // Lower TS should win, reset channel, then apply voice.
            link.Enqueue("MEMBER #c 100 002AAAAAA 1");

            start = DateTime.UtcNow;
            while ((DateTime.UtcNow - start) < TimeSpan.FromMilliseconds(800))
            {
                if (ch.CreatedTs == 100 && ch.GetPrivilege("uid:002AAAAAA") == ChannelPrivilege.Voice)
                {
                    break;
                }

                await Task.Delay(5, cts.Token);
            }

            Assert.Equal(100, ch.CreatedTs);
            Assert.Equal(ChannelPrivilege.Voice, ch.GetPrivilege("uid:002AAAAAA"));

            cts.Cancel();
            link.Complete();
            try { await linkTask; } catch (OperationCanceledException) { }
        }

        [Fact]
        public async Task UserNickCollision_OnBurst_LowerNickTsWins()
        {
            var state = new ServerState();
            var sessions = new FakeSessionRegistry();
            var routing = new RoutingService(sessions, new IrcFormatter());
            var silence = new SilenceService();

            var options = new IrcOptions
            {
                ServerInfo = new ServerInfoOptions { Name = "local", Sid = "001", Description = "d" },
                Links = new[]
                {
                    new LinkOptions { Name = "remote", Sid = "002", Password = "pw", Outbound = false, UserSync = true }
                }
            };

            var watch = new WatchService(Options.Create(options), routing);
            var svc = new ServerLinkService(NullLogger<ServerLinkService>.Instance, new OptionsMonitorStub<IrcOptions>(options), state, routing, sessions, silence, watch);

            // Existing user holds Dup with older TS.
            state.TryAddRemoteUser(new User
            {
                ConnectionId = "uid:003BBBBBB",
                Uid = "003BBBBBB",
                Nick = "Dup",
                NickTs = 100,
                UserName = "u",
                Host = "h",
                IsRegistered = true,
                IsRemote = true,
                RemoteSid = "003"
            });

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

            // Incoming tries to introduce Dup with newer TS; should be forced to collision nick.
            link.Enqueue("USER 002AAAAAA Dup u h 0 200 :Real");

            start = DateTime.UtcNow;
            while ((DateTime.UtcNow - start) < TimeSpan.FromMilliseconds(800))
            {
                if (state.TryGetUserByUid("002AAAAAA", out var u) && u is not null)
                {
                    break;
                }

                await Task.Delay(5, cts.Token);
            }

            Assert.True(state.TryGetUserByUid("003BBBBBB", out var existing) && existing is not null);
            Assert.Equal("Dup", existing!.Nick);

            Assert.True(state.TryGetUserByUid("002AAAAAA", out var incoming) && incoming is not null);
            Assert.Equal("uidAAAAAA", incoming!.Nick);

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
