namespace IRCd.Core.Protocol
{
    public interface IIrcLogRedactor
    {
        string RedactInboundLine(string? line);
    }
}
