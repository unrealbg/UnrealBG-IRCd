namespace IRCd.Shared.Options
{
    public class IrcOptions
    {
        public int IrcPort { get; set; } = 6667;

        public string BindAddress { get; set; } = "0.0.0.0";
    }
}
