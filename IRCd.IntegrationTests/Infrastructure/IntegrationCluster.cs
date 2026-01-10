namespace IRCd.IntegrationTests.Infrastructure
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;

    public sealed class IntegrationCluster : IAsyncDisposable
    {
        private readonly string _repoRoot;
        private readonly string _rootDir;
        private readonly bool _keepLogs;

        private readonly List<(ServerNodeConfig Config, string ConfPath, ServerProcess Proc)> _nodes = new();

        public IntegrationCluster(string repoRoot, string rootDir, bool keepLogs)
        {
            _repoRoot = repoRoot;
            _rootDir = rootDir;
            _keepLogs = keepLogs;

            Directory.CreateDirectory(_rootDir);
        }

        public string RootDir => _rootDir;

        public IReadOnlyList<(ServerNodeConfig Config, string ConfPath, ServerProcess Proc)> Nodes => _nodes;

        public async Task<(ServerNodeConfig Config, ServerProcess Proc)> AddAndStartNodeAsync(ServerNodeConfig config, LinkPeer[] peers, CancellationToken ct)
        {
            var nodeDir = Path.Combine(_rootDir, config.Name);
            Directory.CreateDirectory(nodeDir);

            var confPath = Path.Combine(nodeDir, "ircd.conf");
            await File.WriteAllTextAsync(confPath, config.BuildConf(peers), ct);

            var proc = new ServerProcess(_repoRoot, nodeDir, config.Name, config.ObservabilityPort);
            await proc.StartAsync(confPath, ct);

            _nodes.Add((config, confPath, proc));
            return (config, proc);
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var n in _nodes)
            {
                try
                {
                    await n.Proc.DisposeAsync();
                }
                catch
                {
                    // best-effort
                }
            }

            if (_keepLogs)
            {
                return;
            }

            try
            {
                if (Directory.Exists(_rootDir))
                {
                    Directory.Delete(_rootDir, recursive: true);
                }
            }
            catch
            {
                // best-effort
            }
        }
    }
}
