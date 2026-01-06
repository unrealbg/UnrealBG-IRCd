namespace IRCd.Shared.Options
{
    public sealed class OperOptions
    {
        public string Name { get; set; } = string.Empty;

        public string Password { get; set; } = string.Empty;

        public string? Class { get; set; }
    }
}
