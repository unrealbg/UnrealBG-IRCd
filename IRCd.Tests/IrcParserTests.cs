namespace IRCd.Tests
{
    using IRCd.Core.Protocol;
    using Xunit;

    public class IrcParserTests
    {
        [Fact]
        public void ParseLine_PrivMsg_WithTrailing_ParsesCorrectly()
        {
            var msg = IrcParser.ParseLine("PRIVMSG #chan :hello world");

            Assert.Equal("PRIVMSG", msg.Command);
            Assert.Equal("#chan", msg.Params[0]);
            Assert.Equal("hello world", msg.Trailing);
        }

        [Fact]
        public void ParseLine_WithPrefix_ParsesPrefixAndCommand()
        {
            var msg = IrcParser.ParseLine(":nick!u@h PING :123");

            Assert.Equal("nick!u@h", msg.Prefix);
            Assert.Equal("PING", msg.Command);
            Assert.Equal("123", msg.Trailing);
        }
    }
}
