namespace IRCd.Tests
{
    using IRCd.Core.State;
    using Xunit;

    public sealed class S2SServerTreeBySidTests
    {
        [Fact]
        public void RemoveRemoteServerTreeBySid_RemovesSubtreeOnly()
        {
            var state = new ServerState();

            state.TryRegisterRemoteServer(new RemoteServer { ConnectionId = "link1", Name = "s1", Sid = "002", ParentSid = "001" });
            state.TryRegisterRemoteServer(new RemoteServer { ConnectionId = "link1", Name = "s2", Sid = "003", ParentSid = "002" });
            state.TryRegisterRemoteServer(new RemoteServer { ConnectionId = "link2", Name = "sX", Sid = "010", ParentSid = "001" });

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

            var removed = state.RemoveRemoteServerTreeBySid("002");

            Assert.Contains(removed, s => s.Sid == "002");
            Assert.Contains(removed, s => s.Sid == "003");

            Assert.False(state.TryGetRemoteServerBySid("002", out _));
            Assert.False(state.TryGetRemoteServerBySid("003", out _));

            Assert.True(state.TryGetRemoteServerBySid("010", out _));

            Assert.False(state.TryGetUserByUid("002AAAAAA", out _));
            Assert.False(state.TryGetUserByUid("003BBBBBB", out _));

            Assert.False(state.TryGetNextHopBySid("002", out _));
            Assert.False(state.TryGetNextHopBySid("003", out _));
        }
    }
}
