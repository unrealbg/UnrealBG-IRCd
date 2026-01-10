namespace IRCd.Core.Abstractions
{
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;

    public interface IAuditLogService
    {
        ValueTask LogOperActionAsync(
            string action,
            IClientSession session,
            string? actorUid,
            string? actorNick,
            string? sourceIp,
            string? target,
            string? reason,
            IReadOnlyDictionary<string, object?>? extra,
            CancellationToken ct);
    }
}
