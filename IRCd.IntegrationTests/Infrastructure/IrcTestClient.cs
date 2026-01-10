namespace IRCd.IntegrationTests.Infrastructure
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net.Sockets;
    using System.Text;
    using System.Threading;
    using System.Threading.Channels;
    using System.Threading.Tasks;

    using IRCd.Core.Protocol;

    public sealed class IrcTestClient : IAsyncDisposable
    {
        private readonly TcpClient _tcp;
        private readonly NetworkStream _stream;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _readerLoop;

        private readonly ConcurrentQueue<IrcMessage> _messages = new();
        private readonly SemaphoreSlim _messageSignal = new(0);

        private readonly ConcurrentQueue<string> _rawLines = new();
        private const int MaxRawLines = 200;
        private readonly Channel<string> _rawChannel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = true,
            AllowSynchronousContinuations = true
        });

        public IrcTestClient(string host, int port)
        {
            _tcp = new TcpClient();
            _tcp.NoDelay = true;
            _tcp.Connect(host, port);

            _stream = _tcp.GetStream();
            _readerLoop = Task.Run(ReadLoopAsync);
        }

        public bool IsConnected => _tcp.Connected;

        public string? Nick { get; private set; }

        public async Task RegisterAsync(string nick, string user, CancellationToken ct)
        {
            Nick = nick;
            await SendAsync($"NICK {nick}", ct);
            await SendAsync($"USER {user} 0 * :{user}", ct);

            _ = await WaitForAsync(m => m.Command == "001", TimeSpan.FromSeconds(10), ct);
        }

        public Task SendAsync(string line, CancellationToken ct)
        {
            var bytes = Encoding.UTF8.GetBytes(line + "\r\n");
            return _stream.WriteAsync(bytes, ct).AsTask();
        }

        public async Task JoinAsync(string channel, CancellationToken ct)
        {
            await SendAsync($"JOIN {channel}", ct);
            await WaitForAsync(m => m.Command == "JOIN" && string.Equals(m.Trailing, channel, StringComparison.OrdinalIgnoreCase),
                TimeSpan.FromSeconds(5),
                ct);
        }

        public async Task<HashSet<string>> GetNamesAsync(string channel, CancellationToken ct)
        {
            DrainRawChannel();
            await SendAsync($"NAMES {channel}", ct);

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));

            while (!timeoutCts.IsCancellationRequested)
            {
                string line;
                try
                {
                    line = await _rawChannel.Reader.ReadAsync(timeoutCts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (!(line.Contains(" 353 ", StringComparison.Ordinal) || line.Contains(" 366 ", StringComparison.Ordinal)))
                {
                    continue;
                }

                if (!TryParseRaw(line, out var cmd, out var args, out var trailing))
                {
                    continue;
                }

                if (cmd == "353")
                {
                    if (args.Length >= 3 && string.Equals(args[2], channel, StringComparison.OrdinalIgnoreCase))
                    {
                        if (!string.IsNullOrWhiteSpace(trailing))
                        {
                            foreach (var raw in trailing.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                            {
                                var n = raw.TrimStart('@', '+', '%', '~', '&');
                                if (n.Length > 0)
                                {
                                    names.Add(n);
                                }
                            }
                        }
                    }

                    continue;
                }

                if (cmd == "366")
                {
                    if (args.Length >= 2 && string.Equals(args[1], channel, StringComparison.OrdinalIgnoreCase))
                    {
                        return names;
                    }
                }
            }

            throw new TimeoutException($"Timed out waiting for /NAMES reply for {channel}. Recent raw lines:\n{string.Join("\n", SnapshotRawLines(30))}");
        }

        public async Task OperAsync(string operName, string operPassword, CancellationToken ct)
        {
            await SendAsync($"OPER {operName} {operPassword}", ct);
            _ = await WaitForAsync(m => m.Command == "381", TimeSpan.FromSeconds(5), ct);
        }

        public async Task SquitAsync(string serverName, string reason, CancellationToken ct)
        {
            await SendAsync($"SQUIT {serverName} :{reason}", ct);
        }

        public async Task<IrcMessage> WaitForAsync(Func<IrcMessage, bool> predicate, TimeSpan timeout, CancellationToken ct)
        {
            var deadline = DateTimeOffset.UtcNow + timeout;

            while (DateTimeOffset.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();

                while (_messages.TryDequeue(out var msg))
                {
                    if (predicate(msg))
                    {
                        return msg;
                    }
                }

                var remaining = deadline - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    break;
                }

                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                linked.CancelAfter(remaining);

                try
                {
                    await _messageSignal.WaitAsync(linked.Token);
                }
                catch
                {
                    // timeout or cancellation
                }
            }

            var recent = string.Join("\n", SnapshotRawLines(30));
            throw new TimeoutException($"Timed out waiting for expected IRC message. Recent raw lines:\n{recent}");
        }

        public string[] SnapshotRawLines(int max)
        {
            if (max <= 0)
            {
                return Array.Empty<string>();
            }

            var all = _rawLines.ToArray();
            if (all.Length <= max)
            {
                return all;
            }

            return all[^max..];
        }

        public async ValueTask DisposeAsync()
        {
            try { _cts.Cancel(); } catch { }

            try { _tcp.Close(); } catch { }

            try { await _readerLoop.ConfigureAwait(false); } catch { }

            try { _stream.Dispose(); } catch { }
            try { _tcp.Dispose(); } catch { }

            _cts.Dispose();
            _messageSignal.Dispose();
            try { _rawChannel.Writer.TryComplete(); } catch { }
        }

        private async Task ReadLoopAsync()
        {
            var buf = new byte[4096];
            var sb = new StringBuilder();

            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    var n = await _stream.ReadAsync(buf, _cts.Token);
                    if (n <= 0)
                    {
                        break;
                    }

                    sb.Append(Encoding.UTF8.GetString(buf, 0, n));

                    while (true)
                    {
                        var s = sb.ToString();
                        var idx = s.IndexOf("\n", StringComparison.Ordinal);
                        if (idx < 0)
                        {
                            break;
                        }

                        var line = s[..(idx + 1)];
                        sb.Clear();
                        sb.Append(s[(idx + 1)..]);

                        line = line.TrimEnd('\r', '\n');
                        if (line.Length == 0)
                        {
                            continue;
                        }

                        _rawLines.Enqueue(line);
                        while (_rawLines.Count > MaxRawLines && _rawLines.TryDequeue(out _))
                        {
                        }
                        _rawChannel.Writer.TryWrite(line);

                        IrcMessage msg;
                        try
                        {
                            msg = IrcParser.ParseLine(line);
                        }
                        catch
                        {
                            var cmd = TryExtractCommand(line);
                            if (string.IsNullOrWhiteSpace(cmd))
                            {
                                continue;
                            }

                            msg = new IrcMessage(Prefix: null, Command: cmd, Params: Array.Empty<string>(), Trailing: null);
                        }

                        if (msg.Command == "PING")
                        {
                            var token = msg.Trailing ?? (msg.Params.FirstOrDefault() ?? "");
                            _ = SendAsync($"PONG :{token}", CancellationToken.None);
                        }

                        if (msg.Command == "NICK")
                        {
                            if (!string.IsNullOrWhiteSpace(msg.Prefix))
                            {
                                var prefixNick = msg.Prefix.Split('!', 2)[0];
                                if (!string.IsNullOrWhiteSpace(Nick) && string.Equals(prefixNick, Nick, StringComparison.OrdinalIgnoreCase))
                                {
                                    var newNick = msg.Trailing ?? msg.Params.FirstOrDefault();
                                    if (!string.IsNullOrWhiteSpace(newNick))
                                    {
                                        Nick = newNick;
                                    }
                                }
                            }
                        }

                        _messages.Enqueue(msg);
                        _messageSignal.Release();
                    }
                }
            }
            catch
            {
                // best-effort
            }
        }

        private void DrainRawChannel()
        {
            while (_rawChannel.Reader.TryRead(out _))
            {
            }
        }

        private static bool TryParseRaw(string line, out string command, out string[] args, out string? trailing)
        {
            command = string.Empty;
            args = Array.Empty<string>();
            trailing = null;

            var s = line.TrimEnd('\r', '\n');
            if (s.Length == 0)
            {
                return false;
            }

            var i = 0;

            if (s[0] == '@')
            {
                var sp = s.IndexOf(' ');
                if (sp < 0)
                {
                    return false;
                }

                i = sp + 1;
            }

            if (i < s.Length && s[i] == ':')
            {
                var sp = s.IndexOf(' ', i);
                if (sp < 0)
                {
                    return false;
                }

                i = sp + 1;
            }

            var trailingIndex = s.IndexOf(" :", i, StringComparison.Ordinal);
            string head;
            if (trailingIndex >= 0)
            {
                head = s[i..trailingIndex];
                trailing = s[(trailingIndex + 2)..];
            }
            else
            {
                head = s[i..];
            }

            var tokens = head.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (tokens.Length == 0)
            {
                return false;
            }

            command = tokens[0].ToUpperInvariant();
            args = tokens.Length > 1 ? tokens[1..] : Array.Empty<string>();
            return true;
        }

        private static string? TryExtractCommand(string line)
        {
            var s = line.Trim();
            if (s.Length == 0)
            {
                return null;
            }

            var i = 0;
            if (s[0] == '@')
            {
                var sp = s.IndexOf(' ');
                if (sp < 0)
                {
                    return null;
                }

                i = sp + 1;
            }

            if (i < s.Length && s[i] == ':')
            {
                var sp = s.IndexOf(' ', i);
                if (sp < 0)
                {
                    return null;
                }

                i = sp + 1;
            }

            while (i < s.Length && s[i] == ' ')
            {
                i++;
            }

            if (i >= s.Length)
            {
                return null;
            }

            var end = s.IndexOf(' ', i);
            var cmd = end < 0 ? s[i..] : s[i..end];
            return cmd.ToUpperInvariant();
        }
    }
}
