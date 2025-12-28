using IRCd.Core.Commands;
using IRCd.Core.Commands.Contracts;
using IRCd.Core.Commands.Handlers;
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

        // Core
        services.AddSingleton<ServerState>();

        // Command handlers
        services.AddSingleton<IIrcCommandHandler, PingHandler>();
        services.AddSingleton<IIrcCommandHandler, NickHandler>();
        services.AddSingleton<IIrcCommandHandler, UserHandler>();
        services.AddSingleton<CommandDispatcher>();

        // Transport
        services.AddHostedService<TcpListenerHostedService>();
    })
    .UseConsoleLifetime()
    .Build();

await host.RunAsync();
