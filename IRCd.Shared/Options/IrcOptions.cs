namespace IRCd.Shared.Options
{
    public sealed class IrcOptions
    {
        public int IrcPort { get; set; } = 6667;

        public RateLimitOptions RateLimit { get; set; } = new();

        public PingOptions Ping { get; set; } = new();

        public MotdOptions Motd { get; set; } = new();

        public ConnectionGuardOptions ConnectionGuard { get; set; } = new();
    }
}
