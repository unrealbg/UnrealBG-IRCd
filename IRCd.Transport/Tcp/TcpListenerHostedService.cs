namespace IRCd.Transport.Tcp
{
    using IRCd.Core.Commands;
    using IRCd.Core.Protocol;
    using IRCd.Core.State;
    using IRCd.Shared.Options;

    using Microsoft.Extensions.Hosting;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;

    using System;
    using System.Net;
    using System.Net.Sockets;

    public sealed class TcpListenerHostedService : BackgroundService
    {
        private readonly ILogger<TcpListenerHostedService> _logger;
        private readonly CommandDispatcher _dispatcher;
        private readonly ServerState _state;
        private readonly IOptions<IrcOptions> _options;

        private TcpListener? _listener;

        public TcpListenerHostedService(
            ILogger<TcpListenerHostedService> logger,
            CommandDispatcher dispatcher,
            ServerState state,
            IOptions<IrcOptions> options)
        {
            _logger = logger;
            _dispatcher = dispatcher;
            _state = state;
            _options = options;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var opt = _options.Value;

            var ip = IPAddress.TryParse(opt.BindAddress, out var parsed) ? parsed : IPAddress.Any;
            var port = opt.IrcPort;

            _listener = new TcpListener(ip, port);
            _listener.Start();

            _logger.LogInformation("IRCd listening on {IP}:{Port}", ip, port);

            try
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(stoppingToken);
                    _ = Task.Run(() => HandleClientAsync(client, stoppingToken), stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
                // normal shutdown
            }
            finally
            {
                try { _listener.Stop(); } catch { /* ignore */ }
            }
        }

        private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
        {
            var connectionId = Guid.NewGuid().ToString("N");
            var session = new TcpClientSession(connectionId, client);

            _state.TryAddUser(new User { ConnectionId = connectionId });

            _logger.LogInformation("Client connected {ConnId} from {Remote}", connectionId, session.RemoteEndPoint);

            var writerTask = Task.Run(() => session.RunWriterLoopAsync(ct), ct);

            await session.SendAsync(":server NOTICE * :Welcome. Use NICK/USER. Example: PING :123", ct);

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var line = await session.ReadLineAsync(ct);
                    if (line is null) break;
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    IrcMessage msg;
                    try
                    {
                        msg = IrcParser.ParseLine(line);
                    }
                    catch
                    {
                        await session.SendAsync(":server NOTICE * :Bad line", ct);
                        continue;
                    }

                    await _dispatcher.DispatchAsync(session, msg, _state, ct);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Client loop error {ConnId}", connectionId);
            }
            finally
            {
                await session.CloseAsync("Client disconnected", ct);
                _logger.LogInformation("Client disconnected {ConnId}", connectionId);
            }

            try { await writerTask; } catch { /* ignore */ }
        }
    }
}
