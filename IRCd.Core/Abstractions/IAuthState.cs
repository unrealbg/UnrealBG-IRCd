namespace IRCd.Core.Abstractions
{
    using System.Threading;
    using System.Threading.Tasks;

    public interface IAuthState
    {
        ValueTask<string?> GetIdentifiedAccountAsync(string connectionId, CancellationToken ct);

        ValueTask SetIdentifiedAccountAsync(string connectionId, string? accountName, CancellationToken ct);

        ValueTask ClearAsync(string connectionId, CancellationToken ct);
    }
}
