namespace IRCd.Tests.TestUtils
{
    using System.Collections.Concurrent;
    using System.Net;

    using IRCd.Core.Abstractions;
    using IRCd.Core.Commands;
    using IRCd.Core.Commands.Contracts;
    using IRCd.Core.Protocol;
    using IRCd.Core.Services;
    using IRCd.Core.State;
    using IRCd.Services.DependencyInjection;
    using IRCd.Shared.Options;

    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.Hosting;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Logging.Abstractions;
    using Microsoft.Extensions.Options;

    public sealed class GoldenIrcHarness
    {
        private readonly ServiceProvider _provider;

        public CommandDispatcher Dispatcher { get; }
        public ServerState State { get; }
        public IRCd.Tests.TestDoubles.FakeSessionRegistry Sessions { get; }
        public GoldenTestClock Clock { get; }

        public GoldenIrcHarness(Action<IServiceCollection>? configureServices = null, Action<IrcOptions>? configureOptions = null)
        {
            var services = new ServiceCollection();

            Clock = new GoldenTestClock(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000));

            var options = new IrcOptions
            {
                ServerInfo = new ServerInfoOptions
                {
                    Name = "server",
                    Sid = "001",
                    Description = "test",
                    Network = "testnet",
                },
                Motd = new MotdOptions
                {
                    Lines = new[] { "test motd" },
                },
                Flood = new FloodOptions
                {
                    Commands = new CommandFloodOptions
                    {
                        Enabled = false,
                    },
                },
                RateLimit = new RateLimitOptions
                {
                    Enabled = false,
                },
            };
            configureOptions?.Invoke(options);

            services.AddSingleton<IOptions<IrcOptions>>(Options.Create(options));
            services.AddSingleton<IServerClock>(Clock);

            services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
            services.AddSingleton<IHostEnvironment>(new TestHostEnvironment());

            State = new ServerState();
            Sessions = new IRCd.Tests.TestDoubles.FakeSessionRegistry();

            services.AddSingleton(State);
            services.AddSingleton<ISessionRegistry>(Sessions);

            services.AddSingleton<IMetrics, NullMetrics>();

            // Core infra used by handlers
            services.AddSingleton(new IrcFormatter());
            services.AddSingleton<RoutingService>();
            services.AddSingleton<SilenceService>();
            services.AddSingleton<HostmaskService>();
            services.AddSingleton<WatchService>();
            services.AddSingleton<WhowasService>();
            services.AddSingleton<RateLimitService>();
            services.AddSingleton<LusersService>();

            // From Services DI extension (BanService etc)
            services.AddIrcServices();

            services.AddSingleton<MotdSender>();
            services.AddSingleton<RegistrationService>();

            // SASL handlers depend on this.
            services.AddSingleton<SaslService>();

            services.AddSingleton<ServerLinkService>();

            // Handlers under test (golden compatibility)
            services.AddSingleton<IIrcCommandHandler, IRCd.Core.Commands.Handlers.MotdHandler>();
            services.AddSingleton<IIrcCommandHandler, global::LusersHandler>();
            services.AddSingleton<IIrcCommandHandler, IRCd.Core.Commands.Handlers.NamesHandler>();
            services.AddSingleton<IIrcCommandHandler, IRCd.Core.Commands.Handlers.WhoHandler>();
            services.AddSingleton<IIrcCommandHandler, IRCd.Core.Commands.Handlers.WhoisHandler>();
            services.AddSingleton<IIrcCommandHandler, IRCd.Core.Commands.Handlers.PrivMsgHandler>();
            services.AddSingleton<IIrcCommandHandler, IRCd.Core.Commands.Handlers.JoinHandler>();
            services.AddSingleton<IIrcCommandHandler, IRCd.Core.Commands.Handlers.InviteHandler>();
            services.AddSingleton<IIrcCommandHandler, IRCd.Core.Commands.Handlers.NickHandler>();
            services.AddSingleton<IIrcCommandHandler, IRCd.Core.Commands.Handlers.ModeHandler>();
            services.AddSingleton<IIrcCommandHandler, IRCd.Core.Commands.Handlers.TopicHandler>();
            services.AddSingleton<IIrcCommandHandler, IRCd.Core.Commands.Handlers.CapHandler>();
            services.AddSingleton<IIrcCommandHandler, IRCd.Core.Commands.Handlers.AuthenticateHandler>();

            configureServices?.Invoke(services);

            _provider = services.BuildServiceProvider();

            Dispatcher = new CommandDispatcher(
                _provider.GetRequiredService<IEnumerable<IIrcCommandHandler>>(),
                _provider.GetRequiredService<RateLimitService>(),
                _provider.GetRequiredService<IMetrics>(),
                flood: null,
                clock: _provider.GetRequiredService<IServerClock>());
        }

