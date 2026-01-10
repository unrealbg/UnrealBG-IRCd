namespace IRCd.Tests
{
    using System;
    using System.Collections.Generic;
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

    using Xunit;

    public sealed class CommandDispatcherFloodTests
    {
        private sealed class TestClock : IServerClock
        {
            public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UtcNow;
        }

        private sealed class TestSession : IClientSession
        {
            public string ConnectionId { get; set; } = "c1";
            public EndPoint RemoteEndPoint { get; } = new IPEndPoint(IPAddress.Loopback, 1234);
            public EndPoint LocalEndPoint { get; } = new IPEndPoint(IPAddress.Loopback, 6667);
            public bool IsSecureConnection => false;

            public ISet<string> EnabledCapabilities { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            public string? Nick { get; set; }
            public string? UserName { get; set; }
            public bool PassAccepted { get; set; }
            public bool IsRegistered { get; set; }

            public DateTime LastActivityUtc { get; } = DateTime.UtcNow;
            public DateTime LastPingUtc { get; } = DateTime.UtcNow;
            public bool AwaitingPong { get; }
            public string? LastPingToken { get; }

            public string UserModes => string.Empty;
            public bool TryApplyUserModes(string modeString, out string appliedModes) { appliedModes = "+"; return true; }

            public void OnInboundLine() { }
            public void OnPingSent(string token) { }
            public void OnPongReceived(string? token) { }

            public readonly List<string> Sent = new();

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
                return ValueTask.CompletedTask;
            }
        }

        private sealed class TestMetrics : IMetrics
        {
            public void ConnectionAccepted(bool secure) { }
            public void ConnectionClosed(bool secure) { }
            public void UserRegistered() { }
            public void ChannelCreated() { }
            public void CommandProcessed(string command) { }
            public void FloodKick() { }
            public void OutboundQueueDepth(long depth) { }
            public void OutboundQueueDrop() { }
            public void OutboundQueueOverflowDisconnect() { }
            public MetricsSnapshot GetSnapshot() => new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        }

        private sealed class OptionsMonitorStub<T> : IOptionsMonitor<T> where T : class
        {
            private readonly T _value;

            public OptionsMonitorStub(T value) => _value = value;

            public T CurrentValue => _value;

            public T Get(string? name) => _value;

            public IDisposable? OnChange(Action<T, string?> listener) => null;
        }

        private sealed class CountingHandler : IIrcCommandHandler
        {
            public CountingHandler(string command) => Command = command;

            public string Command { get; }

            public int Calls { get; private set; }

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
        public async Task Privmsg_PerTargetFlood_BlocksSixthMessage()
        {
            var opts = new IrcOptions { RateLimit = new RateLimitOptions { Enabled = false } };
            opts.Flood.Commands.Enabled = true;
            opts.Flood.Commands.ViolationsBeforeDisconnect = 999; // test expects throttling, not disconnect
            opts.Flood.Commands.Messages.MaxEvents = 5;
            opts.Flood.Commands.Messages.WindowSeconds = 10;
            opts.Flood.Commands.Messages.PerTarget = true;

            var rateLimit = new RateLimitService(new OptionsMonitorStub<IrcOptions>(opts));
            var flood = new FloodService(Options.Create(opts), new TestClock());
            var handler = new CountingHandler("PRIVMSG");
            var dispatcher = new CommandDispatcher(new[] { handler }, rateLimit, new TestMetrics(), flood);

            var state = new ServerState();
            state.TryAddUser(new User { ConnectionId = "c1", Nick = "n", UserName = "u", Host = "h", IsRegistered = true });

            var s = new TestSession { ConnectionId = "c1", Nick = "n", UserName = "u", IsRegistered = true };

            for (var i = 0; i < 6; i++)
            {
                await dispatcher.DispatchAsync(s, new IrcMessage(null, "PRIVMSG", new[] { "#chan" }, "hi"), state, CancellationToken.None);
            }

            Assert.Equal(5, handler.Calls);
            Assert.Contains(s.Sent, l => l.Contains("Flood detected", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task JoinPartFlood_BlocksEleventhJoin()
        {
            var opts = new IrcOptions { RateLimit = new RateLimitOptions { Enabled = false } };
            opts.Flood.Commands.Enabled = true;
            opts.Flood.Commands.ViolationsBeforeDisconnect = 999;
            opts.Flood.Commands.JoinPart.MaxEvents = 10;
            opts.Flood.Commands.JoinPart.WindowSeconds = 30;

            var rateLimit = new RateLimitService(new OptionsMonitorStub<IrcOptions>(opts));
            var flood = new FloodService(Options.Create(opts), new TestClock());
            var handler = new CountingHandler("JOIN");
            var dispatcher = new CommandDispatcher(new[] { handler }, rateLimit, new TestMetrics(), flood);

            var state = new ServerState();
            state.TryAddUser(new User { ConnectionId = "c1", Nick = "n", UserName = "u", Host = "h", IsRegistered = true });

            var s = new TestSession { ConnectionId = "c1", Nick = "n", UserName = "u", IsRegistered = true };

            for (var i = 0; i < 11; i++)
            {
                await dispatcher.DispatchAsync(s, new IrcMessage(null, "JOIN", new[] { "#c" }, null), state, CancellationToken.None);
            }

            Assert.Equal(10, handler.Calls);
            Assert.Contains(s.Sent, l => l.Contains("Flood detected", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task NickFlood_BlocksSixthNick()
        {
            var opts = new IrcOptions { RateLimit = new RateLimitOptions { Enabled = false } };
            opts.Flood.Commands.Enabled = true;
            opts.Flood.Commands.ViolationsBeforeDisconnect = 999;
            opts.Flood.Commands.Nick.MaxEvents = 5;
            opts.Flood.Commands.Nick.WindowSeconds = 60;

            var rateLimit = new RateLimitService(new OptionsMonitorStub<IrcOptions>(opts));
            var flood = new FloodService(Options.Create(opts), new TestClock());
            var handler = new CountingHandler("NICK");
            var dispatcher = new CommandDispatcher(new[] { handler }, rateLimit, new TestMetrics(), flood);

            var state = new ServerState();
            state.TryAddUser(new User { ConnectionId = "c1", Nick = "n", UserName = "u", Host = "h", IsRegistered = true });

            var s = new TestSession { ConnectionId = "c1", Nick = "n", UserName = "u", IsRegistered = true };

            for (var i = 0; i < 6; i++)
            {
                await dispatcher.DispatchAsync(s, new IrcMessage(null, "NICK", new[] { "n" + i }, null), state, CancellationToken.None);
            }

            Assert.Equal(5, handler.Calls);
            Assert.Contains(s.Sent, l => l.Contains("Flood detected", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task Oper_IsExempt_FromFloodLimits()
        {
            var opts = new IrcOptions { RateLimit = new RateLimitOptions { Enabled = false } };
            opts.Flood.Commands.Enabled = true;
            opts.Flood.Commands.ExemptOpers = true;
            opts.Flood.Commands.Messages.MaxEvents = 1;
            opts.Flood.Commands.Messages.WindowSeconds = 60;
            opts.Flood.Commands.Messages.PerTarget = true;

            var rateLimit = new RateLimitService(new OptionsMonitorStub<IrcOptions>(opts));
            var flood = new FloodService(Options.Create(opts), new TestClock());
            var handler = new CountingHandler("PRIVMSG");
            var dispatcher = new CommandDispatcher(new[] { handler }, rateLimit, new TestMetrics(), flood);

            var state = new ServerState();
            state.TryAddUser(new User { ConnectionId = "c1", Nick = "oper", UserName = "u", Host = "h", IsRegistered = true, OperName = "oper" });

            var s = new TestSession { ConnectionId = "c1", Nick = "oper", UserName = "u", IsRegistered = true };

            for (var i = 0; i < 20; i++)
            {
                await dispatcher.DispatchAsync(s, new IrcMessage(null, "PRIVMSG", new[] { "#chan" }, "hi"), state, CancellationToken.None);
            }

            Assert.Equal(20, handler.Calls);
            Assert.DoesNotContain(s.Sent, l => l.Contains("Flood detected", StringComparison.OrdinalIgnoreCase));
        }
    }
}
