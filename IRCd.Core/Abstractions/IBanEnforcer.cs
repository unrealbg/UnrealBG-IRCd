namespace IRCd.Core.Abstractions
{
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Core.State;

    public interface IBanEnforcer
    {
        Task EnforceBanImmediatelyAsync(BanEntry ban, CancellationToken ct = default);
    }
}
