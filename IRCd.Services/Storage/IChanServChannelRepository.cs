namespace IRCd.Services.Storage
{
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Services.ChanServ;

    public interface IChanServChannelRepository
    {
        ValueTask<RegisteredChannel?> GetByNameAsync(string channelName, CancellationToken ct);

        IEnumerable<RegisteredChannel> All();

        ValueTask<bool> TryCreateAsync(RegisteredChannel channel, CancellationToken ct);

        ValueTask<bool> TryDeleteAsync(string channelName, CancellationToken ct);

        ValueTask<bool> TryUpdateAsync(RegisteredChannel updated, CancellationToken ct);
    }
}
