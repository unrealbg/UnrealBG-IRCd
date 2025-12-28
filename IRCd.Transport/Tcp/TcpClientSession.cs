namespace IRCd.Transport.Tcp
{
    using IRCd.Core.Abstractions;

    using System.Net;
    using System.Net.Sockets;
    using System.Threading.Channels;

    public sealed class TcpClientSession : IClientSession
    {
        private readonly TcpClient _client;
        private readonly StreamReader _reader;
        private readonly StreamWriter _writer;

        private readonly Channel<string> _outgoing = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        public TcpClientSession(string connectionId, TcpClient client)
        {
            ConnectionId = connectionId;
            _client = client;

            _client.NoDelay = true;

            var stream = _client.GetStream();
            _reader = LineProtocol.CreateReader(stream);
            _writer = LineProtocol.CreateWriter(stream);

            RemoteEndPoint = client.Client.RemoteEndPoint ?? new IPEndPoint(IPAddress.None, 0);
        }

        public string ConnectionId { get; }

        public EndPoint RemoteEndPoint { get; }

        public string? Nick { get; set; }

        public string? UserName { get; set; }

        public bool IsRegistered { get; set; }

        public async ValueTask SendAsync(string line, CancellationToken ct = default)
        {
            await _outgoing.Writer.WriteAsync(line, ct);
        }

        public async ValueTask CloseAsync(string reason, CancellationToken ct = default)
        {
            try
            {
                await SendAsync($":server ERROR :Closing Link: {reason}", ct);
            }
            catch { /* ignore */ }

            try { _client.Close(); } catch { /* ignore */ }
        }

        public async Task RunWriterLoopAsync(CancellationToken ct)
        {
            await foreach (var line in _outgoing.Reader.ReadAllAsync(ct))
            {
                await _writer.WriteLineAsync(line);
            }
        }

        public async Task<string?> ReadLineAsync(CancellationToken ct)
        {
            var line = await _reader.ReadLineAsync();
            if (line is null) return null;

            if (line.Length > LineProtocol.MaxLineChars)
                return line[..LineProtocol.MaxLineChars];

            return line;
        }
    }
}
