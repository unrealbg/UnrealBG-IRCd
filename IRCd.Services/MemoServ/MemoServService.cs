namespace IRCd.Services.MemoServ
{
    using System;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Core.Abstractions;
    using IRCd.Core.State;
    using IRCd.Services.Auth;
    using IRCd.Services.Storage;
    using IRCd.Shared.Options;

    using Microsoft.Extensions.Options;

    public sealed class MemoServService
    {
        private readonly IOptions<IrcOptions> _options;
        private readonly INickAccountRepository _accounts;
        private readonly IAuthState _auth;
        private readonly IMemoRepository _memos;
        private readonly ISessionRegistry? _sessions;

        public MemoServService(IOptions<IrcOptions> options, INickAccountRepository accounts, IAuthState auth, IMemoRepository memos, ISessionRegistry? sessions = null)
        {
            _options = options;
            _accounts = accounts;
            _auth = auth;
            _memos = memos;
            _sessions = sessions;
        }

        public async ValueTask HandleAsync(IClientSession session, string text, ServerState state, CancellationToken ct)
        {
            _ = state;

            var cmdLine = (text ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(cmdLine))
            {
                await ReplyAsync(session, MemoServMessages.HelpIntro, ct);
                return;
            }

            var parts = cmdLine.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var cmd = parts.Length > 0 ? parts[0].ToUpperInvariant() : "HELP";
            var args = parts.Skip(1).ToArray();

            switch (cmd)
            {
                case "HELP":
                    await HelpAsync(session, ct);
                    return;

                case "SEND":
                    await SendAsync(session, args, state, ct);
                    return;

                case "LIST":
                    await ListAsync(session, ct);
                    return;

                case "READ":
                    await ReadAsync(session, args, ct);
                    return;

                case "DEL":
                case "DELETE":
                    await DeleteAsync(session, args, ct);
                    return;

                case "CLEAR":
                    await ClearAsync(session, ct);
                    return;

                default:
                    await ReplyAsync(session, "Unknown command. Try: HELP", ct);
                    return;
            }
        }

        private async ValueTask HelpAsync(IClientSession session, CancellationToken ct)
        {
            await ReplyAsync(session, MemoServMessages.HelpIntro, ct);
            await ReplyAsync(session, MemoServMessages.HelpSend, ct);
            await ReplyAsync(session, MemoServMessages.HelpList, ct);
            await ReplyAsync(session, MemoServMessages.HelpRead, ct);
            await ReplyAsync(session, MemoServMessages.HelpDel, ct);
            await ReplyAsync(session, MemoServMessages.HelpClear, ct);
        }

        private async ValueTask SendAsync(IClientSession session, string[] args, ServerState state, CancellationToken ct)
        {
            if (args.Length < 2)
            {
                await ReplyAsync(session, "Syntax: SEND <nick> <text>", ct);
                return;
            }

            var fromAccount = await RequireIdentifiedAccountAsync(session, ct);
            if (fromAccount is null)
            {
                return;
            }

            var targetNick = args[0];
            var message = string.Join(' ', args.Skip(1));
            if (string.IsNullOrWhiteSpace(message))
            {
                await ReplyAsync(session, "Syntax: SEND <nick> <text>", ct);
                return;
            }

            var targetAccount = await ResolveConfirmedAccountNameAsync(targetNick, ct);
            if (targetAccount is null)
            {
                await ReplyAsync(session, "That nickname is not registered.", ct);
                return;
            }

            var targetAccountRecord = await _accounts.GetByNameAsync(targetAccount, ct);
            if (targetAccountRecord is null || !targetAccountRecord.IsConfirmed)
            {
                await ReplyAsync(session, "That nickname is not registered.", ct);
                return;
            }

            if (!targetAccountRecord.AllowMemos)
            {
                await ReplyAsync(session, "That user does not accept memos.", ct);
                return;
            }

            var memo = new Memo
            {
                FromAccount = fromAccount,
                Text = message,
                SentAtUtc = DateTimeOffset.UtcNow
            };

            var ok = await _memos.TryAddMemoAsync(targetAccount, memo, ct);
            await ReplyAsync(session, ok ? $"Memo sent to {targetAccount}." : "Failed to send memo.", ct);

            if (ok && targetAccountRecord.MemoNotify)
            {
                string? targetConn = null;
                if (!state.TryGetConnectionIdByNick(targetNick, out targetConn) || string.IsNullOrWhiteSpace(targetConn))
                {
                    state.TryGetConnectionIdByNick(targetAccount, out targetConn);
                }

                if (!string.IsNullOrWhiteSpace(targetConn) && _sessions is not null && _sessions.TryGet(targetConn, out var targetSession) && targetSession is not null)
                {
                    var server = _options.Value.ServerInfo?.Name ?? "server";
                    var to = targetSession.Nick ?? "*";
                    var line = $":{MemoServMessages.ServiceName}!services@{server} NOTICE {to} :You have a new memo from {fromAccount}. Use: /msg {MemoServMessages.ServiceName} LIST";
                    await targetSession.SendAsync(line, ct);
                }
            }
        }

        private async ValueTask ListAsync(IClientSession session, CancellationToken ct)
        {
            var account = await RequireIdentifiedAccountAsync(session, ct);
            if (account is null)
            {
                return;
            }

            var list = await _memos.GetMemosAsync(account, ct);
            if (list.Count == 0)
            {
                await ReplyAsync(session, "You have no memos.", ct);
                return;
            }

            var i = 1;
            foreach (var m in list)
            {
                var flag = m.IsRead ? "R" : "N";
                await ReplyAsync(session, $"{i}) [{flag}] From {m.FromAccount}: {m.Text}", ct);
                i++;
            }
        }

        private async ValueTask ReadAsync(IClientSession session, string[] args, CancellationToken ct)
        {
            if (args.Length < 1 || !int.TryParse(args[0], out var n) || n <= 0)
            {
                await ReplyAsync(session, "Syntax: READ <num>", ct);
                return;
            }

            var account = await RequireIdentifiedAccountAsync(session, ct);
            if (account is null)
            {
                return;
            }

            var list = await _memos.GetMemosAsync(account, ct);
            if (n > list.Count)
            {
                await ReplyAsync(session, "No such memo.", ct);
                return;
            }

            var m = list[n - 1];
            await ReplyAsync(session, $"Memo {n} from {m.FromAccount}: {m.Text}", ct);

            if (!m.IsRead)
            {
                var updated = m with { IsRead = true, ReadAtUtc = DateTimeOffset.UtcNow };
                await _memos.TryUpdateMemoAsync(account, updated, ct);
            }
        }

        private async ValueTask DeleteAsync(IClientSession session, string[] args, CancellationToken ct)
        {
            if (args.Length < 1 || !int.TryParse(args[0], out var n) || n <= 0)
            {
                await ReplyAsync(session, "Syntax: DEL <num>", ct);
                return;
            }

            var account = await RequireIdentifiedAccountAsync(session, ct);
            if (account is null)
            {
                return;
            }

            var list = await _memos.GetMemosAsync(account, ct);
            if (n > list.Count)
            {
                await ReplyAsync(session, "No such memo.", ct);
                return;
            }

            var m = list[n - 1];
            var ok = await _memos.TryDeleteMemoAsync(account, m.Id, ct);
            await ReplyAsync(session, ok ? "Deleted." : "Delete failed.", ct);
        }

        private async ValueTask ClearAsync(IClientSession session, CancellationToken ct)
        {
            var account = await RequireIdentifiedAccountAsync(session, ct);
            if (account is null)
            {
                return;
            }

            var ok = await _memos.TryClearAsync(account, ct);
            await ReplyAsync(session, ok ? "Cleared." : "Nothing to clear.", ct);
        }

        private async ValueTask<string?> RequireIdentifiedAccountAsync(IClientSession session, CancellationToken ct)
        {
            var account = await _auth.GetIdentifiedAccountAsync(session.ConnectionId, ct);
            if (string.IsNullOrWhiteSpace(account))
            {
                await ReplyAsync(session, "You must be identified to use MemoServ.", ct);
                return null;
            }

            var exists = await _accounts.GetByNameAsync(account, ct);
            if (exists is null || !exists.IsConfirmed)
            {
                await ReplyAsync(session, "You must be identified to a registered account.", ct);
                return null;
            }

            return account;
        }

        private async ValueTask<string?> ResolveConfirmedAccountNameAsync(string nickOrAccount, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(nickOrAccount))
            {
                return null;
            }

            var acc = await _accounts.GetByNameAsync(nickOrAccount.Trim(), ct);
            if (acc is null || !acc.IsConfirmed)
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(acc.GroupedToAccount))
            {
                var master = await _accounts.GetByNameAsync(acc.GroupedToAccount!, ct);
                if (master is not null && master.IsConfirmed)
                {
                    return master.Name;
                }
            }

            return acc.Name;
        }

        private ValueTask ReplyAsync(IClientSession session, string text, CancellationToken ct)
        {
            var server = _options.Value.ServerInfo?.Name ?? "server";
            var to = session.Nick ?? "*";
            var line = $":{MemoServMessages.ServiceName}!services@{server} NOTICE {to} :{text}";
            return session.SendAsync(line, ct);
        }
    }
}
