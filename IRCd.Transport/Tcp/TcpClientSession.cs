namespace IRCd.Transport.Tcp
{
    using IRCd.Core.Abstractions;

    using System;
    using System.Net;
    using System.Net.Sockets;
    using System.Text;
    using System.Threading;
    using System.Threading.Channels;
    using System.Threading.Tasks;

    public sealed class TcpClientSession : IClientSession
    {
        private readonly TcpClient _client;
        private readonly NetworkStream _stream;
        private readonly StreamReader _reader;
        private readonly StreamWriter _writer;
        private readonly object _userModesLock = new();
        private string _userModes = "";

        private readonly Channel<string> _outgoing;

        private int _closed;

        private string? _lastPingToken;

        public TcpClientSession(string connectionId, TcpClient client)
        {
            ConnectionId = connectionId;

            _client = client;
            _client.NoDelay = true;

            _stream = _client.GetStream();

            _reader = LineProtocol.CreateReader(_stream);
            _writer = LineProtocol.CreateWriter(_stream);

            RemoteEndPoint = client.Client.RemoteEndPoint ?? new IPEndPoint(IPAddress.None, 0);

            _outgoing = Channel.CreateBounded<string>(new BoundedChannelOptions(capacity: 256)
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = true,
                FullMode = BoundedChannelFullMode.DropWrite
            });

            LastActivityUtc = DateTime.UtcNow;
        }

        public string UserModes
        {
            get { lock (_userModesLock) return _userModes; }
        }

        public string ConnectionId { get; }

        public EndPoint RemoteEndPoint { get; }

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

            LastActivityUtc = DateTime.UtcNow;

            if (!_outgoing.Writer.TryWrite(line))
            {
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

        /// <summary>
        /// Dedicated single-reader loop that flushes queued outgoing lines.
        /// Should be run as a background task per session.
        /// </summary>
        public async Task RunWriterLoopAsync(CancellationToken ct)
        {
            try
            {
                await foreach (var line in _outgoing.Reader.ReadAllAsync(ct))
                {
                    if (Volatile.Read(ref _closed) == 1)
                    {
                        break;
                    }

                    await _writer.WriteLineAsync(line);
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
                _outgoing.Writer.TryWrite($":server ERROR :Closing Link: {reason}");
            }
            catch { /* ignore */ }

            try { _outgoing.Writer.TryComplete(); } catch { /* ignore */ }

            try { _client.Close(); } catch { /* ignore */ }

            try { _reader.Dispose(); } catch { /* ignore */ }
            try { _writer.Dispose(); } catch { /* ignore */ }
            try { _stream.Dispose(); } catch { /* ignore */ }

            await ValueTask.CompletedTask;
        }
    }
}
