namespace IRCd.Transport.Tcp
{
    using System;
    using System.Net;
    using System.Net.Sockets;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Core.Abstractions;
    using IRCd.Core.Services;

    using Microsoft.Extensions.Logging;

    public sealed class TcpClientSession : IClientSession
    {
        private readonly TcpClient _client;
        private readonly NetworkStream _stream;
        private readonly StreamReader _reader;
        private readonly StreamWriter _writer;
        private readonly object _userModesLock = new();
        private string _userModes = "";

        private readonly OutboundMessageQueue _outgoing;
        private readonly IMetrics? _metrics;
        private readonly ILogger<TcpClientSession>? _logger;

        private readonly int _maxLineChars;

        private int _closed;

        private string? _lastPingToken;

        public TcpClientSession(
            string connectionId,
            TcpClient client,
            EndPoint localEndPoint,
            bool keepAliveEnabled,
            int keepAliveTimeMs,
            int keepAliveIntervalMs,
            int maxLineChars,
            int outgoingQueueCapacity,
            IMetrics? metrics = null,
            ILogger<TcpClientSession>? logger = null)
        {
            ConnectionId = connectionId;

            _client = client;
            _client.NoDelay = true;

            if (keepAliveEnabled)
            {
                _client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);

                if (OperatingSystem.IsWindows())
                {
                    var timeMs = keepAliveTimeMs > 0 ? keepAliveTimeMs : 120_000;
                    var intervalMs = keepAliveIntervalMs > 0 ? keepAliveIntervalMs : 30_000;

                    var keepaliveValues = new byte[12];
                    BitConverter.GetBytes(1).CopyTo(keepaliveValues, 0);  // on/off
                    BitConverter.GetBytes(timeMs).CopyTo(keepaliveValues, 4);  // time (ms)
                    BitConverter.GetBytes(intervalMs).CopyTo(keepaliveValues, 8);   // interval (ms)
                    _client.Client.IOControl(IOControlCode.KeepAliveValues, keepaliveValues, null);
                }
            }

            _stream = _client.GetStream();

            _reader = LineProtocol.CreateReader(_stream);
            _writer = LineProtocol.CreateWriter(_stream);

            _maxLineChars = maxLineChars > 0 ? maxLineChars : LineProtocol.MaxLineChars;

            RemoteEndPoint = client.Client.RemoteEndPoint ?? new IPEndPoint(IPAddress.None, 0);

            LocalEndPoint = localEndPoint;

            _metrics = metrics;
            _logger = logger;

            _outgoing = new OutboundMessageQueue(outgoingQueueCapacity, _metrics);

            LastActivityUtc = DateTime.UtcNow;
        }

        public string UserModes
        {
            get { lock (_userModesLock) return _userModes; }
        }

        public string ConnectionId { get; }

        public EndPoint RemoteEndPoint { get; }

        public EndPoint LocalEndPoint { get; }

        public bool IsSecureConnection => false;

        public ISet<string> EnabledCapabilities { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public string? Nick { get; set; }

        public string? UserName { get; set; }

        public bool PassAccepted { get; set; }

        public bool IsRegistered { get; set; }

        public DateTime LastActivityUtc { get; private set; }

        public DateTime LastPingUtc { get; private set; }

        public bool AwaitingPong { get; private set; }

        public string? LastPingToken => _lastPingToken;

        public bool TryApplyUserModes(string modeString, out string normalizedModes)
        {
            normalizedModes = "";

            if (string.IsNullOrWhiteSpace(modeString))
                return true;

            var adding = true;

            lock (_userModesLock)
            {
                var set = new HashSet<char>(_userModes);

                foreach (var ch in modeString)
                {
                    if (ch == '+') { adding = true; continue; }
                    if (ch == '-') { adding = false; continue; }

                    if (ch is not ('i' or 'w' or 's'))
                    {
                        return false;
                    }

                    if (adding) set.Add(ch);
                    else set.Remove(ch);
                }

                var arr = set.ToArray();
                Array.Sort(arr);

                _userModes = new string(arr);
                normalizedModes = "+" + _userModes;
                return true;
            }
        }

        public void OnInboundLine()
        {
            LastActivityUtc = DateTime.UtcNow;
        }

        public void OnPingSent(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
                return;

            _lastPingToken = token;
            LastPingUtc = DateTime.UtcNow;
            AwaitingPong = true;
        }

        public void OnPongReceived(string? token)
        {
            AwaitingPong = false;
            _lastPingToken = null;
            LastActivityUtc = DateTime.UtcNow;
        }

        /// <summary>
        /// Enqueue a line to be sent to the client (CRLF will be added by writer).
        /// If the session is closed, this becomes a no-op.
        /// </summary>
        public ValueTask SendAsync(string line, CancellationToken ct = default)
        {
            if (Volatile.Read(ref _closed) == 1)
                return ValueTask.CompletedTask;

            if (!_outgoing.TryEnqueue(line))
            {
                _metrics?.OutboundQueueOverflowDisconnect();
                _logger?.LogWarning("Send queue overflow for {ConnId}; disconnecting", ConnectionId);
                _ = CloseAsync("Send queue overflow", ct);
            }
            return ValueTask.CompletedTask;
        }

        /// <summary>
        /// Reads a single IRC line (without CRLF). Returns null on disconnect or when the stream is disposed.
        /// </summary>
        public async Task<string?> ReadLineAsync(CancellationToken ct)
        {
            try
            {
                var line = await _reader.ReadLineAsync();
                if (line is null)
                {
                    return null;
                }

                if (line.Length > _maxLineChars)
                {
                    await CloseAsync("Input line too long", ct);
                    return null;
                }

                return line;
            }
            catch (ObjectDisposedException)
            {
                return null;
            }
            catch (IOException)
            {
                return null;
            }
        }

        /// <summary>
        /// Dedicated single-reader loop that flushes queued outgoing lines.
        /// Should be run as a background task per session.
        /// </summary>
        public async Task RunWriterLoopAsync(CancellationToken ct)
        {
            try
            {
                await foreach (var line in _outgoing.ReadAllAsync(ct))
                {
                    if (Volatile.Read(ref _closed) == 1)
                    {
                        break;
                    }

                    await _writer.WriteLineAsync(line);
                    _outgoing.MarkDequeued();
                }
            }
            catch (OperationCanceledException)
            {
                // normal during shutdown
            }
            catch (ObjectDisposedException)
            {
                // normal during close/dispose race
            }
            catch (IOException)
            {
                // normal on disconnect
            }
            catch
            {
            }
            finally
            {
                _outgoing.ResetDepth();
            }
        }

        /// <summary>
        /// Idempotent close. Safe to call multiple times.
        /// </summary>
        public async ValueTask CloseAsync(string reason, CancellationToken ct = default)
        {
            if (Interlocked.Exchange(ref _closed, 1) == 1)
                return;

            try
            {
                _outgoing.TryEnqueue($":server ERROR :Closing Link: {reason}");
            }
            catch { /* ignore */ }

            try { _outgoing.Complete(); } catch { /* ignore */ }

            try { _client.Close(); } catch { /* ignore */ }

            try { _reader.Dispose(); } catch { /* ignore */ }
            try { _writer.Dispose(); } catch { /* ignore */ }
            try { _stream.Dispose(); } catch { /* ignore */ }

            await ValueTask.CompletedTask;
        }
    }
}
