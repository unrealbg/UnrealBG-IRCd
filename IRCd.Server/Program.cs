using System.IO;

using IRCd.Core.Abstractions;
using IRCd.Core.Commands;
using IRCd.Core.Commands.Contracts;
using IRCd.Core.Commands.Handlers;
using IRCd.Core.Protocol;
using IRCd.Core.Services;
using IRCd.Core.State;
using IRCd.Services.DependencyInjection;
using IRCd.Server.HostedServices;
using IRCd.Shared.Options;
using IRCd.Transport.Tcp;
using IRCd.Transport.Tls;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using IRCd.Core.Config;

var host = Host.CreateDefaultBuilder(args)
    .UseContentRoot(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..")))
    .ConfigureAppConfiguration((ctx, cfg) =>
    {
        var contentRoot = ctx.HostingEnvironment.ContentRootPath;
        cfg.AddJsonFile(Path.Combine(contentRoot, "appsettings.json"), optional: true, reloadOnChange: true);
        cfg.AddJsonFile(Path.Combine(contentRoot, $"appsettings.{ctx.HostingEnvironment.EnvironmentName}.json"), optional: true);
        cfg.AddEnvironmentVariables(prefix: "IRCD_");
        cfg.AddCommandLine(args);
    })
    .ConfigureLogging(logging =>
    {
        logging.ClearProviders();
        logging.AddConsole();
    })
    .ConfigureServices((ctx, services) =>
    {
        // Options
        services.AddOptions<IrcOptions>()
            .Bind(ctx.Configuration.GetSection("Irc"))
            .PostConfigure(o =>
            {
                var conf = o.ConfigFile;
                if (string.IsNullOrWhiteSpace(conf))
                    conf = "ircd.conf";

                var confPath = Path.IsPathRooted(conf)
                    ? conf
                    : Path.Combine(ctx.HostingEnvironment.ContentRootPath, conf);

                if (File.Exists(confPath))
                {
                    IrcdConfLoader.ApplyConfFile(o, confPath);
                }
            });

        // Core state
        services.AddSingleton<ServerState>();

        // Session registry (one and only)
        services.AddSingleton<InMemorySessionRegistry>();
        services.AddSingleton<ISessionRegistry>(sp => sp.GetRequiredService<InMemorySessionRegistry>());

        // Routing uses the same registry
        services.AddSingleton<IrcFormatter>();
        services.AddSingleton<RoutingService>();

        // IRC services (NickServ, etc.)
        services.AddIrcServices();

        // Observability
            services.AddSingleton<WatchService>();
        services.AddSingleton<IMetrics, DefaultMetrics>();

        // Core services
        services.AddSingleton<RegistrationService>();
        services.AddSingleton<RateLimitService>();
        services.AddSingleton<ConnectionGuardService>();
        services.AddSingleton<ConnectionAuthService>();
        services.AddSingleton<PingService>();
        services.AddHostedService<PingHostedService>();
        services.AddSingleton<LusersService>();
        services.AddSingleton<HostmaskService>();
        services.AddSingleton<SilenceService>();
        services.AddSingleton<MotdSender>();
        services.AddSingleton<ServerLinkService>();
        services.AddSingleton<RuntimeKLineService>();
            services.AddSingleton<IIrcCommandHandler, WatchHandler>();
        services.AddSingleton<RuntimeDLineService>();
        services.AddSingleton<WhowasService>();

        // Flood protection

        // Command handlers
        services.AddSingleton<IIrcCommandHandler, PingHandler>();
        services.AddSingleton<IIrcCommandHandler, CapHandler>();
        services.AddSingleton<IIrcCommandHandler, PassHandler>();
        services.AddSingleton<IIrcCommandHandler, NickHandler>();
        services.AddSingleton<IIrcCommandHandler, UserHandler>();
        services.AddSingleton<IIrcCommandHandler, JoinHandler>();
        services.AddSingleton<IIrcCommandHandler, PartHandler>();
        services.AddSingleton<IIrcCommandHandler, PrivMsgHandler>();
        services.AddSingleton<IIrcCommandHandler, NoticeHandler>();
        services.AddSingleton<IIrcCommandHandler, QuitHandler>();
        services.AddSingleton<IIrcCommandHandler, NamesHandler>();
        services.AddSingleton<IIrcCommandHandler, WhoHandler>();
        services.AddSingleton<IIrcCommandHandler, WhoisHandler>();
        services.AddSingleton<IIrcCommandHandler, ModeHandler>();
        services.AddSingleton<IIrcCommandHandler, TopicHandler>();
        services.AddSingleton<IIrcCommandHandler, KickHandler>();
        services.AddSingleton<IIrcCommandHandler, KickBanHandler>();
        services.AddSingleton<IIrcCommandHandler, InviteHandler>();
        services.AddSingleton<IIrcCommandHandler, KnockHandler>();
        services.AddSingleton<IIrcCommandHandler, ListHandler>();
        services.AddSingleton<IIrcCommandHandler, AwayHandler>();
        services.AddSingleton<IIrcCommandHandler, IsonHandler>();
        services.AddSingleton<IIrcCommandHandler, SilenceHandler>();
        services.AddSingleton<IIrcCommandHandler, ChghostHandler>();
        services.AddSingleton<IIrcCommandHandler, Svs2modeHandler>();
        services.AddSingleton<IIrcCommandHandler, SvsjoinHandler>();
        services.AddSingleton<IIrcCommandHandler, SvsnickHandler>();
        services.AddSingleton<IIrcCommandHandler, SvspartHandler>();
        services.AddSingleton<IIrcCommandHandler, OperwhoHandler>();
        services.AddSingleton<IIrcCommandHandler, OperwhoisHandler>();
        services.AddSingleton<IIrcCommandHandler, TimeHandler>();
        services.AddSingleton<IIrcCommandHandler, UptimeHandler>();
        services.AddSingleton<IIrcCommandHandler, VersionHandler>();
        services.AddSingleton<IIrcCommandHandler, AdminHandler>();
        services.AddSingleton<IIrcCommandHandler, InfoHandler>();
        services.AddSingleton<IIrcCommandHandler, CreditsHandler>();
        services.AddSingleton<IIrcCommandHandler, MotdHandler>();
        services.AddSingleton<IIrcCommandHandler, LinksHandler>();
        services.AddSingleton<IIrcCommandHandler, MapHandler>();
        services.AddSingleton<IIrcCommandHandler, ModulesHandler>();
        services.AddSingleton<IIrcCommandHandler, RulesHandler>();
        services.AddSingleton<IIrcCommandHandler, LusersHandler>();
        services.AddSingleton<IIrcCommandHandler, StatsHandler>();
        services.AddSingleton<IIrcCommandHandler, TraceHandler>();
        services.AddSingleton<IIrcCommandHandler, UserhostHandler>();
        services.AddSingleton<IIrcCommandHandler, WallopsHandler>();
        services.AddSingleton<IIrcCommandHandler, PongHandler>();
        services.AddSingleton<IIrcCommandHandler, OperHandler>();
        services.AddSingleton<IIrcCommandHandler, KillHandler>();
        services.AddSingleton<IIrcCommandHandler, MetricsHandler>();
        services.AddSingleton<IIrcCommandHandler, RehashHandler>();
        services.AddSingleton<IIrcCommandHandler, SquitHandler>();
        services.AddSingleton<IIrcCommandHandler, DieHandler>();
        services.AddSingleton<IIrcCommandHandler, RestartHandler>();
        services.AddSingleton<IIrcCommandHandler, KlineHandler>();
        services.AddSingleton<IIrcCommandHandler, UnklineHandler>();
        services.AddSingleton<IIrcCommandHandler, DlineHandler>();
        services.AddSingleton<IIrcCommandHandler, UndlineHandler>();
        services.AddSingleton<IIrcCommandHandler, WhowasHandler>();

        services.AddSingleton<CommandDispatcher>();

        // Transport
        services.AddHostedService<TcpListenerHostedService>();
        services.AddHostedService<TlsListenerHostedService>();
        services.AddHostedService<ServerLinkListenerHostedService>();
        services.AddHostedService<OutboundLinkHostedService>();
    })
    .UseConsoleLifetime()
    .Build();

await host.RunAsync();
