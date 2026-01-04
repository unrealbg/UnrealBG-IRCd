namespace IRCd.Core.Protocol
{
    using System;

    public static class ServerLinkParser
    {
        public static bool TryParse(string line, out string command, out string[] args, out string? trailing)
        {
            command = string.Empty;
            args = Array.Empty<string>();
            trailing = null;

            if (string.IsNullOrWhiteSpace(line))
            {
                return false;
            }

            if (line[0] == ':')
            {
                var sp = line.IndexOf(' ');
                if (sp < 0)
                    return false;
                line = line[(sp + 1)..];
            }

            var colon = line.IndexOf(" :", StringComparison.Ordinal);
            if (colon >= 0)
            {
                trailing = line[(colon + 2)..];
                line = line[..colon];
            }

            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 0)
                return false;

            command = parts[0];
            if (parts.Length > 1)
            {
                args = new string[parts.Length - 1];
                Array.Copy(parts, 1, args, 0, args.Length);
            }

            return true;
        }
    }
}
