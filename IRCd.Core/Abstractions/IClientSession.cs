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

        DateTime LastActivityUtc { get; }

        DateTime LastPingUtc { get; }

        bool AwaitingPong { get; }

        string? LastPingToken { get; }

        string UserModes { get; }

        bool TryApplyUserModes(string modeString, out string appliedModes);

        void OnInboundLine();

        void OnPingSent(string token);

        void OnPongReceived(string? token);

        ValueTask SendAsync(string line, CancellationToken ct = default);

        ValueTask CloseAsync(string reason, CancellationToken ct = default);
    }
}
