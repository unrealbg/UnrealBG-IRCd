namespace IRCd.Core.Protocol
{
    using System;

    public static class IrcLogRedactor
    {
        private const string Redacted = "[REDACTED]";

        public static string RedactInboundLine(string? line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return line ?? string.Empty;

            var trimmed = line.Trim();

            var i = 0;
            string? prefix = null;

            if (trimmed.Length > 0 && trimmed[0] == ':')
            {
                var sp = trimmed.IndexOf(' ');
                if (sp <= 0)
                {
                    return trimmed;
                }

                prefix = trimmed[..sp];
                i = sp + 1;
                while (i < trimmed.Length && trimmed[i] == ' ')
                {
                    i++;
                }
            }

            if (i >= trimmed.Length)
            {
                return trimmed;
            }

            var cmdEnd = trimmed.IndexOf(' ', i);
            var command = cmdEnd < 0 ? trimmed[i..] : trimmed[i..cmdEnd];
            var rest = cmdEnd < 0 ? string.Empty : trimmed[(cmdEnd + 1)..];

            if (command.Equals("PASS", StringComparison.OrdinalIgnoreCase))
            {
                return Rebuild(prefix, command, RedactFirstParam(rest));
            }

            if (command.Equals("OPER", StringComparison.OrdinalIgnoreCase))
            {
                return Rebuild(prefix, command, RedactSecondParam(rest));
            }

            if (command.Equals("AUTHENTICATE", StringComparison.OrdinalIgnoreCase))
            {
                return Rebuild(prefix, command, Redacted);
            }

            if (command.Equals("PRIVMSG", StringComparison.OrdinalIgnoreCase)
                || command.Equals("NOTICE", StringComparison.OrdinalIgnoreCase))
            {
                return RedactServicesPayload(prefix, command, rest);
            }

            return trimmed;
        }

        private static string Rebuild(string? prefix, string command, string rest)
        {
            if (string.IsNullOrWhiteSpace(rest))
            {
                return prefix is null ? command : $"{prefix} {command}";
            }

            return prefix is null ? $"{command} {rest}" : $"{prefix} {command} {rest}";
        }

        private static string RedactFirstParam(string rest)
        {
            rest = (rest ?? string.Empty).Trim();
            if (rest.Length == 0)
            {
                return rest;
            }

            var firstSpace = rest.IndexOf(' ');
            if (firstSpace < 0)
            {
                return Redacted;
            }

            return Redacted + rest[firstSpace..];
        }

        private static string RedactSecondParam(string rest)
        {
            rest = (rest ?? string.Empty).Trim();
            if (rest.Length == 0)
            {
                return rest;
            }

            var firstSpace = rest.IndexOf(' ');
            if (firstSpace < 0)
            {
                return rest;
            }

            var afterFirst = rest[(firstSpace + 1)..].TrimStart();
            if (afterFirst.Length == 0)
            {
                return rest;
            }

            var secondSpace = afterFirst.IndexOf(' ');
            if (secondSpace < 0)
            {
                return rest[..(firstSpace + 1)] + Redacted;
            }

            var keepFirst = rest[..(firstSpace + 1)];
            var afterSecond = afterFirst[secondSpace..];
            return keepFirst + Redacted + afterSecond;
        }

        private static string RedactServicesPayload(string? prefix, string command, string rest)
        {
            rest = (rest ?? string.Empty).TrimStart();
            if (rest.Length == 0)
            {
                return Rebuild(prefix, command, rest);
            }

            var firstSpace = rest.IndexOf(' ');
            if (firstSpace < 0)
            {
                return Rebuild(prefix, command, rest);
            }

            var target = rest[..firstSpace];
            var afterTarget = rest[(firstSpace + 1)..].TrimStart();

            if (!IsServiceNick(target))
            {
                return Rebuild(prefix, command, rest);
            }

            var colon = afterTarget.IndexOf(':');
            if (colon < 0)
            {
                return Rebuild(prefix, command, rest);
            }

            var beforeTrailing = afterTarget[..colon];
            var trailing = afterTarget[(colon + 1)..];
            var redactedTrailing = RedactServiceCommandText(trailing);

            var rebuiltRest = $"{target} {beforeTrailing}:{redactedTrailing}";
            return Rebuild(prefix, command, rebuiltRest.TrimEnd());
        }

        private static bool IsServiceNick(string target)
            => target.Equals("NickServ", StringComparison.OrdinalIgnoreCase)
               || target.Equals("ChanServ", StringComparison.OrdinalIgnoreCase)
               || target.Equals("OperServ", StringComparison.OrdinalIgnoreCase)
               || target.Equals("MemoServ", StringComparison.OrdinalIgnoreCase)
               || target.Equals("SeenServ", StringComparison.OrdinalIgnoreCase)
               || target.Equals("HostServ", StringComparison.OrdinalIgnoreCase)
               || target.Equals("AdminServ", StringComparison.OrdinalIgnoreCase)
               || target.Equals("DevServ", StringComparison.OrdinalIgnoreCase)
               || target.Equals("HelpServ", StringComparison.OrdinalIgnoreCase)
               || target.Equals("InfoServ", StringComparison.OrdinalIgnoreCase)
               || target.Equals("StatServ", StringComparison.OrdinalIgnoreCase)
               || target.Equals("RootServ", StringComparison.OrdinalIgnoreCase);

        private static string RedactServiceCommandText(string text)
        {
            var t = (text ?? string.Empty).Trim();
            if (t.Length == 0)
            {
                return text ?? string.Empty;
            }

            var parts = t.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 0)
            {
                return t;
            }

            if (parts[0].Equals("IDENTIFY", StringComparison.OrdinalIgnoreCase)
                || parts[0].Equals("REGISTER", StringComparison.OrdinalIgnoreCase)
                || parts[0].Equals("GROUP", StringComparison.OrdinalIgnoreCase)
                || parts[0].Equals("LINK", StringComparison.OrdinalIgnoreCase)
                || parts[0].Equals("GHOST", StringComparison.OrdinalIgnoreCase)
                || parts[0].Equals("DROP", StringComparison.OrdinalIgnoreCase))
            {
                if (parts.Length == 1)
                {
                    return parts[0];
                }

                if (parts.Length == 2)
                {
                    return $"{parts[0]} {Redacted}";
                }

                parts[^1] = Redacted;
                return string.Join(' ', parts);
            }

            if (parts.Length >= 2
                && parts[0].Equals("SET", StringComparison.OrdinalIgnoreCase)
                && parts[1].Equals("PASSWORD", StringComparison.OrdinalIgnoreCase))
            {
                if (parts.Length <= 2)
                {
                    return "SET PASSWORD";
                }

                return "SET PASSWORD " + Redacted;
            }

            return t;
        }
    }
}
