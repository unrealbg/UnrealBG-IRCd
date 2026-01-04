namespace IRCd.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Core.Abstractions;
    using IRCd.Shared.Options;

    using Microsoft.Extensions.Hosting;
    using Microsoft.Extensions.Options;
    using Microsoft.Extensions.Logging;

    public sealed class MotdSender
    {
        private readonly IOptionsMonitor<IrcOptions> _options;
        private readonly IHostEnvironment _env;
        private readonly ILogger<MotdSender> _logger;

        public MotdSender(IOptionsMonitor<IrcOptions> options, IHostEnvironment env, ILogger<MotdSender> logger)
        {
            _options = options;
            _env = env;
            _logger = logger;
        }

        public async ValueTask<bool> TrySendMotdAsync(IClientSession session, CancellationToken ct)
        {
            var motd = _options.CurrentValue.Motd;

            var serverName = _options.CurrentValue.ServerInfo?.Name ?? "server";

            _logger.LogInformation(
                "MOTD requested by {Nick}. FilePath={FilePath} LinesCfgCount={LinesCfgCount}",
                session.Nick,
                motd.FilePath,
                motd.Lines?.Length ?? 0);

            var lines = await LoadMotdLinesAsync(motd, ct);
            if (lines.Count == 0)
            {
                _logger.LogWarning("MOTD is missing/empty after load. Sending 422.");
                await session.SendAsync($":{serverName} 422 {session.Nick} :MOTD File is missing", ct);
                return false;
            }

            var me = session.Nick!;

            await session.SendAsync($":{serverName} 375 {me} :- {serverName} Message of the Day -", ct);

            foreach (var line in lines)
            {
                    await session.SendAsync($":{serverName} 372 {me} :- {line}", ct);
            }

            await session.SendAsync($":{serverName} 376 {me} :End of /MOTD command.", ct);
            return true;
        }

        private async Task<List<string>> LoadMotdLinesAsync(MotdOptions motd, CancellationToken ct)
        {
            if (motd.Lines is { Length: > 0 })
            {
                var inCfg = new List<string>(motd.Lines.Length);
                foreach (var s in motd.Lines)
                {
                    if (!string.IsNullOrWhiteSpace(s))
                        inCfg.Add(s.TrimEnd());
                }

                _logger.LogInformation("MOTD loaded from config lines. Count={Count}", inCfg.Count);
                return inCfg;
            }

            if (string.IsNullOrWhiteSpace(motd.FilePath))
            {
                _logger.LogWarning("MOTD FilePath is empty. Sending missing MOTD.");
                return new List<string>();
            }

            var path = ResolvePath(motd.FilePath);

            _logger.LogInformation(
                "MOTD resolving file. ConfigFilePath={ConfigFilePath} ResolvedPath={ResolvedPath} BaseDir={BaseDir} ContentRoot={ContentRoot}",
                motd.FilePath,
                path,
                AppContext.BaseDirectory,
                _env.ContentRootPath);

            try
            {
                if (!File.Exists(path))
                {
                    var baseCandidate = Path.Combine(AppContext.BaseDirectory, motd.FilePath);
                    var contentCandidate = Path.Combine(_env.ContentRootPath, motd.FilePath);
                    _logger.LogWarning(
                        "MOTD file not found. ResolvedPath={ResolvedPath} ExistsBaseCandidate={ExistsBaseCandidate} ExistsContentCandidate={ExistsContentCandidate}",
                        path,
                        File.Exists(baseCandidate),
                        File.Exists(contentCandidate));
                    return new List<string>();
                }

                var fileLines = await File.ReadAllLinesAsync(path, ct);

                var result = new List<string>(fileLines.Length);
                foreach (var l in fileLines)
                {
                    if (!string.IsNullOrWhiteSpace(l))
                        result.Add(l.TrimEnd());
                }

                _logger.LogInformation("MOTD loaded from file. Path={Path} Lines={Lines}", path, result.Count);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to read MOTD file. ResolvedPath={ResolvedPath}", path);
                return new List<string>();
            }
        }

        private string ResolvePath(string filePath)
        {
            if (Path.IsPathRooted(filePath))
                return filePath;

            var baseDir = AppContext.BaseDirectory;
            var candidate = Path.Combine(baseDir, filePath);
            if (File.Exists(candidate))
                return candidate;

            return Path.Combine(_env.ContentRootPath, filePath);
        }
    }
}
