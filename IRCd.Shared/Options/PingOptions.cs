namespace IRCd.Shared.Options
{
    public sealed class PingOptions
    {
        public bool Enabled { get; set; } = true;

        public int IdleSecondsBeforePing { get; set; } = 60;

        public int DisconnectSecondsAfterPing { get; set; } = 30;

        public string QuitMessage { get; set; } = "Ping timeout";
    }
}
