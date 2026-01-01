namespace IRCd.Core.State
{
    using System;

    public sealed class User
    {
        public string ConnectionId { get; init; } = default!;

        public string? Nick { get; set; }

        public string? UserName { get; set; }

        public string? RealName { get; set; }

        public bool IsRegistered { get; set; }

        public DateTimeOffset ConnectedAtUtc { get; init; } = DateTimeOffset.UtcNow;

        public DateTimeOffset LastActivityUtc { get; set; } = DateTimeOffset.UtcNow;
    }
}
