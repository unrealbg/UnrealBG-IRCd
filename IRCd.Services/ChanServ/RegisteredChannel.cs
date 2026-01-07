namespace IRCd.Services.ChanServ
{
    using System;
    using System.Collections.Generic;

    public sealed record RegisteredChannel
    {
        public string Name { get; init; } = string.Empty;

        public string FounderAccount { get; init; } = string.Empty;

        public string PasswordHash { get; init; } = string.Empty;

        public string? Description { get; init; }

        public string? Url { get; init; }

        public string? Email { get; init; }

        public string? SuccessorAccount { get; init; }

        public string? EntryMessage { get; init; }

        public DateTimeOffset RegisteredAtUtc { get; init; } = DateTimeOffset.UtcNow;

        public IReadOnlyDictionary<string, ChanServFlags> Access { get; init; } = new Dictionary<string, ChanServFlags>(StringComparer.OrdinalIgnoreCase);

        public ChannelMlock? Mlock { get; init; }

        public ChannelTopicLock? TopicLock { get; init; }

        public IReadOnlyDictionary<string, string?> Akicks { get; init; } = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        public bool GuardEnabled { get; init; } = true;

        public bool SeenServEnabled { get; init; } = false;

        public bool RestrictedEnabled { get; init; } = false;

        public bool SuspendedEnabled { get; init; } = false;

        public string? SuspendedReason { get; init; }

        public string? SuspendedBy { get; init; }

        public DateTimeOffset? SuspendedAtUtc { get; init; }

        public ChanServFlags GetFlagsFor(string account)
        {
            if (string.IsNullOrWhiteSpace(account))
            {
                return ChanServFlags.None;
            }

            if (!string.IsNullOrWhiteSpace(FounderAccount) && string.Equals(account, FounderAccount, StringComparison.OrdinalIgnoreCase))
            {
                return ChanServFlags.All;
            }

            if (Access.TryGetValue(account, out var f))
            {
                return f;
            }

            return ChanServFlags.None;
        }

        public bool TryGetAkickReason(string account, out string? reason)
        {
            if (string.IsNullOrWhiteSpace(account))
            {
                reason = null;
                return false;
            }

            if (Akicks.TryGetValue(account, out reason))
            {
                return true;
            }

            reason = null;
            return false;
        }
    }
}
