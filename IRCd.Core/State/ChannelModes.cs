namespace IRCd.Core.State
{
    [Flags]
    public enum ChannelModes
    {
        None = 0,
        NoExternalMessages = 1 << 0,
        TopicOpsOnly = 1 << 1
    }
}
