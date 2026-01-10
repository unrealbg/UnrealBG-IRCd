namespace IRCd.Core.Config;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

using IRCd.Core.Abstractions;
using IRCd.Shared.Options;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

public sealed class IrcConfigManager
{
    private static readonly object RehashLock = new();

    private readonly IrcOptionsStore _store;
    private readonly IHostEnvironment _env;
    private readonly IEnumerable<IConfigReloadListener> _listeners;
    private readonly ILogger<IrcConfigManager> _logger;

    public IrcConfigManager(
        IrcOptionsStore store,
        IHostEnvironment env,
        IEnumerable<IConfigReloadListener> listeners,
        ILogger<IrcConfigManager> logger)
    {
        _store = store;
        _env = env;
        _listeners = listeners ?? Array.Empty<IConfigReloadListener>();
        _logger = logger;
    }

    public IrcOptions Current => _store.Value;

    public RehashResult TryRehashFromConfiguredPath()
    {
        var conf = Current.ConfigFile;
        if (string.IsNullOrWhiteSpace(conf))
            conf = "confs/ircd.conf";

        var confPath = Path.IsPathRooted(conf)
            ? conf
            : Path.Combine(_env.ContentRootPath, conf);

        return TryRehash(confPath);
    }

    public RehashResult TryRehash(string confPath)
    {
        if (string.IsNullOrWhiteSpace(confPath))
            return RehashResult.Fail("REHASH failed: empty config path");

        confPath = Path.GetFullPath(confPath);

        if (!File.Exists(confPath))
            return RehashResult.Fail($"REHASH failed: config file not found ({confPath})");

        lock (RehashLock)
        {
            var oldCfg = Current;

            IrcOptions next;
            try
            {
                var selectedProfile = IrcdConfLoader.TryGetSecurityProfile(confPath) ?? "default";

                next = new IrcOptions
                {
                    ConfigFile = oldCfg.ConfigFile
                };

                next.Security.Profile = selectedProfile;
                SecurityProfileApplier.Apply(next);

                IRCd.Core.Config.IrcdConfLoader.ApplyConfFile(next, confPath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "REHASH parse failed");
                return RehashResult.Fail($"REHASH failed: {ex.Message}");
            }

            var errors = IrcOptionsValidation.Validate(next, _env.ContentRootPath);
            if (errors.Count > 0)
            {
                return new RehashResult(false, errors.ToArray(), Array.Empty<string>());
            }

            var changes = IrcOptionsDiff.Diff(oldCfg, next, maxChanges: 100);

            _store.Swap(next);

            foreach (var c in changes.Take(50))
            {
                _logger.LogInformation("Config changed: {Change}", c);
            }

            foreach (var l in _listeners)
            {
                try { l.OnConfigReloaded(oldCfg, next); }
                catch (Exception ex) { _logger.LogWarning(ex, "Config reload listener failed"); }
            }

            return new RehashResult(true, Array.Empty<string>(), changes.ToArray());
        }
    }

    public readonly record struct RehashResult(bool Success, string[] Errors, string[] Changes)
    {
        public static RehashResult Fail(string error)
            => new(false, new[] { error }, Array.Empty<string>());
    }
}
