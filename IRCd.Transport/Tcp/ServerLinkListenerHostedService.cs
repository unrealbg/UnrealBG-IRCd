namespace IRCd.Transport.Tcp
{
    using System;
    using System.Net;
    using System.Net.Sockets;
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Core.Abstractions;
    using IRCd.Core.Services;
    using IRCd.Shared.Options;

    using Microsoft.Extensions.Hosting;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;

    public sealed class ServerLinkListenerHostedService : BackgroundService
    {
        private readonly ILogger<ServerLinkListenerHostedService> _logger;
        private readonly IOptionsMonitor<IrcOptions> _options;
        private readonly ServerLinkService _links;
        private readonly IAcceptLoopStatus _acceptLoops;

        private TcpListener? _listener;

        private CancellationTokenSource? _acceptCts;
        private IDisposable? _optionsSub;
        private string _listenFingerprint = string.Empty;
        private readonly object _restartLock = new();

        private readonly object _activeLock = new();
        private readonly System.Collections.Generic.Dictionary<string, TcpServerLinkSession> _activeSessions = new(StringComparer.Ordinal);

        private readonly SimpleFloodGate _floodGate;

        public ServerLinkListenerHostedService(
            ILogger<ServerLinkListenerHostedService> logger,
            IOptionsMonitor<IrcOptions> options,
            ServerLinkService links,
            IAcceptLoopStatus acceptLoops)
        {
            _logger = logger;
            _options = options;
            _links = links;
            _acceptLoops = acceptLoops;

            var flood = options.CurrentValue.Flood?.ServerLink;
            var maxLines = flood?.MaxLines > 0 ? flood.MaxLines : 50;
            var windowSeconds = flood?.WindowSeconds > 0 ? flood.WindowSeconds : 10;
            _floodGate = new SimpleFloodGate(maxLines: maxLines, window: TimeSpan.FromSeconds(windowSeconds));
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _listenFingerprint = ComputeFingerprint(_options.CurrentValue);
            _optionsSub = _options.OnChange((cfg, _) =>
            {
                var fp = ComputeFingerprint(cfg);
                if (string.Equals(fp, _listenFingerprint, StringComparison.Ordinal))
                    return;

                _listenFingerprint = fp;
                _logger.LogInformation("S2S listen config changed; restarting listener");
                RequestRestart();
            });

            while (!stoppingToken.IsCancellationRequested)
            {
                var (ip, port) = GetBind(_options.CurrentValue);

                var acceptCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                lock (_restartLock)
                {
                    _acceptCts?.Dispose();
                    _acceptCts = acceptCts;
                }

                var acceptCt = acceptCts.Token;

                _listener = new TcpListener(ip, port);
                _listener.Start();

                _logger.LogInformation("S2S listening on {IP}:{Port}", ip, port);

                try
                {
                    _acceptLoops.AcceptLoopStarted();
                    while (!acceptCt.IsCancellationRequested)
                    {
                        TcpClient client;
                        try
                        {
                            client = await _listener.AcceptTcpClientAsync(acceptCt);
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                        catch (ObjectDisposedException)
                        {
                            break;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "S2S accept failed");
                            continue;
                        }

                        _ = Task.Run(() => HandleLinkAsync(client, stoppingToken), stoppingToken);
                    }
                }
                finally
                {
                    _acceptLoops.AcceptLoopStopped();
                    try { _listener?.Stop(); } catch { }

                    lock (_restartLock)
                    {
                        if (ReferenceEquals(_acceptCts, acceptCts))
                            _acceptCts = null;
                    }

                    acceptCts.Dispose();
                }
            }
        }

        private static (IPAddress Ip, int Port) GetBind(IrcOptions options)
        {
            var bindIp = options.Listen?.BindIp ?? "0.0.0.0";
            var ip = IPAddress.TryParse(bindIp, out var parsed) ? parsed : IPAddress.Any;
            var port = options.Listen?.ServerPort > 0 ? options.Listen.ServerPort : 6900;
            return (ip, port);
        }

        private static string ComputeFingerprint(IrcOptions options)
        {
            var (ip, port) = GetBind(options);
            return $"{ip}:{port}";
        }

        private void RequestRestart()
        {
            CancellationTokenSource? cts;
            lock (_restartLock)
            {
                cts = _acceptCts;
            }

            try { cts?.Cancel(); } catch { }
            try { _listener?.Stop(); } catch { }
        }

        public override Task StopAsync(CancellationToken cancellationToken)
        {
            try { _optionsSub?.Dispose(); } catch { }

            try
            {
                lock (_restartLock)
                {
                    _acceptCts?.Cancel();
                }
            }
            catch { }

            try { _listener?.Stop(); } catch { }

            TcpServerLinkSession[] sessions;
            lock (_activeLock)
            {
                sessions = _activeSessions.Values.ToArray();
            }

            foreach (var s in sessions)
            {
                try { _ = s.CloseAsync("Server shutting down", cancellationToken); } catch { }
            }

            return base.StopAsync(cancellationToken);
        }

        private async Task HandleLinkAsync(TcpClient client, CancellationToken ct)
        {
            var connectionId = Guid.NewGuid().ToString("N");
            var cap = _options.CurrentValue.Transport?.Queues?.ServerLinkSendQueueCapacity ?? 2048;
            var session = new TcpServerLinkSession(connectionId, client, cap);

            lock (_activeLock)
            {
                _activeSessions[connectionId] = session;
            }

            _logger.LogInformation("S2S inbound connection {ConnId} from {Remote}", connectionId, session.RemoteEndPoint);

            try
            {
                if (!_floodGate.Allow(connectionId))
                {
                    await session.CloseAsync("Excess Flood", ct);
                    return;
                }

                await _links.HandleIncomingLinkAsync(session, ct);
            }
            finally
            {
                try { _floodGate.Remove(connectionId); } catch { }

                lock (_activeLock)
                {
                    _activeSessions.Remove(connectionId);
                }
            }

            _logger.LogInformation("S2S disconnected {ConnId}", connectionId);
        }
    }
}
