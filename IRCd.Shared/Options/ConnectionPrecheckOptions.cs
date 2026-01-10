namespace IRCd.Shared.Options;

public sealed class ConnectionPrecheckOptions
{
    /// <summary>
    /// When false (default), the precheck pipeline is effectively disabled and will do no work.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Per-connection overall timeout budget for prechecks.
    /// </summary>
    public int TimeoutMs { get; set; } = 300;

    /// <summary>
    /// Cache TTL for allowed (not listed) results.
    /// </summary>
    public int CacheAllowTtlSeconds { get; set; } = 60;

    /// <summary>
    /// Cache TTL for blocked (listed) results.
    /// </summary>
    public int CacheBlockTtlSeconds { get; set; } = 300;

    /// <summary>
    /// Soft cap for the IP result cache.
    /// </summary>
    public int CacheMaxEntries { get; set; } = 50_000;

    /// <summary>
    /// General DNSBL/RBL zones.
    /// </summary>
    public DnsblZoneOptions[] Dnsbl { get; set; } = Array.Empty<DnsblZoneOptions>();

    /// <summary>
    /// Optional Tor-related DNSBL zones (standard reverse-IP DNSBL format).
    /// </summary>
    public DnsblZoneOptions[] TorDnsbl { get; set; } = Array.Empty<DnsblZoneOptions>();

    /// <summary>
    /// Optional VPN/proxy-related DNSBL zones (standard reverse-IP DNSBL format).
    /// </summary>
    public DnsblZoneOptions[] VpnDnsbl { get; set; } = Array.Empty<DnsblZoneOptions>();

    /// <summary>
    /// If true, skip DNSBL lookups for non-public IPs (loopback / RFC1918 / link-local).
    /// </summary>
    public bool SkipNonPublicIps { get; set; } = true;
}

public sealed class DnsblZoneOptions
{
    public string Zone { get; set; } = string.Empty;

    public string Reason { get; set; } = "Listed";

    /// <summary>
    /// If &gt; 0, add a temporary DLINE for the exact IP.
    /// </summary>
    public int TempDlineSeconds { get; set; }
}
