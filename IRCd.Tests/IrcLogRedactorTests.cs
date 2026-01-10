namespace IRCd.Tests
{
    using IRCd.Core.Protocol;

    using Xunit;

    public sealed class IrcLogRedactorTests
    {
        [Theory]
        [InlineData("PASS hunter2", "PASS [REDACTED]")]
        [InlineData(":nick!u@h PASS hunter2", ":nick!u@h PASS [REDACTED]")]
        [InlineData("OPER opername s3cr3t", "OPER opername [REDACTED]")]
        [InlineData("AUTHENTICATE dGVzdA==", "AUTHENTICATE [REDACTED]")]
        public void RedactInboundLine_RedactsCommonSecretCarryingCommands(string input, string expected)
        {
            var redacted = IrcLogRedactor.RedactInboundLine(input);
            Assert.Equal(expected, redacted);
            Assert.DoesNotContain("hunter2", redacted);
            Assert.DoesNotContain("s3cr3t", redacted);
            Assert.DoesNotContain("dGVzdA==", redacted);
        }

        [Theory]
        [InlineData("PRIVMSG NickServ :IDENTIFY hunter2", "PRIVMSG NickServ :IDENTIFY [REDACTED]")]
        [InlineData("NOTICE ChanServ :IDENTIFY hunter2", "NOTICE ChanServ :IDENTIFY [REDACTED]")]
        [InlineData("PRIVMSG NickServ :REGISTER someuser hunter2", "PRIVMSG NickServ :REGISTER someuser [REDACTED]")]
        [InlineData("PRIVMSG NickServ :SET PASSWORD hunter2", "PRIVMSG NickServ :SET PASSWORD [REDACTED]")]
        public void RedactInboundLine_RedactsServicePasswordPayloads(string input, string expected)
        {
            var redacted = IrcLogRedactor.RedactInboundLine(input);
            Assert.Equal(expected, redacted);
            Assert.DoesNotContain("hunter2", redacted);
        }

        [Fact]
        public void RedactInboundLine_DoesNotRedactNonServiceTargets()
        {
            var input = "PRIVMSG SomeNick :IDENTIFY hunter2";
            var redacted = IrcLogRedactor.RedactInboundLine(input);
            Assert.Equal(input, redacted);
        }
    }
}