        public GoldenTestSession CreateSession(string connectionId, bool registered = false, string? nick = null)
        {
            var session = new GoldenTestSession(connectionId)
            {
                Nick = nick,
                UserName = registered ? "ident" : null,
                IsRegistered = registered,
            };
            Sessions.Add(session);

            if (registered)
            {
                var user = new User
                {
                    ConnectionId = connectionId,
                    Nick = nick ?? connectionId,
                    UserName = "ident",
                    Host = "host",
                    RealName = "Real Name",
                    IsRegistered = true,
                    ConnectedAtUtc = Clock.UtcNow - TimeSpan.FromSeconds(100),
                    LastActivityUtc = Clock.UtcNow - TimeSpan.FromSeconds(10),
                };

                State.TryAddUser(user);
            }

            return session;
        }

        public async Task<string[]> SendRawAsync(GoldenTestSession session, string rawLine)
        {
            session.Clear();
            var msg = IrcParser.ParseLine(rawLine);
            await Dispatcher.DispatchAsync(session, msg, State, CancellationToken.None);
            return session.Sent.ToArray();
        }

        public async Task<string[]> SendRawBatchAsync(GoldenTestSession session, params string[] rawLines)
        {
            session.Clear();
            foreach (var line in rawLines)
            {
                var msg = IrcParser.ParseLine(line);
                await Dispatcher.DispatchAsync(session, msg, State, CancellationToken.None);
            }
            return session.Sent.ToArray();
        }

        public sealed class GoldenTestClock : IServerClock
        {
            public GoldenTestClock(DateTimeOffset initial)
            {
                UtcNow = initial;
            }

            public DateTimeOffset UtcNow { get; set; }
        }

        public sealed class GoldenTestSession : IClientSession
        {
            private readonly ConcurrentQueue<string> _sent = new();

            public GoldenTestSession(string connectionId)
            {
                ConnectionId = connectionId;
                RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, 12345);
                LocalEndPoint = new IPEndPoint(IPAddress.Loopback, 6667);
            }

            public string ConnectionId { get; }
            public EndPoint RemoteEndPoint { get; }
            public EndPoint LocalEndPoint { get; }

            public bool IsSecureConnection => false;

            public ISet<string> EnabledCapabilities { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            public string? Nick { get; set; }
            public string? UserName { get; set; }
            public bool PassAccepted { get; set; }
            public bool IsRegistered { get; set; }

            public DateTime LastActivityUtc { get; private set; } = DateTime.UtcNow;
            public DateTime LastPingUtc { get; private set; } = DateTime.UtcNow;
            public bool AwaitingPong { get; private set; }
            public string? LastPingToken { get; private set; }

            public string UserModes => string.Empty;
            public bool TryApplyUserModes(string modeString, out string appliedModes)
            {
                appliedModes = string.Empty;

                if (string.IsNullOrWhiteSpace(modeString))
                    return false;

                // Minimal validation for golden tests: only accept +i/-i, +Z/-Z, +o/-o sequences.
                // ModeHandler is responsible for enforcing privileges; this only gates unknown flags.
                foreach (var ch in modeString)
                {
                    if (ch is '+' or '-')
                        continue;

                    if (ch is 'i' or 'Z' or 'o')
                        continue;

                    return false;
                }

                appliedModes = modeString;
                return true;
            }

            public void OnInboundLine()
            {
                LastActivityUtc = DateTime.UtcNow;
            }

            public void OnPingSent(string token)
            {
                LastPingUtc = DateTime.UtcNow;
                AwaitingPong = true;
                LastPingToken = token;
            }

            public void OnPongReceived(string? token)
            {
                AwaitingPong = false;
                LastPingToken = token;
            }

            public IReadOnlyCollection<string> Sent => _sent.ToArray();

            public ValueTask SendAsync(string line, CancellationToken ct = default)
            {
                _sent.Enqueue(line);
                return ValueTask.CompletedTask;
            }

            public ValueTask CloseAsync(string reason, CancellationToken ct = default)
                => ValueTask.CompletedTask;

            public void Clear()
            {
                while (_sent.TryDequeue(out _)) { }
            }
        }

        private sealed class NullMetrics : IMetrics
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

        private sealed class TestHostEnvironment : IHostEnvironment
        {
            public string EnvironmentName { get; set; } = Environments.Development;
            public string ApplicationName { get; set; } = "IRCd.Tests";
            public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
            public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; }
                = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(AppContext.BaseDirectory);
        }
    }
}
