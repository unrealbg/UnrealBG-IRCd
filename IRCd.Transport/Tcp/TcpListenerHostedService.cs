namespace IRCd.Transport.Tcp
{
    using IRCd.Core.Commands;
    using IRCd.Core.Protocol;
    using IRCd.Core.State;
    using IRCd.Shared.Options;

    using Microsoft.Extensions.Hosting;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;
    using Microsoft.Win32;

    using System;
    using System.Net;
    using System.Net.Sockets;

    public sealed class TcpListenerHostedService : BackgroundService
    {
        private readonly ILogger<TcpListenerHostedService> _logger;
        private readonly CommandDispatcher _dispatcher;
        private readonly ServerState _state;
        private readonly IOptions<IrcOptions> _options;
        private readonly InMemorySessionRegistry _registry;
        private readonly SimpleFloodGate _flood;
        private TcpListener? _listener;

        public TcpListenerHostedService(
    ILogger<TcpListenerHostedService> logger,
    CommandDispatcher dispatcher,
    ServerState state,
    IOptions<IrcOptions> options,
    InMemorySessionRegistry registry,
    SimpleFloodGate flood)
        {
            _logger = logger;
            _dispatcher = dispatcher;
            _state = state;
            _options = options;
            _registry = registry;
            _flood = flood;
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

            _registry.TryAdd(session);

            _state.TryAddUser(new User { ConnectionId = connectionId });

            _logger.LogInformation("Client connected {ConnId} from {Remote}", connectionId, session.RemoteEndPoint);

            var writerTask = Task.Run(() => session.RunWriterLoopAsync(ct), ct);

            await session.SendAsync(":server NOTICE * :Welcome. Use NICK/USER.", ct);
            await session.SendAsync(":server NOTICE * :Try: JOIN #test | PRIVMSG #test :hello | QUIT :bye", ct);

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var line = await session.ReadLineAsync(ct);
                    if (line is null) break;

                    if (string.IsNullOrWhiteSpace(line)) continue;

                    if (!_flood.Allow(connectionId))
                    {
                        await session.SendAsync(":server NOTICE * :Flood detected, closing link", ct);
                        await session.CloseAsync("Excess flood", ct);
                        break;
                    }

                    IrcMessage msg;
                    try
                    {
                        msg = IrcParser.ParseLine(line);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Bad line from {ConnId}: {Line}", connectionId, line);
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
                _registry.TryRemove(connectionId, out _);
                _flood.Remove(connectionId);

                _state.RemoveUser(connectionId);

                try
                {
                    await session.CloseAsync("Client disconnected", ct);
                }
                catch { /* ignore */ }

                _logger.LogInformation("Client disconnected {ConnId}", connectionId);
            }

            try { await writerTask; } catch { /* ignore */ }
        }
    }
}
