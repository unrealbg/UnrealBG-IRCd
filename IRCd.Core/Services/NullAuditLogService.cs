namespace IRCd.Core.Services
{
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Core.Abstractions;

    public sealed class NullAuditLogService : IAuditLogService
    {
        public static readonly NullAuditLogService Instance = new();

        private NullAuditLogService() { }

        public ValueTask LogOperActionAsync(
            string action,
            IClientSession session,
            string? actorUid,
            string? actorNick,
            string? sourceIp,
            string? target,
            string? reason,
            IReadOnlyDictionary<string, object?>? extra,
            CancellationToken ct)
            => ValueTask.CompletedTask;
    }
}
