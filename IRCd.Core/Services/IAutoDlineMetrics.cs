namespace IRCd.Core.Services
{
    using System.Collections.Generic;

    public interface IAutoDlineMetrics
    {
        long AutoDlinesTotal { get; }

        /// <summary>
        /// Returns aggregate prefixes only (e.g. IPv4 /24, IPv6 /64).
        /// </summary>
        IReadOnlyList<(string Prefix, long OffenseCount)> GetTopOffenders(int topN);
    }
}
