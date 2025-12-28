namespace IRCd.Core.Abstractions
{
    using System.Net;

    public interface IClientSession
    {
        string ConnectionId { get; }

        EndPoint RemoteEndPoint { get; }

        string? Nick { get; set; }

        string? UserName { get; set; }

        bool IsRegistered { get; set; }

        ValueTask SendAsync(string line, CancellationToken ct = default);

        ValueTask CloseAsync(string reason, CancellationToken ct = default);
    }
}
