namespace IRCd.Shared.Options
{
    public sealed class AgentOptions
    {
        public string? LogonMessage { get; set; }

        public string[] Badwords { get; set; } = Array.Empty<string>();
    }
}
