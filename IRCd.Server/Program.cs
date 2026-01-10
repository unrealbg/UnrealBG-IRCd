using System.IO;
using System.Linq;

using IRCd.Core.Abstractions;
using IRCd.Core.Commands;
using IRCd.Core.Commands.Contracts;
using IRCd.Core.Commands.Handlers;
using IRCd.Core.Protocol;
using IRCd.Core.Services;
using IRCd.Core.State;
using IRCd.Core.Security;
using IRCd.Services.DependencyInjection;
using IRCd.Server.HostedServices;
using IRCd.Shared.Options;
using IRCd.Transport.Tcp;
using IRCd.Transport.Tls;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Serilog;
using Serilog.Events;

using IRCd.Core.Config;

static string DetermineContentRoot()
{
    var baseDir = AppContext.BaseDirectory;
    var baseHasConfs = Directory.Exists(Path.Combine(baseDir, "confs"));
    if (baseHasConfs)
    {
        return baseDir;
    }

    // Dev-time convenience (bin/{Debug|Release}/... -> project root)
    var devCandidate = Path.GetFullPath(Path.Combine(baseDir, "..", "..", ".."));
    var devHasConfs = Directory.Exists(Path.Combine(devCandidate, "confs"));
    if (devHasConfs)
    {
        return devCandidate;
    }

    // Safe fallback: keep everything relative to the executable.
    return baseDir;
}

