namespace IRCd.Transport.Tls
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Net.Security;
    using System.Net.Sockets;
    using System.Security.Authentication;
    using System.Security.Cryptography.X509Certificates;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Core.Abstractions;
    using IRCd.Core.Commands;
    using IRCd.Core.Protocol;
    using IRCd.Core.Services;
    using IRCd.Core.State;
    using IRCd.Shared.Options;

    using IRCd.Transport;

    using Microsoft.Extensions.Hosting;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;

    public sealed class TlsListenerHostedService : BackgroundService
    {
        private readonly ILogger<TlsListenerHostedService> _logger;
        private readonly IIrcLogRedactor _logRedactor;
        private readonly CommandDispatcher _dispatcher;
        private readonly ServerState _state;
        private readonly IOptionsMonitor<IrcOptions> _options;
        private readonly ISessionRegistry _sessions;
        private readonly HostmaskService _hostmask;
        private readonly ConnectionGuardService _guard;
        private readonly RoutingService _routing;
        private readonly RateLimitService _rateLimit;
        private readonly BanService _bans;
        private readonly IConnectionPrecheckPipeline _precheck;
        private readonly IHostEnvironment _env;
        private readonly ServerLinkService _links;

        private readonly IMetrics _metrics;

        private readonly IAcceptLoopStatus _acceptLoops;

        private readonly ILoggerFactory _loggerFactory;

        private readonly IRCd.Transport.Tcp.SimpleFloodGate _floodGate;

        private readonly ConnectionAuthService? _authService;

        private readonly LogRateLimiter _guardLogLimiter = new(windowSeconds: 10, maxEventsPerWindow: 3);

        private readonly LogRateLimiter _handshakeLogLimiter = new(windowSeconds: 10, maxEventsPerWindow: 3);

        private CancellationTokenSource? _acceptCts;
        private IDisposable? _optionsSub;
        private string _listenFingerprint = string.Empty;
        private readonly object _restartLock = new();
        private TaskCompletionSource<bool> _wakeup = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly object _listenerLock = new();
        private readonly List<TcpListener> _listeners = new();
        private X509Certificate2? _cert;
        private Dictionary<string, X509Certificate2>? _sniCerts;

        private readonly object _activeLock = new();
        private readonly Dictionary<string, TlsClientSession> _activeSessions = new(StringComparer.Ordinal);

        public TlsListenerHostedService(
            ILogger<TlsListenerHostedService> logger,
            IIrcLogRedactor logRedactor,
            CommandDispatcher dispatcher,
            ServerState state,
            IOptionsMonitor<IrcOptions> options,
            ISessionRegistry sessions,
            ConnectionGuardService guard,
            RoutingService routing,
            HostmaskService hostmask,
            RateLimitService rateLimit,
            BanService bans,
            IConnectionPrecheckPipeline precheck,
            IHostEnvironment env,
            ServerLinkService links,
            IMetrics metrics,
            IAcceptLoopStatus acceptLoops,
            ILoggerFactory loggerFactory,
            ConnectionAuthService? authService = null)
        {
            _logger = logger;
            _logRedactor = logRedactor;
            _dispatcher = dispatcher;
            _state = state;
            _options = options;
            _sessions = sessions;
            _guard = guard;
            _routing = routing;
            _hostmask = hostmask;
            _rateLimit = rateLimit;
            _bans = bans;
            _precheck = precheck;
            _env = env;
            _links = links;
            _metrics = metrics;
            _acceptLoops = acceptLoops;
            _loggerFactory = loggerFactory;

            _authService = authService;

            var flood = options.CurrentValue.Flood?.TlsClient;
            var maxLines = flood?.MaxLines > 0 ? flood.MaxLines : 20;
            var windowSeconds = flood?.WindowSeconds > 0 ? flood.WindowSeconds : 10;
            _floodGate = new IRCd.Transport.Tcp.SimpleFloodGate(maxLines: maxLines, window: TimeSpan.FromSeconds(windowSeconds));
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _listenFingerprint = ComputeFingerprint(_options.CurrentValue);
            _optionsSub = _options.OnChange((cfg, _) =>
            {
                var fp = ComputeFingerprint(cfg);
                if (string.Equals(fp, _listenFingerprint, StringComparison.Ordinal))
                {
                    return;
                }

                _listenFingerprint = fp;
                _logger.LogInformation("TLS listen config changed; restarting listeners");
                Wakeup();
                RequestRestart();
            });

            while (!stoppingToken.IsCancellationRequested)
            {
                var cfg = _options.CurrentValue;
                var listen = cfg.Listen;
                if (listen is null || !listen.EnableTls)
                {
                    await WaitForWakeupAsync(stoppingToken);
                    continue;
                }

                var certPath = listen.TlsCertificatePath;
                var certPassword = listen.TlsCertificatePassword;

                if (string.IsNullOrWhiteSpace(certPath) && listen.AutoGenerateSelfSignedCertificate)
                {
                    try
                    {
                        _cert = SelfSignedCertificateGenerator.CreateAndPersistPfx(
                            _env.ContentRootPath,
                            listen.AutoGeneratedCertPath,
                            listen.AutoGeneratedCertPassword,
                            listen.AutoGeneratedCertCommonName,
                            listen.AutoGeneratedCertDaysValid);

                        _logger.LogWarning(
                            "Generated self-signed TLS certificate at {Path}. This is intended for dev/test, not production.",
                            listen.AutoGeneratedCertPath);

                        certPath = listen.AutoGeneratedCertPath;
                        certPassword = listen.AutoGeneratedCertPassword;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to auto-generate self-signed TLS certificate. TLS listener will not start.");
                        await WaitForWakeupAsync(stoppingToken);
                        continue;
                    }
                }

                if (string.IsNullOrWhiteSpace(certPath))
                {
                    _logger.LogWarning("TLS enabled but TlsCertificatePath is empty. TLS listener will not start.");
                    await WaitForWakeupAsync(stoppingToken);
                    continue;
                }

                try
                {
                    var fullPath = certPath;
                    if (!System.IO.Path.IsPathRooted(fullPath))
                    {
                        fullPath = System.IO.Path.Combine(_env.ContentRootPath, fullPath);
                    }

                    if (!System.IO.File.Exists(fullPath) && listen.AutoGenerateSelfSignedCertificate)
                    {
                        _cert = SelfSignedCertificateGenerator.CreateAndPersistPfx(
                            _env.ContentRootPath,
                            listen.AutoGeneratedCertPath,
                            listen.AutoGeneratedCertPassword,
                            listen.AutoGeneratedCertCommonName,
                            listen.AutoGeneratedCertDaysValid);
                    }
                    else
                    {
                        _cert = string.IsNullOrWhiteSpace(certPassword)
                            ? X509CertificateLoader.LoadPkcs12FromFile(fullPath, null)
                            : X509CertificateLoader.LoadPkcs12FromFile(fullPath, certPassword);
                    }

                    if (listen.TlsCertificates is not null && listen.TlsCertificates.Count > 0)
                    {
                        _sniCerts = new Dictionary<string, X509Certificate2>(StringComparer.OrdinalIgnoreCase);

                        foreach (var kv in listen.TlsCertificates)
                        {
                            var name = kv.Key?.Trim();
                            var certCfg = kv.Value;
                            if (string.IsNullOrWhiteSpace(name) || certCfg is null || string.IsNullOrWhiteSpace(certCfg.Path))
                            {
                                continue;
                            }

                            var p = certCfg.Path;
                            if (!System.IO.Path.IsPathRooted(p))
                            {
                                p = System.IO.Path.Combine(_env.ContentRootPath, p);
                            }

                            if (!System.IO.File.Exists(p))
                            {
                                continue;
                            }

                            var c = string.IsNullOrWhiteSpace(certCfg.Password)
                                ? X509CertificateLoader.LoadPkcs12FromFile(p, null)
                                : X509CertificateLoader.LoadPkcs12FromFile(p, certCfg.Password);

                            _sniCerts[name] = c;
                        }
                    }

                    _logger.LogInformation(
                        "TLS certificate loaded. Subject={Subject} Thumbprint={Thumbprint}",
                        _cert.Subject,
                        _cert.Thumbprint);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to load TLS certificate from {Path}", certPath);
                    await WaitForWakeupAsync(stoppingToken);
                    continue;
                }

                var endpoints = GetTlsEndpoints(cfg);

                var acceptCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                lock (_restartLock)
                {
                    _acceptCts?.Dispose();
                    _acceptCts = acceptCts;
                }

                var acceptCt = acceptCts.Token;

                var tasks = new List<Task>(endpoints.Length);

                foreach (var ep in endpoints)
                {
                    var ip = IPAddress.Any;
                    if (!string.IsNullOrWhiteSpace(ep.BindIp) && IPAddress.TryParse(ep.BindIp, out var parsed))
                    {
                        ip = parsed;
                    }

                    var listener = new TcpListener(ip, ep.Port);
                    listener.Start();

                    lock (_listenerLock)
                    {
                        _listeners.Add(listener);
                    }

                    _logger.LogInformation("IRCd TLS listening on {IP}:{Port}", ip, ep.Port);
                    tasks.Add(Task.Run(() => AcceptLoopTrackedAsync(listener, acceptCt, stoppingToken), stoppingToken));
                }

                try
                {
                    await Task.WhenAll(tasks);
                }
                finally
                {
                    StopListenersOnly();

                    lock (_restartLock)
                    {
                        if (ReferenceEquals(_acceptCts, acceptCts))
                        {
                            _acceptCts = null;
                        }
                    }

                    acceptCts.Dispose();
                }
            }
        }

        private static ListenEndpointOptions[] GetTlsEndpoints(IrcOptions options)
        {
            var endpoints = options.ListenEndpoints?.Where(e => e is not null && e.Tls).ToArray() ?? Array.Empty<ListenEndpointOptions>();
            if (endpoints.Length > 0)
            {
                return endpoints;
            }

            var listen = options.Listen;
            var port = listen?.TlsClientPort > 0 ? listen.TlsClientPort : 6697;
            return new[] { new ListenEndpointOptions { BindIp = listen?.BindIp ?? "0.0.0.0", Port = port, Tls = true } };
        }

        private static string ComputeFingerprint(IrcOptions options)
        {
            var listen = options.Listen;
            if (listen is null || !listen.EnableTls)
            {
                return "disabled";
            }

            var endpoints = GetTlsEndpoints(options)
                .Select(e => $"{(e.BindIp ?? "0.0.0.0").Trim()}:{e.Port}")
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToArray();

            var certKey = (listen.TlsCertificatePath ?? string.Empty).Trim();
            var sniKey = listen.TlsCertificates is null
                ? string.Empty
                : string.Join(",", listen.TlsCertificates.Keys.Where(k => !string.IsNullOrWhiteSpace(k)).Select(k => k.Trim()).OrderBy(k => k, StringComparer.Ordinal));

            return $"{certKey}|{sniKey}|{string.Join("|", endpoints)}";
        }

        private void Wakeup() => _wakeup.TrySetResult(true);

        private Task WaitForWakeupAsync(CancellationToken stoppingToken)
        {
            var tcs = Interlocked.Exchange(ref _wakeup, new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
            return tcs.Task.WaitAsync(stoppingToken);
        }

        private void RequestRestart()
        {
            CancellationTokenSource? cts;
            lock (_restartLock)
            {
                cts = _acceptCts;
            }

            try { cts?.Cancel(); } catch { }
            StopListenersOnly();
        }

        private void StopListenersOnly()
        {
            TcpListener[] listeners;
            lock (_listenerLock)
            {
                listeners = _listeners.ToArray();
                _listeners.Clear();
            }

            foreach (var l in listeners)
            {
                try { l.Stop(); } catch { }
            }
        }

        private async Task AcceptLoopAsync(TcpListener listener, CancellationToken acceptCt, CancellationToken sessionCt)
        {
            while (!acceptCt.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(acceptCt);
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
                    _logger.LogWarning(ex, "TLS Accept failed");
                    continue;
                }

                _ = Task.Run(() => HandleClientAsync(client, sessionCt), sessionCt);
            }
        }

        private async Task AcceptLoopTrackedAsync(TcpListener listener, CancellationToken acceptCt, CancellationToken sessionCt)
        {
            _acceptLoops.AcceptLoopStarted();
            try
            {
                await AcceptLoopAsync(listener, acceptCt, sessionCt);
            }
            finally
            {
                _acceptLoops.AcceptLoopStopped();
            }
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

            TcpListener[] listeners;
            lock (_listenerLock)
            {
                listeners = _listeners.ToArray();
                _listeners.Clear();
            }

            foreach (var l in listeners)
            {
                try { l.Stop(); } catch { }
            }

            TlsClientSession[] sessions;
            lock (_activeLock)
            {
                sessions = _activeSessions.Values.ToArray();
            }

            foreach (var s in sessions)
            {
                try { _ = s.CloseAsync("Server shutting down", cancellationToken); } catch { }
            }

            try
            {
                if (_sniCerts is not null)
                {
                    foreach (var c in _sniCerts.Values)
                    {
                        try { c.Dispose(); } catch { }
                    }

                    _sniCerts = null;
                }
            }
            catch { }
            return base.StopAsync(cancellationToken);
        }

        private static IPAddress GetRemoteIp(TcpClient client)
        {
            if (client.Client.RemoteEndPoint is IPEndPoint ep)
            {
                return ep.Address;
            }

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
                catch { }

                try { client.Close(); } catch { }
                return;
            }

            var localEndPoint = client.Client.LocalEndPoint ?? new IPEndPoint(IPAddress.None, 0);

            if (!_guard.TryAcceptNewConnection(remoteIp, secure: true, out var rejectReason))
            {
                if (_guardLogLimiter.ShouldLog(remoteIp))
                {
                    _logger.LogWarning("TLS client connection rejected from {RemoteIp}: {Reason}", remoteIp, rejectReason);
                }

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
                catch { }

                try { client.Close(); } catch { }
                return;
            }

            var localIpEndPoint = localEndPoint as IPEndPoint ?? new IPEndPoint(IPAddress.None, 0);
            var precheck = await _precheck.CheckAsync(new ConnectionPrecheckContext(remoteIp, localIpEndPoint, Secure: true), ct);
            if (!precheck.Allowed)
            {
                var msg = precheck.RejectMessage ?? "Connection blocked";

                try
                {
                    using var stream = client.GetStream();
                    using var writer = new System.IO.StreamWriter(stream, new UTF8Encoding(false))
                    {
                        NewLine = "\r\n",
                        AutoFlush = true
                    };

                    await writer.WriteLineAsync($"ERROR :{msg}");
                }
                catch { }

                try { client.Close(); } catch { }

                try { _guard.ReleaseActive(remoteIp); } catch { }
                try { _guard.ReleaseUnregistered(remoteIp); } catch { }

                return;
            }

            var connectionId = Guid.NewGuid().ToString("N");

            EndPoint remoteEndPoint = client.Client.RemoteEndPoint ?? new IPEndPoint(IPAddress.None, 0);

            using var net = client.GetStream();

            RemoteCertificateValidationCallback? remoteCertValidation = null;
            LocalCertificateSelectionCallback? certSelector = null;

            if (_options.CurrentValue.Listen?.RequestClientCertificate == true)
            {
                remoteCertValidation = static (_, _, _, _) => true;
            }

            if (_sniCerts is not null && _sniCerts.Count > 0)
            {
                certSelector = (sender, name, localCertificates, remoteCertificate, acceptableIssuers) =>
                {
                    var fallback = _cert ?? throw new InvalidOperationException("TLS certificate not loaded");

                    if (string.IsNullOrWhiteSpace(name))
                        return fallback;

                    return _sniCerts.TryGetValue(name, out var chosen) ? chosen : fallback;
                };
            }

            using var ssl = certSelector is null
                ? new SslStream(net, leaveInnerStreamOpen: false, remoteCertValidation)
                : new SslStream(net, leaveInnerStreamOpen: false, remoteCertValidation, certSelector);

            try
            {
                if (!_guard.TryStartTlsHandshake(remoteIp, out var handshakeReject))
                {
                    if (_guardLogLimiter.ShouldLog(remoteIp))
                    {
                        _logger.LogWarning("TLS handshake rejected from {RemoteIp}: {Reason}", remoteIp, handshakeReject);
                    }

                    try { client.Close(); } catch { }

                    try { _guard.ReleaseActive(remoteIp); } catch { }
                    try { _guard.ReleaseUnregistered(remoteIp); } catch { }

                    return;
                }

                var authOptions = new SslServerAuthenticationOptions
                {
                    ServerCertificate = _cert,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                    AllowRenegotiation = false,
                    ClientCertificateRequired = _options.CurrentValue.Listen?.RequestClientCertificate == true,
                };

                using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                handshakeCts.CancelAfter(TimeSpan.FromSeconds(_guard.GetTlsHandshakeTimeoutSeconds()));

                await ssl.AuthenticateAsServerAsync(authOptions, handshakeCts.Token);
            }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
            {
                if (_handshakeLogLimiter.ShouldLog(remoteIp))
                {
                    _logger.LogInformation(ex, "TLS handshake timed out from {Remote}", remoteEndPoint);
                }

                try { client.Close(); } catch { }

                try { _guard.ReleaseActive(remoteIp); } catch { }
                try { _guard.ReleaseUnregistered(remoteIp); } catch { }

                return;
            }
            catch (Exception ex)
            {
                if (_handshakeLogLimiter.ShouldLog(remoteIp))
                {
                    _logger.LogInformation(ex, "TLS handshake failed from {Remote}", remoteEndPoint);
                }
                try { client.Close(); } catch { }

                try { _guard.ReleaseActive(remoteIp); } catch { }
                try { _guard.ReleaseUnregistered(remoteIp); } catch { }

                return;
            }

            var queueCap = _options.CurrentValue.Transport?.Queues?.ClientSendQueueCapacity ?? 256;
            var maxLineChars = _options.CurrentValue.Transport?.ClientMaxLineChars ?? IRCd.Transport.Tcp.LineProtocol.MaxLineChars;
            var sessionLogger = _loggerFactory.CreateLogger<TlsClientSession>();
            var session = new TlsClientSession(connectionId, remoteEndPoint, localEndPoint, ssl, maxLineChars, queueCap, _metrics, sessionLogger);

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
                IsSecureConnection = true,
                RemoteIp = remoteIp.ToString(),
            });

            _sessions.Add(session);

            _metrics.ConnectionAccepted(secure: true);
            metricsCounted = true;

            _logger.LogInformation("TLS client connected {ConnId} from {Remote}", connectionId, session.RemoteEndPoint);

            var writerTask = Task.Run(() => session.RunWriterLoopAsync(ct), ct);

            var serverName = _options.CurrentValue.ServerInfo?.Name ?? "server";
            await session.SendAsync($":{serverName} NOTICE * :Welcome (TLS). Use NICK/USER.", ct);

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

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    if (_guard.Enabled && !session.IsRegistered && DateTimeOffset.UtcNow > registrationDeadlineUtc)
                    {
                        try { await session.SendAsync("ERROR :Registration timeout", ct); } catch { }
                        try { await session.CloseAsync("Registration timeout", ct); } catch { }
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
                    catch (ObjectDisposedException) { break; }
                    catch (InvalidOperationException) { break; }

                    pendingRead = null;

                    if (line is null)
                    {
                        break;
                    }

                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    if (!_floodGate.Allow(connectionId))
                    {
                        _metrics.FloodKick();
                        try { await session.SendAsync("ERROR :Excess Flood", ct); } catch { }
                        try { await session.CloseAsync("Excess Flood", ct); } catch { }
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
                        SafeIrcLogger.LogBadInboundLine(_logger, _logRedactor, connectionId, line, ex);
                        continue;
                    }

                    try
                    {
                        await _dispatcher.DispatchAsync(session, msg, _state, ct);
                    }
                    catch (Exception ex)
                    {
                        SafeIrcLogger.LogDispatchException(_logger, connectionId, msg.Command, ex);
                        break;
                    }

                    if (!markedRegistered && session.IsRegistered)
                    {
                        markedRegistered = true;

                        _guard.MarkRegistered(remoteIp);
                        unregisteredReleased = true;
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                SafeIrcLogger.LogClientLoopError(_logger, connectionId, ex, tls: true);
            }
            finally
            {
                try
                {
                    if (session.IsRegistered && !string.IsNullOrWhiteSpace(session.Nick))
                    {
                        var nick = session.Nick!;
                        var user = session.UserName ?? "u";
                        var displayedHost = host;

                        var quitLine = $":{nick}!{user}@{displayedHost} QUIT :Client disconnected";

                        var recipients = new HashSet<string>(StringComparer.Ordinal);

                        foreach (var chName in _state.GetUserChannels(connectionId))
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
                catch { }

                try { _sessions.Remove(connectionId); } catch { }
                try { _state.RemoveUser(connectionId); } catch { }
                try { _rateLimit.ClearConnection(connectionId); } catch { }
                try { _floodGate.Remove(connectionId); } catch { }

                lock (_activeLock)
                {
                    _activeSessions.Remove(connectionId);
                }

                if (!session.IsRegistered && !unregisteredReleased)
                {
                    try { _guard.ReleaseUnregistered(remoteIp); } catch { }
                }

                try { _guard.ReleaseActive(remoteIp); } catch { }

                try { await session.CloseAsync("Client disconnected", CancellationToken.None); } catch { }

                if (metricsCounted)
                {
                    _metrics.ConnectionClosed(secure: true);
                }

                _logger.LogInformation("TLS client disconnected {ConnId}", connectionId);
            }

            try { await writerTask; } catch { }
        }
    }
}
