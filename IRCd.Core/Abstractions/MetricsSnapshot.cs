namespace IRCd.Core.Abstractions
{
    public sealed record MetricsSnapshot(
        long ConnectionsAccepted,
        long ConnectionsClosed,
        long ActiveConnections,
        long RegisteredUsersTotal,
        long ChannelsCreatedTotal,
        long CommandsTotal,
        double CommandsPerSecond,
        long FloodKicksTotal,
        long OutboundQueueDepth,
        long OutboundQueueMaxDepth,
        long OutboundQueueDroppedTotal,
        long OutboundQueueOverflowDisconnectsTotal);
}
