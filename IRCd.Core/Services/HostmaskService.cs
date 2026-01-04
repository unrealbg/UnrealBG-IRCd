namespace IRCd.Core.Services
{
    using System;
    using System.Net;
    using System.Security.Cryptography;

    public sealed class HostmaskService
    {
        /// <summary>
        /// Returns a stable "cloak" host for the given IP. Production-friendly, fast, no DNS.
        /// </summary>
        public string GetDisplayedHost(IPAddress? ip)
        {
            if (ip is null || ip.Equals(IPAddress.None))
                return "unknown";

            var bytes = ip.GetAddressBytes();
            var hash = SHA256.HashData(bytes);
            var hex = Convert.ToHexString(hash).ToLowerInvariant();

            return $"user-{hex[..8]}.unrealbg";
        }
    }
}
