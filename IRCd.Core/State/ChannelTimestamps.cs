namespace IRCd.Core.State
{
    public static class ChannelTimestamps
    {
        public static long NowTs() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }
}
