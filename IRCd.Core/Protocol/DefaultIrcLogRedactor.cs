namespace IRCd.Core.Protocol
{
    public sealed class DefaultIrcLogRedactor : IIrcLogRedactor
    {
        public string RedactInboundLine(string? line) => IrcLogRedactor.RedactInboundLine(line);
    }
}
