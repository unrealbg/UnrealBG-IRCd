namespace IRCd.Services.ChanServ
{
    public sealed record ChannelTopicLock
    {
        public bool Enabled { get; init; }

        public string? LockedTopic { get; init; }
    }
}
