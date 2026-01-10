namespace IRCd.Tests
{
    using System;
    using System.Net;
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Core.Abstractions;
    using IRCd.Core.Commands.Handlers;
    using IRCd.Core.Protocol;
    using IRCd.Core.Services;
    using IRCd.Core.State;
    using IRCd.Services;
    using IRCd.Services.DependencyInjection;
    using IRCd.Shared.Options;
    using IRCd.Tests.TestDoubles;

    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.Logging.Abstractions;
    using Microsoft.Extensions.Options;

    using Xunit;

    public sealed class BotServTests
    {
        private sealed class OptionsMonitorStub<T> : IOptionsMonitor<T> where T : class
        {
            private readonly T _value;

            public OptionsMonitorStub(T value) => _value = value;

            public T CurrentValue => _value;

            public T Get(string? name) => _value;

            public IDisposable? OnChange(Action<T, string?> listener) => null;
        }

        private sealed class TestSession : IClientSession
        {
            public string ConnectionId { get; set; } = "c1";
            public EndPoint RemoteEndPoint { get; } = new IPEndPoint(IPAddress.Loopback, 1234);
            public EndPoint LocalEndPoint { get; } = new IPEndPoint(IPAddress.Loopback, 6667);
            public bool IsSecureConnection => false;

            public System.Collections.Generic.ISet<string> EnabledCapabilities { get; } =
                new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);

            public string? Nick { get; set; }
            public string? UserName { get; set; }
            public bool PassAccepted { get; set; }
            public bool IsRegistered { get; set; }

            public DateTime LastActivityUtc { get; } = DateTime.UtcNow;
            public DateTime LastPingUtc { get; } = DateTime.UtcNow;
            public bool AwaitingPong { get; }
            public string? LastPingToken { get; }

            public string UserModes => string.Empty;
            public bool TryApplyUserModes(string modeString, out string appliedModes) { appliedModes = modeString; return true; }

            public void OnInboundLine() { }
            public void OnPingSent(string token) { }
            public void OnPongReceived(string? token) { }

            public readonly System.Collections.Generic.List<string> Sent = new();

            public ValueTask SendAsync(string line, CancellationToken ct = default)
            {
                Sent.Add(line);
                return ValueTask.CompletedTask;
            }

            public ValueTask CloseAsync(string reason, CancellationToken ct = default) => ValueTask.CompletedTask;
        }

        [Fact]
        public async Task BotServ_List_ShowsConfiguredBots()
        {
            var state = new ServerState();
            var sessions = new FakeSessionRegistry();
            var routing = new RoutingService(sessions, new IrcFormatter());
            var silence = new SilenceService();

            var opts = Options.Create(new IrcOptions
            {
                ServerInfo = new ServerInfoOptions { Name = "srv", Sid = "001" },
                Services = new ServicesOptions
                {
                    BotServ = new BotServOptions
                    {
                        Bots = new[] { new BotOptions { Nick = "TriviaBot", RealName = "Trivia" } }
                    }
                },
                Classes = new[] { new OperClassOptions { Name = "botop", Capabilities = new[] { "botserv" } } }
            });

            ServiceUserSeeder.EnsureServiceUsers(state, opts.Value, NullLogger.Instance);

            var services = new ServiceCollection();
            services.AddSingleton<ISessionRegistry>(sessions);
            services.AddSingleton(routing);
            services.AddSingleton(silence);
            services.AddSingleton(new HostmaskService());
            services.AddSingleton<IOptions<IrcOptions>>(opts);
            services.AddIrcServices();

            using var sp = services.BuildServiceProvider();

            var links = new ServerLinkService(
                NullLogger<ServerLinkService>.Instance,
                new OptionsMonitorStub<IrcOptions>(opts.Value),
                state,
                routing,
                sessions,
                silence,
                new WatchService(opts, routing));

            var h = new PrivMsgHandler(
                routing,
                links,
                sp.GetRequiredService<HostmaskService>(),
                opts,
                silence,
                sp.GetRequiredService<IServiceCommandDispatcher>(),
                sp.GetRequiredService<IServiceChannelEvents>());

            state.TryAddUser(new User { ConnectionId = "oper", Nick = "oper", UserName = "o", Host = "h", IsRegistered = true, Modes = UserModes.Operator, OperClass = "botop" });
            var operSess = new TestSession { ConnectionId = "oper", Nick = "oper", UserName = "o", IsRegistered = true };
            sessions.Add(operSess);

            await h.HandleAsync(operSess, new IrcMessage(null, "PRIVMSG", new[] { "BotServ" }, "LIST"), state, CancellationToken.None);

            Assert.Contains(operSess.Sent, l => l.Contains("TriviaBot", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task BotServ_AssignJoinSayAct_Works()
        {
            var state = new ServerState();
            var sessions = new FakeSessionRegistry();
            var routing = new RoutingService(sessions, new IrcFormatter());
            var silence = new SilenceService();

            var opts = Options.Create(new IrcOptions
            {
                ServerInfo = new ServerInfoOptions { Name = "srv", Sid = "001" },
                Services = new ServicesOptions
                {
                    BotServ = new BotServOptions
                    {
                        Bots = new[] { new BotOptions { Nick = "TriviaBot", RealName = "Trivia" } }
                    }
                },
                Classes = new[] { new OperClassOptions { Name = "botop", Capabilities = new[] { "botserv" } } }
            });

            ServiceUserSeeder.EnsureServiceUsers(state, opts.Value, NullLogger.Instance);

            var services = new ServiceCollection();
            services.AddSingleton<ISessionRegistry>(sessions);
            services.AddSingleton(routing);
            services.AddSingleton(silence);
            services.AddSingleton(new HostmaskService());
            services.AddSingleton<IOptions<IrcOptions>>(opts);
            services.AddIrcServices();

            using var sp = services.BuildServiceProvider();

            var links = new ServerLinkService(
                NullLogger<ServerLinkService>.Instance,
                new OptionsMonitorStub<IrcOptions>(opts.Value),
                state,
                routing,
                sessions,
                silence,
                new WatchService(opts, routing));

            var h = new PrivMsgHandler(
                routing,
                links,
                sp.GetRequiredService<HostmaskService>(),
                opts,
                silence,
                sp.GetRequiredService<IServiceCommandDispatcher>(),
                sp.GetRequiredService<IServiceChannelEvents>());

            state.TryAddUser(new User { ConnectionId = "oper", Nick = "oper", UserName = "o", Host = "h", IsRegistered = true, Modes = UserModes.Operator, OperClass = "botop" });
            state.TryAddUser(new User { ConnectionId = "peer", Nick = "peer", UserName = "p", Host = "peer.host", IsRegistered = true, Modes = UserModes.None });

            state.TryJoinChannel("oper", "oper", "#c");
            state.TryJoinChannel("peer", "peer", "#c");

            var operSess = new TestSession { ConnectionId = "oper", Nick = "oper", UserName = "o", IsRegistered = true };
            var peerSess = new TestSession { ConnectionId = "peer", Nick = "peer", UserName = "p", IsRegistered = true };
            sessions.Add(operSess);
            sessions.Add(peerSess);

            await h.HandleAsync(operSess, new IrcMessage(null, "PRIVMSG", new[] { "BotServ" }, "ASSIGN #c TriviaBot"), state, CancellationToken.None);
            await h.HandleAsync(operSess, new IrcMessage(null, "PRIVMSG", new[] { "BotServ" }, "JOIN #c"), state, CancellationToken.None);

            await h.HandleAsync(operSess, new IrcMessage(null, "PRIVMSG", new[] { "BotServ" }, "SAY #c hello"), state, CancellationToken.None);
            Assert.Contains(peerSess.Sent, l => l.Contains(":TriviaBot!bot@services.local PRIVMSG #c :hello", StringComparison.OrdinalIgnoreCase));

            await h.HandleAsync(operSess, new IrcMessage(null, "PRIVMSG", new[] { "BotServ" }, "ACT #c waves"), state, CancellationToken.None);
            Assert.Contains(peerSess.Sent, l => l.Contains(":TriviaBot!bot@services.local PRIVMSG #c :\u0001ACTION waves\u0001", StringComparison.OrdinalIgnoreCase));
        }
    }
}
