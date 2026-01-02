namespace IRCd.Shared.Options
{
    public sealed class RateLimitOptions
    {
        public bool Enabled { get; set; } = true;

        public TokenBucketOptions PrivMsg { get; set; } = new() { Capacity = 5, RefillTokens = 1, RefillPeriodSeconds = 1 };

        public TokenBucketOptions Notice { get; set; } = new() { Capacity = 5, RefillTokens = 1, RefillPeriodSeconds = 1 };

        public TokenBucketOptions Join { get; set; } = new() { Capacity = 3, RefillTokens = 1, RefillPeriodSeconds = 3 };

        public FloodDisconnectOptions Disconnect { get; set; } = new();
    }
}
