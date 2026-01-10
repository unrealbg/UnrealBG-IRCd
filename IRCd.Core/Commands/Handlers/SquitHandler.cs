namespace IRCd.Core.Commands.Handlers
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Core.Abstractions;
    using IRCd.Core.Commands.Contracts;
    using IRCd.Core.Services;
    using IRCd.Core.State;
    using IRCd.Shared.Options;

    using Microsoft.Extensions.Options;

    public sealed class SquitHandler : IIrcCommandHandler
    {
        public string Command => "SQUIT";

        private readonly IOptions<IrcOptions> _options;
        private readonly ServerLinkService _links;
        private readonly IAuditLogService _audit;

        public SquitHandler(IOptions<IrcOptions> options, ServerLinkService links, IAuditLogService? audit = null)
        {
            _options = options;
            _links = links;
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

            state.TryGetUser(session.ConnectionId, out var user);
            var sourceIp = user?.RemoteIp ?? (session.RemoteEndPoint is IPEndPoint ip ? ip.Address.ToString() : null);

            if (user is null || !OperCapabilityService.HasCapability(_options.Value, user, "squit"))
            {
                await session.SendAsync($":{serverName} 481 {me} :Permission Denied- You're not an IRC operator", ct);

                var requestedTarget = msg.Params.Count > 0 ? msg.Params[0] : null;
                var requestedReason = msg.Trailing;
                await _audit.LogOperActionAsync(
                    action: "SQUIT",
                    session: session,
                    actorUid: user?.Uid,
                    actorNick: user?.Nick ?? me,
                    sourceIp: sourceIp,
                    target: requestedTarget,
                    reason: requestedReason,
                    extra: new Dictionary<string, object?> { ["success"] = false, ["error"] = "permission_denied" },
                    ct: ct);
                return;
            }

            if (msg.Params.Count < 1)
            {
                await session.SendAsync($":{serverName} 461 {me} SQUIT :Not enough parameters", ct);

                await _audit.LogOperActionAsync(
                    action: "SQUIT",
                    session: session,
                    actorUid: user.Uid,
                    actorNick: user.Nick ?? me,
                    sourceIp: sourceIp,
                    target: null,
                    reason: null,
                    extra: new Dictionary<string, object?> { ["success"] = false, ["error"] = "not_enough_parameters" },
                    ct: ct);
                return;
            }

            var target = msg.Params[0] ?? string.Empty;
            var reason = msg.Trailing;
            if (string.IsNullOrWhiteSpace(reason) && msg.Params.Count >= 2)
            {
                reason = msg.Params[1];
            }

            if (string.IsNullOrWhiteSpace(reason))
                reason = "Requested";

            string? sid = null;

            if (target.Length == 3 && target.All(char.IsLetterOrDigit))
            {
                sid = target;
            }
            else
            {
                sid = state.GetRemoteServers()
                    .FirstOrDefault(s => s is not null && string.Equals(s.Name, target, StringComparison.OrdinalIgnoreCase))
                    ?.Sid;
            }

            if (string.IsNullOrWhiteSpace(sid))
            {
                await session.SendAsync($":{serverName} 402 {me} {target} :No such server", ct);

                await _audit.LogOperActionAsync(
                    action: "SQUIT",
                    session: session,
                    actorUid: user.Uid,
                    actorNick: user.Nick ?? me,
                    sourceIp: sourceIp,
                    target: target,
                    reason: reason,
                    extra: new Dictionary<string, object?> { ["success"] = false, ["error"] = "no_such_server" },
                    ct: ct);
                return;
            }

            var ok = await _links.LocalSquitAsync(sid, reason, ct);
            if (!ok)
            {
                await session.SendAsync($":{serverName} 402 {me} {target} :No such server", ct);

                await _audit.LogOperActionAsync(
                    action: "SQUIT",
                    session: session,
                    actorUid: user.Uid,
                    actorNick: user.Nick ?? me,
                    sourceIp: sourceIp,
                    target: target,
                    reason: reason,
                    extra: new Dictionary<string, object?> { ["success"] = false, ["error"] = "no_such_server" },
                    ct: ct);
                return;
            }

            await session.SendAsync($":{serverName} NOTICE {me} :SQUIT {sid} :{reason}", ct);

            await _audit.LogOperActionAsync(
                action: "SQUIT",
                session: session,
                actorUid: user.Uid,
                actorNick: user.Nick ?? me,
                sourceIp: sourceIp,
                target: sid,
                reason: reason,
                extra: new Dictionary<string, object?> { ["success"] = true, ["requestedTarget"] = target },
                ct: ct);
        }
    }
}
