namespace IRCd.Core.Commands.Handlers
{
    using System;
    using System.Collections.Generic;
    using System.Net;
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Core.Abstractions;
    using IRCd.Core.Commands.Contracts;
    using IRCd.Core.Protocol;
    using IRCd.Core.Services;
    using IRCd.Core.State;
    using IRCd.Shared.Options;

    using Microsoft.Extensions.Options;

    public sealed class ChghostHandler : IIrcCommandHandler
    {
        public string Command => "CHGHOST";

        private readonly IOptions<IrcOptions> _options;
        private readonly RoutingService _routing;
        private readonly ISessionRegistry _sessions;
        private readonly IAuditLogService _audit;

        public ChghostHandler(IOptions<IrcOptions> options, RoutingService routing, ISessionRegistry sessions, IAuditLogService? audit = null)
        {
            _options = options;
            _routing = routing;
            _sessions = sessions;
            _audit = audit ?? NullAuditLogService.Instance;
        }

        public async ValueTask HandleAsync(IClientSession session, IrcMessage msg, ServerState state, CancellationToken ct)
        {
            var serverName = _options.Value.ServerInfo?.Name ?? "server";

            if (!session.IsRegistered)
            {
                await session.SendAsync($":{serverName} 451 {(session.Nick ?? "*")} :You have not registered", ct);
                return;
            }

            var me = session.Nick ?? "*";

            if (!state.TryGetUser(session.ConnectionId, out var oper) || oper is null || !OperCapabilityService.HasCapability(_options.Value, oper, "chghost"))
            {
                await session.SendAsync($":{serverName} 481 {me} :Permission Denied- You're not an IRC operator", ct);
                return;
            }

            var sourceIp = oper.RemoteIp ?? (session.RemoteEndPoint as IPEndPoint)?.Address.ToString();

            if (msg.Params.Count < 3)
            {
                await session.SendAsync($":{serverName} 461 {me} CHGHOST :Not enough parameters", ct);
                return;
            }

            var targetNick = (msg.Params[0] ?? string.Empty).Trim();
            var newIdent = (msg.Params[1] ?? string.Empty).Trim();
            var newHost = (msg.Params[2] ?? string.Empty).Trim();

            if (!IrcValidation.IsValidNick(targetNick, out _))
            {
                await session.SendAsync($":{serverName} 401 {me} {targetNick} :No such nick", ct);
                return;
            }

            if (newIdent.Length == 0 || newIdent.IndexOfAny([' ', '\r', '\n', '\0', '\t', ':']) >= 0)
            {
                await session.SendAsync($":{serverName} NOTICE {me} :Invalid ident", ct);
                return;
            }

            if (newHost.Length == 0 || newHost.IndexOfAny([' ', '\r', '\n', '\0', '\t', ':']) >= 0)
            {
                await session.SendAsync($":{serverName} NOTICE {me} :Invalid host", ct);
                return;
            }

            if (!state.TryGetConnectionIdByNick(targetNick, out var targetConnId) || string.IsNullOrWhiteSpace(targetConnId))
            {
                await session.SendAsync($":{serverName} 401 {me} {targetNick} :No such nick", ct);
                return;
            }

            if (!state.TryGetUser(targetConnId!, out var targetUser) || targetUser is null)
            {
                await session.SendAsync($":{serverName} 401 {me} {targetNick} :No such nick", ct);
                return;
            }

            if (targetUser.IsService)
            {
                await session.SendAsync($":{serverName} NOTICE {me} :Cannot CHGHOST services", ct);
                return;
            }

            if (targetUser.IsRemote)
            {
                await session.SendAsync($":{serverName} NOTICE {me} :Cannot CHGHOST remote users", ct);
                return;
            }

            var oldIdent = string.IsNullOrWhiteSpace(targetUser.UserName) ? "u" : targetUser.UserName!;
            var oldHost = state.GetHostFor(targetConnId!);

            targetUser.UserName = newIdent;
            targetUser.Host = newHost;

            if (_sessions.TryGet(targetConnId!, out var targetSession) && targetSession is not null)
            {
                targetSession.UserName = newIdent;
            }

            var line = $":{targetNick}!{oldIdent}@{oldHost} CHGHOST {newIdent} {newHost}";

            var recipients = new HashSet<string>(StringComparer.Ordinal) { targetConnId! };

            foreach (var chName in state.GetUserChannels(targetConnId!))
            {
                if (!state.TryGetChannel(chName, out var ch) || ch is null)
                    continue;

                foreach (var member in ch.Members)
                {
                    recipients.Add(member.ConnectionId);
                }
            }

            foreach (var connId in recipients)
            {
                await _routing.SendToUserAsync(connId, line, ct);
            }

            await session.SendAsync($":{serverName} NOTICE {me} :CHGHOST {targetNick} {newIdent} {newHost}", ct);

            await _audit.LogOperActionAsync(
                action: "CHGHOST",
                session: session,
                actorUid: oper.Uid,
                actorNick: oper.Nick ?? me,
                sourceIp: sourceIp,
                target: targetNick,
                reason: null,
                extra: new Dictionary<string, object?>
                {
                    ["oldIdent"] = oldIdent,
                    ["oldHost"] = oldHost,
                    ["newIdent"] = newIdent,
                    ["newHost"] = newHost,
                },
                ct: ct);
        }
    }
}
