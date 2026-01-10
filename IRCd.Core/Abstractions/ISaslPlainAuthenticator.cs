namespace IRCd.Core.Abstractions
{
    using System.Threading;
    using System.Threading.Tasks;

    public interface ISaslPlainAuthenticator
    {
        ValueTask<SaslAuthenticateResult> AuthenticatePlainAsync(string authcid, string password, CancellationToken ct);
    }

    public sealed record SaslAuthenticateResult(bool Success, string? AccountName, string? Error);
}
