namespace IRCd.Tests
{
    using IRCd.Core.State;
    using Xunit;

    public sealed class S2SUidCollisionTests
    {
        [Fact]
        public void TryAddRemoteUser_UidCollision_IsRejected()
        {
            var state = new ServerState();

            var u1 = new User
            {
                ConnectionId = "uid:002AAAAAA",
                Uid = "002AAAAAA",
                Nick = "Nick1",
                UserName = "u",
                Host = "h",
                IsRegistered = true,
                IsRemote = true,
                RemoteSid = "002"
            };

            Assert.True(state.TryAddRemoteUser(u1));

            var u2 = new User
            {
                ConnectionId = "uid:002AAAAAA",
                Uid = "002AAAAAA",
                Nick = "Nick2",
                UserName = "u",
                Host = "h",
                IsRegistered = true,
                IsRemote = true,
                RemoteSid = "003"
            };

            Assert.False(state.TryAddRemoteUser(u2));

            Assert.True(state.TryGetUserByUid("002AAAAAA", out var got1));
            Assert.Equal("Nick1", got1!.Nick);

            Assert.False(state.TryGetUserByUid("002AAAAAA_", out _));
        }
    }
}
