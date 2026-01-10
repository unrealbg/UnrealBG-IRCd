namespace IRCd.Shared.Options
{
    public sealed class AutoDlineOptions
    {
        public bool Enabled { get; set; } = false;

        /// <summary>
        /// Offense score window; score resets if no offenses arrive in this window.
        /// </summary>
        public int WindowSeconds { get; set; } = 60;

        /// <summary>
        /// Number of offenses within the window required to apply an auto DLINE.
        /// </summary>
        public int Threshold { get; set; } = 10;

        /// <summary>
        /// Initial DLINE duration (seconds).
        /// </summary>
        public int BaseDurationSeconds { get; set; } = 60;

        /// <summary>
        /// Exponential backoff multiplier applied per strike.
        /// </summary>
        public int BackoffFactor { get; set; } = 2;

        /// <summary>
        /// Maximum DLINE duration (seconds).
        /// </summary>
        public int MaxDurationSeconds { get; set; } = 3600;

        /// <summary>
        /// IP/CIDR allowlist exempt from automatic DLINE escalation.
        /// Example: "192.0.2.0/24", "2001:db8::/32".
        /// </summary>
        public string[] WhitelistCidrs { get; set; } = System.Array.Empty<string>();
    }
}
