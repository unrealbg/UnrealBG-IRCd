namespace IRCd.Services.Storage
{
    using System;
    using System.Collections.Concurrent;
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Services.SeenServ;

    public sealed class InMemorySeenRepository : ISeenRepository
    {
        private readonly ConcurrentDictionary<string, SeenRecord> _seen = new(StringComparer.OrdinalIgnoreCase);

        public ValueTask<SeenRecord?> GetAsync(string nick, CancellationToken ct)
        {
            _ = ct;
            if (string.IsNullOrWhiteSpace(nick))
            {
                return ValueTask.FromResult<SeenRecord?>(null);
            }

            _seen.TryGetValue(nick.Trim(), out var rec);
            return ValueTask.FromResult<SeenRecord?>(rec);
        }

        public ValueTask<bool> TryUpsertAsync(SeenRecord record, CancellationToken ct)
        {
            _ = ct;
            if (record is null || string.IsNullOrWhiteSpace(record.Nick))
            {
                return ValueTask.FromResult(false);
            }

            _seen[record.Nick.Trim()] = record;
            return ValueTask.FromResult(true);
        }
    }
}
