namespace IRCd.Transport.Tcp
{
    using IRCd.Core.Abstractions;

    using System.Net;
    using System.Net.Sockets;
    using System.Text;
    using System.Threading.Channels;

    public sealed class TcpClientSession : IClientSession
    {
        private readonly TcpClient _client;
        private readonly NetworkStream _stream;
        private readonly StreamReader _reader;
        private readonly StreamWriter _writer;

        private readonly Channel<string> _outgoing;

        private int _closed;

        public TcpClientSession(string connectionId, TcpClient client)
        {
            ConnectionId = connectionId;

            _client = client;
            _client.NoDelay = true;

            _stream = _client.GetStream();

            _reader = new StreamReader(_stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
            _writer = new StreamWriter(_stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), bufferSize: 4096, leaveOpen: true)
            {
                NewLine = "\r\n",
                AutoFlush = true
            };

            RemoteEndPoint = client.Client.RemoteEndPoint ?? new IPEndPoint(IPAddress.None, 0);

            _outgoing = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = true
            });
        }

        public string ConnectionId { get; }
        public EndPoint RemoteEndPoint { get; }

        public string? Nick { get; set; }
        public string? UserName { get; set; }
        public bool IsRegistered { get; set; }

        /// <summary>
        /// Enqueue a line to be sent to the client (CRLF will be added by writer).
        /// If the session is closed, this becomes a no-op.
        /// </summary>
        public ValueTask SendAsync(string line, CancellationToken ct = default)
        {
            if (Volatile.Read(ref _closed) == 1)
                return ValueTask.CompletedTask;

            _outgoing.Writer.TryWrite(line);
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

                const int maxChars = 510;
                if (line.Length > maxChars)
                {
                    line = line[..maxChars];
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
                // not throw out of background loop
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