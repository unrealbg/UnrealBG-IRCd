namespace IRCd.Shared.Options
{
    public sealed class IrcOptions
    {
        public string? ConfigFile { get; set; }

        public string? ClientPassword { get; set; }

        public string? OperPassword { get; set; }

        public OperOptions[] Opers { get; set; } = Array.Empty<OperOptions>();

        public OperClassOptions[] Classes { get; set; } = Array.Empty<OperClassOptions>();

        public KLineOptions[] KLines { get; set; } = Array.Empty<KLineOptions>();

        public DLineOptions[] DLines { get; set; } = Array.Empty<DLineOptions>();

        public DenyOptions[] Denies { get; set; } = Array.Empty<DenyOptions>();

        public WarnOptions[] Warns { get; set; } = Array.Empty<WarnOptions>();

        public TriggerOptions[] Triggers { get; set; } = Array.Empty<TriggerOptions>();

        public ServerInfoOptions ServerInfo { get; set; } = new();

        public ListenOptions Listen { get; set; } = new();

        public ListenEndpointOptions[] ListenEndpoints { get; set; } = Array.Empty<ListenEndpointOptions>();

        public LinkOptions[] Links { get; set; } = Array.Empty<LinkOptions>();

        public int IrcPort { get; set; } = 6667;

        public RateLimitOptions RateLimit { get; set; } = new();

        public PingOptions Ping { get; set; } = new();

        public TransportOptions Transport { get; set; } = new();

        public FloodOptions Flood { get; set; } = new();

        public AuthOptions Auth { get; set; } = new();

        public MotdOptions Motd { get; set; } = new();

        public MotdVhostOptions[] MotdByVhost { get; set; } = Array.Empty<MotdVhostOptions>();

        public ConnectionGuardOptions ConnectionGuard { get; set; } = new();

        public CommandLimitsOptions Limits { get; set; } = new();

        public IsupportOptions Isupport { get; set; } = new();

        public ServicesOptions Services { get; set; } = new();
    }
}
