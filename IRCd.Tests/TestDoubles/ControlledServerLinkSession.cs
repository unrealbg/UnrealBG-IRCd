namespace IRCd.Tests.TestDoubles;

using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

using IRCd.Core.Abstractions;

public sealed class ControlledServerLinkSession : IServerLinkSession
{
    private readonly Channel<string?> _incoming = Channel.CreateUnbounded<string?>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false
    });

    public ControlledServerLinkSession(string connectionId, EndPoint? remoteEndPoint = null)
    {
        ConnectionId = connectionId;
        RemoteEndPoint = remoteEndPoint ?? new IPEndPoint(IPAddress.Loopback, 1);
    }

    public string ConnectionId { get; }

    public EndPoint RemoteEndPoint { get; }

    public bool IsAuthenticated { get; set; }

    public string? RemoteServerName { get; set; }

    public string? RemoteSid { get; set; }

    public string? Pass { get; set; }

    public bool CapabReceived { get; set; }

    public bool UserSyncEnabled { get; set; } = true;

    public List<string> Outgoing { get; } = new();

    public void Enqueue(string? line) => _incoming.Writer.TryWrite(line);

    public void Complete() => _incoming.Writer.TryComplete();

    public ValueTask SendAsync(string line, CancellationToken ct = default)
    {
        Outgoing.Add(line);
        return ValueTask.CompletedTask;
    }

    public async Task<string?> ReadLineAsync(CancellationToken ct)
    {
        while (await _incoming.Reader.WaitToReadAsync(ct))
        {
            if (_incoming.Reader.TryRead(out var item))
                return item;
        }

        return null;
    }

    public Task RunWriterLoopAsync(CancellationToken ct) => Task.CompletedTask;

    public Task CloseAsync(string reason, CancellationToken ct) => Task.CompletedTask;
}
