namespace IRCd.Services.NickServ
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Core.Abstractions;
    using IRCd.Services.Storage;

    public sealed class NickServSaslPlainAuthenticator : ISaslPlainAuthenticator
    {
        private readonly INickAccountRepository _repo;

        public NickServSaslPlainAuthenticator(INickAccountRepository repo)
        {
            _repo = repo;
        }

        public async ValueTask<SaslAuthenticateResult> AuthenticatePlainAsync(string authcid, string password, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(authcid) || string.IsNullOrWhiteSpace(password))
            {
                return new SaslAuthenticateResult(false, null, "Missing credentials");
            }

            var acc = await _repo.GetByNameAsync(authcid.Trim(), ct);
            if (acc is null || !acc.IsConfirmed)
            {
                return new SaslAuthenticateResult(false, null, "Invalid account");
            }

            var master = acc;
            if (!string.IsNullOrWhiteSpace(acc.GroupedToAccount))
            {
                var m = await _repo.GetByNameAsync(acc.GroupedToAccount!, ct);
                if (m is not null)
                {
                    master = m;
                }
            }

            if (!master.IsConfirmed)
            {
                return new SaslAuthenticateResult(false, null, "Invalid account");
            }

            if (!PasswordHasher.Verify(password, master.PasswordHash))
            {
                return new SaslAuthenticateResult(false, null, "Invalid credentials");
            }

            return new SaslAuthenticateResult(true, master.Name, null);
        }
    }
}
