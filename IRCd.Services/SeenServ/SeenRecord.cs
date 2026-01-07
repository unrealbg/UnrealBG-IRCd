namespace IRCd.Services.SeenServ
{
    using System;

    public sealed record SeenRecord
    {
        public string Nick { get; init; } = string.Empty;

        public string? UserName { get; init; }

        public string? Host { get; init; }

        public DateTimeOffset WhenUtc { get; init; } = DateTimeOffset.UtcNow;

        public string Message { get; init; } = string.Empty;
    }
}
