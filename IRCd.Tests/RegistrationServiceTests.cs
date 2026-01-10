namespace IRCd.Tests;

using System.Threading;
using System.Threading.Tasks;

using IRCd.Core.Abstractions;
using IRCd.Core.Protocol;
using IRCd.Core.Services;
using IRCd.Core.State;
using IRCd.Shared.Options;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Xunit;

public sealed class RegistrationServiceTests
{
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

    private sealed class TestSession : IClientSession
    {
        public string ConnectionId { get; } = "c1";
        public System.Net.EndPoint RemoteEndPoint { get; } = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 1234);
        public System.Net.EndPoint LocalEndPoint { get; } = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 6667);
        public bool IsSecureConnection => false;

        public ISet<string> EnabledCapabilities { get; } =
            new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);

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
            Sent.Add(line);
            return ValueTask.CompletedTask;
        }

        public ValueTask CloseAsync(string reason, CancellationToken ct = default)
            => ValueTask.CompletedTask;
    }

    private sealed class TestHostEnv : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "IRCd.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    [Fact]
    public async Task TryCompleteRegistrationAsync_SendsWelcomeAndIsupport()
    {
        var opts = Options.Create(new IrcOptions
        {
            ServerInfo = new ServerInfoOptions { Name = "srv", Network = "net" },
            Motd = new MotdOptions { Lines = new[] { "hi" } }
        });

        var motd = new MotdSender(new OptionsMonitorStub<IrcOptions>(opts.Value), new TestHostEnv(), NullLogger<MotdSender>.Instance);
        var routing = new RoutingService(new IRCd.Tests.TestDoubles.FakeSessionRegistry(), new IrcFormatter());
        var watch = new WatchService(opts, routing);
        var banRepo = new InMemoryBanRepository();
        var banService = new BanService(banRepo, NullLogger<BanService>.Instance);
        var reg = new RegistrationService(opts, motd, new TestMetrics(), watch, banService, auth: null);

        var state = new ServerState();
        state.TryAddUser(new User { ConnectionId = "c1" });

        var s = new TestSession { Nick = "nick", UserName = "user" };

        await reg.TryCompleteRegistrationAsync(s, state, CancellationToken.None);

        Assert.True(s.IsRegistered);
        Assert.Contains(s.Sent, l => l.Contains(" 001 nick "));
        Assert.Contains(s.Sent, l => l.Contains(" 005 nick "));
    }

    private sealed class OptionsMonitorStub<T> : IOptionsMonitor<T>
    {
        public OptionsMonitorStub(T value) => CurrentValue = value;

        public T CurrentValue { get; }

        public T Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
