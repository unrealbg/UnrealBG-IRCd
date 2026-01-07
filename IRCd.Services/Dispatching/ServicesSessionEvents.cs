namespace IRCd.Services.Dispatching
{
    using System;
    using System.Collections.Concurrent;
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Core.Abstractions;
    using IRCd.Core.State;
    using IRCd.Services.Auth;
    using IRCd.Services.SeenServ;
    using IRCd.Services.Storage;
    using IRCd.Shared.Options;

    using Microsoft.Extensions.Options;

    public sealed class ServicesSessionEvents : IServiceSessionEvents
    {
        private readonly IAuthState _auth;
        private readonly INickAccountRepository _nicks;
        private readonly IOptions<IrcOptions> _options;
        private readonly ISeenRepository _seen;

        private readonly ConcurrentDictionary<string, CancellationTokenSource> _enforcementCtsByConn = new(StringComparer.OrdinalIgnoreCase);

        public ServicesSessionEvents(IAuthState auth, INickAccountRepository nicks, IOptions<IrcOptions> options, ISeenRepository seen)
        {
            _auth = auth;
            _nicks = nicks;
            _options = options;
            _seen = seen;
        }

        public async ValueTask OnNickChangedAsync(IClientSession session, string? oldNick, string newNick, ServerState state, CancellationToken ct)
        {
            if (!string.IsNullOrWhiteSpace(oldNick))
            {
                var host = state.GetHostFor(session.ConnectionId);
                await _seen.TryUpsertAsync(new SeenRecord
                {
                    Nick = oldNick!,
                    UserName = session.UserName,
                    Host = host,
                    WhenUtc = DateTimeOffset.UtcNow,
                    Message = $"changed nick to {newNick}"
                }, ct);
            }

            var identified = await _auth.GetIdentifiedAccountAsync(session.ConnectionId, ct);
            if (string.IsNullOrWhiteSpace(identified))
            {
                await MaybeStartNickEnforcementAsync(session, newNick, ct);
                return;
            }

            if (!await IsNickInIdentifiedAccountAsync(identified, newNick, ct))
            {
                await _auth.ClearAsync(session.ConnectionId, ct);
            }

            await MaybeStartNickEnforcementAsync(session, newNick, ct);
        }

        public async ValueTask OnQuitAsync(IClientSession session, string reason, ServerState state, CancellationToken ct)
        {
            if (session.IsRegistered && session.Nick is { Length: > 0 } nick)
            {
                var host = state.GetHostFor(session.ConnectionId);
                await _seen.TryUpsertAsync(new SeenRecord
                {
                    Nick = nick,
                    UserName = session.UserName,
                    Host = host,
                    WhenUtc = DateTimeOffset.UtcNow,
                    Message = $"quit: {reason}"
                }, ct);
            }

            CancelNickEnforcement(session.ConnectionId);
            await _auth.ClearAsync(session.ConnectionId, ct);
        }

        public async ValueTask<bool> IsNickRegisteredAsync(string nick, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(nick) || string.Equals(nick, "*", StringComparison.Ordinal))
            {
                return false;
            }

            var acc = await _nicks.GetByNameAsync(nick, ct);
            return acc is not null && acc.IsConfirmed;
        }

        public async ValueTask<bool> IsIdentifiedForNickAsync(string connectionId, string nick, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(connectionId))
                return false;

            if (string.IsNullOrWhiteSpace(nick) || string.Equals(nick, "*", StringComparison.Ordinal))
                return false;

            var identified = await _auth.GetIdentifiedAccountAsync(connectionId, ct);
            if (string.IsNullOrWhiteSpace(identified))
                return false;

            return await IsNickInIdentifiedAccountAsync(identified, nick, ct);
        }

        private void CancelNickEnforcement(string connectionId)
        {
            if (string.IsNullOrWhiteSpace(connectionId))
            {
                return;
            }

            if (_enforcementCtsByConn.TryRemove(connectionId, out var cts))
            {
                try { cts.Cancel(); } catch { }
                try { cts.Dispose(); } catch { }
            }
        }

        private async ValueTask MaybeStartNickEnforcementAsync(IClientSession session, string newNick, CancellationToken ct)
        {
            CancelNickEnforcement(session.ConnectionId);

            var cfg = _options.Value.Services?.NickServ ?? new NickServOptions();
            if (!cfg.EnforceRegisteredNicks)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(newNick) || string.Equals(newNick, "*", StringComparison.Ordinal))
            {
                return;
            }

            var acc = await _nicks.GetByNameAsync(newNick, ct);
            if (acc is null)
            {
                return;
            }

            if (!acc.IsConfirmed)
            {
                return;
            }

            var master = acc;
            if (!string.IsNullOrWhiteSpace(acc.GroupedToAccount))
            {
                var m = await _nicks.GetByNameAsync(acc.GroupedToAccount!, ct);
                if (m is not null)
                {
                    master = m;
                }
            }

            if (!master.IsConfirmed)
            {
                return;
            }

            if (!master.Enforce)
            {
                return;
            }

            var identified = await _auth.GetIdentifiedAccountAsync(session.ConnectionId, ct);
            if (identified is not null && string.Equals(identified, master.Name, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (!master.Kill)
            {
                var to = session.Nick ?? "*";
                var server = _options.Value.ServerInfo?.Name ?? "server";
                await session.SendAsync($":NickServ!services@{server} NOTICE {to} :This nickname is registered. Please IDENTIFY to use it.", ct);
                return;
            }

            var toNick = session.Nick ?? "*";
            var serverName = _options.Value.ServerInfo?.Name ?? "server";
            await session.SendAsync($":NickServ!services@{serverName} NOTICE {toNick} :This nickname is registered and protected with KILL. You have 30 seconds to IDENTIFY or you will be disconnected.", ct);

            var delaySeconds = cfg.EnforceDelaySeconds;
            if (delaySeconds <= 0)
            {
                await session.CloseAsync("KILL protection: IDENTIFY required", ct);
                return;
            }

            var cts = new CancellationTokenSource();
            _enforcementCtsByConn[session.ConnectionId] = cts;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cts.Token);
                    if (cts.Token.IsCancellationRequested)
                    {
                        return;
                    }

                    var ident2 = await _auth.GetIdentifiedAccountAsync(session.ConnectionId, cts.Token);
                    if (ident2 is not null && string.Equals(ident2, master.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }

                    if (!string.Equals(session.Nick, newNick, StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }

                    await session.CloseAsync("KILL protection: IDENTIFY required", cts.Token);
                }
                catch
                {
                    // ignore
                }
                finally
                {
                    CancelNickEnforcement(session.ConnectionId);
                }
            });
        }

        private async ValueTask<bool> IsNickInIdentifiedAccountAsync(string identifiedAccount, string newNick, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(identifiedAccount) || string.IsNullOrWhiteSpace(newNick) || string.Equals(newNick, "*", StringComparison.Ordinal))
            {
                return false;
            }

            var acc = await _nicks.GetByNameAsync(newNick, ct);
            if (acc is null)
            {
                return false;
            }

            var masterName = string.IsNullOrWhiteSpace(acc.GroupedToAccount) ? acc.Name : acc.GroupedToAccount;
            return string.Equals(masterName, identifiedAccount, StringComparison.OrdinalIgnoreCase);
        }
    }
}
