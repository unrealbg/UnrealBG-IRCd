namespace IRCd.Core.Services
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Linq;

    using IRCd.Core.State;

    public sealed class WhowasService
    {
        private readonly ConcurrentDictionary<string, ConcurrentQueue<WhowasEntry>> _byNick = new(StringComparer.OrdinalIgnoreCase);

        private const int MaxEntriesPerNick = 5;

        public void Record(User user, string? explicitNick = null, string? signoff = null)
        {
            if (user is null)
                return;

            var nick = (explicitNick ?? user.Nick)?.Trim();
            if (string.IsNullOrWhiteSpace(nick))
                return;

            var entry = new WhowasEntry(
                Nick: nick,
                UserName: user.UserName ?? "user",
                Host: user.Host ?? "localhost",
                RealName: user.RealName ?? string.Empty,
                Signoff: signoff ?? string.Empty,
                RecordedUtc: DateTimeOffset.UtcNow);

            var q = _byNick.GetOrAdd(nick, _ => new ConcurrentQueue<WhowasEntry>());
            q.Enqueue(entry);

            while (q.Count > MaxEntriesPerNick && q.TryDequeue(out _)) { }
        }

        public IReadOnlyList<WhowasEntry> Get(string nick)
        {
            if (string.IsNullOrWhiteSpace(nick))
                return Array.Empty<WhowasEntry>();

            if (!_byNick.TryGetValue(nick.Trim(), out var q) || q is null)
                return Array.Empty<WhowasEntry>();

            return q.ToArray().Reverse().ToArray();
        }

        public sealed record WhowasEntry(
            string Nick,
            string UserName,
            string Host,
            string RealName,
            string Signoff,
            DateTimeOffset RecordedUtc);
    }
}
