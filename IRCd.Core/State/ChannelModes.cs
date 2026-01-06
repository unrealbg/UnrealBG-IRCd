namespace IRCd.Core.State
{
    [Flags]
    public enum ChannelModes
    {
        None = 0,
        NoExternalMessages = 1 << 0,
        TopicOpsOnly = 1 << 1,

        InviteOnly = 1 << 2,
        Key = 1 << 3,
        Limit = 1 << 4,

        Moderated = 1 << 5,
        Secret = 1 << 6,
        Private = 1 << 7
    }
}
