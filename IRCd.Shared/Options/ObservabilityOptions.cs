namespace IRCd.Shared.Options
{
    public sealed class ObservabilityOptions
    {
        public bool Enabled { get; set; } = false;

        public string BindIp { get; set; } = "127.0.0.1";

        public int Port { get; set; } = 6060;
    }
}
