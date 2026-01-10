namespace IRCd.Core.Services
{
    using System;
    using System.Collections.Concurrent;
    using System.Text.RegularExpressions;

    public readonly record struct ChannelBanMatchInput(
        string Nick,
        string UserName,
        string Host,
        string? Account);

    /// <summary>
    /// Pure matching logic for bans.
    /// Supports traditional wildcard masks and a subset of extbans (currently ~a: account bans).
    /// </summary>
    public sealed class BanMatcher
    {
        public static BanMatcher Shared { get; } = new();

        private readonly ConcurrentDictionary<string, Regex> _regexCache = new(StringComparer.Ordinal);

        public bool IsWildcardMatch(string mask, string value)
        {
            if (string.IsNullOrWhiteSpace(mask) || value is null)
            {
                return false;
            }

            mask = mask.Trim();

            var rx = _regexCache.GetOrAdd(mask, static m =>
            {
                var pattern = "^" + Regex.Escape(m)
                    .Replace(@"\*", ".*")
                    .Replace(@"\?", ".") + "$";

                return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            });

            return rx.IsMatch(value);
        }

        public bool IsChannelBanMatch(string banMask, ChannelBanMatchInput input)
            => IsChannelMaskMatch(banMask, input);

        public bool IsChannelExceptionMatch(string exceptionMask, ChannelBanMatchInput input)
            => IsChannelMaskMatch(exceptionMask, input);

        private bool IsChannelMaskMatch(string mask, ChannelBanMatchInput input)
        {
            if (string.IsNullOrWhiteSpace(mask))
            {
                return false;
            }

            mask = mask.Trim();

            if (TryParseAccountExtBan(mask, out var accountMask))
            {
                var account = NormalizeAccountName(input.Account);
                if (string.IsNullOrEmpty(account) || account == "*")
                {
                    return false;
                }

                return IsWildcardMatch(accountMask, account);
            }

            var userName = string.IsNullOrWhiteSpace(input.UserName) ? "u" : input.UserName;
            var host = string.IsNullOrWhiteSpace(input.Host) ? "localhost" : input.Host;
            var value = $"{input.Nick}!{userName}@{host}";
            return IsWildcardMatch(mask, value);
        }

        private static bool TryParseAccountExtBan(string mask, out string accountMask)
        {
            accountMask = string.Empty;

            if (mask.Length < 4)
            {
                return false;
            }

            if (mask[0] != '~')
            {
                return false;
            }

            var colonIdx = mask.IndexOf(':', StringComparison.Ordinal);
            if (colonIdx <= 1)
            {
                return false;
            }

            var typeToken = mask[1..colonIdx];
            if (!typeToken.Equals("a", StringComparison.OrdinalIgnoreCase)
                && !typeToken.Equals("account", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var rest = mask[(colonIdx + 1)..].Trim();
            if (string.IsNullOrWhiteSpace(rest))
            {
                return false;
            }

            accountMask = rest;
            return true;
        }

        private static string? NormalizeAccountName(string? account)
        {
            if (string.IsNullOrWhiteSpace(account))
            {
                return null;
            }

            return account.Trim();
        }
    }
}
