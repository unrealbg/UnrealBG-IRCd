namespace IRCd.IntegrationTests.Infrastructure;

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

public sealed class ServerCluster : IAsyncDisposable
{
    private readonly string _repoRoot;
    private readonly string _rootDir;

    private readonly List<ServerProcess> _processes = new();

    public ServerCluster(string? rootDir = null)
    {
        _repoRoot = RepoRoot.Find();

        _rootDir = rootDir ?? Path.Combine(Path.GetTempPath(), "UnrealBG-IRCd", "integration", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_rootDir);
    }

    public string RootDir => _rootDir;

    public async Task<(ServerNodeConfig Node, ServerProcess Proc, string ConfigPath)> StartNodeAsync(
        ServerNodeConfig node,
        LinkPeer[] peers,
        CancellationToken ct)
    {
        var nodeDir = Path.Combine(_rootDir, node.Name);
        Directory.CreateDirectory(nodeDir);

        var configPath = Path.Combine(nodeDir, "ircd.conf");
        var conf = node.BuildConf(peers);
        await File.WriteAllTextAsync(configPath, conf, ct);

        var proc = new ServerProcess(_repoRoot, nodeDir, node.Name, node.ObservabilityPort);
        _processes.Add(proc);

        await proc.StartAsync(configPath, ct);

        return (node, proc, configPath);
    }

    public async ValueTask DisposeAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        for (var i = _processes.Count - 1; i >= 0; i--)
        {
            try { await _processes[i].DisposeAsync(); } catch { }
        }

        var keep = string.Equals(Environment.GetEnvironmentVariable("IRCD_IT_KEEP"), "1", StringComparison.OrdinalIgnoreCase);
        if (!keep)
        {
            try { Directory.Delete(_rootDir, recursive: true); } catch { }
        }
    }
}
