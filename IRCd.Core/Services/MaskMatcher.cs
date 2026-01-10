namespace IRCd.Core.Services
{
    public static class MaskMatcher
    {
        public static bool IsMatch(string mask, string value)
        {
            return BanMatcher.Shared.IsWildcardMatch(mask, value);
        }
    }
}
