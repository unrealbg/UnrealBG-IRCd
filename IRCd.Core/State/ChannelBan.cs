namespace IRCd.Core.State
{
    public sealed record ChannelBan(string Mask, string SetBy, DateTimeOffset SetAtUtc);
}
