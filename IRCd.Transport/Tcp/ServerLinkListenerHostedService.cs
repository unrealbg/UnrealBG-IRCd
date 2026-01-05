namespace IRCd.Transport.Tcp
{
    using System;
    using System.Net;
    using System.Net.Sockets;
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Core.Services;
    using IRCd.Shared.Options;

    using Microsoft.Extensions.Hosting;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;

    public sealed class ServerLinkListenerHostedService : BackgroundService
    {
        private readonly ILogger<ServerLinkListenerHostedService> _logger;
        private readonly IOptions<IrcOptions> _options;
        private readonly ServerLinkService _links;

        private TcpListener? _listener;

        private readonly object _activeLock = new();
        private readonly System.Collections.Generic.Dictionary<string, TcpServerLinkSession> _activeSessions = new(StringComparer.Ordinal);

        private readonly SimpleFloodGate _floodGate = new(maxLines: 50, window: TimeSpan.FromSeconds(10));

        public ServerLinkListenerHostedService(
            ILogger<ServerLinkListenerHostedService> logger,
            IOptions<IrcOptions> options,
            ServerLinkService links)
        {
            _logger = logger;
            _options = options;
            _links = links;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var bindIp = _options.Value.Listen?.BindIp ?? "0.0.0.0";
            var ip = IPAddress.TryParse(bindIp, out var parsed) ? parsed : IPAddress.Any;
            var port = _options.Value.Listen?.ServerPort > 0 ? _options.Value.Listen.ServerPort : 6900;

            _listener = new TcpListener(ip, port);
            _listener.Start();

            _logger.LogInformation("S2S listening on {IP}:{Port}", ip, port);

            while (!stoppingToken.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(stoppingToken);
                }
                catch (OperationCanceledException)
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

        public override Task StopAsync(CancellationToken cancellationToken)
        {
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
            var session = new TcpServerLinkSession(connectionId, client);

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
