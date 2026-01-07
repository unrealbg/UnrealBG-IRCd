namespace IRCd.Services.Storage
{
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Services.HostServ;

    public interface IVHostRepository
    {
        ValueTask<VHostRecord?> GetAsync(string nick, CancellationToken ct);

        IEnumerable<VHostRecord> All();

        ValueTask<bool> TryUpsertAsync(VHostRecord record, CancellationToken ct);

        ValueTask<bool> TryDeleteAsync(string nick, CancellationToken ct);
    }
}
