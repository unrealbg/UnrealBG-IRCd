namespace IRCd.Tests
{
    using IRCd.Core.Services;

    using Xunit;

    public sealed class BanMatcherTests
    {
        [Fact]
        public void WildcardMatch_StillMatchesHostmask()
        {
            var matcher = new BanMatcher();
            Assert.True(matcher.IsWildcardMatch("*!*@evil.host", "nick!u@evil.host"));
            Assert.False(matcher.IsWildcardMatch("*!*@evil.host", "nick!u@good.host"));
        }

        [Fact]
        public void AccountExtban_MatchesIdentifiedAccount()
        {
            var matcher = new BanMatcher();
            var input = new ChannelBanMatchInput("nick", "u", "h", "alice");

            Assert.True(matcher.IsChannelBanMatch("~a:alice", input));
            Assert.True(matcher.IsChannelBanMatch("~a:ali*", input));
            Assert.True(matcher.IsChannelBanMatch("~account:alice", input));
            Assert.False(matcher.IsChannelBanMatch("~a:bob", input));
        }

        [Fact]
        public void AccountExtban_DoesNotMatchWhenNoAccount()
        {
            var matcher = new BanMatcher();
            var input = new ChannelBanMatchInput("nick", "u", "h", "*");

            Assert.False(matcher.IsChannelBanMatch("~a:alice", input));
        }
    }
}
