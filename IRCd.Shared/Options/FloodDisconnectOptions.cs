namespace IRCd.Shared.Options
{
    public sealed class FloodDisconnectOptions
    {
        public bool Enabled { get; set; } = true;

        public int MaxViolations { get; set; } = 10;

        public int WindowSeconds { get; set; } = 30;

        public string QuitMessage { get; set; } = "Excess flood";
    }
}
