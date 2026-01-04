namespace IRCd.Shared.Options
{
    public sealed class ListenOptions
    {
        public int ClientPort { get; set; } = 6667;

        public int ServerPort { get; set; } = 6900;

        public string BindIp { get; set; } = "0.0.0.0";
    }
}
