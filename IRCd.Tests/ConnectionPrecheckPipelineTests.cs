using System;
using System.Collections.Generic;
using System.Linq;
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

public sealed class ConnectionPrecheckPipelineTests
{
    [Fact]
    public async Task Disabled_DoesNotQueryDns_Allows()
    {
        var opts = new IrcOptions
        {
            ConnectionPrecheck = new ConnectionPrecheckOptions
            {
                Enabled = false,
                Dnsbl = new[] { new DnsblZoneOptions { Zone = "dnsbl.example", Reason = "Listed" } }
            }
        };

        var resolver = new FakeDnsResolver(_ => Task.FromResult(true));
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var bans = new BanService(new InMemoryBanRepository(), NullLogger<BanService>.Instance);

        var sut = new ConnectionPrecheckPipeline(new OptionsMonitorStub<IrcOptions>(opts), clock, resolver, bans, NullLogger<ConnectionPrecheckPipeline>.Instance);

        var res = await sut.CheckAsync(new ConnectionPrecheckContext(IPAddress.Parse("1.2.3.4"), new IPEndPoint(IPAddress.Loopback, 6667), Secure: false), CancellationToken.None);

        Assert.True(res.Allowed);
        Assert.Equal(0, resolver.CallCount);
    }

    [Fact]
    public async Task Listed_CachesBlock_ByIp()
    {
        var opts = new IrcOptions
        {
            ConnectionPrecheck = new ConnectionPrecheckOptions
            {
                Enabled = true,
                TimeoutMs = 200,
                CacheBlockTtlSeconds = 300,
                CacheAllowTtlSeconds = 300,
                SkipNonPublicIps = false,
                Dnsbl = new[] { new DnsblZoneOptions { Zone = "dnsbl.example", Reason = "Bad" } }
            }
        };

        var resolver = new FakeDnsResolver(q => Task.FromResult(true));
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var bans = new BanService(new InMemoryBanRepository(), NullLogger<BanService>.Instance);

        var sut = new ConnectionPrecheckPipeline(new OptionsMonitorStub<IrcOptions>(opts), clock, resolver, bans, NullLogger<ConnectionPrecheckPipeline>.Instance);

        var ip = IPAddress.Parse("1.2.3.4");

        var r1 = await sut.CheckAsync(new ConnectionPrecheckContext(ip, new IPEndPoint(IPAddress.Loopback, 6667), Secure: false), CancellationToken.None);
        var r2 = await sut.CheckAsync(new ConnectionPrecheckContext(ip, new IPEndPoint(IPAddress.Loopback, 6667), Secure: false), CancellationToken.None);

        Assert.False(r1.Allowed);
        Assert.False(r2.Allowed);
        Assert.Equal(1, resolver.CallCount);

        Assert.Contains("dnsbl.example", r1.RejectMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NotListed_CachesAllow_ByIp()
    {
        var opts = new IrcOptions
        {
            ConnectionPrecheck = new ConnectionPrecheckOptions
            {
                Enabled = true,
                TimeoutMs = 200,
                CacheAllowTtlSeconds = 300,
                CacheBlockTtlSeconds = 300,
                SkipNonPublicIps = false,
                Dnsbl = new[] { new DnsblZoneOptions { Zone = "dnsbl.example", Reason = "Bad" } }
            }
        };

        var resolver = new FakeDnsResolver(q => Task.FromResult(false));
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var bans = new BanService(new InMemoryBanRepository(), NullLogger<BanService>.Instance);

        var sut = new ConnectionPrecheckPipeline(new OptionsMonitorStub<IrcOptions>(opts), clock, resolver, bans, NullLogger<ConnectionPrecheckPipeline>.Instance);

        var ip = IPAddress.Parse("1.2.3.4");

        var r1 = await sut.CheckAsync(new ConnectionPrecheckContext(ip, new IPEndPoint(IPAddress.Loopback, 6667), Secure: false), CancellationToken.None);
        var r2 = await sut.CheckAsync(new ConnectionPrecheckContext(ip, new IPEndPoint(IPAddress.Loopback, 6667), Secure: false), CancellationToken.None);

        Assert.True(r1.Allowed);
        Assert.True(r2.Allowed);
        Assert.Equal(1, resolver.CallCount);
    }

    [Fact]
    public async Task AllowCache_Expires_AndRequeries()
    {
        var start = DateTimeOffset.UtcNow;

        var opts = new IrcOptions
        {
            ConnectionPrecheck = new ConnectionPrecheckOptions
            {
                Enabled = true,
                TimeoutMs = 200,
                CacheAllowTtlSeconds = 1,
                CacheBlockTtlSeconds = 300,
                SkipNonPublicIps = false,
                Dnsbl = new[] { new DnsblZoneOptions { Zone = "dnsbl.example", Reason = "Bad" } }
            }
        };

        var resolver = new FakeDnsResolver(q => Task.FromResult(false));
        var clock = new FakeClock(start);
        var bans = new BanService(new InMemoryBanRepository(), NullLogger<BanService>.Instance);

        var sut = new ConnectionPrecheckPipeline(new OptionsMonitorStub<IrcOptions>(opts), clock, resolver, bans, NullLogger<ConnectionPrecheckPipeline>.Instance);

        var ip = IPAddress.Parse("1.2.3.4");

        var r1 = await sut.CheckAsync(new ConnectionPrecheckContext(ip, new IPEndPoint(IPAddress.Loopback, 6667), Secure: false), CancellationToken.None);
        Assert.True(r1.Allowed);
        Assert.Equal(1, resolver.CallCount);

        clock.UtcNow = start.AddSeconds(2);

        var r2 = await sut.CheckAsync(new ConnectionPrecheckContext(ip, new IPEndPoint(IPAddress.Loopback, 6667), Secure: false), CancellationToken.None);
        Assert.True(r2.Allowed);
        Assert.Equal(2, resolver.CallCount);
    }

    [Fact]
    public async Task Timeout_FailsOpen_Allows()
    {
        var opts = new IrcOptions
        {
            ConnectionPrecheck = new ConnectionPrecheckOptions
            {
                Enabled = true,
                TimeoutMs = 10,
                CacheAllowTtlSeconds = 0,
                CacheBlockTtlSeconds = 0,
                SkipNonPublicIps = false,
                Dnsbl = new[] { new DnsblZoneOptions { Zone = "dnsbl.example", Reason = "Bad" } }
            }
        };

        var resolver = new FakeDnsResolver(async (q, ct) =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(200), ct);
            return true;
        });

        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var bans = new BanService(new InMemoryBanRepository(), NullLogger<BanService>.Instance);

        var sut = new ConnectionPrecheckPipeline(new OptionsMonitorStub<IrcOptions>(opts), clock, resolver, bans, NullLogger<ConnectionPrecheckPipeline>.Instance);

        var res = await sut.CheckAsync(new ConnectionPrecheckContext(IPAddress.Parse("1.2.3.4"), new IPEndPoint(IPAddress.Loopback, 6667), Secure: false), CancellationToken.None);
        Assert.True(res.Allowed);
    }