var host = Host.CreateDefaultBuilder(args)
    .UseContentRoot(DetermineContentRoot())
    .UseSerilog((ctx, services, lc) =>
    {
        // Prefer config-driven setup (confs/appsettings.json), but keep a safe default for dev.
        var hasConfig = ctx.Configuration.GetSection("Serilog").GetChildren().Any();

        lc.ReadFrom.Services(services);

        if (hasConfig)
        {
            lc.ReadFrom.Configuration(ctx.Configuration);
            return;
        }

        lc.MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("System", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.Console(
                outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext} {Message:lj}{NewLine}{Exception}");
    })
    .ConfigureAppConfiguration((ctx, cfg) =>
    {
        var contentRoot = ctx.HostingEnvironment.ContentRootPath;
        cfg.AddJsonFile(Path.Combine(contentRoot, "confs", "appsettings.json"), optional: true, reloadOnChange: true);
        cfg.AddJsonFile(Path.Combine(contentRoot, "confs", $"appsettings.{ctx.HostingEnvironment.EnvironmentName}.json"), optional: true);
        cfg.AddEnvironmentVariables(prefix: "IRCD_");
        cfg.AddCommandLine(args);
    })
    .ConfigureServices((ctx, services) =>
    {
        // Options (atomic snapshot store; supports transactional REHASH)
        var probe = new IrcOptions();
        ctx.Configuration.GetSection("Irc").Bind(probe);

        var conf = probe.ConfigFile;
        if (string.IsNullOrWhiteSpace(conf))
            conf = "confs/ircd.conf";

        var confPath = Path.IsPathRooted(conf)
            ? conf
            : Path.Combine(ctx.HostingEnvironment.ContentRootPath, conf);

        var selectedProfile = probe.Security?.Profile ?? "default";
        if (File.Exists(confPath))
        {
            var p = IrcdConfLoader.TryGetSecurityProfile(confPath);
            if (!string.IsNullOrWhiteSpace(p))
                selectedProfile = p;
        }

        var initial = new IrcOptions();
        initial.Security.Profile = selectedProfile;
        SecurityProfileApplier.Apply(initial);

        // Apply explicit config on top of profile defaults.
        ctx.Configuration.GetSection("Irc").Bind(initial);
        initial.Security.Profile = selectedProfile;

        if (File.Exists(confPath))
        {
            IrcdConfLoader.ApplyConfFile(initial, confPath);
        }

        services.AddSingleton(new IrcOptionsStore(initial));
        services.AddSingleton<Microsoft.Extensions.Options.IOptions<IrcOptions>>(sp => sp.GetRequiredService<IrcOptionsStore>());
        services.AddSingleton<Microsoft.Extensions.Options.IOptionsMonitor<IrcOptions>>(sp => sp.GetRequiredService<IrcOptionsStore>());
        services.AddSingleton<IrcConfigManager>();

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

        // Pseudo users for services
        services.AddHostedService<ServicesPseudoUsersHostedService>();

        // Startup diagnostics (logs resolved MOTD paths, etc.)
        services.AddHostedService<StartupDiagnosticsHostedService>();

        // Non-IRC operational endpoints (disabled by default)
        services.AddHostedService<ObservabilityHttpHostedService>();

        // Observability
        services.AddSingleton<WatchService>();
        services.AddSingleton<IMetrics, DefaultMetrics>();
        services.AddSingleton<IAcceptLoopStatus, AcceptLoopStatus>();

        // Time
        services.AddSingleton<IServerClock, SystemServerClock>();

        // Core services
        // Unified ban engine
        services.AddSingleton<JsonBanRepository>();
        services.AddSingleton<OptionsBanRepository>();
        services.AddSingleton<IBanRepository>(sp =>
            new CompositeBanRepository(
                sp.GetRequiredService<JsonBanRepository>(),
                sp.GetRequiredService<OptionsBanRepository>()));
        services.AddSingleton<BanService>();
        services.AddSingleton<BanEnforcementService>();
        services.AddSingleton<IBanEnforcer>(sp => sp.GetRequiredService<BanEnforcementService>());
        services.AddHostedService(sp => sp.GetRequiredService<BanEnforcementService>());

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
        services.AddSingleton<RuntimeDenyService>();
        services.AddSingleton<RuntimeWarnService>();
        services.AddSingleton<RuntimeTriggerService>();
        services.AddSingleton<WhowasService>();

        // Connection prechecks (DNSBL/Tor/VPN heuristics; default disabled)
        services.AddSingleton<IDnsResolver, SystemDnsResolver>();
        services.AddSingleton<IConnectionPrecheckPipeline, ConnectionPrecheckPipeline>();

        // Audit logging (disabled by default)
        services.AddSingleton<IAuditLogService, AuditLogService>();

        // Security
        services.AddSingleton<IOperPasswordVerifier, OperPasswordVerifier>();

        services.AddSingleton<BanMatcher>();

        // SASL
        services.AddSingleton<SaslService>();

        // Log redaction
        services.AddSingleton<IIrcLogRedactor, DefaultIrcLogRedactor>();

        // Flood protection
        services.AddSingleton<FloodService>();

        // Automatic temporary DLINE escalation (disabled by default; configured via autodline { ... })
        services.AddSingleton<AutoDlineService>();
        services.AddSingleton<IAutoDlineMetrics>(sp => sp.GetRequiredService<AutoDlineService>());

        // Command handlers
        services.AddSingleton<IIrcCommandHandler, PingHandler>();
        services.AddSingleton<IIrcCommandHandler, CapHandler>();
        services.AddSingleton<IIrcCommandHandler, AuthenticateHandler>();
        services.AddSingleton<IIrcCommandHandler, PassHandler>();
        services.AddSingleton<IIrcCommandHandler, NickHandler>();
        services.AddSingleton<IIrcCommandHandler, UserHandler>();
        services.AddSingleton<IIrcCommandHandler, JoinHandler>();
        services.AddSingleton<IIrcCommandHandler, PartHandler>();
        services.AddSingleton<IIrcCommandHandler, PrivMsgHandler>();
        services.AddSingleton<IIrcCommandHandler, NoticeHandler>();
        services.AddSingleton<IIrcCommandHandler, NsHandler>();
        services.AddSingleton<IIrcCommandHandler, NickServCommandHandler>();
        services.AddSingleton<IIrcCommandHandler, CsHandler>();
        services.AddSingleton<IIrcCommandHandler, ChanServCommandHandler>();
        services.AddSingleton<IIrcCommandHandler, OsHandler>();
        services.AddSingleton<IIrcCommandHandler, OperServCommandHandler>();
        services.AddSingleton<IIrcCommandHandler, MsHandler>();
        services.AddSingleton<IIrcCommandHandler, MemoServCommandHandler>();
        services.AddSingleton<IIrcCommandHandler, SsHandler>();
        services.AddSingleton<IIrcCommandHandler, SeenServCommandHandler>();
        services.AddSingleton<IIrcCommandHandler, IsHandler>();
        services.AddSingleton<IIrcCommandHandler, InfoServCommandHandler>();
        services.AddSingleton<IIrcCommandHandler, StatServCommandHandler>();
        services.AddSingleton<IIrcCommandHandler, AdminServCommandHandler>();
        services.AddSingleton<IIrcCommandHandler, DevServCommandHandler>();
        services.AddSingleton<IIrcCommandHandler, HelpServCommandHandler>();
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
        services.AddSingleton<IIrcCommandHandler, QlineHandler>();
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
