namespace IRCd.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Net;
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Core.Abstractions;
    using IRCd.Core.Protocol;
    using IRCd.Core.Services;
    using IRCd.Core.State;
    using IRCd.Tests.TestDoubles;

    public sealed class OutboundBackpressureTests
    {
        [Fact]
        public async Task OutboundMessageQueue_PreservesOrdering()
        {
            var metrics = new DefaultMetrics();
            var q = new OutboundMessageQueue(capacity: 16, metrics);

            Assert.True(q.TryEnqueue("a"));
            Assert.True(q.TryEnqueue("b"));
            Assert.True(q.TryEnqueue("c"));

            q.Complete();

            var got = new List<string>();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));

            await foreach (var line in q.ReadAllAsync(cts.Token))
            {
                got.Add(line);
                q.MarkDequeued();
            }

            Assert.Equal(new[] { "a", "b", "c" }, got);
        }

        [Fact]
        public void OutboundMessageQueue_DropsWhenFull_TracksDepthAndDrops()
        {
            var metrics = new DefaultMetrics();
            var q = new OutboundMessageQueue(capacity: 2, metrics);

            Assert.True(q.TryEnqueue("1"));
            Assert.True(q.TryEnqueue("2"));
            Assert.False(q.TryEnqueue("3"));

            var snap = metrics.GetSnapshot();
            Assert.Equal(2, snap.OutboundQueueDepth);
            Assert.Equal(2, snap.OutboundQueueMaxDepth);
            Assert.Equal(1, snap.OutboundQueueDroppedTotal);

            q.Complete();
        }

        [Fact]
        public void SendAsync_WhenQueueOverflows_DisconnectsAndIncrementsOverflowMetric()
        {
            var metrics = new DefaultMetrics();
            var session = new QueueBackedClientSession("c1", outgoingQueueCapacity: 1, metrics);

            session.SendAsync("one");
            session.SendAsync("two");

            Assert.True(session.IsClosed);

            var snap = metrics.GetSnapshot();
            Assert.Equal(1, snap.OutboundQueueDroppedTotal);
            Assert.Equal(1, snap.OutboundQueueOverflowDisconnectsTotal);
        }

        [Fact]
        public async Task RoutingService_BroadcastToChannel_DoesNotAwaitSends()
        {
            var sessions = new FakeSessionRegistry();
            var formatter = new IrcFormatter();
            var routing = new RoutingService(sessions, formatter);

            var never = new NeverCompletingSendClientSession("c1");
            sessions.Add(never);

            var channel = new Channel("#c");
            Assert.True(channel.TryAddMember("c1", "nick1", ChannelPrivilege.Normal));

            var broadcast = routing.BroadcastToChannelAsync(channel, "NOTICE #c :hi", excludeConnectionId: null, CancellationToken.None);

            await broadcast.AsTask().WaitAsync(TimeSpan.FromMilliseconds(200));

            Assert.Equal(1, never.SendCalls);
        }

        [Fact]
        public async Task RoutingService_SendToUser_DoesNotAwaitSend()
        {
            var sessions = new FakeSessionRegistry();
            var formatter = new IrcFormatter();
            var routing = new RoutingService(sessions, formatter);

            var never = new NeverCompletingSendClientSession("c1");
            sessions.Add(never);

            var send = routing.SendToUserAsync("c1", "PING :123", CancellationToken.None);

            await send.AsTask().WaitAsync(TimeSpan.FromMilliseconds(200));

            Assert.Equal(1, never.SendCalls);
        }

        private sealed class QueueBackedClientSession : IClientSession
        {
            private readonly OutboundMessageQueue _outgoing;
            private readonly IMetrics? _metrics;
            private int _closed;

            public QueueBackedClientSession(string connectionId, int outgoingQueueCapacity, IMetrics? metrics)
            {
                ConnectionId = connectionId;
                _metrics = metrics;
                _outgoing = new OutboundMessageQueue(outgoingQueueCapacity, metrics);
                RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, 1);
                LocalEndPoint = new IPEndPoint(IPAddress.Loopback, 2);
                LastActivityUtc = DateTime.UtcNow;
            }

            public bool IsClosed => Volatile.Read(ref _closed) == 1;

            public string ConnectionId { get; }
            public EndPoint RemoteEndPoint { get; }
            public EndPoint LocalEndPoint { get; }
            public bool IsSecureConnection => false;
            public ISet<string> EnabledCapabilities { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public string? Nick { get; set; }
            public string? UserName { get; set; }
            public bool PassAccepted { get; set; }
            public bool IsRegistered { get; set; }
            public DateTime LastActivityUtc { get; private set; }
            public DateTime LastPingUtc { get; private set; }
            public bool AwaitingPong { get; private set; }
            public string? LastPingToken { get; }
            public string UserModes => "";

            public bool TryApplyUserModes(string modeString, out string appliedModes)
            {
                appliedModes = "+";
                return true;
            }

            public void OnInboundLine() => LastActivityUtc = DateTime.UtcNow;

            public void OnPingSent(string token)
            {
                _ = token;
                LastPingUtc = DateTime.UtcNow;
                AwaitingPong = true;
            }

            public void OnPongReceived(string? token)
            {
                _ = token;
                AwaitingPong = false;
                LastActivityUtc = DateTime.UtcNow;
            }

            public ValueTask SendAsync(string line, CancellationToken ct = default)
            {
                _ = ct;

                if (Volatile.Read(ref _closed) == 1)
                    return ValueTask.CompletedTask;

                if (!_outgoing.TryEnqueue(line))
                {
                    _metrics?.OutboundQueueOverflowDisconnect();
                    _ = CloseAsync("Send queue overflow", default);
                }

                return ValueTask.CompletedTask;
            }

            public ValueTask CloseAsync(string reason, CancellationToken ct = default)
            {
                _ = reason;
                _ = ct;

                if (Interlocked.Exchange(ref _closed, 1) == 1)
                    return ValueTask.CompletedTask;

                _outgoing.Complete();
                return ValueTask.CompletedTask;
            }
        }

        private sealed class NeverCompletingSendClientSession : IClientSession
        {
            private readonly TaskCompletionSource _never = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private int _sendCalls;

            public NeverCompletingSendClientSession(string connectionId)
            {
                ConnectionId = connectionId;
                RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, 1);
                LocalEndPoint = new IPEndPoint(IPAddress.Loopback, 2);
                LastActivityUtc = DateTime.UtcNow;
            }

            public int SendCalls => Volatile.Read(ref _sendCalls);

            public string ConnectionId { get; }
            public EndPoint RemoteEndPoint { get; }
            public EndPoint LocalEndPoint { get; }
            public bool IsSecureConnection => false;
            public ISet<string> EnabledCapabilities { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public string? Nick { get; set; }
            public string? UserName { get; set; }
            public bool PassAccepted { get; set; }
            public bool IsRegistered { get; set; }
            public DateTime LastActivityUtc { get; private set; }
            public DateTime LastPingUtc { get; private set; }
            public bool AwaitingPong { get; private set; }
            public string? LastPingToken { get; }
            public string UserModes => "";

            public bool TryApplyUserModes(string modeString, out string appliedModes)
            {
                _ = modeString;
                appliedModes = "+";
                return true;
            }

            public void OnInboundLine() => LastActivityUtc = DateTime.UtcNow;

            public void OnPingSent(string token)
            {
                _ = token;
                LastPingUtc = DateTime.UtcNow;
                AwaitingPong = true;
            }

            public void OnPongReceived(string? token)
            {
                _ = token;
                AwaitingPong = false;
                LastActivityUtc = DateTime.UtcNow;
            }

            public ValueTask SendAsync(string line, CancellationToken ct = default)
            {
                _ = line;
                _ = ct;
                Interlocked.Increment(ref _sendCalls);
                return new ValueTask(_never.Task);
            }

            public ValueTask CloseAsync(string reason, CancellationToken ct = default)
            {
                _ = reason;
                _ = ct;
                return ValueTask.CompletedTask;
            }
        }
    }
}
