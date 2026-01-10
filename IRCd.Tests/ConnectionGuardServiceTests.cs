namespace IRCd.Tests
{
    using System;
    using System.Net;

    using IRCd.Core.Abstractions;
    using IRCd.Core.Services;
    using IRCd.Shared.Options;

    using Microsoft.Extensions.Options;

    using Xunit;

    public sealed class ConnectionGuardServiceTests
    {
        [Fact]
        public void TryAcceptNewConnection_UsesSeparateTlsAndPlainBuckets()
        {
            var clock = new FakeClock(new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero));

            var options = new IrcOptions
            {
                ConnectionGuard = new ConnectionGuardOptions
                {
                    Enabled = true,
                    WindowSeconds = 60,
                    MaxConnectionsPerWindowPerIp = 1,
                    MaxConnectionsPerWindowPerIpTls = 1,
                    MaxTlsHandshakesPerWindowPerIp = 10,
                    MaxActiveConnectionsPerIp = 100,
                    MaxUnregisteredPerIp = 100,
                    GlobalMaxActiveConnections = 0,
                }
            };

            var svc = new ConnectionGuardService(new OptionsMonitorStub<IrcOptions>(options), clock);
            var ip = IPAddress.Parse("203.0.113.5");

            Assert.True(svc.TryAcceptNewConnection(ip, secure: false, out _));
            Assert.False(svc.TryAcceptNewConnection(ip, secure: false, out _));

            Assert.True(svc.TryAcceptNewConnection(ip, secure: true, out _));
            Assert.False(svc.TryAcceptNewConnection(ip, secure: true, out _));
        }

        [Fact]
        public void TryStartTlsHandshake_IsRateLimitedPerIp()
        {
            var clock = new FakeClock(new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero));

            var options = new IrcOptions
            {
                ConnectionGuard = new ConnectionGuardOptions
                {
                    Enabled = true,
                    WindowSeconds = 10,
                    MaxTlsHandshakesPerWindowPerIp = 2,
                    MaxConnectionsPerWindowPerIp = 100,
                    MaxConnectionsPerWindowPerIpTls = 100,
                    MaxActiveConnectionsPerIp = 100,
                    MaxUnregisteredPerIp = 100,
                }
            };

            var svc = new ConnectionGuardService(new OptionsMonitorStub<IrcOptions>(options), clock);
            var ip = IPAddress.Parse("198.51.100.7");

            Assert.True(svc.TryStartTlsHandshake(ip, out _));
            Assert.True(svc.TryStartTlsHandshake(ip, out _));
            Assert.False(svc.TryStartTlsHandshake(ip, out _));

            clock.Advance(TimeSpan.FromSeconds(11));

            Assert.True(svc.TryStartTlsHandshake(ip, out _));
        }

        [Fact]
        public void GlobalMaxActiveConnections_RejectsAndReleases()
        {
            var clock = new FakeClock(new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero));

            var options = new IrcOptions
            {
                ConnectionGuard = new ConnectionGuardOptions
                {
                    Enabled = true,
                    WindowSeconds = 60,
                    MaxConnectionsPerWindowPerIp = 100,
                    MaxConnectionsPerWindowPerIpTls = 100,
                    MaxTlsHandshakesPerWindowPerIp = 100,
                    MaxActiveConnectionsPerIp = 100,
                    MaxUnregisteredPerIp = 100,
                    GlobalMaxActiveConnections = 1,
                }
            };

            var svc = new ConnectionGuardService(new OptionsMonitorStub<IrcOptions>(options), clock);

            var ip1 = IPAddress.Parse("203.0.113.1");
            var ip2 = IPAddress.Parse("203.0.113.2");

            Assert.True(svc.TryAcceptNewConnection(ip1, secure: false, out _));
            Assert.False(svc.TryAcceptNewConnection(ip2, secure: false, out _));

            svc.ReleaseActive(ip1);

            Assert.True(svc.TryAcceptNewConnection(ip2, secure: false, out _));
        }

        private sealed class FakeClock : IServerClock
        {
            public FakeClock(DateTimeOffset initialUtcNow) => UtcNow = initialUtcNow;

            public DateTimeOffset UtcNow { get; private set; }

            public void Advance(TimeSpan delta) => UtcNow = UtcNow.Add(delta);
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
