namespace IRCd.Core.Services
{
    using System.Text.RegularExpressions;

    public static class MaskMatcher
    {
        public static bool IsMatch(string mask, string value)
        {
            var pattern = "^" + Regex.Escape(mask)
                .Replace(@"\*", ".*")
                .Replace(@"\?", ".") + "$";

            return Regex.IsMatch(value, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
    }
}
