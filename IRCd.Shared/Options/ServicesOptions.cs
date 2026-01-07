namespace IRCd.Shared.Options
{
    public sealed class ServicesOptions
    {
        public NickServOptions NickServ { get; set; } = new();

        public ChanServOptions ChanServ { get; set; } = new();

        public AdminServOptions AdminServ { get; set; } = new();

        public HostServOptions HostServ { get; set; } = new();

        public BotServOptions BotServ { get; set; } = new();

        public AgentOptions Agent { get; set; } = new();

        public MemoServOptions MemoServ { get; set; } = new();

        public SeenServOptions SeenServ { get; set; } = new();
    }

    public sealed class NickServOptions
    {
        public bool EnforceRegisteredNicks { get; set; } = true;

        public int EnforceDelaySeconds { get; set; } = 30;

        public string? AccountsFilePath { get; set; }

        public bool RequireEmailConfirmation { get; set; } = false;

        public int PendingRegistrationExpiryHours { get; set; } = 24;

        public int AccountExpiryDays { get; set; } = 28;

        public NickServSmtpOptions Smtp { get; set; } = new();
    }

    public sealed class NickServSmtpOptions
    {
        public string? Host { get; set; }

        public int Port { get; set; } = 587;

        public bool UseSsl { get; set; } = true;

        public string? Username { get; set; }

        public string? Password { get; set; }

        public string? FromAddress { get; set; }

        public string? FromName { get; set; }
    }

    public sealed class ChanServOptions
    {
        public string? ChannelsFilePath { get; set; }

        public bool AutoJoinRegisteredChannels { get; set; } = true;
    }

    public sealed class MemoServOptions
    {
        public string? MemosFilePath { get; set; }
    }

    public sealed class SeenServOptions
    {
        public string? SeenFilePath { get; set; }
    }
}
