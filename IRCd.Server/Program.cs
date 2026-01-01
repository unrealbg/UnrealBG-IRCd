using IRCd.Core.Abstractions;
using IRCd.Core.Commands;
using IRCd.Core.Commands.Contracts;
using IRCd.Core.Commands.Handlers;
using IRCd.Core.Services;
using IRCd.Core.State;
using IRCd.Shared.Options;
using IRCd.Transport.Tcp;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var host = Host.CreateDefaultBuilder(args)
    .ConfigureAppConfiguration((ctx, cfg) =>
    {
        cfg.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);
        cfg.AddJsonFile($"appsettings.{ctx.HostingEnvironment.EnvironmentName}.json", optional: true);
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
        services.Configure<IrcOptions>(ctx.Configuration.GetSection("Irc"));

        // Core state
        services.AddSingleton<ServerState>();

        // Session registry (transport implementation, core abstraction)
        services.AddSingleton<InMemorySessionRegistry>();
        services.AddSingleton<ISessionRegistry>(sp => sp.GetRequiredService<InMemorySessionRegistry>());

        // Routing (core service)
        services.AddSingleton<RoutingService>();

        // Registration service (core service)
        services.AddSingleton<RegistrationService>();

        // Flood protection (transport-level)
        services.AddSingleton(new SimpleFloodGate(maxLines: 12, window: TimeSpan.FromSeconds(10)));

        // Command handlers
        services.AddSingleton<IIrcCommandHandler, PingHandler>();
        services.AddSingleton<IIrcCommandHandler, NickHandler>();
        services.AddSingleton<IIrcCommandHandler, UserHandler>();

        services.AddSingleton<IIrcCommandHandler, JoinHandler>();
        services.AddSingleton<IIrcCommandHandler, PartHandler>();
        services.AddSingleton<IIrcCommandHandler, PrivMsgHandler>();
        services.AddSingleton<IIrcCommandHandler, QuitHandler>();

        services.AddSingleton<IIrcCommandHandler, NamesHandler>();
        services.AddSingleton<IIrcCommandHandler, WhoHandler>();
        services.AddSingleton<IIrcCommandHandler, WhoisHandler>();

        services.AddSingleton<IIrcCommandHandler, ModeHandler>();
        services.AddSingleton<IIrcCommandHandler, TopicHandler>();
        services.AddSingleton<IIrcCommandHandler, KickHandler>();

        services.AddSingleton<CommandDispatcher>();

        // Transport
        services.AddHostedService<TcpListenerHostedService>();
    })
    .UseConsoleLifetime()
    .Build();

await host.RunAsync();
