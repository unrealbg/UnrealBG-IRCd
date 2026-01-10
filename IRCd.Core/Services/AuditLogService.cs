namespace IRCd.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text.Encodings.Web;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Core.Abstractions;
    using IRCd.Shared.Options;

    using Microsoft.Extensions.Hosting;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;

    public sealed class AuditLogService : IAuditLogService
    {
        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false,
        };

        private readonly ILogger<AuditLogService> _logger;
        private readonly IOptions<IrcOptions> _options;
        private readonly IHostEnvironment _env;
        private readonly SemaphoreSlim _fileLock = new(1, 1);

        public AuditLogService(ILogger<AuditLogService> logger, IOptions<IrcOptions> options, IHostEnvironment env)
        {
            _logger = logger;
            _options = options;
            _env = env;
        }

        public async ValueTask LogOperActionAsync(
            string action,
            IClientSession session,
            string? actorUid,
            string? actorNick,
            string? sourceIp,
            string? target,
            string? reason,
            IReadOnlyDictionary<string, object?>? extra,
            CancellationToken ct)
        {
            var cfg = _options.Value.Audit;
            if (cfg is null || !cfg.Enabled)
                return;

            var record = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["type"] = "oper_action",
                ["tsUtc"] = DateTimeOffset.UtcNow,
                ["action"] = action,
                ["actorNick"] = actorNick,
                ["actorUid"] = actorUid,
                ["actorConnId"] = session.ConnectionId,
                ["sourceIp"] = sourceIp,
                ["secure"] = session.IsSecureConnection,
                ["target"] = target,
                ["reason"] = reason,
            };

            if (!string.IsNullOrWhiteSpace(session.ClientCertificateFingerprintSha256))
            {
                record["tlsClientCertFpSha256"] = session.ClientCertificateFingerprintSha256;
            }

            if (!string.IsNullOrWhiteSpace(session.ClientCertificateSubject))
            {
                record["tlsClientCertSubject"] = session.ClientCertificateSubject;
            }

            if (extra is not null)
            {
                foreach (var kv in extra)
                    record[kv.Key] = kv.Value;
            }

            var filePath = cfg.FilePath;
            if (string.IsNullOrWhiteSpace(filePath))
            {
                _logger.LogInformation(
                    "AUDIT {Action} actorNick={ActorNick} actorUid={ActorUid} sourceIp={SourceIp} target={Target}",
                    action,
                    actorNick,
                    actorUid,
                    sourceIp,
                    target);

                return;
            }

            var full = Path.IsPathRooted(filePath)
                ? filePath
                : Path.Combine(_env.ContentRootPath, filePath);

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(full) ?? _env.ContentRootPath);

                var json = JsonSerializer.Serialize(record, JsonOpts);
                await _fileLock.WaitAsync(ct);
                try
                {
                    await File.AppendAllTextAsync(full, json + Environment.NewLine, ct);
                }
                finally
                {
                    _fileLock.Release();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to write audit log to {Path}", full);
            }
        }
    }
}
