namespace IRCd.Core.Services
{
    using System;
    using System.Net;
    using System.Net.Sockets;

    internal static class IpMaskMatcher
    {
        public static bool MatchesIpOrCidr(IPAddress ip, string mask)
        {
            if (string.IsNullOrWhiteSpace(mask))
            {
                return false;
            }

            mask = mask.Trim();

            if (mask.Contains('/', StringComparison.Ordinal))
            {
                var parts = mask.Split('/');
                if (parts.Length != 2)
                {
                    return false;
                }

                if (!IPAddress.TryParse(parts[0], out var networkIp))
                {
                    return false;
                }

                if (!int.TryParse(parts[1], out var prefixLength))
                {
                    return false;
                }

                return IsInCidr(ip, networkIp, prefixLength);
            }

            if (mask.Contains('*', StringComparison.Ordinal) || mask.Contains('?', StringComparison.Ordinal))
            {
                var ipString = ip.ToString();
                return WildcardMatch(ipString, mask);
            }

            if (IPAddress.TryParse(mask, out var banIp))
                return ip.Equals(banIp);

            return false;
        }

        private static bool IsInCidr(IPAddress ip, IPAddress network, int prefixLength)
        {
            if (ip.AddressFamily != network.AddressFamily)
            {
                return false;
            }

            var ipBytes = ip.GetAddressBytes();
            var networkBytes = network.GetAddressBytes();

            if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            {
                if (prefixLength < 0 || prefixLength > 128)
                {
                    return false;
                }
            }
            else if (ip.AddressFamily == AddressFamily.InterNetwork)
            {
                if (prefixLength < 0 || prefixLength > 32)
                {
                    return false;
                }
            }
            else
            {
                return false;
            }

            var bytesToCheck = prefixLength / 8;
            var bitsToCheck = prefixLength % 8;

            for (var i = 0; i < bytesToCheck; i++)
            {
                if (ipBytes[i] != networkBytes[i])
                {
                    return false;
                }
            }

            if (bitsToCheck > 0)
            {
                var mask = (byte)(0xFF << (8 - bitsToCheck));
                if ((ipBytes[bytesToCheck] & mask) != (networkBytes[bytesToCheck] & mask))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool WildcardMatch(string text, string pattern)
        {
            var textIndex = 0;
            var patternIndex = 0;
            var textBacktrack = -1;
            var patternBacktrack = -1;

            while (textIndex < text.Length)
            {
                if (patternIndex < pattern.Length && (pattern[patternIndex] == text[textIndex] || pattern[patternIndex] == '?'))
                {
                    textIndex++;
                    patternIndex++;
                }
                else if (patternIndex < pattern.Length && pattern[patternIndex] == '*')
                {
                    patternBacktrack = patternIndex;
                    textBacktrack = textIndex;
                    patternIndex++;
                }
                else if (patternBacktrack != -1)
                {
                    patternIndex = patternBacktrack + 1;
                    textBacktrack++;
                    textIndex = textBacktrack;
                }
                else
                {
                    return false;
                }
            }

            while (patternIndex < pattern.Length && pattern[patternIndex] == '*')
            {
                patternIndex++;
            }

            return patternIndex == pattern.Length;
        }
    }
}
