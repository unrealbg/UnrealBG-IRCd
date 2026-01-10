using IRCd.Core.Services;

public sealed class AcceptLoopStatusTests
{
    [Fact]
    public void ActiveAcceptLoops_IncrementsAndDecrements()
    {
        var s = new AcceptLoopStatus();

        Assert.Equal(0, s.ActiveAcceptLoops);

        s.AcceptLoopStarted();
        s.AcceptLoopStarted();
        Assert.Equal(2, s.ActiveAcceptLoops);

        s.AcceptLoopStopped();
        Assert.Equal(1, s.ActiveAcceptLoops);

        s.AcceptLoopStopped();
        Assert.Equal(0, s.ActiveAcceptLoops);

        // Should not go negative
        s.AcceptLoopStopped();
        Assert.Equal(0, s.ActiveAcceptLoops);
    }
}
