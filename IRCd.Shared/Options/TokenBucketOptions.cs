namespace IRCd.Shared.Options
{
    public sealed class TokenBucketOptions
    {
        public int Capacity { get; set; } = 10;

        public int RefillTokens { get; set; } = 1;

        public int RefillPeriodSeconds { get; set; } = 1;
    }
}
