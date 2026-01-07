namespace IRCd.Services.Storage
{
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Services.NickServ;

    public interface INickAccountRepository
    {
        ValueTask<NickAccount?> GetByNameAsync(string name, CancellationToken ct);

        IEnumerable<NickAccount> All();

        ValueTask<bool> TryCreateAsync(NickAccount account, CancellationToken ct);

        ValueTask<bool> TryUpdatePasswordHashAsync(string name, string passwordHash, CancellationToken ct);

        ValueTask<bool> TryUpdateAsync(NickAccount updated, CancellationToken ct);

        ValueTask<bool> TryDeleteAsync(string name, CancellationToken ct);
    }
}
