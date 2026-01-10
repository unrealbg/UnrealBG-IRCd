namespace IRCd.Core.Commands.Handlers
{
    using System;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Core.Abstractions;
    using IRCd.Core.Commands.Contracts;
    using IRCd.Core.Protocol;
    using IRCd.Core.Services;
    using IRCd.Core.State;
    using IRCd.Shared.Options;

    using Microsoft.Extensions.Options;

    public sealed class AuthenticateHandler : IIrcCommandHandler
    {
        public string Command => "AUTHENTICATE";

        private readonly IOptions<IrcOptions> _options;
        private readonly SaslService _sasl;
        private readonly IAuthState _auth;
        private readonly ISaslPlainAuthenticator _authenticator;

        public AuthenticateHandler(
            IOptions<IrcOptions> options,
            SaslService sasl,
            IAuthState auth,
            ISaslPlainAuthenticator authenticator)
        {
            _options = options;
            _sasl = sasl;
            _auth = auth;
            _authenticator = authenticator;
        }

        public async ValueTask HandleAsync(IClientSession session, IrcMessage msg, ServerState state, CancellationToken ct)
        {
            var serverName = _options.Value.ServerInfo?.Name ?? "server";
            var nick = session.Nick ?? "*";

            if (session.IsRegistered)
            {
                await session.SendAsync($":{serverName} 907 {nick} :You have already authenticated using SASL", ct);
                return;
            }

            if (!session.EnabledCapabilities.Contains("sasl"))
            {
                await session.SendAsync($":{serverName} 904 {nick} :SASL authentication failed", ct);
                return;
            }

            var param = msg.Params.Count > 0 ? msg.Params[0] : msg.Trailing;
            if (param is null)
            {
                await session.SendAsync($":{serverName} 904 {nick} :SASL authentication failed", ct);
                return;
            }

            var s = _sasl.GetOrCreate(session.ConnectionId);

            if (s.Completed)
            {
                await session.SendAsync($":{serverName} 907 {nick} :You have already authenticated using SASL", ct);
                return;
            }

            if (!s.InProgress)
            {
                if (param == "*")
                {
                    await session.SendAsync($":{serverName} 906 {nick} :SASL authentication aborted", ct);
                    _sasl.Clear(session.ConnectionId);
                    return;
                }

                var mech = param.Trim();
                if (mech.Equals("PLAIN", StringComparison.OrdinalIgnoreCase))
                {
                    s.Start("PLAIN");
                }
                else if (mech.Equals("EXTERNAL", StringComparison.OrdinalIgnoreCase) && _options.Value.Sasl.External.Enabled)
                {
                    s.Start("EXTERNAL");
                }
                else
                {
                    await session.SendAsync($":{serverName} 905 {nick} {mech} :SASL mechanism not supported", ct);
                    return;
                }

                await session.SendAsync("AUTHENTICATE +", ct);
                return;
            }

            if (param == "*")
            {
                await session.SendAsync($":{serverName} 906 {nick} :SASL authentication aborted", ct);
                _sasl.Clear(session.ConnectionId);
                return;
            }

            if (!s.TryAppendChunk(param, out var assembled, out var appendError))
            {
                await session.SendAsync($":{serverName} 904 {nick} :SASL authentication failed", ct);
                _sasl.Clear(session.ConnectionId);
                return;
            }

            if (!string.IsNullOrWhiteSpace(appendError))
            {
                await session.SendAsync($":{serverName} 904 {nick} :SASL authentication failed", ct);
                _sasl.Clear(session.ConnectionId);
                return;
            }

            if (assembled is null)
            {
                return;
            }

            if (string.Equals(s.Mechanism, "PLAIN", StringComparison.OrdinalIgnoreCase))
            {
                string authcid;
                string password;

                try
                {
                    var decoded = Convert.FromBase64String(assembled);
                    var text = Encoding.UTF8.GetString(decoded);
                    var parts = text.Split('\0');
                    if (parts.Length != 3)
                    {
                        throw new FormatException("Invalid PLAIN payload");
                    }

                    authcid = parts[1];
                    password = parts[2];
                }
                catch
                {
                    await session.SendAsync($":{serverName} 904 {nick} :SASL authentication failed", ct);
                    _sasl.Clear(session.ConnectionId);
                    return;
                }

                if (string.IsNullOrWhiteSpace(authcid) || string.IsNullOrWhiteSpace(password))
                {
                    await session.SendAsync($":{serverName} 904 {nick} :SASL authentication failed", ct);
                    _sasl.Clear(session.ConnectionId);
                    return;
                }

                var result = await _authenticator.AuthenticatePlainAsync(authcid, password, ct);
                if (!result.Success || string.IsNullOrWhiteSpace(result.AccountName))
                {
                    await session.SendAsync($":{serverName} 904 {nick} :SASL authentication failed", ct);
                    _sasl.Clear(session.ConnectionId);
                    return;
                }

                await _auth.SetIdentifiedAccountAsync(session.ConnectionId, result.AccountName, ct);
                s.MarkCompleted();

                await session.SendAsync($":{serverName} 900 {nick} {nick} {result.AccountName} :You are now logged in as {result.AccountName}", ct);
                await session.SendAsync($":{serverName} 903 {nick} :SASL authentication successful", ct);
                return;
            }

            if (string.Equals(s.Mechanism, "EXTERNAL", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    _ = Convert.FromBase64String(assembled);
                }
                catch
                {
                    await session.SendAsync($":{serverName} 904 {nick} :SASL authentication failed", ct);
                    _sasl.Clear(session.ConnectionId);
                    return;
                }

                if (!session.IsSecureConnection)
                {
                    await session.SendAsync($":{serverName} 904 {nick} :SASL authentication failed", ct);
                    _sasl.Clear(session.ConnectionId);
                    return;
                }

                var fp = NormalizeHexFingerprint(session.ClientCertificateFingerprintSha256);
                var subj = session.ClientCertificateSubject?.Trim();

                var account = default(string?);
                if (!string.IsNullOrWhiteSpace(fp) && _options.Value.Sasl.External.FingerprintToAccount.TryGetValue(fp, out var byFp))
                {
                    account = byFp;
                }
                else if (!string.IsNullOrWhiteSpace(subj) && _options.Value.Sasl.External.SubjectToAccount.TryGetValue(subj, out var bySubj))
                {
                    account = bySubj;
                }

                if (string.IsNullOrWhiteSpace(account))
                {
                    await session.SendAsync($":{serverName} 904 {nick} :SASL authentication failed", ct);
                    _sasl.Clear(session.ConnectionId);
                    return;
                }

                await _auth.SetIdentifiedAccountAsync(session.ConnectionId, account, ct);
                s.MarkCompleted();

                await session.SendAsync($":{serverName} 900 {nick} {nick} {account} :You are now logged in as {account}", ct);
                await session.SendAsync($":{serverName} 903 {nick} :SASL authentication successful", ct);
                return;
            }

            await session.SendAsync($":{serverName} 904 {nick} :SASL authentication failed", ct);
            _sasl.Clear(session.ConnectionId);
        }

        private static string NormalizeHexFingerprint(string? input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return string.Empty;

            Span<char> buf = stackalloc char[input.Length];
            var n = 0;

            foreach (var ch in input)
            {
                var c = ch;
                if (c is >= '0' and <= '9') { buf[n++] = c; continue; }
                if (c is >= 'a' and <= 'f') { buf[n++] = (char)(c - 32); continue; }
                if (c is >= 'A' and <= 'F') { buf[n++] = c; continue; }
            }

            return n == 0 ? string.Empty : new string(buf[..n]);
        }
    }
}
