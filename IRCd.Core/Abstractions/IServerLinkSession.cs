namespace IRCd.Core.Abstractions
{
    using System.Net;
    using System.Threading;
    using System.Threading.Tasks;

    public interface IServerLinkSession
    {
        string ConnectionId { get; }

        EndPoint RemoteEndPoint { get; }

        ValueTask SendAsync(string line, CancellationToken ct = default);

        Task<string?> ReadLineAsync(CancellationToken ct);

        Task RunWriterLoopAsync(CancellationToken ct);

        Task CloseAsync(string reason, CancellationToken ct);

        bool IsAuthenticated { get; set; }

        string? RemoteServerName { get; set; }

        string? RemoteSid { get; set; }

        string? Pass { get; set; }

        bool CapabReceived { get; set; }

        bool UserSyncEnabled { get; set; }
    }
}
