namespace IRCd.Shared.Options
{
    public sealed class MotdVhostOptions
    {
        /// <summary>
        /// Match key for MOTD selection. Supported: "*", "ip" (e.g. "127.0.0.1"), or "ip:port".
        /// </summary>
        public string Vhost { get; set; } = "*";

        public MotdOptions Motd { get; set; } = new();
    }
}
