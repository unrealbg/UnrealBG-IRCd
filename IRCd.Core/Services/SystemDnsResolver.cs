namespace IRCd.Core.Services;

using System.Net;
using IRCd.Core.Abstractions;

public sealed class SystemDnsResolver : IDnsResolver
{
    public async Task<bool> HasAnyAddressAsync(string fqdn, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(fqdn))
        {
            return false;
        }

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(fqdn).WaitAsync(ct);
            return addresses is { Length: > 0 };
        }
        catch
        {
            return false;
        }
    }
}
