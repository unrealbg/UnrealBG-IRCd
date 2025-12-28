namespace IRCd.Core.Protocol
{
    public sealed record IrcMessage(
    string? Prefix,
    string Command,
    IReadOnlyList<string> Params,
    string? Trailing
);
}
