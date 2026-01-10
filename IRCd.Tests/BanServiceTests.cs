namespace IRCd.Tests
{
    using System;
    using System.Net;
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Core.Abstractions;
    using IRCd.Core.Services;
    using IRCd.Core.State;

    using Microsoft.Extensions.Logging.Abstractions;

    using Xunit;

    public sealed class BanServiceTests
    {
        [Fact]
        public async Task AddBan_KLINE_AddsSuccessfully()
        {
            var repository = new InMemoryBanRepository();
            var logger = NullLogger<BanService>.Instance;
            var banService = new BanService(repository, logger);

            var ban = new BanEntry
            {
                Type = BanType.KLINE,
                Mask = "*!*@evil.host",
                Reason = "Test ban",
                SetBy = "operator"
            };

            var added = await banService.AddAsync(ban, CancellationToken.None);

            Assert.NotNull(added);
            Assert.Equal(BanType.KLINE, added.Type);
            Assert.Equal("*!*@evil.host", added.Mask);
        }

        [Fact]
        public async Task TryMatchUser_KLINE_MatchesWildcard()
        {
            var repository = new InMemoryBanRepository();
            var logger = NullLogger<BanService>.Instance;
            var banService = new BanService(repository, logger);

            var ban = new BanEntry
            {
                Type = BanType.KLINE,
                Mask = "*!*@evil.host",
                Reason = "Test ban",
                SetBy = "operator"
            };

            await banService.AddAsync(ban, CancellationToken.None);

            var match = await banService.TryMatchUserAsync("baduser", "user", "evil.host", CancellationToken.None);

            Assert.NotNull(match);
            Assert.Equal("*!*@evil.host", match!.Mask);
        }

        [Fact]
        public async Task TryMatchUser_KLINE_DoesNotMatchDifferentHost()
        {
            var repository = new InMemoryBanRepository();
            var logger = NullLogger<BanService>.Instance;
            var banService = new BanService(repository, logger);

            var ban = new BanEntry
            {
                Type = BanType.KLINE,
                Mask = "*!*@evil.host",
                Reason = "Test ban",
                SetBy = "operator"
            };

            await banService.AddAsync(ban, CancellationToken.None);

            var match = await banService.TryMatchUserAsync("gooduser", "user", "good.host", CancellationToken.None);

            Assert.Null(match);
        }

        [Fact]
        public async Task TryMatchIp_DLINE_MatchesCIDR()
        {
            var repository = new InMemoryBanRepository();
            var logger = NullLogger<BanService>.Instance;
            var banService = new BanService(repository, logger);

            var ban = new BanEntry
            {
                Type = BanType.DLINE,
                Mask = "192.168.1.0/24",
                Reason = "Test ban",
                SetBy = "operator"
            };

            await banService.AddAsync(ban, CancellationToken.None);

            var ip = IPAddress.Parse("192.168.1.100");
            var match = await banService.TryMatchIpAsync(ip, CancellationToken.None);

            Assert.NotNull(match);
            Assert.Equal("192.168.1.0/24", match!.Mask);
        }

        [Fact]
        public async Task TryMatchIp_DLINE_DoesNotMatchOutsideCIDR()
        {
            var repository = new InMemoryBanRepository();
            var logger = NullLogger<BanService>.Instance;
            var banService = new BanService(repository, logger);

            var ban = new BanEntry
            {
                Type = BanType.DLINE,
                Mask = "192.168.1.0/24",
                Reason = "Test ban",
                SetBy = "operator"
            };

            await banService.AddAsync(ban, CancellationToken.None);

            var ip = IPAddress.Parse("192.168.2.100");
            var match = await banService.TryMatchIpAsync(ip, CancellationToken.None);

            Assert.Null(match);
        }

        [Fact]
        public async Task TryMatchNick_QLINE_MatchesPattern()
        {
            var repository = new InMemoryBanRepository();
            var logger = NullLogger<BanService>.Instance;
            var banService = new BanService(repository, logger);

            var ban = new BanEntry
            {
                Type = BanType.QLINE,
                Mask = "Guest*",
                Reason = "Reserved",
                SetBy = "server"
            };

            await banService.AddAsync(ban, CancellationToken.None);

            var match = await banService.TryMatchNickAsync("Guest12345", CancellationToken.None);

            Assert.NotNull(match);
            Assert.Equal("Guest*", match!.Mask);
        }

        [Fact]
        public async Task RemoveBan_RemovesSuccessfully()
        {
            var repository = new InMemoryBanRepository();
            var logger = NullLogger<BanService>.Instance;
            var banService = new BanService(repository, logger);

            var ban = new BanEntry
            {
                Type = BanType.KLINE,
                Mask = "*!*@evil.host",
                Reason = "Test ban",
                SetBy = "operator"
            };

            await banService.AddAsync(ban, CancellationToken.None);
            var removed = await banService.RemoveAsync(BanType.KLINE, "*!*@evil.host", CancellationToken.None);

            Assert.True(removed);

            var match = await banService.TryMatchUserAsync("baduser", "user", "evil.host", CancellationToken.None);
            Assert.Null(match);
        }

        [Fact]
        public async Task ExpiredBan_DoesNotMatch()
        {
            var repository = new InMemoryBanRepository();
            var logger = NullLogger<BanService>.Instance;
            var banService = new BanService(repository, logger);

            var ban = new BanEntry
            {
                Type = BanType.KLINE,
                Mask = "*!*@evil.host",
                Reason = "Test ban",
                SetBy = "operator",
                ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-10) // Expired 10 seconds ago
            };

            await banService.AddAsync(ban, CancellationToken.None);

            var match = await banService.TryMatchUserAsync("baduser", "user", "evil.host", CancellationToken.None);

            Assert.Null(match); // Should not match because ban is expired
        }

        [Fact]
        public void ParseDuration_ParsesCorrectly()
        {
            var oneHour = BanEntry.ParseDuration("1h");
            Assert.NotNull(oneHour);
            Assert.True(oneHour.Value > DateTimeOffset.UtcNow.AddMinutes(59));

            var twoDays = BanEntry.ParseDuration("2d");
            Assert.NotNull(twoDays);
            Assert.True(twoDays.Value > DateTimeOffset.UtcNow.AddDays(1));

            var perm = BanEntry.ParseDuration("perm");
            Assert.Null(perm);

            var invalid = BanEntry.ParseDuration("invalid");
            Assert.Null(invalid);
        }
    }
}
