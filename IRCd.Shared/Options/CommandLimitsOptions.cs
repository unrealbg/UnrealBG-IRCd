namespace IRCd.Shared.Options;

public sealed class CommandLimitsOptions
{
    public int MaxIsonNames { get; set; } = 128;

    public int MaxWhoMaskLength { get; set; } = 256;

    public int MaxWhoisTargets { get; set; } = 5;

    public int MaxUserhostTargets { get; set; } = 10;

    public int MaxNamesChannels { get; set; } = 10;

    public int MaxListTargets { get; set; } = 20;

    public int MaxListModes { get; set; } = 60;

    public int MaxPrivmsgTargets { get; set; } = 4;

    public int MaxNoticeTargets { get; set; } = 4;

    public int MaxSilenceEntries { get; set; } = 15;

    public int MaxWatchEntries { get; set; } = 128;
}
