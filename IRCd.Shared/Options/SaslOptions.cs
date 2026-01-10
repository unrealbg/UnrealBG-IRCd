namespace IRCd.Shared.Options;

public sealed class SaslOptions
{
    public SaslExternalOptions External { get; set; } = new();
}

public sealed class SaslExternalOptions
{
    public bool Enabled { get; set; } = true;

    public Dictionary<string, string> FingerprintToAccount { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, string> SubjectToAccount { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
