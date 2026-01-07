namespace IRCd.Core.State
{
    using System;

    public sealed class User
    {
        public string ConnectionId { get; init; } = default!;

        public bool IsService { get; set; }

        public bool IsRemote { get; set; }

        public string? RemoteSid { get; set; }

        public string? Uid { get; set; }

        public long NickTs { get; set; } = ChannelTimestamps.NowTs();

        public string? Nick { get; set; }

        public string? UserName { get; set; }

        public string? RealName { get; set; }

        public string? AwayMessage { get; set; }

        public string? Host { get; set; }

        public string? RemoteIp { get; set; }

        public bool IsSecureConnection { get; set; }

        public bool IsRegistered { get; set; }

        public string? OperName { get; set; }

        public string? OperClass { get; set; }

        public UserModes Modes { get; set; } = UserModes.None;

        public DateTimeOffset ConnectedAtUtc { get; init; } = DateTimeOffset.UtcNow;

        public DateTimeOffset LastActivityUtc { get; set; } = DateTimeOffset.UtcNow;
    }
}
