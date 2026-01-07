namespace IRCd.Transport.Tcp
{
    using System;
    using System.Collections.Generic;
    using System.Net;
    using System.Net.Sockets;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Core.Abstractions;
    using IRCd.Core.Commands;
    using IRCd.Core.Protocol;
    using IRCd.Core.Services;
    using IRCd.Core.State;
    using IRCd.Shared.Options;

    using Microsoft.Extensions.Hosting;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;

    public sealed class TcpListenerHostedService : BackgroundService
    {
        private readonly ILogger<TcpListenerHostedService> _logger;
        private readonly CommandDispatcher _dispatcher;
        private readonly ServerState _state;
        private readonly IOptions<IrcOptions> _options;
        private readonly ISessionRegistry _sessions;
        private readonly HostmaskService _hostmask;

        private readonly BanService _bans;

        private readonly RateLimitService _rateLimit;

        private readonly ServerLinkService _links;

        private readonly SimpleFloodGate _floodGate;

        private readonly ConnectionGuardService _guard;

        private readonly RoutingService _routing;

        private readonly IMetrics _metrics;

        private readonly ConnectionAuthService? _authService;

        private readonly object _listenerLock = new();
        private readonly List<TcpListener> _listeners = new();

        private readonly object _activeLock = new();
        private readonly Dictionary<string, TcpClientSession> _activeSessions = new(StringComparer.Ordinal);

        public TcpListenerHostedService(
            ILogger<TcpListenerHostedService> logger,
            CommandDispatcher dispatcher,
            ServerState state,
            IOptions<IrcOptions> options,
            ISessionRegistry sessions,
            ConnectionGuardService guard,
            RoutingService routing,
            HostmaskService hostmask,
            RateLimitService rateLimit,
            BanService bans,
            ServerLinkService links,
            IMetrics metrics,
            ConnectionAuthService? authService = null)
        {
            _logger = logger;
            _dispatcher = dispatcher;
            _state = state;
            _options = options;
            _sessions = sessions;
            _guard = guard;
            _routing = routing;
            _hostmask = hostmask;
            _rateLimit = rateLimit;
            _bans = bans;
            _links = links;
            _metrics = metrics;
            _authService = authService;

            var flood = options.Value.Flood?.Client;
            var maxLines = flood?.MaxLines > 0 ? flood.MaxLines : 20;
            var windowSeconds = flood?.WindowSeconds > 0 ? flood.WindowSeconds : 10;
            _floodGate = new SimpleFloodGate(maxLines: maxLines, window: TimeSpan.FromSeconds(windowSeconds));
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var endpoints = _options.Value.ListenEndpoints?.Where(e => e is not null && !e.Tls).ToArray() ?? Array.Empty<ListenEndpointOptions>();
            if (endpoints.Length == 0)
            {
                var port = _options.Value.Listen?.ClientPort > 0
                    ? _options.Value.Listen.ClientPort
                    : _options.Value.IrcPort;

                endpoints = new[] { new ListenEndpointOptions { BindIp = _options.Value.Listen?.BindIp ?? "0.0.0.0", Port = port, Tls = false } };
            }

            var tasks = new List<Task>(endpoints.Length);

            foreach (var ep in endpoints)
            {
                var ip = IPAddress.Any;
                if (!string.IsNullOrWhiteSpace(ep.BindIp) && IPAddress.TryParse(ep.BindIp, out var parsed))
                    ip = parsed;

                var listener = new TcpListener(ip, ep.Port);
                listener.Start();

                lock (_listenerLock)
                {
                    _listeners.Add(listener);
                }

                _logger.LogInformation("IRCd listening on {IP}:{Port}", ip, ep.Port);

                tasks.Add(Task.Run(() => AcceptLoopAsync(listener, stoppingToken), stoppingToken));
            }

            await Task.WhenAll(tasks);
        }

        private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(ct);
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
                    _logger.LogWarning(ex, "Accept failed");
                    continue;
                }

                _ = Task.Run(() => HandleClientAsync(client, ct), ct);
            }
        }

        public override Task StopAsync(CancellationToken cancellationToken)
        {
            TcpListener[] listeners;
            lock (_listenerLock)
            {
                listeners = _listeners.ToArray();
                _listeners.Clear();
            }

            foreach (var l in listeners)
            {
                try { l.Stop(); } catch { /* ignore */ }
            }

            TcpClientSession[] sessions;
            lock (_activeLock)
            {
                sessions = _activeSessions.Values.ToArray();
            }

            foreach (var s in sessions)
            {
                try { _ = s.CloseAsync("Server shutting down", cancellationToken); } catch { /* ignore */ }
            }

            return base.StopAsync(cancellationToken);
        }

        private static IPAddress GetRemoteIp(TcpClient client)
        {
            if (client.Client.RemoteEndPoint is IPEndPoint ep)
                return ep.Address;

            return IPAddress.None;
        }

        private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
        {
            var remoteIp = GetRemoteIp(client);

            var ipBan = await _bans.TryMatchIpAsync(remoteIp, ct);
            if (ipBan is not null)
            {
                try
                {
                    using var stream = client.GetStream();
                    using var writer = new System.IO.StreamWriter(stream, new UTF8Encoding(false))
                    {
                        NewLine = "\r\n",
                        AutoFlush = true
                    };

                    var banText = ipBan.Type == BanType.ZLINE ? "Z-Lined" : "D-Lined";
                    await writer.WriteLineAsync($"ERROR :{banText} ({ipBan.Reason})");
                }
                catch { /* ignore */ }

                try { client.Close(); } catch { /* ignore */ }
                return;
            }

            var localEndPoint = client.Client.LocalEndPoint ?? new IPEndPoint(IPAddress.None, 0);

            if (_guard.Enabled)
            {
                if (!_guard.TryAcceptNewConnection(remoteIp, out var rejectReason))
                {
                    try
                    {
                        using var stream = client.GetStream();
                        using var writer = new System.IO.StreamWriter(stream, new UTF8Encoding(false))
                        {
                            NewLine = "\r\n",
                            AutoFlush = true
                        };

                        await writer.WriteLineAsync($"ERROR :{rejectReason}");
                    }
                    catch { /* ignore */ }

                    try { client.Close(); } catch { /* ignore */ }
                    return;
                }
            }

            var connectionId = Guid.NewGuid().ToString("N");
            var tcp = _options.Value.Transport?.Tcp;
            var keepAliveEnabled = tcp?.KeepAliveEnabled ?? true;
            var keepAliveTimeMs = tcp?.KeepAliveTimeMs ?? 120_000;
            var keepAliveIntervalMs = tcp?.KeepAliveIntervalMs ?? 30_000;

            var queueCap = _options.Value.Transport?.Queues?.ClientSendQueueCapacity ?? 256;

            var session = new TcpClientSession(
                connectionId,
                client,
                localEndPoint,
                keepAliveEnabled,
                keepAliveTimeMs,
                keepAliveIntervalMs,
                queueCap);

            var metricsCounted = false;

            lock (_activeLock)
            {
                _activeSessions[connectionId] = session;
            }

            var now = DateTimeOffset.UtcNow;
            var host = _hostmask.GetDisplayedHost(remoteIp);

            _state.TryAddUser(new User
            {
                ConnectionId = connectionId,
                ConnectedAtUtc = now,
                LastActivityUtc = now,
                Host = host,
                RemoteIp = remoteIp.ToString(),
                IsSecureConnection = false
            });

            _sessions.Add(session);

            _metrics.ConnectionAccepted(secure: false);
            metricsCounted = true;

            _logger.LogInformation("Client connected {ConnId} from {Remote}", connectionId, session.RemoteEndPoint);

            var writerTask = Task.Run(() => session.RunWriterLoopAsync(ct), ct);

            var serverName = _options.Value.ServerInfo?.Name ?? "server";
            await session.SendAsync($":{serverName} NOTICE * :Welcome. Use NICK/USER.", ct);

            if (_authService is not null)
            {
                var remotePort = (session.RemoteEndPoint as IPEndPoint)?.Port ?? 0;
                var localPort = (session.LocalEndPoint as IPEndPoint)?.Port ?? 0;
                _authService.StartAuthChecks(session, remoteIp, remotePort, localPort, ct);
            }

            Task<string?>? pendingRead = null;

            var registrationDeadlineUtc = DateTimeOffset.UtcNow.AddSeconds(_guard.GetRegistrationTimeoutSeconds());
            var unregisteredReleased = false;
            var markedRegistered = false;
            var disconnectReason = "Unknown";

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    if (_guard.Enabled && !session.IsRegistered && DateTimeOffset.UtcNow > registrationDeadlineUtc)
                    {
                        disconnectReason = "Registration timeout";
                        try { await session.SendAsync("ERROR :Registration timeout", ct); } catch { /* ignore */ }
                        try { await session.CloseAsync("Registration timeout", ct); } catch { /* ignore */ }
                        break;
                    }

                    pendingRead ??= session.ReadLineAsync(ct);

                    var tick = Task.Delay(TimeSpan.FromSeconds(1), ct);
                    var completed = await Task.WhenAny(pendingRead, tick);

                    if (completed != pendingRead)
                    {
                        continue;
                    }

                    string? line;
                    try
                    {
                        line = await pendingRead;
                    }
                    catch (ObjectDisposedException) 
                    { 
                        disconnectReason = "Socket disposed";
                        break; 
                    }
                    catch (InvalidOperationException) 
                    { 
                        disconnectReason = "Invalid operation";
                        break; 
                    }

                    pendingRead = null;

                    if (line is null)
                    {
                        disconnectReason = "TCP connection closed by client";
                        break;
                    }

                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    if (!_floodGate.Allow(connectionId))
                    {
                        disconnectReason = "Excess Flood";
                        _metrics.FloodKick();
                        try { await session.SendAsync("ERROR :Excess Flood", ct); } catch { /* ignore */ }
                        try { await session.CloseAsync("Excess Flood", ct); } catch { /* ignore */ }
                        break;
                    }

                    session.OnInboundLine();

                    IrcMessage msg;
                    try
                    {
                        msg = IrcParser.ParseLine(line);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Bad line from {ConnId}: {Line}", connectionId, line);
                        continue;
                    }

                    try
                    {
                        await _dispatcher.DispatchAsync(session, msg, _state, ct);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "FATAL: DispatchAsync threw exception for {ConnId} command {Command}", connectionId, msg.Command);
                        disconnectReason = $"Server error processing {msg.Command}";
                        break;
                    }

                    if (!markedRegistered && session.IsRegistered)
                    {
                        markedRegistered = true;

                        if (_guard.Enabled)
                        {
                            _guard.MarkRegistered(remoteIp);
                            unregisteredReleased = true;
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                disconnectReason = "Server shutdown";
            }
            catch (Exception ex)
            {
                disconnectReason = $"Exception: {ex.GetType().Name}";
                _logger.LogWarning(ex, "Client loop error {ConnId}", connectionId);
            }
            finally
            {
                var duration = DateTimeOffset.UtcNow - now;
                _logger.LogInformation("Connection {ConnId} ({Nick}) ending after {Duration:F1}s: {Reason}", 
                    connectionId, 
                    session.Nick ?? "<not registered>",
                    duration.TotalSeconds,
                    disconnectReason);

                try
                {
                    if (session.IsRegistered && !string.IsNullOrWhiteSpace(session.Nick))
                    {
                        var nick = session.Nick!;
                        var user = session.UserName ?? "u";
                        var displayedHost = "localhost";

                        if (_state.TryGetUser(connectionId, out var u) && u is not null && !string.IsNullOrWhiteSpace(u.Host))
                        {
                            displayedHost = u.Host!;
                        }

                        var quitLine = $":{nick}!{user}@{displayedHost} QUIT :Client disconnected";

                        var recipients = new HashSet<string>(StringComparer.Ordinal);
                        var channels = _state.GetUserChannels(connectionId).ToList();

                        _logger.LogInformation("Broadcasting QUIT for {Nick} to {ChannelCount} channels", nick, channels.Count);

                        foreach (var chName in channels)
                        {
                            if (_state.TryGetChannel(chName, out var ch) && ch is not null)
                            {
                                foreach (var m in ch.Members)
                                {
                                    if (m.ConnectionId != connectionId)
                                        recipients.Add(m.ConnectionId);
                                }
                            }
                        }

                        _logger.LogInformation("Sending QUIT for {Nick} to {RecipientCount} recipients", nick, recipients.Count);

                        foreach (var rid in recipients)
                        {
                            await _routing.SendToUserAsync(rid, quitLine, CancellationToken.None);
                        }

                        if (_state.TryGetUser(connectionId, out var meUser) && meUser is not null && !string.IsNullOrWhiteSpace(meUser.Uid))
                        {
                            await _links.PropagateQuitAsync(meUser.Uid!, "Client disconnected", CancellationToken.None);
                        }
                    }
                }
                catch { /* ignore */ }

                try { _sessions.Remove(connectionId); } catch { /* ignore */ }
                try { _state.RemoveUser(connectionId); } catch { /* ignore */ }
                try { _rateLimit.ClearConnection(connectionId); } catch { /* ignore */ }

                lock (_activeLock)
                {
                    _activeSessions.Remove(connectionId);
                }

                if (_guard.Enabled && !session.IsRegistered && !unregisteredReleased)
                {
                    try { _guard.ReleaseUnregistered(remoteIp); } catch { /* ignore */ }
                }

                if (_guard.Enabled)
                {
                    try { _guard.ReleaseActive(remoteIp); } catch { /* ignore */ }
                }

                try { await session.CloseAsync("Client disconnected", CancellationToken.None); } catch { /* ignore */ }

                if (metricsCounted)
                {
                    _metrics.ConnectionClosed(secure: false);
                }

                _logger.LogInformation("Client disconnected {ConnId}", connectionId);
            }

            try { await writerTask; } catch { /* ignore */ }
        }
    }
}
