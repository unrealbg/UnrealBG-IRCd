namespace IRCd.Shared.Options
{
    public sealed class OperClassOptions
    {
        public string Name { get; set; } = string.Empty;

        public string[] Capabilities { get; set; } = Array.Empty<string>();

        public int? MaxConnectionsPerIp { get; set; }

        public string? Description { get; set; }
    }
}
