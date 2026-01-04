using System.IO;

using IRCd.Core.Abstractions;
using IRCd.Core.Commands;
using IRCd.Core.Commands.Contracts;
using IRCd.Core.Commands.Handlers;
using IRCd.Core.Protocol;
using IRCd.Core.Services;
using IRCd.Core.State;
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

        // Core services
        services.AddSingleton<RegistrationService>();
        services.AddSingleton<RateLimitService>();
        services.AddSingleton<ConnectionGuardService>();
        services.AddSingleton<PingService>();
        services.AddHostedService<PingHostedService>();
        services.AddSingleton<LusersService>();
        services.AddSingleton<HostmaskService>();
        services.AddSingleton<MotdSender>();
        services.AddSingleton<ServerLinkService>();

        // Flood protection
        services.AddSingleton(new SimpleFloodGate(maxLines: 12, window: TimeSpan.FromSeconds(10)));

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
        services.AddSingleton<IIrcCommandHandler, ListHandler>();
        services.AddSingleton<IIrcCommandHandler, MotdHandler>();
        services.AddSingleton<IIrcCommandHandler, LinksHandler>();
        services.AddSingleton<IIrcCommandHandler, LusersHandler>();
        services.AddSingleton<IIrcCommandHandler, UserhostHandler>();
        services.AddSingleton<IIrcCommandHandler, PongHandler>();

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
