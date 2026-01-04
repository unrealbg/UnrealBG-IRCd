namespace IRCd.Core.Protocol
{
    using System;

    public static class IrcValidation
    {
        public const int DefaultNickLen = 20;
        public const int DefaultChannelLen = 50;

        public static bool IsValidNick(string? nick, out string error)
        {
            error = string.Empty;

            if (string.IsNullOrWhiteSpace(nick))
            {
                error = "No nickname given";
                return false;
            }

            nick = nick.Trim();

            if (nick.Length > DefaultNickLen)
            {
                error = "Erroneous nickname";
                return false;
            }

            if (nick.IndexOfAny([' ', ':', '\r', '\n', '\0', '\t']) >= 0)
            {
                error = "Erroneous nickname";
                return false;
            }

            var c0 = nick[0];
            if (char.IsDigit(c0) || c0 == '-')
            {
                error = "Erroneous nickname";
                return false;
            }

            foreach (var ch in nick)
            {
                if (ch <= 0x20 || ch == 0x7F)
                {
                    error = "Erroneous nickname";
                    return false;
                }

                if (char.IsLetterOrDigit(ch))
                    continue;

                if (ch is '[' or ']' or '\\' or '`' or '_' or '^' or '{' or '|' or '}' or '-')
                    continue;

                error = "Erroneous nickname";
                return false;
            }

            return true;
        }

        public static bool IsValidChannel(string? channel, out string error)
        {
            error = string.Empty;

            if (string.IsNullOrWhiteSpace(channel))
            {
                error = "No such channel";
                return false;
            }

            channel = channel.Trim();

            if (channel.Length > DefaultChannelLen)
            {
                error = "No such channel";
                return false;
            }

            if (!channel.StartsWith('#'))
            {
                error = "No such channel";
                return false;
            }

            if (channel.IndexOfAny([' ', ',', '\u0007', ':', '\r', '\n', '\0', '\t']) >= 0)
            {
                error = "No such channel";
                return false;
            }

            return channel.Length > 1;
        }
    }
}
