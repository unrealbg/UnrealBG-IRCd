namespace IRCd.Core.Commands.Handlers
{
    using System;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Core.Abstractions;
    using IRCd.Core.Commands.Contracts;
    using IRCd.Core.Config;
    using IRCd.Core.Services;
    using IRCd.Core.State;
    using IRCd.Shared.Options;

    using Microsoft.Extensions.Hosting;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;

    public sealed class RehashHandler : IIrcCommandHandler
    {
        public string Command => "REHASH";

        private static readonly object RehashLock = new();

        private readonly ILogger<RehashHandler> _logger;
        private readonly IOptions<IrcOptions> _options;
        private readonly IHostEnvironment _env;

        public RehashHandler(ILogger<RehashHandler> logger, IOptions<IrcOptions> options, IHostEnvironment env)
        {
            _logger = logger;
            _options = options;
            _env = env;
        }

        public async ValueTask HandleAsync(IClientSession session, Protocol.IrcMessage msg, ServerState state, CancellationToken ct)
        {
            if (!session.IsRegistered)
            {
                await session.SendAsync($":server 451 {(session.Nick ?? "*")} :You have not registered", ct);
                return;
            }

            var me = session.Nick!;
            var serverName = _options.Value.ServerInfo?.Name ?? "server";

            if (!state.TryGetUser(session.ConnectionId, out var user) || user is null || !OperCapabilityService.HasCapability(_options.Value, user, "rehash"))
            {
                await session.SendAsync($":{serverName} 481 {me} :Permission Denied- You're not an IRC operator", ct);
                return;
            }

            var conf = _options.Value.ConfigFile;
            if (string.IsNullOrWhiteSpace(conf))
                conf = "ircd.conf";

            var confPath = Path.IsPathRooted(conf)
                ? conf
                : Path.Combine(_env.ContentRootPath, conf);

            if (!File.Exists(confPath))
            {
                await session.SendAsync($":{serverName} NOTICE {me} :REHASH failed: config file not found ({confPath})", ct);
                return;
            }

            try
            {
                lock (RehashLock)
                {
                    var o = _options.Value;

                    o.Opers = Array.Empty<OperOptions>();
                    o.Classes = Array.Empty<OperClassOptions>();
                    o.KLines = Array.Empty<KLineOptions>();
                    o.DLines = Array.Empty<DLineOptions>();
                    o.Links = Array.Empty<LinkOptions>();
                    o.ListenEndpoints = Array.Empty<ListenEndpointOptions>();
                    o.MotdByVhost = Array.Empty<MotdVhostOptions>();

                    IrcdConfLoader.ApplyConfFile(o, confPath);
                }

                await session.SendAsync($":{serverName} 382 {me} {Path.GetFileName(confPath)} :Rehashing", ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "REHASH failed");
                await session.SendAsync($":{serverName} NOTICE {me} :REHASH failed: {ex.Message}", ct);
            }
        }
    }
}
