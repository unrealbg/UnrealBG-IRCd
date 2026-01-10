namespace IRCd.IntegrationTests.Infrastructure
{
    using System;
    using System.Diagnostics;
    using System.IO;
    using System.Net.Http;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;

    public sealed class ServerProcess : IAsyncDisposable
    {
        private readonly string _workDir;
        private readonly string _repoRoot;
        private readonly string _nodeName;
        private readonly int _observabilityPort;

        private Process? _process;

        public ServerProcess(string repoRoot, string workDir, string nodeName, int observabilityPort)
        {
            _repoRoot = repoRoot;
            _workDir = workDir;
            _nodeName = nodeName;
            _observabilityPort = observabilityPort;

            Directory.CreateDirectory(_workDir);
        }

        public string WorkDir => _workDir;

        public string StdoutPath => Path.Combine(_workDir, "stdout.log");

        public string StderrPath => Path.Combine(_workDir, "stderr.log");

        public bool IsRunning => _process is { HasExited: false };

        public async Task StartAsync(string configPath, CancellationToken ct)
        {
            if (_process is { HasExited: false })
                throw new InvalidOperationException("Server process already running.");

            var stdout = new FileStream(StdoutPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
            var stderr = new FileStream(StderrPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);

            var psi = new ProcessStartInfo
            {
                FileName = "dotnet",
                WorkingDirectory = _repoRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            // dotnet run --no-build --project IRCd.Server -- --Irc:ConfigFile <abs>
            psi.ArgumentList.Add("run");
            psi.ArgumentList.Add("--no-build");
            psi.ArgumentList.Add("--project");
            psi.ArgumentList.Add(Path.Combine(_repoRoot, "IRCd.Server", "IRCd.Server.csproj"));
            psi.ArgumentList.Add("--");
            psi.ArgumentList.Add("--Irc:ConfigFile");
            psi.ArgumentList.Add(configPath);

            var p = new Process { StartInfo = psi, EnableRaisingEvents = true };

            if (!p.Start())
                throw new InvalidOperationException("Failed to start dotnet process.");

            _process = p;

            _ = PumpStreamAsync(p.StandardOutput, stdout, ct);
            _ = PumpStreamAsync(p.StandardError, stderr, ct);

            await WaitForHealthyAsync(ct);
        }

        public async Task StopAsync(CancellationToken ct)
        {
            var p = _process;
            if (p is null)
                return;

            try
            {
                if (!p.HasExited)
                {
                    try
                    {
                        p.Kill(entireProcessTree: true);
                    }
                    catch
                    {
                        // ignore
                    }

                    try
                    {
                        await p.WaitForExitAsync(ct);
                    }
                    catch
                    {
                        // ignore
                    }
                }
            }
            finally
            {
                p.Dispose();
                _process = null;
            }
        }

        public async ValueTask DisposeAsync()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await StopAsync(cts.Token);
        }

        private async Task WaitForHealthyAsync(CancellationToken ct)
        {
            using var http = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(2)
            };

            var url = $"http://127.0.0.1:{_observabilityPort}/healthz";
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(15);

            while (DateTimeOffset.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();

                if (_process is { HasExited: true })
                    throw new InvalidOperationException($"Server process for {_nodeName} exited early. Logs: {StdoutPath} {StderrPath}");

                try
                {
                    using var resp = await http.GetAsync(url, ct);
                    if ((int)resp.StatusCode == 200)
                        return;
                }
                catch
                {
                    // retry
                }

                await Task.Delay(200, ct);
            }

            throw new TimeoutException($"Timed out waiting for /healthz on {_nodeName} ({url}). Logs: {StdoutPath} {StderrPath}");
        }

        private static async Task PumpStreamAsync(StreamReader reader, FileStream output, CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync();
                    if (line is null)
                        break;

                    var bytes = Encoding.UTF8.GetBytes(line + Environment.NewLine);
                    await output.WriteAsync(bytes, ct);
                    await output.FlushAsync(ct);
                }
            }
            catch
            {
                // best-effort
            }
            finally
            {
                try { output.Dispose(); } catch { }
            }
        }
    }
}
