namespace IRCd.Core.Services
{
    using System;
    using IRCd.Core.Abstractions;

    public sealed class DefaultMetrics : IMetrics
    {
        private long _connectionsAccepted;
        private long _connectionsClosed;
        private long _activeConnections;
        private long _registeredUsersTotal;
        private long _channelsCreatedTotal;
        private long _commandsTotal;
        private long _floodKicksTotal;

        private readonly object _rateLock = new();
        private readonly long[] _cmdBuckets = new long[10];
        private readonly long[] _cmdBucketSeconds = new long[10];

        public void ConnectionAccepted(bool secure)
        {
            _ = secure;
            Interlocked.Increment(ref _connectionsAccepted);
            Interlocked.Increment(ref _activeConnections);
        }

        public void ConnectionClosed(bool secure)
        {
            _ = secure;
            Interlocked.Increment(ref _connectionsClosed);
            Interlocked.Decrement(ref _activeConnections);
        }

        public void UserRegistered()
        {
            Interlocked.Increment(ref _registeredUsersTotal);
        }

        public void ChannelCreated()
        {
            Interlocked.Increment(ref _channelsCreatedTotal);
        }

        public void CommandProcessed(string command)
        {
            _ = command;

            Interlocked.Increment(ref _commandsTotal);

            var unixSecond = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var idx = (int)(unixSecond % _cmdBuckets.Length);

            lock (_rateLock)
            {
                if (_cmdBucketSeconds[idx] != unixSecond)
                {
                    _cmdBucketSeconds[idx] = unixSecond;
                    _cmdBuckets[idx] = 0;
                }

                _cmdBuckets[idx]++;
            }
        }

        public void FloodKick()
        {
            Interlocked.Increment(ref _floodKicksTotal);
        }

        public MetricsSnapshot GetSnapshot()
        {
            double cps;

            lock (_rateLock)
            {
                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                long sum = 0;
                long seconds = 0;

                for (var i = 0; i < _cmdBuckets.Length; i++)
                {
                    var sec = _cmdBucketSeconds[i];
                    if (sec == 0)
                        continue;

                    if (now - sec > _cmdBuckets.Length)
                        continue;

                    sum += _cmdBuckets[i];
                    seconds++;
                }

                cps = seconds > 0 ? (double)sum / seconds : 0.0;
            }

            var active = Interlocked.Read(ref _activeConnections);
            if (active < 0) active = 0;

            return new MetricsSnapshot(
                ConnectionsAccepted: Interlocked.Read(ref _connectionsAccepted),
                ConnectionsClosed: Interlocked.Read(ref _connectionsClosed),
                ActiveConnections: active,
                RegisteredUsersTotal: Interlocked.Read(ref _registeredUsersTotal),
                ChannelsCreatedTotal: Interlocked.Read(ref _channelsCreatedTotal),
                CommandsTotal: Interlocked.Read(ref _commandsTotal),
                CommandsPerSecond: cps,
                FloodKicksTotal: Interlocked.Read(ref _floodKicksTotal));
        }
    }
}
