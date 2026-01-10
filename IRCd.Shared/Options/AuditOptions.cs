namespace IRCd.Shared.Options
{
    public sealed class AuditOptions
    {
        public bool Enabled { get; set; } = false;

        public string? FilePath { get; set; }
    }
}
