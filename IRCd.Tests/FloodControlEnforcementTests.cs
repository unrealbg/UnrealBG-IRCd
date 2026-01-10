namespace IRCd.Tests
{
    using System;
    using System.Net;
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Core.Abstractions;
    using IRCd.Core.Commands;
    using IRCd.Core.Commands.Contracts;
    using IRCd.Core.Protocol;
    using IRCd.Core.Services;
    using IRCd.Core.State;
    using IRCd.Shared.Options;

    using Microsoft.Extensions.Options;

    public sealed class FloodControlEnforcementTests
    {
        private sealed class FakeClock : IServerClock
        {
            public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UtcNow;
        }

        private sealed class TestMetrics : IMetrics
        {
            public int FloodKicks;
            public void ConnectionAccepted(bool secure) { }
            public void ConnectionClosed(bool secure) { }
            public void UserRegistered() { }
            public void ChannelCreated() { }
            public void CommandProcessed(string command) { }
            public void FloodKick() => FloodKicks++;
            public void OutboundQueueDepth(long depth) { }
            public void OutboundQueueDrop() { }
            public void OutboundQueueOverflowDisconnect() { }
            public MetricsSnapshot GetSnapshot() => new(0, 0, 0, 0, 0, 0, 0, FloodKicks, 0, 0, 0, 0);
        }

        private sealed class RecordingSession : IClientSession
        {
            private int _closed;

            public RecordingSession(string id)
            {
                ConnectionId = id;
                RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, 1234);
                LocalEndPoint = new IPEndPoint(IPAddress.Loopback, 6667);
                LastActivityUtc = DateTime.UtcNow;
            }

            public bool Closed => Volatile.Read(ref _closed) == 1;

            public string ConnectionId { get; }
            public EndPoint RemoteEndPoint { get; }
            public EndPoint LocalEndPoint { get; }
            public bool IsSecureConnection => false;
            public ISet<string> EnabledCapabilities { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public string? Nick { get; set; } = "n";
            public string? UserName { get; set; } = "u";
            public bool PassAccepted { get; set; }
            public bool IsRegistered { get; set; } = true;
            public DateTime LastActivityUtc { get; private set; }
            public DateTime LastPingUtc { get; private set; }
            public bool AwaitingPong { get; private set; }
            public string? LastPingToken { get; private set; }
            public string UserModes => string.Empty;

            public readonly System.Collections.Generic.List<string> Sent = new();

            public bool TryApplyUserModes(string modeString, out string appliedModes) { appliedModes = "+"; return true; }
            public void OnInboundLine() => LastActivityUtc = DateTime.UtcNow;
            public void OnPingSent(string token) { LastPingToken = token; LastPingUtc = DateTime.UtcNow; AwaitingPong = true; }
            public void OnPongReceived(string? token) { _ = token; AwaitingPong = false; LastPingToken = null; LastActivityUtc = DateTime.UtcNow; }

            public ValueTask SendAsync(string line, CancellationToken ct = default)
            {
                _ = ct;
                Sent.Add(line);
                return ValueTask.CompletedTask;
            }

            public ValueTask CloseAsync(string reason, CancellationToken ct = default)
            {
                _ = reason;
                _ = ct;
                Interlocked.Exchange(ref _closed, 1);
                return ValueTask.CompletedTask;
            }
        }

        private sealed class NoopHandler : IIrcCommandHandler
        {
            public NoopHandler(string cmd) => Command = cmd;
            public string Command { get; }
            public int Calls;

            public ValueTask HandleAsync(IClientSession session, IrcMessage msg, ServerState state, CancellationToken ct)
            {
                _ = session;
                _ = msg;
                _ = state;
                _ = ct;
                Calls++;
                return ValueTask.CompletedTask;
            }
        }

        [Fact]
        public async Task ExceedingBucket_WarnsThenDisconnects_WithError()
        {
            var clock = new FakeClock { UtcNow = DateTimeOffset.UtcNow };

            var opts = new IrcOptions();
            opts.Flood.Commands.Enabled = true;
            opts.Flood.Commands.ExemptOpers = false;
            opts.Flood.Commands.ViolationsBeforeDisconnect = 2;
            opts.Flood.Commands.WarningCooldownSeconds = 0;
            opts.Flood.Commands.WhoWhois.MaxEvents = 1;
            opts.Flood.Commands.WhoWhois.WindowSeconds = 60;

            var flood = new FloodService(Options.Create(opts), clock);
            var metrics = new TestMetrics();
            var rateLimit = new RateLimitService(new OptionsMonitorStub<IrcOptions>(opts));

            var handler = new NoopHandler("WHO");
            var dispatcher = new CommandDispatcher(new[] { handler }, rateLimit, metrics, flood);

            var state = new ServerState();
            state.TryAddUser(new User { ConnectionId = "c1", Nick = "n", UserName = "u", Host = "h", IsRegistered = true });

            var s = new RecordingSession("c1");

            // First allowed
            await dispatcher.DispatchAsync(s, new IrcMessage(null, "WHO", new[] { "#c" }, null), state, CancellationToken.None);
            Assert.False(s.Closed);

            // First violation: warn + throttle
            await dispatcher.DispatchAsync(s, new IrcMessage(null, "WHO", new[] { "#c" }, null), state, CancellationToken.None);
            Assert.False(s.Closed);

            // Second violation: disconnect
            await dispatcher.DispatchAsync(s, new IrcMessage(null, "WHO", new[] { "#c" }, null), state, CancellationToken.None);
            Assert.True(s.Closed);
            Assert.Contains(s.Sent, l => l.Contains("ERROR :Excess Flood", StringComparison.OrdinalIgnoreCase));
            Assert.True(metrics.FloodKicks >= 1);
        }

        [Fact]
        public async Task Bucket_Refills_AfterWindow_AllowsAgain()
        {
            var clock = new FakeClock { UtcNow = DateTimeOffset.UtcNow };

            var opts = new IrcOptions();
            opts.Flood.Commands.Enabled = true;
            opts.Flood.Commands.ViolationsBeforeDisconnect = 999;
            opts.Flood.Commands.WarningCooldownSeconds = 0;
            opts.Flood.Commands.Nick.MaxEvents = 1;
            opts.Flood.Commands.Nick.WindowSeconds = 10;

            var flood = new FloodService(Options.Create(opts), clock);
            var metrics = new TestMetrics();
            var rateLimit = new RateLimitService(new OptionsMonitorStub<IrcOptions>(opts));

            var handler = new NoopHandler("NICK");
            var dispatcher = new CommandDispatcher(new[] { handler }, rateLimit, metrics, flood);

            var state = new ServerState();
            state.TryAddUser(new User { ConnectionId = "c1", Nick = "n", UserName = "u", Host = "h", IsRegistered = true });

            var s = new RecordingSession("c1");

            await dispatcher.DispatchAsync(s, new IrcMessage(null, "NICK", new[] { "n1" }, null), state, CancellationToken.None);
            await dispatcher.DispatchAsync(s, new IrcMessage(null, "NICK", new[] { "n2" }, null), state, CancellationToken.None);

            Assert.Equal(1, handler.Calls);

            clock.UtcNow = clock.UtcNow.AddSeconds(11);

            await dispatcher.DispatchAsync(s, new IrcMessage(null, "NICK", new[] { "n3" }, null), state, CancellationToken.None);
            Assert.Equal(2, handler.Calls);
        }

        [Fact]
        public async Task ModeBucket_ThrottlesWhenTooFast()
        {
            var clock = new FakeClock { UtcNow = DateTimeOffset.UtcNow };

            var opts = new IrcOptions();
            opts.Flood.Commands.Enabled = true;
            opts.Flood.Commands.ViolationsBeforeDisconnect = 999;
            opts.Flood.Commands.WarningCooldownSeconds = 0;
            opts.Flood.Commands.Mode.MaxEvents = 2;
            opts.Flood.Commands.Mode.WindowSeconds = 60;

            var flood = new FloodService(Options.Create(opts), clock);
            var metrics = new TestMetrics();
            var rateLimit = new RateLimitService(new OptionsMonitorStub<IrcOptions>(opts));

            var handler = new NoopHandler("MODE");
            var dispatcher = new CommandDispatcher(new[] { handler }, rateLimit, metrics, flood);

            var state = new ServerState();
            state.TryAddUser(new User { ConnectionId = "c1", Nick = "n", UserName = "u", Host = "h", IsRegistered = true });

            var s = new RecordingSession("c1");

            await dispatcher.DispatchAsync(s, new IrcMessage(null, "MODE", new[] { "#c", "+m" }, null), state, CancellationToken.None);
            await dispatcher.DispatchAsync(s, new IrcMessage(null, "MODE", new[] { "#c", "+m" }, null), state, CancellationToken.None);
            await dispatcher.DispatchAsync(s, new IrcMessage(null, "MODE", new[] { "#c", "+m" }, null), state, CancellationToken.None);

            Assert.Equal(2, handler.Calls);
            Assert.Contains(s.Sent, l => l.Contains("Flood detected", StringComparison.OrdinalIgnoreCase));
        }

        private sealed class OptionsMonitorStub<T> : IOptionsMonitor<T> where T : class
        {
            private readonly T _value;
            public OptionsMonitorStub(T value) => _value = value;
            public T CurrentValue => _value;
            public T Get(string? name) => _value;
            public IDisposable? OnChange(Action<T, string?> listener) => null;
        }
    }
}
