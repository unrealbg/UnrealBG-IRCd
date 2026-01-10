namespace IRCd.Core.Commands.Handlers
{
    using System;
    using System.Collections.Generic;
    using System.Net;
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Core.Abstractions;
    using IRCd.Core.Commands.Contracts;
    using IRCd.Core.Config;
    using IRCd.Core.Services;
    using IRCd.Core.State;
    using IRCd.Shared.Options;

    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;

    public sealed class RehashHandler : IIrcCommandHandler
    {
        public string Command => "REHASH";

        private readonly ILogger<RehashHandler> _logger;
        private readonly IOptions<IrcOptions> _options;
        private readonly IrcConfigManager _config;
        private readonly IAuditLogService _audit;

        public RehashHandler(ILogger<RehashHandler> logger, IOptions<IrcOptions> options, IrcConfigManager config, IAuditLogService? audit = null)
        {
            _logger = logger;
            _options = options;
            _config = config;
            _audit = audit ?? NullAuditLogService.Instance;
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

            var sourceIp = user.RemoteIp ?? (session.RemoteEndPoint as IPEndPoint)?.Address.ToString();

            try
            {
                var result = _config.TryRehashFromConfiguredPath();
                if (!result.Success)
                {
                    foreach (var e in result.Errors.Take(10))
                    {
                        await session.SendAsync($":{serverName} NOTICE {me} :{e}", ct);
                    }

                    await _audit.LogOperActionAsync(
                        action: "REHASH",
                        session: session,
                        actorUid: user.Uid,
                        actorNick: user.Nick ?? me,
                        sourceIp: sourceIp,
                        target: "ircd.conf",
                        reason: null,
                        extra: new Dictionary<string, object?> { ["success"] = false },
                        ct: ct);
                    return;
                }

                await session.SendAsync($":{serverName} 382 {me} ircd.conf :Rehashing", ct);

                await _audit.LogOperActionAsync(
                    action: "REHASH",
                    session: session,
                    actorUid: user.Uid,
                    actorNick: user.Nick ?? me,
                    sourceIp: sourceIp,
                    target: "ircd.conf",
                    reason: null,
                    extra: new Dictionary<string, object?> { ["success"] = true },
                    ct: ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "REHASH failed");
                await session.SendAsync($":{serverName} NOTICE {me} :REHASH failed: {ex.Message}", ct);

                await _audit.LogOperActionAsync(
                    action: "REHASH",
                    session: session,
                    actorUid: user.Uid,
                    actorNick: user.Nick ?? me,
                    sourceIp: sourceIp,
                    target: "ircd.conf",
                    reason: null,
                    extra: new Dictionary<string, object?> { ["success"] = false, ["exceptionType"] = ex.GetType().Name },
                    ct: ct);
            }
        }
    }
}
