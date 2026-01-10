namespace IRCd.Tests
{
    using IRCd.Core.Protocol;
    using Xunit;

    public sealed class IrcValidationTests
    {
        [Theory]
        [InlineData("nick")]
        [InlineData("Nick_123")]
        [InlineData("a-b")]
        [InlineData("[test]")]
        public void IsValidNick_ValidExamples(string nick)
        {
            Assert.True(IrcValidation.IsValidNick(nick, out _));
        }

        [Theory]
        [InlineData("")]
        [InlineData(" ")]
        [InlineData("123")]
        [InlineData("-test")]
        [InlineData("bad nick")]
        [InlineData("bad:nick")]
        public void IsValidNick_InvalidExamples(string nick)
        {
            Assert.False(IrcValidation.IsValidNick(nick, out _));
        }

        [Theory]
        [InlineData("#chan")]
        [InlineData("#test-123")]
        public void IsValidChannel_ValidExamples(string channel)
        {
            Assert.True(IrcValidation.IsValidChannel(channel, out _));
        }

        [Theory]
        [InlineData("chan")]
        [InlineData("")]
        [InlineData("#")]
        [InlineData("#bad chan")]
        [InlineData("#bad,chan")]
        [InlineData("#bad:chan")]
        public void IsValidChannel_InvalidExamples(string channel)
        {
            Assert.False(IrcValidation.IsValidChannel(channel, out _));
        }
    }
}
