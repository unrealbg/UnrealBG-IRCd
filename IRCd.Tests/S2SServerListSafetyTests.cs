namespace IRCd.Tests
{
    using IRCd.Core.State;
    using Xunit;

    public sealed class S2SServerListSafetyTests
    {
        [Fact]
        public void TrySetNextHopBySid_DoesNotThrow_AndTryGetWorks()
        {
            var state = new ServerState();

            state.TrySetNextHopBySid("002", "link1");

            Assert.True(state.TryGetNextHopBySid("002", out var hop));
            Assert.Equal("link1", hop);
        }

        [Fact]
        public void RemoveRemoteServerTreeByConnection_SubtreeRemoval_DoesNotRemoveUnrelatedServers()
        {
            var state = new ServerState();

            state.TryRegisterRemoteServer(new RemoteServer { ConnectionId = "link1", Name = "s1", Sid = "002", ParentSid = "001" });
            state.TryRegisterRemoteServer(new RemoteServer { ConnectionId = "link2", Name = "sX", Sid = "010", ParentSid = "001" });

            var removed = state.RemoveRemoteServerTreeByConnection("link1");
            Assert.Contains(removed, s => s.Sid == "002");

            Assert.True(state.TryGetRemoteServerBySid("010", out var stillThere));
            Assert.NotNull(stillThere);
        }
    }
}
