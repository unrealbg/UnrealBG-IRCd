namespace IRCd.Shared.Options;

public sealed class FloodOptions
{
    public FloodGateOptions Client { get; set; } = new()
    {
        MaxLines = 20,
        WindowSeconds = 10,
    };

    public FloodGateOptions TlsClient { get; set; } = new()
    {
        MaxLines = 20,
        WindowSeconds = 10,
    };

    public FloodGateOptions ServerLink { get; set; } = new()
    {
        MaxLines = 50,
        WindowSeconds = 10,
    };
}

public sealed class FloodGateOptions
{
    public int MaxLines { get; set; }

    public int WindowSeconds { get; set; }
}
