namespace IRCd.Services.Storage
{
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Services.AdminServ;

    public interface IAdminStaffRepository
    {
        ValueTask<AdminStaffEntry?> GetByAccountAsync(string account, CancellationToken ct);

        IEnumerable<AdminStaffEntry> All();

        ValueTask<bool> TryUpsertAsync(AdminStaffEntry entry, CancellationToken ct);

        ValueTask<bool> TryDeleteAsync(string account, CancellationToken ct);
    }
}
