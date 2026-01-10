namespace IRCd.Core.Abstractions
{
    public interface IMetrics
    {
        void ConnectionAccepted(bool secure);

        void ConnectionClosed(bool secure);

        void UserRegistered();

        void ChannelCreated();

        void CommandProcessed(string command);

        void FloodKick();

        void OutboundQueueDepth(long depth);

        void OutboundQueueDrop();

        void OutboundQueueOverflowDisconnect();

        MetricsSnapshot GetSnapshot();
    }
}
