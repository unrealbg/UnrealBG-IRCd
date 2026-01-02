namespace IRCd.Shared.Options
{
    public sealed class MotdOptions
    {
        public string? FilePath { get; set; }

        public string[] Lines { get; set; } = Array.Empty<string>();
    }
}
