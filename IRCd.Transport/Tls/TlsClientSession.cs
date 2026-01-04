namespace IRCd.Transport.Tls
{
    using System;
    using System.Net;
    using System.Net.Security;
    using System.Text;
    using System.Threading;
    using System.Threading.Channels;
    using System.Threading.Tasks;

    using IRCd.Core.Abstractions;
    using IRCd.Transport.Tcp;

    public sealed class TlsClientSession : IClientSession
    {
        private readonly SslStream _ssl;
        private readonly StreamReader _reader;
        private readonly StreamWriter _writer;

        private readonly Channel<string> _outgoing;

        private readonly object _userModesLock = new();
        private string _userModes = "";

        private int _closed;
        private string? _lastPingToken;

        public TlsClientSession(string connectionId, EndPoint remoteEndPoint, SslStream ssl)
        {
            ConnectionId = connectionId;
            RemoteEndPoint = remoteEndPoint;
            _ssl = ssl;

            _reader = LineProtocol.CreateReader(_ssl);
            _writer = LineProtocol.CreateWriter(_ssl);

            _outgoing = Channel.CreateBounded<string>(new BoundedChannelOptions(capacity: 256)
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = true,
                FullMode = BoundedChannelFullMode.DropWrite
            });

            LastActivityUtc = DateTime.UtcNow;
        }

        public string ConnectionId { get; }

        public EndPoint RemoteEndPoint { get; }

        public bool IsSecureConnection => true;

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

        public ValueTask SendAsync(string line, CancellationToken ct = default)
        {
            if (Volatile.Read(ref _closed) == 1)
                return ValueTask.CompletedTask;

            LastActivityUtc = DateTime.UtcNow;

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

                if (line.Length > LineProtocol.MaxLineChars)
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
                await foreach (var line in _outgoing.Reader.ReadAllAsync(ct))
                {
                    if (Volatile.Read(ref _closed) == 1)
                        break;

                    await _writer.WriteLineAsync(line);
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
        }

        public async ValueTask CloseAsync(string reason, CancellationToken ct = default)
        {
            if (Interlocked.Exchange(ref _closed, 1) == 1)
                return;

            try { _outgoing.Writer.TryComplete(); } catch { }

            try { _ssl.Close(); } catch { }
            try { _ssl.Dispose(); } catch { }

            await Task.CompletedTask;
        }
    }
}
