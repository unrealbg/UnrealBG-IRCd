namespace IRCd.Transport.Tls
{
    using System;
    using System.Net;
    using System.Net.Security;
    using System.Security.Cryptography;
    using System.Security.Cryptography.X509Certificates;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Core.Abstractions;
    using IRCd.Core.Services;
    using IRCd.Transport.Tcp;
    using Microsoft.Extensions.Logging;

    public sealed class TlsClientSession : IClientSession
    {
        private readonly SslStream _ssl;
        private readonly StreamReader _reader;
        private readonly StreamWriter _writer;

        private readonly OutboundMessageQueue _outgoing;
        private readonly IMetrics? _metrics;
        private readonly ILogger<TlsClientSession>? _logger;

        private readonly object _userModesLock = new();
        private string _userModes = "";

        private readonly int _maxLineChars;

        private int _closed;
        private string? _lastPingToken;

        private int _certCaptured;
        private string? _clientCertSubject;
        private string? _clientCertFpSha256;

        public TlsClientSession(string connectionId, EndPoint remoteEndPoint, EndPoint localEndPoint, SslStream ssl, int maxLineChars, int outgoingQueueCapacity, IMetrics? metrics = null, ILogger<TlsClientSession>? logger = null)
        {
            ConnectionId = connectionId;
            RemoteEndPoint = remoteEndPoint;
            LocalEndPoint = localEndPoint;
            _ssl = ssl;
            _metrics = metrics;
            _logger = logger;

            _reader = LineProtocol.CreateReader(_ssl);
            _writer = LineProtocol.CreateWriter(_ssl);

            _maxLineChars = maxLineChars > 0 ? maxLineChars : LineProtocol.MaxLineChars;

            _outgoing = new OutboundMessageQueue(outgoingQueueCapacity, _metrics);

            LastActivityUtc = DateTime.UtcNow;
        }

        public string ConnectionId { get; }

        public EndPoint RemoteEndPoint { get; }

        public EndPoint LocalEndPoint { get; }

        public bool IsSecureConnection => true;

        public string? ClientCertificateSubject
        {
            get
            {
                CaptureClientCertIfNeeded();
                return _clientCertSubject;
            }
        }

        public string? ClientCertificateFingerprintSha256
        {
            get
            {
                CaptureClientCertIfNeeded();
                return _clientCertFpSha256;
            }
        }

        private void CaptureClientCertIfNeeded()
        {
            if (Interlocked.CompareExchange(ref _certCaptured, 1, 0) != 0)
                return;

            try
            {
                var cert = _ssl.RemoteCertificate;
                if (cert is null)
                    return;

                using var cert2 = cert as X509Certificate2 ?? new X509Certificate2(cert);
                _clientCertSubject = cert2.Subject;
                _clientCertFpSha256 = Convert.ToHexString(SHA256.HashData(cert2.RawData));
            }
            catch
            {
                // Ignore certificate extraction errors; treat as no cert.
            }
        }

        public ISet<string> EnabledCapabilities { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public string? Nick { get; set; }

        public string? UserName { get; set; }

        public bool PassAccepted { get; set; }

        public bool IsRegistered { get; set; }

        public DateTime LastActivityUtc { get; private set; }

        public DateTime LastPingUtc { get; private set; }

        public bool AwaitingPong { get; private set; }

        public string? LastPingToken => _lastPingToken;

        public string UserModes
        {
            get { lock (_userModesLock) return _userModes; }
        }

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
                        return false;

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
            {
                return;
            }

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

        public ValueTask SendAsync(string line, CancellationToken ct = default)
        {
            if (Volatile.Read(ref _closed) == 1)
            {
                return ValueTask.CompletedTask;
            }

            if (!_outgoing.TryEnqueue(line))
            {
                _metrics?.OutboundQueueOverflowDisconnect();
                _logger?.LogWarning("Send queue overflow for {ConnId}; disconnecting", ConnectionId);
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
            }
            catch (ObjectDisposedException)
            {
            }
            catch (IOException)
            {
            }
            catch
            {
            }
            finally
            {
                _outgoing.ResetDepth();
            }
        }

        public async ValueTask CloseAsync(string reason, CancellationToken ct = default)
        {
            if (Interlocked.Exchange(ref _closed, 1) == 1)
            {
                return;
            }

            try { _outgoing.Complete(); } catch { }
            try { _ssl.Close(); } catch { }
            try { _ssl.Dispose(); } catch { }

            await Task.CompletedTask;
        }
    }
}
