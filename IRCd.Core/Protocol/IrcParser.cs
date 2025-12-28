namespace IRCd.Core.Protocol
{
    using System.Text;

    public static class IrcParser
    {
        public static IrcMessage ParseLine(string line)
        {
            line = line.TrimEnd('\r', '\n');

            string? prefix = null;

            var i = 0;
            if (line.StartsWith(':'))
            {
                var space = line.IndexOf(' ');
                if (space <= 1)
                {
                    throw new FormatException("Invalid prefix");
                }

                prefix = line[1..space];
                i = space + 1;
            }

            string? trailing = null;
            var trailingIndex = line.IndexOf(" :", i, StringComparison.Ordinal);
            string head;

            if (trailingIndex >= 0)
            {
                head = line[i..trailingIndex];
                trailing = line[(trailingIndex + 2)..];
            }
            else
            {
                head = line[i..];
            }

            var tokens = SplitBySpaces(head);
            if (tokens.Count == 0)
            {
                throw new FormatException("Missing command");
            }

            var command = tokens[0].ToUpperInvariant();
            var @params = tokens.Count > 1 ? tokens.Skip(1).ToArray() : Array.Empty<string>();

            return new IrcMessage(prefix, command, @params, trailing);
        }

        private static List<string> SplitBySpaces(string s)
        {
            var result = new List<string>();
            var sb = new StringBuilder();
            var inToken = false;

            for (int i = 0; i < s.Length; i++)
            {
                var c = s[i];
                if (c == ' ')
                {
                    if (inToken)
                    {
                        result.Add(sb.ToString());
                        sb.Clear();
                        inToken = false;
                    }

                    continue;
                }

                sb.Append(c);
                inToken = true;
            }

            if (inToken) result.Add(sb.ToString());
            {
                return result;
            }
        }
    }
}
