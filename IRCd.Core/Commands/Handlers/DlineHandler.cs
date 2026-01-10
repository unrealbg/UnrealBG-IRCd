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

    public sealed class DlineHandler : IIrcCommandHandler
    {
        public string Command => "DLINE";

        private readonly IOptions<IrcOptions> _options;
        private readonly BanService _banService;
        private readonly IBanEnforcer _enforcement;
        private readonly IAuditLogService _audit;

        public DlineHandler(IOptions<IrcOptions> options, BanService banService, IBanEnforcer enforcement, IAuditLogService? audit = null)
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

            if (!state.TryGetUser(session.ConnectionId, out var user) || user is null || !OperCapabilityService.HasCapability(_options.Value, user, "dline"))
            {
                await session.SendAsync($":{serverName} 481 {me} :Permission Denied- You're not an IRC operator", ct);
                return;
            }

            var sourceIp = user.RemoteIp ?? (session.RemoteEndPoint as IPEndPoint)?.Address.ToString();

            if (msg.Params.Count < 1)
            {
                await session.SendAsync($":{serverName} 461 {me} DLINE :Not enough parameters", ct);
                return;
            }

            var rawMask = msg.Params[0] ?? string.Empty;

            if (rawMask.StartsWith("-", StringComparison.Ordinal))
            {
                var toRemove = rawMask.TrimStart('-').Trim();
                var removed = await _banService.RemoveAsync(BanType.DLINE, toRemove, ct);
                await session.SendAsync($":{serverName} NOTICE {me} :UNDLINE {(removed ? "removed" : "not found")} {toRemove}", ct);

                await _audit.LogOperActionAsync(
                    action: "UNDLINE",
                    session: session,
                    actorUid: user.Uid,
                    actorNick: user.Nick ?? me,
                    sourceIp: sourceIp,
                    target: toRemove,
                    reason: null,
                    extra: new Dictionary<string, object?> { ["banType"] = "DLINE", ["removed"] = removed },
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
                        reason = "Banned";

                    var ban = new BanEntry
                    {
                        Type = BanType.DLINE,
                        Mask = mask,
                        Reason = reason,
                        SetBy = me,
                        ExpiresAt = expiresAt
                    };

                    await _banService.AddAsync(ban, ct);
                    await _enforcement.EnforceBanImmediatelyAsync(ban, ct);

                    var expireText = expiresAt.HasValue ? $"expires {expiresAt.Value:yyyy-MM-dd HH:mm:ss} UTC" : "permanent";
                    await session.SendAsync($":{serverName} NOTICE {me} :DLINE added {mask} ({expireText}) :{reason}", ct);

                    await _audit.LogOperActionAsync(
                        action: "DLINE",
                        session: session,
                        actorUid: user.Uid,
                        actorNick: user.Nick ?? me,
                        sourceIp: sourceIp,
                        target: mask,
                        reason: reason,
                        extra: new Dictionary<string, object?> { ["banType"] = "DLINE", ["expiresAtUtc"] = expiresAt },
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
                banReason = "Banned";

            var permanentBan = new BanEntry
            {
                Type = BanType.DLINE,
                Mask = mask,
                Reason = banReason,
                SetBy = me,
                ExpiresAt = null
            };

            await _banService.AddAsync(permanentBan, ct);
            await _enforcement.EnforceBanImmediatelyAsync(permanentBan, ct);

            await session.SendAsync($":{serverName} NOTICE {me} :DLINE added {mask} (permanent) :{banReason}", ct);

            await _audit.LogOperActionAsync(
                action: "DLINE",
                session: session,
                actorUid: user.Uid,
                actorNick: user.Nick ?? me,
                sourceIp: sourceIp,
                target: mask,
                reason: banReason,
                extra: new Dictionary<string, object?> { ["banType"] = "DLINE", ["expiresAtUtc"] = null },
                ct: ct);
        }
    }
}
