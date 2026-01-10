namespace IRCd.Core.Services;

using System.Collections.Concurrent;
using System.Net;

using IRCd.Core.Abstractions;
using IRCd.Core.State;
using IRCd.Shared.Options;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

public sealed class ConnectionPrecheckPipeline : IConnectionPrecheckPipeline
{
    private readonly IOptionsMonitor<IrcOptions> _options;
    private readonly IServerClock _clock;
    private readonly IDnsResolver _dns;
    private readonly BanService _bans;
    private readonly ILogger<ConnectionPrecheckPipeline> _logger;

    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);

    public ConnectionPrecheckPipeline(
        IOptionsMonitor<IrcOptions> options,
        IServerClock clock,
        IDnsResolver dns,
        BanService bans,
        ILogger<ConnectionPrecheckPipeline> logger)
    {
        _options = options;
        _clock = clock;
        _dns = dns;
        _bans = bans;
        _logger = logger;
    }

    public async Task<ConnectionPrecheckResult> CheckAsync(ConnectionPrecheckContext context, CancellationToken ct)
    {
        var cfg = _options.CurrentValue.ConnectionPrecheck;
        if (!cfg.Enabled)
        {
            return new ConnectionPrecheckResult(true, null);
        }

        if (cfg.SkipNonPublicIps && !IsPublicRoutable(context.RemoteIp))
        {
            return new ConnectionPrecheckResult(true, null);
        }

        var key = context.RemoteIp.ToString();
        var now = _clock.UtcNow;

        if (_cache.TryGetValue(key, out var cached) && cached.ExpiresAtUtc > now)
        {
            return cached.Result;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(1, cfg.TimeoutMs)));

        var result = await RunChecksAsync(context, cfg, cts.Token);

        TryCache(key, result, cfg, now);

        return result;
    }

    private async Task<ConnectionPrecheckResult> RunChecksAsync(ConnectionPrecheckContext ctx, ConnectionPrecheckOptions cfg, CancellationToken ct)
    {
        var all = new (string Category, DnsblZoneOptions[] Zones)[]
        {
            ("DNSBL", cfg.Dnsbl),
            ("Tor", cfg.TorDnsbl),
            ("VPN", cfg.VpnDnsbl),
        };

        foreach (var (category, zones) in all)
        {
            if (zones is null || zones.Length == 0)
            {
                continue;
            }

            foreach (var z in zones)
            {
                var zone = (z.Zone ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(zone))
                {
                    continue;
                }

                var query = BuildDnsblQuery(ctx.RemoteIp, zone);
                if (query is null)
                {
                    continue;
                }

                var listed = false;
                try
                {
                    listed = await _dns.HasAnyAddressAsync(query, ct);
                }
                catch (OperationCanceledException)
                {
                    return new ConnectionPrecheckResult(true, null);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Precheck DNS error for {Query}", query);
                    listed = false;
                }

                if (!listed)
                {
                    continue;
                }

                var reason = string.IsNullOrWhiteSpace(z.Reason) ? "Listed" : z.Reason.Trim();
                var message = $"Connection blocked ({category}: {zone}) - {reason}";

                if (z.TempDlineSeconds > 0)
                {
                    try
                    {
                        var expiresAt = _clock.UtcNow.AddSeconds(z.TempDlineSeconds);
                        await _bans.AddAsync(new BanEntry
                        {
                            Type = BanType.DLINE,
                            Mask = ctx.RemoteIp.ToString(),
                            Reason = message,
                            SetBy = "precheck",
                            ExpiresAt = expiresAt,
                        }, ct);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to add temporary DLINE for {Ip}", ctx.RemoteIp);
                    }
                }

                return new ConnectionPrecheckResult(false, message);
            }
        }

        return new ConnectionPrecheckResult(true, null);
    }

    private void TryCache(string key, ConnectionPrecheckResult result, ConnectionPrecheckOptions cfg, DateTimeOffset now)
    {
        try
        {
            var ttlSeconds = result.Allowed
                ? Math.Max(0, cfg.CacheAllowTtlSeconds)
                : Math.Max(0, cfg.CacheBlockTtlSeconds);

            if (ttlSeconds <= 0)
            {
                return;
            }

            if (_cache.Count >= Math.Max(100, cfg.CacheMaxEntries))
            {
                foreach (var kv in _cache)
                {
                    if (kv.Value.ExpiresAtUtc <= now)
                    {
                        _cache.TryRemove(kv.Key, out _);
                    }
                }

                if (_cache.Count >= Math.Max(100, cfg.CacheMaxEntries))
                {
                    return;
                }
            }

            _cache[key] = new CacheEntry(result, now.AddSeconds(ttlSeconds));
        }
        catch
        {
            // Cache is best-effort.
        }
    }

    private static string? BuildDnsblQuery(IPAddress ip, string zone)
    {
        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return $"{b[3]}.{b[2]}.{b[1]}.{b[0]}.{zone}";
        }

        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            var bytes = ip.GetAddressBytes();
            var chars = new char[bytes.Length * 2];
            for (var i = 0; i < bytes.Length; i++)
            {
                var hex = bytes[i].ToString("x2");
                chars[i * 2] = hex[0];
                chars[i * 2 + 1] = hex[1];
            }

            var sb = new System.Text.StringBuilder(chars.Length * 2);
            for (var i = chars.Length - 1; i >= 0; i--)
            {
                sb.Append(chars[i]);
                sb.Append('.');
            }

            sb.Append(zone);
            return sb.ToString();
        }

        return null;
    }

    private static bool IsPublicRoutable(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip))
        {
            return false;
        }

        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();

            if (b[0] == 10)
            {
                return false;
            }

            if (b[0] == 127)
            {
                return false;
            }

            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
            {
                return false;
            }

            if (b[0] == 192 && b[1] == 168)
            {
                return false;
            }

            if (b[0] == 169 && b[1] == 254)
            {
                return false;
            }

            if (b[0] == 0)
            {
                return false;
            }

            return true;
        }

        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal)
            {
                return false;
            }

            var b = ip.GetAddressBytes();
            if ((b[0] & 0xFE) == 0xFC)
            {
                return false;
            }

            return true;
        }

        return false;
    }

    private sealed record CacheEntry(ConnectionPrecheckResult Result, DateTimeOffset ExpiresAtUtc);
}
