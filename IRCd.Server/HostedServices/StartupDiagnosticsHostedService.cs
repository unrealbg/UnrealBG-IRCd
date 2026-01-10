namespace IRCd.Server.HostedServices
{
    using System;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Shared.Options;

    using Microsoft.Extensions.Hosting;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;

    public sealed class StartupDiagnosticsHostedService : IHostedService
    {
        private readonly IOptions<IrcOptions> _options;
        private readonly IHostEnvironment _env;
        private readonly ILogger<StartupDiagnosticsHostedService> _logger;

        public StartupDiagnosticsHostedService(IOptions<IrcOptions> options, IHostEnvironment env, ILogger<StartupDiagnosticsHostedService> logger)
        {
            _options = options;
            _env = env;
            _logger = logger;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            try
            {
                var cfg = _options.Value;

                _logger.LogInformation(
                    "Startup diagnostics: BaseDir={BaseDir} ContentRoot={ContentRoot}",
                    AppContext.BaseDirectory,
                    _env.ContentRootPath);

                LogMotd("motd", cfg.Motd);

                if (cfg.MotdByVhost is { Length: > 0 })
                {
                    foreach (var m in cfg.MotdByVhost)
                    {
                        if (m is null)
                        {
                            continue;
                        }

                        var key = string.IsNullOrWhiteSpace(m.Vhost) ? "(empty)" : m.Vhost.Trim();
                        LogMotd($"motdByVhost[{key}]", m.Motd);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Startup diagnostics failed");
            }

            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        private void LogMotd(string label, MotdOptions motd)
        {
            if (motd.Lines is { Length: > 0 })
            {
                _logger.LogInformation("{Label}: MOTD is configured inline. Lines={Lines}", label, motd.Lines.Length);
                return;
            }

            if (string.IsNullOrWhiteSpace(motd.FilePath))
            {
                _logger.LogWarning("{Label}: MOTD file path is empty", label);
                return;
            }

            var configured = motd.FilePath;
            var resolved = ResolvePath(configured);
            var existsResolved = File.Exists(resolved);

            var baseCandidate = Path.Combine(AppContext.BaseDirectory, configured);
            var contentCandidate = Path.Combine(_env.ContentRootPath, configured);

            _logger.LogInformation(
                "{Label}: MOTD configured path={ConfiguredPath} resolved={ResolvedPath} exists={Exists}",
                label,
                configured,
                resolved,
                existsResolved);

            if (!existsResolved)
            {
                _logger.LogWarning(
                    "{Label}: MOTD missing. ExistsBaseCandidate={ExistsBase} ExistsContentCandidate={ExistsContent}",
                    label,
                    File.Exists(baseCandidate),
                    File.Exists(contentCandidate));
            }
        }

        private string ResolvePath(string filePath)
        {
            if (Path.IsPathRooted(filePath))
            {
                return filePath;
            }

            var baseDir = AppContext.BaseDirectory;
            var candidate = Path.Combine(baseDir, filePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            return Path.Combine(_env.ContentRootPath, filePath);
        }
    }
}
