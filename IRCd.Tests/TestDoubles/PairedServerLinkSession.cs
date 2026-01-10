namespace IRCd.Tests.TestDoubles;

using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

using IRCd.Core.Abstractions;

public sealed class PairedServerLinkSession : IServerLinkSession
{
    private readonly Channel<string?> _incoming = Channel.CreateUnbounded<string?>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false
    });

    private PairedServerLinkSession(string connectionId, EndPoint? remoteEndPoint)
    {
        ConnectionId = connectionId;
        RemoteEndPoint = remoteEndPoint ?? new IPEndPoint(IPAddress.Loopback, 1);
    }

    public static (PairedServerLinkSession A, PairedServerLinkSession B) CreatePair(string connectionIdA = "connA", string connectionIdB = "connB")
    {
        var a = new PairedServerLinkSession(connectionIdA, remoteEndPoint: new IPEndPoint(IPAddress.Loopback, 10001));
        var b = new PairedServerLinkSession(connectionIdB, remoteEndPoint: new IPEndPoint(IPAddress.Loopback, 10002));
        a.Peer = b;
        b.Peer = a;
        return (a, b);
    }

    private PairedServerLinkSession? Peer { get; set; }

    public string ConnectionId { get; }

    public EndPoint RemoteEndPoint { get; }

    public bool IsAuthenticated { get; set; }

    public string? RemoteServerName { get; set; }

    public string? RemoteSid { get; set; }

    public string? Pass { get; set; }

    public bool CapabReceived { get; set; }

    public bool UserSyncEnabled { get; set; } = true;

    public List<string> Outgoing { get; } = new();

    public ValueTask SendAsync(string line, CancellationToken ct = default)
    {
        Outgoing.Add(line);

        // Simulate TCP: line written on this end is read on the peer.
        Peer?._incoming.Writer.TryWrite(line);
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

    public Task CloseAsync(string reason, CancellationToken ct)
    {
        _incoming.Writer.TryComplete();
        return Task.CompletedTask;
    }

    public void Complete() => _incoming.Writer.TryComplete();
}
