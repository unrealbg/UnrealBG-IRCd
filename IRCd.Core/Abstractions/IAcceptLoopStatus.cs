namespace IRCd.Core.Abstractions
{
    public interface IAcceptLoopStatus
    {
        long ActiveAcceptLoops { get; }

        void AcceptLoopStarted();

        void AcceptLoopStopped();
    }
}
