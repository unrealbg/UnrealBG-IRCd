namespace IRCd.Core.Services
{
    using System;
    using System.Linq;
    using System.Net;
    using System.Net.Sockets;
    using System.IO;
    using System.Text;
    using System.Threading;
    using System.Threading.Channels;
    using System.Threading.Tasks;

    using IRCd.Core.Abstractions;
    using IRCd.Shared.Options;

    using Microsoft.Extensions.Hosting;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;

    public sealed class OutboundLinkHostedService : BackgroundService
    {
        private readonly ILogger<OutboundLinkHostedService> _logger;
        private readonly IOptionsMonitor<IrcOptions> _options;
        private readonly ServerLinkService _linkService;

        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _failures = new(StringComparer.OrdinalIgnoreCase);

        public OutboundLinkHostedService(
            ILogger<OutboundLinkHostedService> logger,
            IOptionsMonitor<IrcOptions> options,
            ServerLinkService linkService)
        {
            _logger = logger;
            _options = options;
            _linkService = linkService;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var links = _options.CurrentValue.Links
                    .Where(l => l.Outbound)
                    .ToArray();

                foreach (var link in links)
                {
                    if (stoppingToken.IsCancellationRequested)
                        break;

                    var delay = GetBackoffDelaySeconds(link);
                    if (delay > 0)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(delay), stoppingToken);
                    }

                    await TryConnectOnceAsync(link, stoppingToken);
                }

                var scanSeconds = _options.CurrentValue.Transport?.S2S?.OutboundScanIntervalSeconds ?? 10;
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, scanSeconds)), stoppingToken);
            }
        }

        private int GetBackoffDelaySeconds(LinkOptions link)
        {
            if (!_failures.TryGetValue(link.Name, out var n) || n <= 0)
                return 0;

            var s2s = _options.CurrentValue.Transport?.S2S;
            var maxSeconds = s2s?.OutboundBackoffMaxSeconds ?? 30;
            var maxExponent = s2s?.OutboundBackoffMaxExponent ?? 4;

            var s = Math.Min(maxSeconds, 1 << Math.Min(maxExponent, n - 1));
            return s;
        }

        private async Task TryConnectOnceAsync(LinkOptions link, CancellationToken ct)
        {
            try
            {
                using var client = new TcpClient();

                _logger.LogInformation("S2S outbound connecting to {Name} {Host}:{Port}", link.Name, link.Host, link.Port);
                await client.ConnectAsync(link.Host, link.Port, ct);

                var connId = Guid.NewGuid().ToString("N");
                var cap = _options.CurrentValue.Transport?.Queues?.ServerLinkSendQueueCapacity ?? 2048;
                var session = new OutboundTcpServerLinkSession(connId, client, cap)
                {
                    UserSyncEnabled = link.UserSync
                };

                await _linkService.HandleOutboundLinkAsync(session, link, ct);

                _failures[link.Name] = 0;
            }
            catch (OperationCanceledException)
            {
                // shutdown
            }
            catch (Exception ex)
            {
                var failureLimit = _options.CurrentValue.Transport?.S2S?.OutboundFailureLimit ?? 10;
                _failures.AddOrUpdate(link.Name, 1, (_, v) => Math.Min(v + 1, failureLimit));
                _logger.LogDebug(ex, "S2S outbound connect failed to {Name} {Host}:{Port}", link.Name, link.Host, link.Port);
            }
        }
        private sealed class OutboundTcpServerLinkSession : IServerLinkSession
        {
            private readonly TcpClient _client;
            private readonly NetworkStream _stream;
            private readonly StreamReader _reader;
            private readonly StreamWriter _writer;
            private readonly Channel<string> _outgoing;
            private int _closed;

            public OutboundTcpServerLinkSession(string connectionId, TcpClient client, int outgoingQueueCapacity)
            {
                ConnectionId = connectionId;
                _client = client;
                _client.NoDelay = true;
                _stream = _client.GetStream();
                _reader = new StreamReader(_stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
                _writer = new StreamWriter(_stream, new UTF8Encoding(false), bufferSize: 4096, leaveOpen: true)
                {
                    NewLine = "\r\n",
                    AutoFlush = true
                };

                RemoteEndPoint = client.Client.RemoteEndPoint ?? new IPEndPoint(IPAddress.None, 0);

                var cap = outgoingQueueCapacity > 0 ? outgoingQueueCapacity : 2048;
                _outgoing = Channel.CreateBounded<string>(new BoundedChannelOptions(cap)
                {
                    SingleReader = true,
                    SingleWriter = false,
                    AllowSynchronousContinuations = true,
                    FullMode = BoundedChannelFullMode.DropOldest
                });
            }

            public string ConnectionId { get; }

            public EndPoint RemoteEndPoint { get; }

            public bool IsAuthenticated { get; set; }

            public string? RemoteServerName { get; set; }

            public string? RemoteSid { get; set; }

            public string? Pass { get; set; }

            public bool CapabReceived { get; set; }

            public bool UserSyncEnabled { get; set; }

            public ValueTask SendAsync(string line, CancellationToken ct = default)
            {
                if (Volatile.Read(ref _closed) == 1)
                    return ValueTask.CompletedTask;

                if (!_outgoing.Writer.TryWrite(line))
                {
                    _ = CloseAsync("Send queue overflow", ct);
                }
                return ValueTask.CompletedTask;
            }

            public async Task<string?> ReadLineAsync(CancellationToken ct)
            {
                try
                {
                    var line = await _reader.ReadLineAsync();
                    if (line is null)
                        return null;

                    const int maxChars = 510;
                    if (line.Length > maxChars)
                        line = line[..maxChars];

                    return line;
                }
                catch (ObjectDisposedException) { return null; }
                catch (IOException) { return null; }
            }

            public async Task RunWriterLoopAsync(CancellationToken ct)
            {
                try
                {
                    await foreach (var line in _outgoing.Reader.ReadAllAsync(ct))
                    {
                        if (Volatile.Read(ref _closed) == 1)
                            break;

                        await _writer.WriteLineAsync(line);
                    }
                }
                catch (OperationCanceledException) { }
                catch (ObjectDisposedException) { }
                catch (IOException) { }
                catch { }
            }

            public async Task CloseAsync(string reason, CancellationToken ct)
            {
                if (Interlocked.Exchange(ref _closed, 1) == 1)
                    return;

                try { _outgoing.Writer.TryComplete(); } catch { }
                try { _client.Close(); } catch { }
                await Task.CompletedTask;
            }
        }
    }
}
