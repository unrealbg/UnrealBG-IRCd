namespace IRCd.Tests
{
    using IRCd.Core.State;
    using Xunit;

    public sealed class S2SSquitHandlingTests
    {
        [Fact]
        public void RemoveRemoteServerTreeByConnection_WhenCalled_RemovesAllNextHopsForSubtree()
        {
            var state = new ServerState();

            state.TryRegisterRemoteServer(new RemoteServer { ConnectionId = "link1", Name = "s1", Sid = "002", ParentSid = "001" });
            state.TryRegisterRemoteServer(new RemoteServer { ConnectionId = "link1", Name = "s2", Sid = "003", ParentSid = "002" });

            Assert.True(state.TryGetNextHopBySid("002", out _));
            Assert.True(state.TryGetNextHopBySid("003", out _));

            state.RemoveRemoteServerTreeByConnection("link1");

            Assert.False(state.TryGetNextHopBySid("002", out _));
            Assert.False(state.TryGetNextHopBySid("003", out _));
        }
    }
}
