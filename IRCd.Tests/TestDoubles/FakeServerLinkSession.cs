namespace IRCd.Tests.TestDoubles;

using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using IRCd.Core.Abstractions;

public sealed class FakeServerLinkSession : IServerLinkSession
{
    private readonly Queue<string?> _incoming;

    public FakeServerLinkSession(string connectionId, IEnumerable<string?> incoming, EndPoint? remoteEndPoint = null)
    {
        ConnectionId = connectionId;
        _incoming = new Queue<string?>(incoming);
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

    public ValueTask SendAsync(string line, CancellationToken ct = default)
    {
        Outgoing.Add(line);
        return ValueTask.CompletedTask;
    }

    public Task<string?> ReadLineAsync(CancellationToken ct)
    {
        if (_incoming.Count == 0)
            return Task.FromResult<string?>(null);

        return Task.FromResult(_incoming.Dequeue());
    }

    public Task RunWriterLoopAsync(CancellationToken ct) => Task.CompletedTask;

    public Task CloseAsync(string reason, CancellationToken ct) => Task.CompletedTask;
}
