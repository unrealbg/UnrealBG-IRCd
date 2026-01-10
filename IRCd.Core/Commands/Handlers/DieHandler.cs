namespace IRCd.Core.Commands.Handlers
{
    using System.Collections.Generic;
    using System.Net;
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Core.Abstractions;
    using IRCd.Core.Commands.Contracts;
    using IRCd.Core.Services;
    using IRCd.Core.State;
    using IRCd.Shared.Options;

    using Microsoft.Extensions.Hosting;
    using Microsoft.Extensions.Options;

    public sealed class DieHandler : IIrcCommandHandler
    {
        public string Command => "DIE";

        private readonly IOptions<IrcOptions> _options;
        private readonly IHostApplicationLifetime _lifetime;
        private readonly IAuditLogService _audit;

        public DieHandler(IOptions<IrcOptions> options, IHostApplicationLifetime lifetime, IAuditLogService? audit = null)
        {
            _options = options;
            _lifetime = lifetime;
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

            if (!state.TryGetUser(session.ConnectionId, out var user) || user is null || !OperCapabilityService.HasCapability(_options.Value, user, "die"))
            {
                await session.SendAsync($":{serverName} 481 {me} :Permission Denied- You're not an IRC operator", ct);
                return;
            }

            var sourceIp = user.RemoteIp ?? (session.RemoteEndPoint as IPEndPoint)?.Address.ToString();
            var reason = msg.Trailing;
            if (string.IsNullOrWhiteSpace(reason) && msg.Params.Count >= 1)
            {
                reason = msg.Params[0];
            }

            if (string.IsNullOrWhiteSpace(reason))
            {
                reason = null;
            }

            await session.SendAsync($":{serverName} NOTICE {me} :Server is shutting down", ct);

            await _audit.LogOperActionAsync(
                action: "DIE",
                session: session,
                actorUid: user.Uid,
                actorNick: user.Nick ?? me,
                sourceIp: sourceIp,
                target: null,
                reason: reason,
                extra: new Dictionary<string, object?> { ["success"] = true },
                ct: ct);
            _lifetime.StopApplication();
        }
    }
}
