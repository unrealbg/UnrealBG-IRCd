namespace IRCd.Core.Services
{
    using System;
    using System.Collections.Concurrent;
    using System.Net;
    using System.Net.Sockets;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using IRCd.Core.Abstractions;
    using IRCd.Shared.Options;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;

    public sealed class ConnectionAuthService
    {
        private readonly ILogger<ConnectionAuthService> _logger;
        private readonly IOptions<IrcOptions> _options;

        private readonly ConcurrentDictionary<string, Task> _inflight = new(StringComparer.Ordinal);

        public ConnectionAuthService(ILogger<ConnectionAuthService> logger, IOptions<IrcOptions> options)
        {
            _logger = logger;
            _options = options;
        }

        public void StartAuthChecks(IClientSession session, IPAddress clientIp, int remotePort, int localPort, CancellationToken ct)
        {
            var auth = _options.Value.Auth;
            if (auth is null || !auth.Enabled)
                return;

            if (session is null || string.IsNullOrWhiteSpace(session.ConnectionId))
                return;

            var connectionId = session.ConnectionId;

            if (_inflight.ContainsKey(connectionId))
                return;

            var task = Task.Run(async () =>
            {
                try
                {
                    await PerformAuthChecksAsync(session, clientIp, remotePort, localPort, ct);
                }
                catch (OperationCanceledException)
                {
                    // ignore
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error performing AUTH checks for {ConnId}", connectionId);
                }
                finally
                {
                    _inflight.TryRemove(connectionId, out _);
                }
            }, ct);

            if (!_inflight.TryAdd(connectionId, task))
            {
                // If another starter won the race, let this task clean itself up when done.
            }
        }

        public async ValueTask AwaitAuthChecksAsync(string connectionId, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(connectionId))
                return;

            if (!_inflight.TryGetValue(connectionId, out var task) || task is null)
                return;

            try
            {
                await task;
            }
            catch (OperationCanceledException)
            {
                // ignore
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error awaiting AUTH checks for {ConnId}", connectionId);
            }
        }

        public async ValueTask PerformAuthChecksAsync(IClientSession session, IPAddress clientIp, int remotePort, int localPort, CancellationToken ct)
        {
            var auth = _options.Value.Auth;
            if (auth is null || !auth.Enabled)
                return;

            await session.SendAsync($"NOTICE AUTH :*** Checking your IP address...", ct);

            if (auth.ReverseDnsEnabled)
            {
                await session.SendAsync($"NOTICE AUTH :*** Looking up your hostname...", ct);
                var hostname = await ReverseDnsLookupAsync(clientIp, TimeSpan.FromSeconds(Math.Max(1, auth.ReverseDnsTimeoutSeconds)), ct);

                if (!string.IsNullOrWhiteSpace(hostname) && hostname != clientIp.ToString())
                {
                    await session.SendAsync($"NOTICE AUTH :*** Found your hostname", ct);
                    _logger.LogInformation("Resolved hostname for {IP}: {Hostname}", clientIp, hostname);
                }
                else
                {
                    await session.SendAsync($"NOTICE AUTH :*** Couldn't look up your hostname", ct);
                }
            }

            if (auth.IdentEnabled)
            {
                await session.SendAsync($"NOTICE AUTH :*** Checking Ident", ct);
                var identResponse = await CheckIdentAsync(
                    clientIp,
                    remotePort,
                    localPort,
                    TimeSpan.FromSeconds(Math.Max(1, auth.IdentTimeoutSeconds)),
                    ct);

                if (!string.IsNullOrWhiteSpace(identResponse))
                {
                    await session.SendAsync($"NOTICE AUTH :*** Got Ident response", ct);
                    _logger.LogInformation("Ident response for {IP}: {Ident}", clientIp, identResponse);
                }
                else
                {
                    await session.SendAsync($"NOTICE AUTH :*** No Ident response", ct);
                }
            }
        }

        private async Task<string?> ReverseDnsLookupAsync(IPAddress ip, TimeSpan timeout, CancellationToken ct)
        {
            try
            {
                var hostEntryTask = Dns.GetHostEntryAsync(ip);
                var completed = await Task.WhenAny(hostEntryTask, Task.Delay(timeout, ct));
                if (completed != hostEntryTask)
                    return null;

                var hostEntry = await hostEntryTask;
                return hostEntry.HostName;
            }
            catch (SocketException)
            {
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error performing reverse DNS lookup for {IP}", ip);
                return null;
            }
        }

        private async Task<string?> CheckIdentAsync(IPAddress ip, int remotePort, int localPort, TimeSpan timeout, CancellationToken ct)
        {
            if (remotePort == 0 || localPort == 0)
                return null;

            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(timeout);

                using var client = new TcpClient();
                await client.ConnectAsync(ip.ToString(), 113, cts.Token);

                using var stream = client.GetStream();
                
                var query = $"{remotePort} , {localPort}\r\n";
                var queryBytes = Encoding.ASCII.GetBytes(query);
                await stream.WriteAsync(queryBytes, cts.Token);

                var buffer = new byte[512];
                var bytesRead = await stream.ReadAsync(buffer, cts.Token);
                
                if (bytesRead > 0)
                {
                    var response = Encoding.ASCII.GetString(buffer, 0, bytesRead).Trim();
                    
                    var parts = response.Split(':');
                    if (parts.Length >= 4 && parts[1].Trim().Equals("USERID", StringComparison.OrdinalIgnoreCase))
                    {
                        return parts[3].Trim();
                    }
                }

                return null;
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (SocketException)
            {
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error checking ident for {IP}:{Port}", ip, remotePort);
                return null;
            }
        }
    }
}
