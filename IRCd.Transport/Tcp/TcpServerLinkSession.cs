namespace IRCd.Transport.Tcp
{
    using System;
    using System.Net;
    using System.Net.Sockets;
    using System.Text;
    using System.Threading;
    using System.Threading.Channels;
    using System.Threading.Tasks;

    using IRCd.Core.Abstractions;

    public sealed class TcpServerLinkSession : IServerLinkSession
    {
        private readonly TcpClient _client;
        private readonly NetworkStream _stream;
        private readonly StreamReader _reader;
        private readonly StreamWriter _writer;
        private readonly Channel<string> _outgoing;

        private int _closed;

        public TcpServerLinkSession(string connectionId, TcpClient client)
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

            _outgoing = Channel.CreateBounded<string>(new BoundedChannelOptions(2048)
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
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (IOException) { }
            catch { }
        }

        public async Task CloseAsync(string reason, CancellationToken ct)
        {
            if (Interlocked.Exchange(ref _closed, 1) == 1)
                return;

            try
            {
                _outgoing.Writer.TryComplete();
            }
            catch { }

            try { _client.Close(); } catch { }
            try { await Task.CompletedTask; } catch { }
        }
    }
}
