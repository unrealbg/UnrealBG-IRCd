namespace IRCd.Core.Commands.Handlers
{
    using System;
    using System.Collections.Generic;
    using System.Net;
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Core.Abstractions;
    using IRCd.Core.Commands.Contracts;
    using IRCd.Core.Services;
    using IRCd.Core.State;
    using IRCd.Shared.Options;

    using Microsoft.Extensions.Options;

    public sealed class QlineHandler : IIrcCommandHandler
    {
        public string Command => "QLINE";

        private readonly IOptions<IrcOptions> _options;
        private readonly BanService _banService;
        private readonly IBanEnforcer _enforcement;
        private readonly IAuditLogService _audit;

        public QlineHandler(IOptions<IrcOptions> options, BanService banService, IBanEnforcer enforcement, IAuditLogService? audit = null)
        {
            _options = options;
            _banService = banService;
            _enforcement = enforcement;
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

            if (!state.TryGetUser(session.ConnectionId, out var user) || user is null || !OperCapabilityService.HasCapability(_options.Value, user, "qline"))
            {
                await session.SendAsync($":{serverName} 481 {me} :Permission Denied- You're not an IRC operator", ct);
                return;
            }

            var sourceIp = user.RemoteIp ?? (session.RemoteEndPoint as IPEndPoint)?.Address.ToString();

            if (msg.Params.Count < 1)
            {
                await session.SendAsync($":{serverName} 461 {me} QLINE :Not enough parameters", ct);
                return;
            }

            var rawMask = msg.Params[0] ?? string.Empty;

            if (rawMask.StartsWith("-", StringComparison.Ordinal))
            {
                var toRemove = rawMask.TrimStart('-').Trim();
                var removed = await _banService.RemoveAsync(BanType.QLINE, toRemove, ct);
                await session.SendAsync($":{serverName} NOTICE {me} :UNQLINE {(removed ? "removed" : "not found")} {toRemove}", ct);

                await _audit.LogOperActionAsync(
                    action: "UNQLINE",
                    session: session,
                    actorUid: user.Uid,
                    actorNick: user.Nick ?? me,
                    sourceIp: sourceIp,
                    target: toRemove,
                    reason: null,
                    extra: new Dictionary<string, object?> { ["banType"] = "QLINE", ["removed"] = removed },
                    ct: ct);
                return;
            }

            var mask = rawMask.Trim();
            
            DateTimeOffset? expiresAt = null;
            if (msg.Params.Count >= 2 && !string.IsNullOrWhiteSpace(msg.Params[1]))
            {
                var possibleDuration = msg.Params[1];
                if (char.IsDigit(possibleDuration[0]) || possibleDuration.Equals("perm", StringComparison.OrdinalIgnoreCase))
                {
                    expiresAt = BanEntry.ParseDuration(possibleDuration);
                    var reason = msg.Trailing;
                    if (string.IsNullOrWhiteSpace(reason) && msg.Params.Count >= 3)
                    {
                        reason = msg.Params[2];
                    }
                    if (string.IsNullOrWhiteSpace(reason))
                        reason = "Reserved nickname";

                    var ban = new BanEntry
                    {
                        Type = BanType.QLINE,
                        Mask = mask,
                        Reason = reason,
                        SetBy = me,
                        ExpiresAt = expiresAt
                    };

                    await _banService.AddAsync(ban, ct);
                    await _enforcement.EnforceBanImmediatelyAsync(ban, ct);

                    var expireText = expiresAt.HasValue ? $"expires {expiresAt.Value:yyyy-MM-dd HH:mm:ss} UTC" : "permanent";
                    await session.SendAsync($":{serverName} NOTICE {me} :QLINE added {mask} ({expireText}) :{reason}", ct);

                    await _audit.LogOperActionAsync(
                        action: "QLINE",
                        session: session,
                        actorUid: user.Uid,
                        actorNick: user.Nick ?? me,
                        sourceIp: sourceIp,
                        target: mask,
                        reason: reason,
                        extra: new Dictionary<string, object?> { ["banType"] = "QLINE", ["expiresAtUtc"] = expiresAt },
                        ct: ct);
                    return;
                }
            }

            var banReason = msg.Trailing;
            if (string.IsNullOrWhiteSpace(banReason) && msg.Params.Count >= 2)
            {
                banReason = msg.Params[1];
            }
            if (string.IsNullOrWhiteSpace(banReason))
                banReason = "Reserved nickname";

            var permanentBan = new BanEntry
            {
                Type = BanType.QLINE,
                Mask = mask,
                Reason = banReason,
                SetBy = me,
                ExpiresAt = null
            };

            await _banService.AddAsync(permanentBan, ct);
            await _enforcement.EnforceBanImmediatelyAsync(permanentBan, ct);

            await session.SendAsync($":{serverName} NOTICE {me} :QLINE added {mask} (permanent) :{banReason}", ct);

            await _audit.LogOperActionAsync(
                action: "QLINE",
                session: session,
                actorUid: user.Uid,
                actorNick: user.Nick ?? me,
                sourceIp: sourceIp,
                target: mask,
                reason: banReason,
                extra: new Dictionary<string, object?> { ["banType"] = "QLINE", ["expiresAtUtc"] = null },
                ct: ct);
        }
    }
}
