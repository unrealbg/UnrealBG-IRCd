namespace IRCd.Services.Storage
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Services.HostServ;

    public sealed class InMemoryVHostRepository : IVHostRepository
    {
        private readonly ConcurrentDictionary<string, VHostRecord> _items = new(StringComparer.OrdinalIgnoreCase);

        public ValueTask<VHostRecord?> GetAsync(string nick, CancellationToken ct)
        {
            _ = ct;
            if (string.IsNullOrWhiteSpace(nick))
            {
                return ValueTask.FromResult<VHostRecord?>(null);
            }

            _items.TryGetValue(nick.Trim(), out var v);
            return ValueTask.FromResult<VHostRecord?>(v);
        }

        public IEnumerable<VHostRecord> All() => _items.Values;

        public ValueTask<bool> TryUpsertAsync(VHostRecord record, CancellationToken ct)
        {
            _ = ct;
            if (record is null || string.IsNullOrWhiteSpace(record.Nick) || string.IsNullOrWhiteSpace(record.VHost))
            {
                return ValueTask.FromResult(false);
            }

            record.UpdatedUtc = DateTimeOffset.UtcNow;
            _items[record.Nick.Trim()] = record;
            return ValueTask.FromResult(true);
        }

        public ValueTask<bool> TryDeleteAsync(string nick, CancellationToken ct)
        {
            _ = ct;
            if (string.IsNullOrWhiteSpace(nick))
            {
                return ValueTask.FromResult(false);
            }

            return ValueTask.FromResult(_items.TryRemove(nick.Trim(), out _));
        }
    }
}
