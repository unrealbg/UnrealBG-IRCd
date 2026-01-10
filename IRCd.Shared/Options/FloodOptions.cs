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

    public CommandFloodOptions Commands { get; set; } = new();
}

public sealed class FloodGateOptions
{
    public int MaxLines { get; set; }

    public int WindowSeconds { get; set; }
}

public sealed class CommandFloodOptions
{
    public bool Enabled { get; set; } = true;

    public int ViolationsBeforeDisconnect { get; set; } = 3;

    public int WarningCooldownSeconds { get; set; } = 5;

    public int ViolationResetSeconds { get; set; } = 60;

    public bool ExemptOpers { get; set; } = true;

    public int OperMultiplier { get; set; } = 4;

    public bool TempDlineEnabled { get; set; } = false;
    public int TempDlineMinutes { get; set; } = 10;

    public CommandFloodBucketOptions Messages { get; set; } = new() { MaxEvents = 10, WindowSeconds = 10, PerTarget = true };
    public CommandFloodBucketOptions JoinPart { get; set; } = new() { MaxEvents = 10, WindowSeconds = 30 };
    public CommandFloodBucketOptions WhoWhois { get; set; } = new() { MaxEvents = 10, WindowSeconds = 10 };
    public CommandFloodBucketOptions Mode { get; set; } = new() { MaxEvents = 10, WindowSeconds = 10 };
    public CommandFloodBucketOptions Nick { get; set; } = new() { MaxEvents = 5, WindowSeconds = 60 };
}

public sealed class CommandFloodBucketOptions
{
    public bool Enabled { get; set; } = true;

    public int MaxEvents { get; set; }

    public int WindowSeconds { get; set; }

    // For PRIVMSG/NOTICE: track per target (connectionId:target) instead of per session.
    public bool PerTarget { get; set; } = false;
}
