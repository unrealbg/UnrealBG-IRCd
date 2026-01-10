namespace IRCd.Shared.Options;

public sealed class TransportOptions
{
    public int ClientMaxLineChars { get; set; } = 510;

    public TcpTransportOptions Tcp { get; set; } = new();

    public S2STransportOptions S2S { get; set; } = new();

    public TransportQueueOptions Queues { get; set; } = new();
}

public sealed class TcpTransportOptions
{
    public bool KeepAliveEnabled { get; set; } = true;

    public int KeepAliveTimeMs { get; set; } = 120_000;

    public int KeepAliveIntervalMs { get; set; } = 30_000;
}

public sealed class S2STransportOptions
{
    public int InboundHandshakeTimeoutSeconds { get; set; } = 15;

    public int OutboundScanIntervalSeconds { get; set; } = 10;

    public int MsgIdCacheTtlSeconds { get; set; } = 120;

    public int MsgIdCacheMaxEntries { get; set; } = 50_000;

    public int OutboundBackoffMaxSeconds { get; set; } = 30;

    public int OutboundBackoffMaxExponent { get; set; } = 4;

    public int OutboundFailureLimit { get; set; } = 10;
}

public sealed class TransportQueueOptions
{
    public int ClientSendQueueCapacity { get; set; } = 256;

    public int ServerLinkSendQueueCapacity { get; set; } = 2048;
}
