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
            return base.StopAsync(cancellationToken);
        }

        private async Task HandleLinkAsync(TcpClient client, CancellationToken ct)
        {
            var connectionId = Guid.NewGuid().ToString("N");
            var session = new TcpServerLinkSession(connectionId, client);

            _logger.LogInformation("S2S inbound connection {ConnId} from {Remote}", connectionId, session.RemoteEndPoint);

            await _links.HandleIncomingLinkAsync(session, ct);

            _logger.LogInformation("S2S disconnected {ConnId}", connectionId);
        }
    }
}
