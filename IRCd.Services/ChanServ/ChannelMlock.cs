namespace IRCd.Services.ChanServ
{
    using IRCd.Core.State;

    public sealed record ChannelMlock
    {
        public ChannelModes SetModes { get; init; }

        public ChannelModes ClearModes { get; init; }

        public bool KeyLocked { get; init; }

        public string? Key { get; init; }

        public bool LimitLocked { get; init; }

        public int? Limit { get; init; }

        public string Raw { get; init; } = string.Empty;
    }
}
