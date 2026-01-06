namespace IRCd.Shared.Options;

public sealed class AuthOptions
{
    public bool Enabled { get; set; } = true;

    public bool ReverseDnsEnabled { get; set; } = true;

    public int ReverseDnsTimeoutSeconds { get; set; } = 5;

    public bool IdentEnabled { get; set; } = true;

    public int IdentTimeoutSeconds { get; set; } = 5;

    public int AuthNoticeDelayMs { get; set; } = 0;
}
