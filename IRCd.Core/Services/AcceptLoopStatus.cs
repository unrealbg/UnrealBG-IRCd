namespace IRCd.Core.Services
{
    using IRCd.Core.Abstractions;

    public sealed class AcceptLoopStatus : IAcceptLoopStatus
    {
        private long _active;

        public long ActiveAcceptLoops
        {
            get
            {
                var v = Interlocked.Read(ref _active);
                return v < 0 ? 0 : v;
            }
        }

        public void AcceptLoopStarted()
            => Interlocked.Increment(ref _active);

        public void AcceptLoopStopped()
        {
            var v = Interlocked.Decrement(ref _active);
            if (v < 0)
            {
                Interlocked.Exchange(ref _active, 0);
            }
        }
    }
}
