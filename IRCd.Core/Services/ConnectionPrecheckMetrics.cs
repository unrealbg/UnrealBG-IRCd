namespace IRCd.Core.Services
{
    public interface IConnectionPrecheckMetrics
    {
        void Timeout();

        void Error();

        void Deny();

        ConnectionPrecheckMetricsSnapshot GetSnapshot();
    }

    public sealed record ConnectionPrecheckMetricsSnapshot(
        long TimeoutsTotal,
        long ErrorsTotal,
        long DeniesTotal);

    public sealed class ConnectionPrecheckMetrics : IConnectionPrecheckMetrics
    {
        private long _timeoutsTotal;
        private long _errorsTotal;
        private long _deniesTotal;

        public void Timeout() => Interlocked.Increment(ref _timeoutsTotal);

        public void Error() => Interlocked.Increment(ref _errorsTotal);

        public void Deny() => Interlocked.Increment(ref _deniesTotal);

        public ConnectionPrecheckMetricsSnapshot GetSnapshot()
            => new(
                TimeoutsTotal: Interlocked.Read(ref _timeoutsTotal),
                ErrorsTotal: Interlocked.Read(ref _errorsTotal),
                DeniesTotal: Interlocked.Read(ref _deniesTotal));
    }
}
