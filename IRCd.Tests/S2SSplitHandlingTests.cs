namespace IRCd.Tests
{
    using IRCd.Core.State;
    using Xunit;

    public sealed class S2SSplitHandlingTests
    {
        [Fact]
        public void RemoveRemoteServerTreeByConnection_RemovesServersAndRemoteUsers()
        {
            var state = new ServerState();

            // Root server on link conn
            state.TryRegisterRemoteServer(new RemoteServer
            {
                ConnectionId = "link1",
                Name = "s1",
                Sid = "002",
                ParentSid = "001",
                Description = "root"
            });

            // Child server behind root (same link)
            state.TryRegisterRemoteServer(new RemoteServer
            {
                ConnectionId = "link1",
                Name = "s2",
                Sid = "003",
                ParentSid = "002",
                Description = "child"
            });

            // Remote users on both servers
            state.TryAddRemoteUser(new User
            {
                ConnectionId = "uid:002AAAAAA",
                Uid = "002AAAAAA",
                Nick = "Nick1",
                UserName = "u",
                Host = "h",
                IsRegistered = true,
                IsRemote = true,
                RemoteSid = "002"
            });

            state.TryAddRemoteUser(new User
            {
                ConnectionId = "uid:003BBBBBB",
                Uid = "003BBBBBB",
                Nick = "Nick2",
                UserName = "u",
                Host = "h",
                IsRegistered = true,
                IsRemote = true,
                RemoteSid = "003"
            });

            var removed = state.RemoveRemoteServerTreeByConnection("link1");

            Assert.Contains(removed, s => s.Sid == "002");
            Assert.Contains(removed, s => s.Sid == "003");

            Assert.False(state.TryGetUserByUid("002AAAAAA", out _));
            Assert.False(state.TryGetUserByUid("003BBBBBB", out _));

            Assert.False(state.TryGetNextHopBySid("002", out _));
            Assert.False(state.TryGetNextHopBySid("003", out _));
        }
    }
}
