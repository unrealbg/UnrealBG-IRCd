namespace IRCd.Core.Services
{
    using System;
    using System.Threading;
    using System.Threading.Channels;
    using System.Threading.Tasks;

    using IRCd.Core.Abstractions;

    /// <summary>
    /// Bounded, single-reader outbound queue that tracks depth metrics and signals overflow.
    /// </summary>
    public sealed class OutboundMessageQueue
    {
        private readonly Channel<string> _channel;
        private readonly IMetrics? _metrics;
        private long _depth;

        public OutboundMessageQueue(int capacity, IMetrics? metrics = null)
        {
            var cap = capacity > 0 ? capacity : 256;
            _channel = Channel.CreateBounded<string>(new BoundedChannelOptions(cap)
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = true,
                FullMode = BoundedChannelFullMode.Wait
            });

            _metrics = metrics;
            _metrics?.OutboundQueueDepth(0);
        }

        public bool TryEnqueue(string line)
        {
            if (_channel.Writer.TryWrite(line))
            {
                var depth = Interlocked.Increment(ref _depth);
                _metrics?.OutboundQueueDepth(depth);
                return true;
            }

            _metrics?.OutboundQueueDrop();
            return false;
        }

        public IAsyncEnumerable<string> ReadAllAsync(CancellationToken ct)
            => _channel.Reader.ReadAllAsync(ct);

        public void MarkDequeued()
        {
            var depth = Interlocked.Decrement(ref _depth);
            if (depth < 0)
            {
                depth = 0;
                Interlocked.Exchange(ref _depth, 0);
            }
            _metrics?.OutboundQueueDepth(depth);
        }

        public void Complete()
        {
            _channel.Writer.TryComplete();
            ResetDepth();
        }

        public void ResetDepth()
        {
            Interlocked.Exchange(ref _depth, 0);
            _metrics?.OutboundQueueDepth(0);
        }
    }
}
