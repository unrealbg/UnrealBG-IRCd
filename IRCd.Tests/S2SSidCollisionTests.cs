namespace IRCd.Tests
{
    using IRCd.Core.State;
    using Xunit;

    public sealed class S2SSidCollisionTests
    {
        [Fact]
        public void TrySetNextHopBySid_WhenAlreadySet_CanBeDetectedByCaller()
        {
            var state = new ServerState();

            state.TrySetNextHopBySid("002", "link1");
            Assert.True(state.TryGetNextHopBySid("002", out var hop1));
            Assert.Equal("link1", hop1);

            // Simulate a conflicting update attempt (what ServerLinkService now rejects).
            state.TrySetNextHopBySid("002", "link2");
            Assert.True(state.TryGetNextHopBySid("002", out var hop2));
            Assert.Equal("link2", hop2);
        }
    }
}
