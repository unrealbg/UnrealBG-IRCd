namespace IRCd.Shared.Options
{
    public sealed class IrcOptions
    {
        public string? ConfigFile { get; set; }

        public string? ClientPassword { get; set; }

        public string? OperPassword { get; set; }

        public ServerInfoOptions ServerInfo { get; set; } = new();

        public ListenOptions Listen { get; set; } = new();

        public LinkOptions[] Links { get; set; } = Array.Empty<LinkOptions>();

        public int IrcPort { get; set; } = 6667;

        public RateLimitOptions RateLimit { get; set; } = new();

        public PingOptions Ping { get; set; } = new();

        public MotdOptions Motd { get; set; } = new();

        public ConnectionGuardOptions ConnectionGuard { get; set; } = new();

        public CommandLimitsOptions Limits { get; set; } = new();
    }
}
