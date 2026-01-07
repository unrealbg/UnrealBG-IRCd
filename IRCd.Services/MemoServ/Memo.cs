namespace IRCd.Services.MemoServ
{
    using System;

    public sealed record Memo
    {
        public Guid Id { get; init; } = Guid.NewGuid();

        public DateTimeOffset SentAtUtc { get; init; } = DateTimeOffset.UtcNow;

        public string FromAccount { get; init; } = string.Empty;

        public string Text { get; init; } = string.Empty;

        public bool IsRead { get; init; } = false;

        public DateTimeOffset? ReadAtUtc { get; init; }
    }
}
