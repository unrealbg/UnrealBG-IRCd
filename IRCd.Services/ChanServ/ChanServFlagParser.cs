namespace IRCd.Services.ChanServ
{
    using System;
    using System.Collections.Generic;

    internal static class ChanServFlagParser
    {
        public static bool TryParse(string input, out ChanServFlags flags)
        {
            flags = ChanServFlags.None;

            if (string.IsNullOrWhiteSpace(input))
            {
                return false;
            }

            var tokens = input.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (tokens.Length == 0)
            {
                return false;
            }

            foreach (var raw in tokens)
            {
                var t = raw.Trim();
                if (t.Length == 0)
                {
                    continue;
                }

                var op = '+';
                if (t[0] == '+' || t[0] == '-')
                {
                    op = t[0];
                    t = t.Substring(1);
                }

                if (!TryMapToken(t, out var mapped))
                {
                    return false;
                }

                if (op == '-')
                {
                    flags &= ~mapped;
                }
                else
                {
                    flags |= mapped;
                }
            }

            return true;
        }

        private static bool TryMapToken(string token, out ChanServFlags mapped)
        {
            mapped = ChanServFlags.None;
            if (string.IsNullOrWhiteSpace(token))
            {
                return false;
            }

            var t = token.Trim().ToUpperInvariant();
            mapped = t switch
            {
                "FOUNDER" => ChanServFlags.Founder,
                "FLAGS" => ChanServFlags.Flags,
                "OP" => ChanServFlags.Op,
                "VOICE" => ChanServFlags.Voice,
                "INVITE" => ChanServFlags.Invite,
                "KICK" => ChanServFlags.Kick,
                "BAN" => ChanServFlags.Ban,
                "ALL" => ChanServFlags.All,
                _ => ChanServFlags.None
            };

            if (mapped == ChanServFlags.Founder)
            {
                return false;
            }

            return mapped != ChanServFlags.None;
        }

        public static string FormatFlags(ChanServFlags flags)
        {
            if (flags == ChanServFlags.None)
            {
                return "NONE";
            }

            var parts = new List<string>();

            if (flags.HasFlag(ChanServFlags.Flags)) parts.Add("FLAGS");
            if (flags.HasFlag(ChanServFlags.Op)) parts.Add("OP");
            if (flags.HasFlag(ChanServFlags.Voice)) parts.Add("VOICE");
            if (flags.HasFlag(ChanServFlags.Invite)) parts.Add("INVITE");
            if (flags.HasFlag(ChanServFlags.Kick)) parts.Add("KICK");
            if (flags.HasFlag(ChanServFlags.Ban)) parts.Add("BAN");

            return string.Join(',', parts);
        }
    }
}
