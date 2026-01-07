namespace IRCd.Shared.Options
{
    public sealed class BotServOptions
    {
        public BotOptions[] Bots { get; set; } = Array.Empty<BotOptions>();
    }

    public sealed class BotOptions
    {
        public string Nick { get; set; } = string.Empty;

        public string RealName { get; set; } = "Channel Bot";
    }
}
