namespace IRCd.Shared.Options
{
    public sealed class LinkOptions
    {
        public string Name { get; set; } = string.Empty;

        public string Sid { get; set; } = string.Empty;

        public string Host { get; set; } = "127.0.0.1";

        public int Port { get; set; } = 6900;

        public string Password { get; set; } = string.Empty;

        public bool Outbound { get; set; } = false;

        public bool UserSync { get; set; } = false;
    }
}
