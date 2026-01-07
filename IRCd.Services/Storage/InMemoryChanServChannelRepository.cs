namespace IRCd.Services.Storage
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Services.ChanServ;

    public sealed class InMemoryChanServChannelRepository : IChanServChannelRepository
    {
        private readonly ConcurrentDictionary<string, RegisteredChannel> _channels = new(StringComparer.OrdinalIgnoreCase);

        public ValueTask<RegisteredChannel?> GetByNameAsync(string channelName, CancellationToken ct)
        {
            _ = ct;
            if (string.IsNullOrWhiteSpace(channelName))
            {
                return ValueTask.FromResult<RegisteredChannel?>(null);
            }

            _channels.TryGetValue(channelName, out var ch);
            return ValueTask.FromResult<RegisteredChannel?>(ch);
        }

        public IEnumerable<RegisteredChannel> All()
            => _channels.Values;

        public ValueTask<bool> TryCreateAsync(RegisteredChannel channel, CancellationToken ct)
        {
            _ = ct;
            if (channel is null || string.IsNullOrWhiteSpace(channel.Name))
            {
                return ValueTask.FromResult(false);
            }

            return ValueTask.FromResult(_channels.TryAdd(channel.Name, channel));
        }

        public ValueTask<bool> TryDeleteAsync(string channelName, CancellationToken ct)
        {
            _ = ct;
            if (string.IsNullOrWhiteSpace(channelName))
            {
                return ValueTask.FromResult(false);
            }

            return ValueTask.FromResult(_channels.TryRemove(channelName, out _));
        }

        public ValueTask<bool> TryUpdateAsync(RegisteredChannel updated, CancellationToken ct)
        {
            _ = ct;
            if (updated is null || string.IsNullOrWhiteSpace(updated.Name))
            {
                return ValueTask.FromResult(false);
            }

            while (true)
            {
                if (!_channels.TryGetValue(updated.Name, out var existing))
                {
                    return ValueTask.FromResult(false);
                }

                if (_channels.TryUpdate(updated.Name, updated, existing))
                {
                    return ValueTask.FromResult(true);
                }
            }
        }
    }
}
