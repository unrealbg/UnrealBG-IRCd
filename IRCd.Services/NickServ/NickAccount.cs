namespace IRCd.Services.NickServ
{
    using System;
    using System.Collections.Generic;

    public sealed record NickAccount
    {
        public required string Name { get; init; }

        public required string PasswordHash { get; init; }

        public string? GroupedToAccount { get; init; }

        public bool IsConfirmed { get; init; } = true;

        public string? PendingConfirmationCodeHash { get; init; }

        public DateTimeOffset? PendingConfirmationExpiresAtUtc { get; init; }

        public DateTimeOffset? PendingRegisteredAtUtc { get; init; }

        public string? Email { get; init; }

        public bool HideEmail { get; init; }

        public bool Enforce { get; init; } = true;

        public bool Kill { get; init; } = true;

        public bool Secure { get; init; } = false;

        public bool AllowMemos { get; init; } = true;

        public bool MemoNotify { get; init; } = true;

        public bool MemoSignon { get; init; } = true;

        public bool NoLink { get; init; } = true;

        public IReadOnlyList<string> AccessMasks { get; init; } = Array.Empty<string>();

        public DateTimeOffset RegisteredAtUtc { get; init; } = DateTimeOffset.UtcNow;
    }
}
