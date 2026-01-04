namespace IRCd.Core.Protocol
{
    using System;
    using System.Globalization;

    using IRCd.Core.Abstractions;

    public sealed class IrcFormatter
    {
        public string FormatFor(IClientSession session, string rawLine)
        {
            if (string.IsNullOrWhiteSpace(rawLine))
                return rawLine;

            if (!session.EnabledCapabilities.Contains("server-time"))
                return rawLine;

            if (rawLine.Length > 0 && rawLine[0] == '@')
                return rawLine;

            var ts = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
            return $"@time={ts} {rawLine}";
        }
    }
}
