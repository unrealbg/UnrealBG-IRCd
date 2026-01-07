namespace IRCd.Services.Storage
{
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Services.SeenServ;

    public interface ISeenRepository
    {
        ValueTask<SeenRecord?> GetAsync(string nick, CancellationToken ct);

        ValueTask<bool> TryUpsertAsync(SeenRecord record, CancellationToken ct);
    }
}
