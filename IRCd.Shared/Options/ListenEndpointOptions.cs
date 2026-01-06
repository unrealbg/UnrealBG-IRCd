namespace IRCd.Shared.Options
{
    public sealed class ListenEndpointOptions
    {
        public string BindIp { get; set; } = "0.0.0.0";

        public int Port { get; set; } = 6667;

        public bool Tls { get; set; } = false;

        /// <summary>
        /// Optional identifier; not used by runtime yet.
        /// </summary>
        public string? Name { get; set; }
    }
}
