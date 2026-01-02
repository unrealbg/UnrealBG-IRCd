namespace IRCd.Shared.Options
{
    public sealed class ConnectionGuardOptions
    {
        /// <summary>
        /// Enable/disable connection guard.
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Max new TCP connections allowed per IP within the window.
        /// </summary>
        public int MaxConnectionsPerWindowPerIp { get; set; } = 20;

        /// <summary>
        /// Sliding window for MaxConnectionsPerWindowPerIp.
        /// </summary>
        public int WindowSeconds { get; set; } = 60;

        /// <summary>
        /// Maximum simultaneous unregistered (unknown) connections per IP.
        /// </summary>
        public int MaxUnregisteredPerIp { get; set; } = 3;

        /// <summary>
        /// Client must complete registration (NICK+USER) within this time or gets disconnected.
        /// </summary>
        public int RegistrationTimeoutSeconds { get; set; } = 30;

        /// <summary>
        /// Message shown when a connection is refused early.
        /// </summary>
        public string RejectMessage { get; set; } = "Connection throttled";
    }
}