    [Fact]
    public async Task Listed_WithTempDline_AddsTemporaryDline()
    {
        var repo = new InMemoryBanRepository();
        var bans = new BanService(repo, NullLogger<BanService>.Instance);

        var opts = new IrcOptions
        {
            ConnectionPrecheck = new ConnectionPrecheckOptions
            {
                Enabled = true,
                TimeoutMs = 200,
                CacheAllowTtlSeconds = 0,
                CacheBlockTtlSeconds = 0,
                SkipNonPublicIps = false,
                Dnsbl = new[]
                {
                    new DnsblZoneOptions
                    {
                        Zone = "dnsbl.example",
                        Reason = "Listed",
                        TempDlineSeconds = 60
                    }
                }
            }
        };

        var resolver = new FakeDnsResolver(q => Task.FromResult(true));
        var clock = new FakeClock(DateTimeOffset.UtcNow);

        var sut = new ConnectionPrecheckPipeline(new OptionsMonitorStub<IrcOptions>(opts), clock, resolver, bans, NullLogger<ConnectionPrecheckPipeline>.Instance);

        var ip = IPAddress.Parse("1.2.3.4");
        var res = await sut.CheckAsync(new ConnectionPrecheckContext(ip, new IPEndPoint(IPAddress.Loopback, 6667), Secure: false), CancellationToken.None);

        Assert.False(res.Allowed);

        var dlines = await repo.GetActiveByTypeAsync(BanType.DLINE, CancellationToken.None);
        Assert.Contains(dlines, b => string.Equals(b.Mask, "1.2.3.4", StringComparison.Ordinal));
    }

    private sealed class FakeDnsResolver : IDnsResolver
    {
        private readonly Func<string, CancellationToken, Task<bool>> _impl;

        public int CallCount => _calls.Count;

        private readonly List<string> _queries = new();
        private readonly List<int> _calls = new();

        public IReadOnlyList<string> Queries
        {
            get
            {
                lock (_queries)
                {
                    return _queries.ToList();
                }
            }
        }

        public FakeDnsResolver(Func<string, Task<bool>> impl)
            : this((q, _) => impl(q))
        {
        }

        public FakeDnsResolver(Func<string, CancellationToken, Task<bool>> impl)
        {
            _impl = impl;
        }

        public Task<bool> HasAnyAddressAsync(string fqdn, CancellationToken ct)
        {
            lock (_queries)
            {
                _queries.Add(fqdn);
                _calls.Add(1);
            }

            return _impl(fqdn, ct);
        }
    }

    private sealed class FakeClock : IServerClock
    {
        public FakeClock(DateTimeOffset start)
        {
            UtcNow = start;
        }

        public DateTimeOffset UtcNow { get; set; }
    }

    private sealed class OptionsMonitorStub<T> : IOptionsMonitor<T> where T : class
    {
        public OptionsMonitorStub(T value)
        {
            CurrentValue = value;
        }

        public T CurrentValue { get; }

        public T Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
