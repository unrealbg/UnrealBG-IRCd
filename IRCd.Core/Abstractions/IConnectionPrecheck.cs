namespace IRCd.Core.Abstractions;

using System.Net;

public sealed record ConnectionPrecheckContext(
    IPAddress RemoteIp,
    IPEndPoint LocalEndPoint,
    bool Secure);

public sealed record ConnectionPrecheckResult(
    bool Allowed,
    string? RejectMessage);

public interface IConnectionPrecheckPipeline
{
    Task<ConnectionPrecheckResult> CheckAsync(ConnectionPrecheckContext context, CancellationToken ct);
}

public interface IDnsResolver
{
    /// <summary>
    /// Returns true if a DNS query for <paramref name="fqdn"/> resolves to at least one IP.
    /// </summary>
    Task<bool> HasAnyAddressAsync(string fqdn, CancellationToken ct);
}
