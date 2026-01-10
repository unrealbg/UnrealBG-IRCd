namespace IRCd.Tests
{
    using System;
    using System.Net;
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Core.Abstractions;
    using IRCd.Core.Services;
    using IRCd.Core.State;
    using IRCd.Shared.Options;

    using Microsoft.Extensions.Logging.Abstractions;
    using Microsoft.Extensions.Options;

    using Xunit;

    public sealed class AutoDlineEscalationTests
    {
        private sealed class TestClock : IServerClock
        {
            public DateTimeOffset UtcNow { get; set; }
        }

        private sealed class RealClock : IServerClock
        {
            public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
        }

        private sealed class TestSession : IClientSession
        {
            public string ConnectionId { get; set; } = "c1";
            public EndPoint RemoteEndPoint { get; set; } = new IPEndPoint(IPAddress.Parse("198.51.100.10"), 1234);
            public EndPoint LocalEndPoint { get; set; } = new IPEndPoint(IPAddress.Loopback, 6667);
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
            public bool TryApplyUserModes(string modeString, out string appliedModes) { appliedModes = modeString; return true; }

            public void OnInboundLine() { }
            public void OnPingSent(string token) { }
            public void OnPongReceived(string? token) { }

            public ValueTask SendAsync(string line, CancellationToken ct = default) => ValueTask.CompletedTask;
            public ValueTask CloseAsync(string reason, CancellationToken ct = default) => ValueTask.CompletedTask;
        }

        [Fact]
        public async Task EscalatesDuration_WithExponentialBackoff_UpToCap()
        {
            var clock = new TestClock { UtcNow = DateTimeOffset.UtcNow };

            var options = new IrcOptions
            {
                AutoDline = new AutoDlineOptions
                {
                    Enabled = true,
                    WindowSeconds = 60,
                    Threshold = 3,
                    BaseDurationSeconds = 10,
                    BackoffFactor = 2,
                    MaxDurationSeconds = 25,
                }
            };

            var repo = new InMemoryBanRepository();
            var bans = new BanService(repo, NullLogger<BanService>.Instance);
            var service = new AutoDlineService(new OptionsMonitorStub<IrcOptions>(options), clock, bans, NullLogger<AutoDlineService>.Instance);

            var state = new ServerState();
            var session = new TestSession();

            // First strike: 3 offenses -> 10s
            Assert.False(await service.ObserveRateLimitAsync(session, state, CancellationToken.None));
            Assert.False(await service.ObserveRateLimitAsync(session, state, CancellationToken.None));
            Assert.True(await service.ObserveRateLimitAsync(session, state, CancellationToken.None));

            var match1 = await bans.TryMatchIpAsync(IPAddress.Parse("198.51.100.10"), CancellationToken.None);
            Assert.NotNull(match1);
            Assert.NotNull(match1!.ExpiresAt);
            Assert.InRange((match1.ExpiresAt!.Value - clock.UtcNow).TotalSeconds, 9, 11);

            // Second strike: 3 more offenses -> 20s
            clock.UtcNow = clock.UtcNow.AddSeconds(1);
            Assert.False(await service.ObserveFloodAsync(session, state, CancellationToken.None));
            Assert.False(await service.ObserveFloodAsync(session, state, CancellationToken.None));
            Assert.True(await service.ObserveFloodAsync(session, state, CancellationToken.None));

            var match2 = await bans.TryMatchIpAsync(IPAddress.Parse("198.51.100.10"), CancellationToken.None);
            Assert.NotNull(match2);
            Assert.NotNull(match2!.ExpiresAt);
            Assert.InRange((match2.ExpiresAt!.Value - clock.UtcNow).TotalSeconds, 19, 21);

            // Third strike: would be 40s, capped to 25s
            clock.UtcNow = clock.UtcNow.AddSeconds(1);
            Assert.False(await service.ObserveRateLimitAsync(session, state, CancellationToken.None));
            Assert.False(await service.ObserveRateLimitAsync(session, state, CancellationToken.None));
            Assert.True(await service.ObserveRateLimitAsync(session, state, CancellationToken.None));

            var match3 = await bans.TryMatchIpAsync(IPAddress.Parse("198.51.100.10"), CancellationToken.None);
            Assert.NotNull(match3);
            Assert.NotNull(match3!.ExpiresAt);
            Assert.InRange((match3.ExpiresAt!.Value - clock.UtcNow).TotalSeconds, 24, 26);

            Assert.Equal(3, service.AutoDlinesTotal);
            var top = service.GetTopOffenders(1);
            Assert.Single(top);
            Assert.Equal("198.51.100.0/24", top[0].Prefix);
        }

        [Fact]
        public async Task WhitelistCidrs_ExemptsFromAutoDline()
        {
            var clock = new TestClock { UtcNow = DateTimeOffset.UtcNow };

            var options = new IrcOptions
            {
                AutoDline = new AutoDlineOptions
                {
                    Enabled = true,
                    WindowSeconds = 60,
                    Threshold = 2,
                    BaseDurationSeconds = 10,
                    WhitelistCidrs = new[] { "203.0.113.0/24" },
                }
            };

            var repo = new InMemoryBanRepository();
            var bans = new BanService(repo, NullLogger<BanService>.Instance);
            var service = new AutoDlineService(new OptionsMonitorStub<IrcOptions>(options), clock, bans);

            var state = new ServerState();
            var session = new TestSession { RemoteEndPoint = new IPEndPoint(IPAddress.Parse("203.0.113.7"), 1234) };

            Assert.False(await service.ObserveRateLimitAsync(session, state, CancellationToken.None));
            Assert.False(await service.ObserveRateLimitAsync(session, state, CancellationToken.None));

            var match = await bans.TryMatchIpAsync(IPAddress.Parse("203.0.113.7"), CancellationToken.None);
            Assert.Null(match);
            Assert.Equal(0, service.AutoDlinesTotal);
        }

        [Fact]
        public async Task Opers_AreExempt()
        {
            var clock = new TestClock { UtcNow = DateTimeOffset.UtcNow };

            var options = new IrcOptions
            {
                AutoDline = new AutoDlineOptions
                {
                    Enabled = true,
                    WindowSeconds = 60,
                    Threshold = 2,
                    BaseDurationSeconds = 10,
                }
            };

            var repo = new InMemoryBanRepository();
            var bans = new BanService(repo, NullLogger<BanService>.Instance);
            var service = new AutoDlineService(new OptionsMonitorStub<IrcOptions>(options), clock, bans);

            var state = new ServerState();
            state.TryAddUser(new User { ConnectionId = "c1", Nick = "oper", UserName = "o", Host = "h", IsRegistered = true, OperName = "oper", Modes = UserModes.Operator });

            var session = new TestSession { ConnectionId = "c1" };

            Assert.False(await service.ObserveFloodAsync(session, state, CancellationToken.None));
            Assert.False(await service.ObserveFloodAsync(session, state, CancellationToken.None));

            var match = await bans.TryMatchIpAsync(IPAddress.Parse("198.51.100.10"), CancellationToken.None);
            Assert.Null(match);
            Assert.Equal(0, service.AutoDlinesTotal);
        }

        [Fact]
        public async Task Expiry_AllowsReoffenseAfterBanExpires()
        {
            var options = new IrcOptions
            {
                AutoDline = new AutoDlineOptions
                {
                    Enabled = true,
                    WindowSeconds = 60,
                    Threshold = 2,
                    BaseDurationSeconds = 1,
                    BackoffFactor = 2,
                    MaxDurationSeconds = 10,
                }
            };

        var clock = new RealClock();
            var repo = new InMemoryBanRepository();
            var bans = new BanService(repo, NullLogger<BanService>.Instance);
            var service = new AutoDlineService(new OptionsMonitorStub<IrcOptions>(options), clock, bans);

            var state = new ServerState();
            var session = new TestSession();

            Assert.False(await service.ObserveRateLimitAsync(session, state, CancellationToken.None));
            Assert.True(await service.ObserveRateLimitAsync(session, state, CancellationToken.None));

            var match1 = await bans.TryMatchIpAsync(IPAddress.Parse("198.51.100.10"), CancellationToken.None);
            Assert.NotNull(match1);

            // Wait for expiry (BanEntry.IsActive uses real UtcNow).
            await Task.Delay(TimeSpan.FromMilliseconds(1200));
            await bans.CleanupExpiredAsync(CancellationToken.None);

            var matchExpired = await bans.TryMatchIpAsync(IPAddress.Parse("198.51.100.10"), CancellationToken.None);
            Assert.Null(matchExpired);

            // Re-offense should be possible again.
            Assert.False(await service.ObserveFloodAsync(session, state, CancellationToken.None));
            Assert.True(await service.ObserveFloodAsync(session, state, CancellationToken.None));

            var match2 = await bans.TryMatchIpAsync(IPAddress.Parse("198.51.100.10"), CancellationToken.None);
            Assert.NotNull(match2);
            Assert.True(service.AutoDlinesTotal >= 2);
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
