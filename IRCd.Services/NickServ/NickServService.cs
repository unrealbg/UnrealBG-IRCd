namespace IRCd.Services.NickServ
{
    using System;
    using System.Collections.Concurrent;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Core.Abstractions;
    using IRCd.Core.Services;
    using IRCd.Core.State;
    using IRCd.Services.Email;
    using IRCd.Services.MemoServ;
    using IRCd.Services.Storage;
    using IRCd.Shared.Options;

    using Microsoft.Extensions.Options;

    public sealed class NickServService
    {
        private const int NotRegisteredCooldownMs = 2000;

        private readonly IOptions<IrcOptions> _options;
        private readonly INickAccountRepository _repo;
        private readonly IAuthState _auth;
        private readonly IEmailSender _email;
        private readonly IMemoRepository _memos;
        private readonly ISessionRegistry? _sessions;
        private readonly IChanServChannelRepository? _channels;
        private readonly IRCd.Core.Services.RuntimeDenyService? _deny;

        private readonly ConcurrentDictionary<string, long> _notRegisteredUntilTickByConn = new(StringComparer.Ordinal);

        public NickServService(IOptions<IrcOptions> options, INickAccountRepository repo, IAuthState auth, IEmailSender email, IMemoRepository memos, ISessionRegistry? sessions = null, IChanServChannelRepository? channels = null, IRCd.Core.Services.RuntimeDenyService? deny = null)
        {
            _options = options;
            _repo = repo;
            _auth = auth;
            _email = email;
            _memos = memos;
            _sessions = sessions;
            _channels = channels;
            _deny = deny;
        }

        public async ValueTask HandleAsync(IClientSession session, string text, ServerState state, CancellationToken ct)
        {
            var cmdLine = (text ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(cmdLine))
            {
                await ReplyAsync(session, NickServMessages.HelpIntro, ct);
                return;
            }

            var parts = cmdLine.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var cmd = parts.Length > 0 ? parts[0].ToUpperInvariant() : "HELP";
            var args = parts.Skip(1).ToArray();

            switch (cmd)
            {
                case "HELP":
                    await HelpAsync(session, args, ct);
                    return;

                case "REGISTER":
                    await RegisterAsync(session, args, state, ct);
                    return;

                case "CONFIRM":
                    await ConfirmAsync(session, args, ct);
                    return;

                case "IDENTIFY":
                case "ID":
                    await IdentifyAsync(session, args, state, ct);
                    return;

                case "LOGOUT":
                    await _auth.ClearAsync(session.ConnectionId, ct);
                    await ReplyAsync(session, "You are now logged out.", ct);
                    return;

                case "INFO":
                    await InfoAsync(session, args, state, ct);
                    return;

                case "LIST":
                    await ListAsync(session, args, ct);
                    return;

                case "ACCESS":
                    await AccessAsync(session, args, state, ct);
                    return;

                case "GROUP":
                    await GroupAsync(session, args, ct);
                    return;

                case "LINK":
                    await GroupAsync(session, args, ct);
                    return;

                case "LINKS":
                    await LinksAsync(session, args, ct);
                    return;

                case "UNGROUP":
                    await UngroupAsync(session, args, ct);
                    return;

                case "UNLINK":
                    await UngroupAsync(session, args, ct);
                    return;

                case "LISTCHANS":
                    await ListChansAsync(session, args, ct);
                    return;

                case "GHOST":
                    await GhostAsync(session, args, state, ct);
                    return;

                case "RECOVER":
                    await RecoverAsync(session, args, state, ct);
                    return;

                case "RELEASE":
                    await ReleaseAsync(session, args, ct);
                    return;

                case "STATUS":
                    await StatusAsync(session, args, state, ct);
                    return;

                case "ACC":
                    await AccAsync(session, args, state, ct);
                    return;

                case "SET":
                    await SetAsync(session, args, state, ct);
                    return;

                case "DROP":
                    await DropAccountAsync(session, args, ct);
                    return;

                default:
                    await ReplyAsync(session, "Unknown command. Try: HELP", ct);
                    return;
            }
        }

        private async ValueTask HelpAsync(IClientSession session, string[] args, CancellationToken ct)
        {
            if (args.Length == 0)
            {
                await ReplyAsync(session, NickServMessages.HelpIntro, ct);
                await ReplyAsync(session, NickServMessages.HelpRegister, ct);
                await ReplyAsync(session, NickServMessages.HelpRegister2, ct);
                await ReplyAsync(session, NickServMessages.HelpConfirm, ct);
                await ReplyAsync(session, NickServMessages.HelpIdentify, ct);
                await ReplyAsync(session, NickServMessages.HelpIdentify2, ct);
                await ReplyAsync(session, NickServMessages.HelpLogout, ct);
                await ReplyAsync(session, NickServMessages.HelpInfo, ct);
                await ReplyAsync(session, NickServMessages.HelpList, ct);
                await ReplyAsync(session, NickServMessages.HelpAccess, ct);
                await ReplyAsync(session, NickServMessages.HelpSetPassword, ct);
                await ReplyAsync(session, NickServMessages.HelpSetEnforce, ct);
                await ReplyAsync(session, NickServMessages.HelpSetKill, ct);
                await ReplyAsync(session, NickServMessages.HelpSetEmail, ct);
                await ReplyAsync(session, NickServMessages.HelpSetHideEmail, ct);
                await ReplyAsync(session, NickServMessages.HelpSetSecure, ct);
                await ReplyAsync(session, NickServMessages.HelpSetAllowMemos, ct);
                await ReplyAsync(session, NickServMessages.HelpSetMemoNotify, ct);
                await ReplyAsync(session, NickServMessages.HelpSetMemoSignon, ct);
                await ReplyAsync(session, NickServMessages.HelpSetNoLink, ct);
                await ReplyAsync(session, NickServMessages.HelpGroup, ct);
                await ReplyAsync(session, NickServMessages.HelpLink, ct);
                await ReplyAsync(session, NickServMessages.HelpLinks, ct);
                await ReplyAsync(session, NickServMessages.HelpUngroup, ct);
                await ReplyAsync(session, NickServMessages.HelpUnlink, ct);
                await ReplyAsync(session, NickServMessages.HelpListChans, ct);
                await ReplyAsync(session, NickServMessages.HelpGhost, ct);
                await ReplyAsync(session, NickServMessages.HelpRecover, ct);
                await ReplyAsync(session, NickServMessages.HelpRelease, ct);
                await ReplyAsync(session, NickServMessages.HelpStatus, ct);
                await ReplyAsync(session, NickServMessages.HelpAcc, ct);
                await ReplyAsync(session, NickServMessages.HelpDrop, ct);
                return;
            }

            var sub = args[0].ToUpperInvariant();
            var line = sub switch
            {
                "REGISTER" => NickServMessages.HelpRegister,
                "CONFIRM" => NickServMessages.HelpConfirm,
                "IDENTIFY" or "ID" => NickServMessages.HelpIdentify,
                "LOGOUT" => NickServMessages.HelpLogout,
                "INFO" => NickServMessages.HelpInfo,
                "LIST" => NickServMessages.HelpList,
                "ACCESS" => NickServMessages.HelpAccess,
                "SET" => NickServMessages.HelpSetPassword,
                "GROUP" => NickServMessages.HelpGroup,
                "LINK" => NickServMessages.HelpLink,
                "LINKS" => NickServMessages.HelpLinks,
                "UNGROUP" => NickServMessages.HelpUngroup,
                "UNLINK" => NickServMessages.HelpUnlink,
                "LISTCHANS" => NickServMessages.HelpListChans,
                "GHOST" => NickServMessages.HelpGhost,
                "RECOVER" => NickServMessages.HelpRecover,
                "RELEASE" => NickServMessages.HelpRelease,
                "STATUS" => NickServMessages.HelpStatus,
                "ACC" => NickServMessages.HelpAcc,
                "DROP" => NickServMessages.HelpDrop,
                _ => NickServMessages.HelpIntro
            };

            await ReplyAsync(session, line, ct);
        }

        private bool IsDeniedForRegister(IClientSession session, string nickToRegister, ServerState state, out string reason)
        {
            reason = "Denied";

            if (_deny is null)
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(nickToRegister) || string.Equals(nickToRegister, "*", StringComparison.Ordinal))
            {
                return false;
            }

            var userName = session.UserName ?? "u";
            var host = state.GetHostFor(session.ConnectionId);
            return _deny.TryMatch(nickToRegister.Trim(), userName, host, out reason);
        }

        private async ValueTask ListAsync(IClientSession session, string[] args, CancellationToken ct)
        {
            var mask = args.Length > 0 && !string.IsNullOrWhiteSpace(args[0]) ? args[0].Trim() : "*";

            var matches = _repo.All()
                .Where(a => a is not null && a.IsConfirmed && !string.IsNullOrWhiteSpace(a.Name) && MaskMatcher.IsMatch(mask, a.Name))
                .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
                .Take(50)
                .Select(a => a.Name)
                .ToList();

            if (matches.Count == 0)
            {
                await ReplyAsync(session, "No matches.", ct);
                return;
            }

            foreach (var name in matches)
            {
                await ReplyAsync(session, name, ct);
            }
        }

        private async ValueTask AccessAsync(IClientSession session, string[] args, ServerState state, CancellationToken ct)
        {
            _ = state;

            if (args.Length == 0)
            {
                await AccessListAsync(session, new[] { "LIST" }, ct);
                return;
            }

            var sub = args[0].ToUpperInvariant();

            if (sub is not ("ADD" or "DEL" or "DELETE" or "LIST" or "CLEAR"))
            {
                await AccAsync(session, args, state, ct);
                return;
            }

            await AccessListAsync(session, args, ct);
        }

        private async ValueTask AccessListAsync(IClientSession session, string[] args, CancellationToken ct)
        {
            var identified = await RequireIdentifiedAccountAsync(session, ct);
            if (identified is null)
            {
                return;
            }

            var account = await _repo.GetByNameAsync(identified, ct);
            if (account is null)
            {
                await ReplyAsync(session, "Your account is not registered.", ct);
                return;
            }

            var (master, masterName) = await ResolveMasterAsync(account, ct);

            var sub = args[0].ToUpperInvariant();
            switch (sub)
            {
                case "LIST":
                    if (args.Length >= 2 && !string.IsNullOrWhiteSpace(args[1]) && !string.Equals(args[1].Trim(), masterName, StringComparison.OrdinalIgnoreCase))
                    {
                        await ReplyAsync(session, "You may only view your own access list.", ct);
                        return;
                    }

                    if (master.AccessMasks.Count == 0)
                    {
                        await ReplyAsync(session, "Access list is empty.", ct);
                        return;
                    }

                    for (var i = 0; i < master.AccessMasks.Count; i++)
                    {
                        await ReplyAsync(session, $"{i + 1}. {master.AccessMasks[i]}", ct);
                    }

                    return;

                case "ADD":
                    if (args.Length < 2 || string.IsNullOrWhiteSpace(args[1]))
                    {
                        await ReplyAsync(session, "Syntax: ACCESS ADD <mask>", ct);
                        return;
                    }

                    {
                        var mask = args[1].Trim();
                        if (mask.Length > 128)
                        {
                            await ReplyAsync(session, "Mask too long.", ct);
                            return;
                        }

                        var list = master.AccessMasks.ToList();
                        if (list.Any(m => string.Equals(m, mask, StringComparison.OrdinalIgnoreCase)))
                        {
                            await ReplyAsync(session, "That mask is already on your access list.", ct);
                            return;
                        }

                        if (list.Count >= 50)
                        {
                            await ReplyAsync(session, "Access list is full.", ct);
                            return;
                        }

                        list.Add(mask);
                        var ok = await _repo.TryUpdateAsync(master with { AccessMasks = list }, ct);
                        await ReplyAsync(session, ok ? "Mask added." : "ACCESS ADD failed.", ct);
                        return;
                    }

                case "DEL":
                case "DELETE":
                    if (args.Length < 2 || string.IsNullOrWhiteSpace(args[1]))
                    {
                        await ReplyAsync(session, "Syntax: ACCESS DEL <mask|number>", ct);
                        return;
                    }

                    {
                        var token = args[1].Trim();
                        var list = master.AccessMasks.ToList();
                        var removed = false;

                        if (int.TryParse(token, out var index))
                        {
                            var i = index - 1;
                            if (i >= 0 && i < list.Count)
                            {
                                list.RemoveAt(i);
                                removed = true;
                            }
                        }
                        else
                        {
                            var match = list.FirstOrDefault(m => string.Equals(m, token, StringComparison.OrdinalIgnoreCase));
                            if (match is not null)
                            {
                                list.Remove(match);
                                removed = true;
                            }
                        }

                        if (!removed)
                        {
                            await ReplyAsync(session, "No such mask.", ct);
                            return;
                        }

                        var ok = await _repo.TryUpdateAsync(master with { AccessMasks = list }, ct);
                        await ReplyAsync(session, ok ? "Mask removed." : "ACCESS DEL failed.", ct);
                        return;
                    }

                case "CLEAR":
                    {
                        var ok = await _repo.TryUpdateAsync(master with { AccessMasks = Array.Empty<string>() }, ct);
                        await ReplyAsync(session, ok ? "Access list cleared." : "ACCESS CLEAR failed.", ct);
                        return;
                    }

                default:
                    await ReplyAsync(session, "Syntax: ACCESS ADD|DEL|LIST|CLEAR", ct);
                    return;
            }
        }

        private async ValueTask LinksAsync(IClientSession session, string[] args, CancellationToken ct)
        {
            string? account;

            if (args.Length > 0 && !string.IsNullOrWhiteSpace(args[0]))
            {
                account = args[0].Trim();
            }
            else
            {
                account = await RequireIdentifiedAccountAsync(session, ct);
            }

            if (string.IsNullOrWhiteSpace(account) || string.Equals(account, "*", StringComparison.Ordinal))
            {
                await ReplyAsync(session, "Syntax: LINKS [account]", ct);
                return;
            }

            var linked = _repo.All()
                .Where(a => a is not null && a.IsConfirmed && !string.IsNullOrWhiteSpace(a.GroupedToAccount) && string.Equals(a.GroupedToAccount, account, StringComparison.OrdinalIgnoreCase))
                .Select(a => a.Name)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (linked.Count == 0)
            {
                await ReplyAsync(session, "No linked nicks.", ct);
                return;
            }

            await ReplyAsync(session, $"Linked nicks for '{account}':", ct);
            foreach (var nick in linked)
            {
                await ReplyAsync(session, nick, ct);
            }
        }

        private async ValueTask ConfirmAsync(IClientSession session, string[] args, CancellationToken ct)
        {
            string? targetNick;
            string code;

            if (args.Length >= 2)
            {
                targetNick = args[0].Trim();
                code = args[1].Trim();
            }
            else
            {
                targetNick = session.Nick;
                if (string.IsNullOrWhiteSpace(targetNick) || string.Equals(targetNick, "*", StringComparison.Ordinal))
                {
                    await ReplyAsync(session, "You must be using a nickname.", ct);
                    return;
                }

                if (args.Length < 1 || string.IsNullOrWhiteSpace(args[0]))
                {
                    await ReplyAsync(session, "Syntax: CONFIRM <code> or CONFIRM <nick> <code>", ct);
                    return;
                }

                code = args[0].Trim();
            }

            if (string.IsNullOrWhiteSpace(targetNick) || string.Equals(targetNick, "*", StringComparison.Ordinal))
            {
                await ReplyAsync(session, "Syntax: CONFIRM <code> or CONFIRM <nick> <code>", ct);
                return;
            }

            var account = await _repo.GetByNameAsync(targetNick, ct);
            if (account is null)
            {
                await ReplyNotRegisteredThrottledAsync(session, ct);
                return;
            }

            if (!account.IsConfirmed)
            {
                await ReplyAsync(session, "This nickname has a pending registration. Please CONFIRM it first.", ct);
                return;
            }

            if (account.IsConfirmed)
            {
                await ReplyAsync(session, "This nickname is already confirmed.", ct);
                return;
            }

            var expires = account.PendingConfirmationExpiresAtUtc;
            if (expires is null || expires.Value <= DateTimeOffset.UtcNow)
            {
                await ReplyAsync(session, "Confirmation code expired. Please REGISTER again.", ct);
                return;
            }

            if (!ConfirmationCode.EqualsHash(account.PendingConfirmationCodeHash, code))
            {
                await ReplyAsync(session, "Invalid confirmation code.", ct);
                return;
            }

            var confirmed = account with
            {
                IsConfirmed = true,
                PendingConfirmationCodeHash = null,
                PendingConfirmationExpiresAtUtc = null,
                PendingRegisteredAtUtc = null,
                RegisteredAtUtc = DateTimeOffset.UtcNow,
            };

            var ok = await _repo.TryUpdateAsync(confirmed, ct);
            if (!ok)
            {
                await ReplyAsync(session, "Confirmation failed.", ct);
                return;
            }

            await _auth.SetIdentifiedAccountAsync(session.ConnectionId, confirmed.Name, ct);
            await ReplyAsync(session, $"Nickname '{confirmed.Name}' confirmed and identified.", ct);
        }

        private async ValueTask DropAccountAsync(IClientSession session, string[] args, CancellationToken ct)
        {
            var nick = session.Nick;
            if (string.IsNullOrWhiteSpace(nick) || string.Equals(nick, "*", StringComparison.Ordinal))
            {
                await ReplyAsync(session, "You must be registered with a nickname.", ct);
                return;
            }

            if (args.Length < 1 || string.IsNullOrWhiteSpace(args[0]))
            {
                await ReplyAsync(session, "Syntax: DROP <password>", ct);
                return;
            }

            var account = await _repo.GetByNameAsync(nick, ct);
            if (account is null)
            {
                await ReplyNotRegisteredThrottledAsync(session, ct);
                return;
            }

            var (master, masterName) = await ResolveMasterAsync(account, ct);
            var identified = await _auth.GetIdentifiedAccountAsync(session.ConnectionId, ct);
            if (identified is null || !string.Equals(identified, masterName, StringComparison.OrdinalIgnoreCase))
            {
                await ReplyAsync(session, "You must be identified to drop your account.", ct);
                return;
            }

            if (!PasswordHasher.Verify(args[0], master.PasswordHash))
            {
                await ReplyAsync(session, "Password incorrect.", ct);
                return;
            }

            if (string.Equals(account.Name, masterName, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var a in _repo.All())
                {
                    if (!string.IsNullOrWhiteSpace(a.GroupedToAccount) && string.Equals(a.GroupedToAccount, masterName, StringComparison.OrdinalIgnoreCase))
                    {
                        await _repo.TryUpdateAsync(a with { GroupedToAccount = null }, ct);
                    }
                }
            }

            var ok = await _repo.TryDeleteAsync(account.Name, ct);
            if (!ok)
            {
                await ReplyAsync(session, "Drop failed.", ct);
                return;
            }

            await _auth.ClearAsync(session.ConnectionId, ct);
            await ReplyAsync(session, $"Nickname '{account.Name}' has been dropped.", ct);
        }

        private async ValueTask RegisterAsync(IClientSession session, string[] args, ServerState state, CancellationToken ct)
        {
            string? targetNick;
            string email;
            string password;

            if (args.Length >= 3)
            {
                targetNick = args[0].Trim();
                email = args[1].Trim();
                password = args[2];

                if (string.IsNullOrWhiteSpace(targetNick) || string.Equals(targetNick, "*", StringComparison.Ordinal))
                {
                    await ReplyAsync(session, "Syntax: REGISTER <nick> <email> <password>", ct);
                    return;
                }
            }
            else if (args.Length >= 2)
            {
                targetNick = session.Nick;
                if (string.IsNullOrWhiteSpace(targetNick) || string.Equals(targetNick, "*", StringComparison.Ordinal))
                {
                    await ReplyAsync(session, "You must be using a nickname.", ct);
                    return;
                }

                email = args[0].Trim();
                password = args[1];
            }
            else
            {
                await ReplyAsync(session, "Syntax: REGISTER <email> <password> or REGISTER <nick> <email> <password>", ct);
                return;
            }

            if (!string.IsNullOrWhiteSpace(targetNick) && IsDeniedForRegister(session, targetNick, state, out var denyReason))
            {
                await ReplyAsync(session, $"Registration denied ({denyReason}).", ct);
                return;
            }

            if (string.IsNullOrWhiteSpace(email) || !email.Contains('@'))
            {
                await ReplyAsync(session, "Invalid email address.", ct);
                return;
            }

            var cfg = _options.Value.Services?.NickServ ?? new NickServOptions();
            var requireConfirm = cfg.RequireEmailConfirmation;

            var existing = await _repo.GetByNameAsync(targetNick, ct);
            if (existing is not null)
            {
                if (existing.IsConfirmed)
                {
                    await ReplyAsync(session, "This nickname is already registered.", ct);
                    return;
                }

                var expires = existing.PendingConfirmationExpiresAtUtc;
                if (expires is not null && expires.Value > DateTimeOffset.UtcNow)
                {
                    await ReplyAsync(session, "This nickname has a pending registration. Check your email and use CONFIRM to complete it.", ct);
                    return;
                }

                await ReplyAsync(session, "This nickname has an expired pending registration. Please try REGISTER again.", ct);
                return;
            }

            if (requireConfirm)
            {
                if (!_email.IsConfigured)
                {
                    await ReplyAsync(session, "NickServ email confirmation is enabled but SMTP is not configured.", ct);
                    return;
                }

                var code = ConfirmationCode.Generate();
                var expiresAt = DateTimeOffset.UtcNow.AddHours(Math.Max(1, cfg.PendingRegistrationExpiryHours));

                var pending = new NickAccount
                {
                    Name = targetNick,
                    PasswordHash = PasswordHasher.Hash(password),
                    Email = email,
                    HideEmail = false,
                    IsConfirmed = false,
                    PendingConfirmationCodeHash = ConfirmationCode.Hash(code),
                    PendingConfirmationExpiresAtUtc = expiresAt,
                    PendingRegisteredAtUtc = DateTimeOffset.UtcNow,
                    RegisteredAtUtc = DateTimeOffset.UtcNow,
                };

                var okPending = await _repo.TryCreateAsync(pending, ct);
                if (!okPending)
                {
                    await ReplyAsync(session, "Registration failed.", ct);
                    return;
                }

                var network = _options.Value.ServerInfo?.Network;
                if (string.IsNullOrWhiteSpace(network))
                    network = _options.Value.ServerInfo?.Name ?? "IRC";

                var subject = "UnrealBG Nickname Registration";

                var expiryText = FormatExpiryText(cfg.PendingRegistrationExpiryHours);

                var issuedByNick = session.Nick ?? targetNick;
                var issuedByUser = session.UserName;
                if (string.IsNullOrWhiteSpace(issuedByUser))
                    issuedByUser = "unknown";

                var issuedByHost = session.RemoteEndPoint is System.Net.IPEndPoint ip
                    ? ip.Address.ToString()
                    : session.RemoteEndPoint.ToString();

                var issuedBy = $"{issuedByNick}!~{issuedByUser}@{issuedByHost}";

                var body =
$"Hello, {targetNick}!\n\n" +
$"Someone (probably you) has registered this nickname on {network} IRC Network.\n\n" +
$"Nickname password: {password}\n\n" +
$"Please remember your password since it is encrypted in our database and we cannot retrieve it for you.\n" +
$"If you happen to forget your password, use the SENDPASS command to get a new one.\n\n" +
$"To confirm the registration, please enter the following command on IRC:\n\n" +
$"/NickServ CONFIRM {targetNick} {code}\n\n" +
$"The confirmation code will expire in {expiryText} .\n" +
$"Do NOT reply to this email since it is generated automatically.\n\n" +
$"You receive this message due to the command issued by: {issuedBy}\n";

                await _email.SendAsync(email, subject, body, ct);

                await ReplyAsync(session, $"Registration pending for '{targetNick}'. A confirmation email has been sent to {email}.", ct);
                await ReplyAsync(session, "After you receive it, use: CONFIRM <nick> <code>", ct);
                return;
            }

            var account = new NickAccount
            {
                Name = targetNick,
                PasswordHash = PasswordHasher.Hash(password),
                Email = email,
                HideEmail = false
            };

            var ok = await _repo.TryCreateAsync(account, ct);
            if (!ok)
            {
                await ReplyAsync(session, "Registration failed.", ct);
                return;
            }

            await _auth.SetIdentifiedAccountAsync(session.ConnectionId, targetNick, ct);
            await ReplyAsync(session, $"Nickname '{targetNick}' registered and identified.", ct);
        }

        private static string FormatExpiryText(int pendingRegistrationExpiryHours)
        {
            var hours = pendingRegistrationExpiryHours;
            if (hours <= 0)
                hours = 24;

            if (hours % 24 == 0)
            {
                var days = hours / 24;
                if (days <= 1)
                    return "1 day";
                return $"{days} days";
            }

            if (hours == 1)
                return "1 hour";
            return $"{hours} hours";
        }

        private async ValueTask IdentifyAsync(IClientSession session, string[] args, ServerState state, CancellationToken ct)
        {
            string? targetNick;
            string? password;

            if (args.Length >= 2)
            {
                targetNick = args[0].Trim();
                password = args[1];

                if (string.IsNullOrWhiteSpace(targetNick))
                {
                    await ReplyAsync(session, "Syntax: IDENTIFY <nick> <password>", ct);
                    return;
                }
            }
            else
            {
                targetNick = session.Nick;
                if (string.IsNullOrWhiteSpace(targetNick) || string.Equals(targetNick, "*", StringComparison.Ordinal))
                {
                    await ReplyAsync(session, "You must be using a nickname.", ct);
                    return;
                }

                password = args.Length >= 1 && !string.IsNullOrWhiteSpace(args[0]) ? args[0] : null;
            }

            var account = await _repo.GetByNameAsync(targetNick, ct);
            if (account is null)
            {
                await ReplyNotRegisteredThrottledAsync(session, ct);
                return;
            }

            var (master, masterName) = await ResolveMasterAsync(account, ct);

            if (master.Secure && !session.IsSecureConnection)
            {
                await ReplyAsync(session, "SECURE is enabled for this account. You must IDENTIFY over a secure connection.", ct);
                return;
            }

            if (string.IsNullOrEmpty(password))
            {
                if (!TryGetSessionHostmask(session, state, out var sessionHostmask))
                {
                    await ReplyAsync(session, "Syntax: IDENTIFY <password>", ct);
                    return;
                }

                if (!IsOnAccessList(master, sessionHostmask))
                {
                    await ReplyAsync(session, "Syntax: IDENTIFY <password>", ct);
                    return;
                }
            }
            else
            {
                if (!PasswordHasher.Verify(password, master.PasswordHash))
                {
                    await ReplyAsync(session, "Password incorrect.", ct);
                    return;
                }
            }

            await _auth.SetIdentifiedAccountAsync(session.ConnectionId, masterName, ct);
            await ReplyAsync(session, $"You are now identified for '{masterName}'.", ct);

            if (master.MemoSignon)
            {
                var unread = await _memos.GetUnreadCountAsync(masterName, ct);
                if (unread > 0)
                {
                    var server = _options.Value.ServerInfo?.Name ?? "server";
                    var to = session.Nick ?? "*";
                    var plural = unread == 1 ? "memo" : "memos";
                    var line = $":{MemoServMessages.ServiceName}!services@{server} NOTICE {to} :You have {unread} new {plural}. Use: /msg {MemoServMessages.ServiceName} LIST";
                    await session.SendAsync(line, ct);
                }
            }
        }

        private static bool TryGetSessionHostmask(IClientSession session, ServerState state, out string hostmask)
        {
            hostmask = string.Empty;
            if (!state.TryGetUser(session.ConnectionId, out var user) || user is null)
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(user.Nick) || string.IsNullOrWhiteSpace(user.UserName) || string.IsNullOrWhiteSpace(user.Host))
            {
                return false;
            }

            hostmask = $"{user.Nick}!{user.UserName}@{user.Host}";
            return true;
        }

        private static bool TryGetUserHostmask(ServerState state, string connectionId, out string hostmask)
        {
            hostmask = string.Empty;
            if (!state.TryGetUser(connectionId, out var user) || user is null)
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(user.Nick) || string.IsNullOrWhiteSpace(user.UserName) || string.IsNullOrWhiteSpace(user.Host))
            {
                return false;
            }

            hostmask = $"{user.Nick}!{user.UserName}@{user.Host}";
            return true;
        }

        private static bool IsOnAccessList(NickAccount master, string sessionHostmask)
        {
            if (master.AccessMasks is null || master.AccessMasks.Count == 0)
            {
                return false;
            }

            var bang = sessionHostmask.IndexOf('!');
            var at = sessionHostmask.IndexOf('@');
            var userAtHost = (bang >= 0 && at > bang) ? sessionHostmask[(bang + 1)..] : sessionHostmask;
            var hostOnly = (at >= 0 && at + 1 < sessionHostmask.Length) ? sessionHostmask[(at + 1)..] : sessionHostmask;

            foreach (var mask in master.AccessMasks)
            {
                if (string.IsNullOrWhiteSpace(mask))
                {
                    continue;
                }

                if (MaskMatcher.IsMatch(mask, sessionHostmask) || MaskMatcher.IsMatch(mask, userAtHost) || MaskMatcher.IsMatch(mask, hostOnly))
                {
                    return true;
                }
            }

            return false;
        }

        private async ValueTask SetAsync(IClientSession session, string[] args, ServerState state, CancellationToken ct)
        {
            _ = state;

            if (args.Length < 1)
            {
                await ReplyAsync(session, "Syntax: SET PASSWORD <newpassword> | SET ENFORCE ON|OFF | SET KILL ON|OFF | SET EMAIL <email>|NONE | SET HIDEMAIL ON|OFF | SET SECURE ON|OFF | SET ALLOWMEMOS ON|OFF | SET MEMONOTIFY ON|OFF | SET MEMOSIGNON ON|OFF | SET NOLINK ON|OFF", ct);
                return;
            }

            var identified = await RequireIdentifiedAccountAsync(session, ct);
            if (identified is null)
            {
                return;
            }

            var account = await _repo.GetByNameAsync(identified, ct);
            if (account is null)
            {
                await ReplyAsync(session, "Your account is not registered.", ct);
                return;
            }

            var sub = args[0].ToUpperInvariant();
            switch (sub)
            {
                case "PASSWORD":
                    if (args.Length < 2 || string.IsNullOrWhiteSpace(args[1]))
                    {
                        await ReplyAsync(session, "Syntax: SET PASSWORD <newpassword>", ct);
                        return;
                    }

                    {
                        var ok = await _repo.TryUpdatePasswordHashAsync(account.Name, PasswordHasher.Hash(args[1]), ct);
                        await ReplyAsync(session, ok ? "Password updated." : "Password update failed.", ct);
                        return;
                    }

                case "ENFORCE":
                    if (args.Length < 2)
                    {
                        await ReplyAsync(session, "Syntax: SET ENFORCE ON|OFF", ct);
                        return;
                    }

                    {
                        var on = args[1].Equals("ON", StringComparison.OrdinalIgnoreCase) || args[1].Equals("YES", StringComparison.OrdinalIgnoreCase) || args[1].Equals("TRUE", StringComparison.OrdinalIgnoreCase);
                        var ok = await _repo.TryUpdateAsync(account with { Enforce = on }, ct);
                        await ReplyAsync(session, ok ? $"ENFORCE is now {(on ? "ON" : "OFF")}." : "SET ENFORCE failed.", ct);
                        return;
                    }

                case "KILL":
                    if (args.Length < 2)
                    {
                        await ReplyAsync(session, "Syntax: SET KILL ON|OFF", ct);
                        return;
                    }

                    {
                        var on = args[1].Equals("ON", StringComparison.OrdinalIgnoreCase) || args[1].Equals("YES", StringComparison.OrdinalIgnoreCase) || args[1].Equals("TRUE", StringComparison.OrdinalIgnoreCase);
                        var ok = await _repo.TryUpdateAsync(account with { Kill = on }, ct);
                        await ReplyAsync(
                            session,
                            ok
                                ? (on
                                    ? "KILL protection is now ON. Unidentified users will be disconnected after 30 seconds."
                                    : "KILL protection is now OFF.")
                                : "SET KILL failed.",
                            ct);
                        return;
                    }

                case "EMAIL":
                    if (args.Length < 2)
                    {
                        await ReplyAsync(session, "Syntax: SET EMAIL <email>|NONE", ct);
                        return;
                    }

                    {
                        var value = args[1].Trim();
                        var email = value.Equals("NONE", StringComparison.OrdinalIgnoreCase) ? null : value;
                        var ok = await _repo.TryUpdateAsync(account with { Email = string.IsNullOrWhiteSpace(email) ? null : email }, ct);
                        await ReplyAsync(session, ok ? "Email updated." : "SET EMAIL failed.", ct);
                        return;
                    }

                case "HIDEMAIL":
                    if (args.Length < 2)
                    {
                        await ReplyAsync(session, "Syntax: SET HIDEMAIL ON|OFF", ct);
                        return;
                    }

                    {
                        var on = args[1].Equals("ON", StringComparison.OrdinalIgnoreCase) || args[1].Equals("YES", StringComparison.OrdinalIgnoreCase) || args[1].Equals("TRUE", StringComparison.OrdinalIgnoreCase);
                        var ok = await _repo.TryUpdateAsync(account with { HideEmail = on }, ct);
                        await ReplyAsync(session, ok ? $"HIDEMAIL is now {(on ? "ON" : "OFF")}." : "SET HIDEMAIL failed.", ct);
                        return;
                    }

                case "SECURE":
                    if (args.Length < 2)
                    {
                        await ReplyAsync(session, "Syntax: SET SECURE ON|OFF", ct);
                        return;
                    }

                    {
                        var on = args[1].Equals("ON", StringComparison.OrdinalIgnoreCase) || args[1].Equals("YES", StringComparison.OrdinalIgnoreCase) || args[1].Equals("TRUE", StringComparison.OrdinalIgnoreCase);
                        var ok = await _repo.TryUpdateAsync(account with { Secure = on }, ct);
                        await ReplyAsync(session, ok ? $"SECURE is now {(on ? "ON" : "OFF")}." : "SET SECURE failed.", ct);
                        return;
                    }

                case "ALLOWMEMOS":
                    if (args.Length < 2)
                    {
                        await ReplyAsync(session, "Syntax: SET ALLOWMEMOS ON|OFF", ct);
                        return;
                    }

                    {
                        var on = args[1].Equals("ON", StringComparison.OrdinalIgnoreCase) || args[1].Equals("YES", StringComparison.OrdinalIgnoreCase) || args[1].Equals("TRUE", StringComparison.OrdinalIgnoreCase);
                        var ok = await _repo.TryUpdateAsync(account with { AllowMemos = on }, ct);
                        await ReplyAsync(session, ok ? $"ALLOWMEMOS is now {(on ? "ON" : "OFF")}." : "SET ALLOWMEMOS failed.", ct);
                        return;
                    }

                case "MEMONOTIFY":
                    if (args.Length < 2)
                    {
                        await ReplyAsync(session, "Syntax: SET MEMONOTIFY ON|OFF", ct);
                        return;
                    }

                    {
                        var on = args[1].Equals("ON", StringComparison.OrdinalIgnoreCase) || args[1].Equals("YES", StringComparison.OrdinalIgnoreCase) || args[1].Equals("TRUE", StringComparison.OrdinalIgnoreCase);
                        var ok = await _repo.TryUpdateAsync(account with { MemoNotify = on }, ct);
                        await ReplyAsync(session, ok ? $"MEMONOTIFY is now {(on ? "ON" : "OFF")}." : "SET MEMONOTIFY failed.", ct);
                        return;
                    }

                case "MEMOSIGNON":
                    if (args.Length < 2)
                    {
                        await ReplyAsync(session, "Syntax: SET MEMOSIGNON ON|OFF", ct);
                        return;
                    }

                    {
                        var on = args[1].Equals("ON", StringComparison.OrdinalIgnoreCase) || args[1].Equals("YES", StringComparison.OrdinalIgnoreCase) || args[1].Equals("TRUE", StringComparison.OrdinalIgnoreCase);
                        var ok = await _repo.TryUpdateAsync(account with { MemoSignon = on }, ct);
                        await ReplyAsync(session, ok ? $"MEMOSIGNON is now {(on ? "ON" : "OFF")}." : "SET MEMOSIGNON failed.", ct);
                        return;
                    }

                case "NOLINK":
                    if (args.Length < 2)
                    {
                        await ReplyAsync(session, "Syntax: SET NOLINK ON|OFF", ct);
                        return;
                    }

                    {
                        var on = args[1].Equals("ON", StringComparison.OrdinalIgnoreCase) || args[1].Equals("YES", StringComparison.OrdinalIgnoreCase) || args[1].Equals("TRUE", StringComparison.OrdinalIgnoreCase);
                        var ok = await _repo.TryUpdateAsync(account with { NoLink = on }, ct);
                        await ReplyAsync(session, ok ? $"NOLINK is now {(on ? "ON" : "OFF")}." : "SET NOLINK failed.", ct);
                        return;
                    }

                default:
                    await ReplyAsync(session, "Syntax: SET PASSWORD <newpassword> | SET ENFORCE ON|OFF | SET EMAIL <email>|NONE | SET HIDEMAIL ON|OFF | SET KILL ON|OFF | SET SECURE ON|OFF | SET ALLOWMEMOS ON|OFF | SET MEMONOTIFY ON|OFF | SET MEMOSIGNON ON|OFF | SET NOLINK ON|OFF", ct);
                    return;
            }
        }

        private async ValueTask GhostAsync(IClientSession session, string[] args, ServerState state, CancellationToken ct)
        {
            if (args.Length < 2)
            {
                await ReplyAsync(session, "Syntax: GHOST <nick> <password>", ct);
                return;
            }

            var targetNick = args[0].Trim();
            var password = args[1];

            if (string.IsNullOrWhiteSpace(targetNick))
            {
                await ReplyAsync(session, "Syntax: GHOST <nick> <password>", ct);
                return;
            }

            var account = await _repo.GetByNameAsync(targetNick, ct);
            if (account is null)
            {
                await ReplyAsync(session, "That nickname is not registered.", ct);
                return;
            }

            var (master, _) = await ResolveMasterAsync(account, ct);

            if (!PasswordHasher.Verify(password, master.PasswordHash))
            {
                await ReplyAsync(session, "Password incorrect.", ct);
                return;
            }

            if (!state.TryGetConnectionIdByNick(targetNick, out var targetConn) || targetConn is null)
            {
                await ReplyAsync(session, "That nickname is not in use.", ct);
                return;
            }

            if (state.TryGetUser(targetConn, out var u) && u is not null && u.IsRemote)
            {
                await ReplyAsync(session, "GHOST for remote users is not supported.", ct);
                return;
            }

            if (_sessions is null)
            {
                await ReplyAsync(session, "GHOST is not available on this server.", ct);
                return;
            }

            if (!_sessions.TryGet(targetConn, out var targetSess) || targetSess is null)
            {
                await ReplyAsync(session, "Unable to ghost that user.", ct);
                return;
            }

            if (string.Equals(targetConn, session.ConnectionId, StringComparison.Ordinal))
            {
                await ReplyAsync(session, "You cannot GHOST yourself.", ct);
                return;
            }

            try
            {
                await targetSess.CloseAsync("GHOSTed by NickServ", ct);
            }
            catch
            {
                // ignore
            }

            await ReplyAsync(session, $"Ghosted '{targetNick}'.", ct);
        }

        private async ValueTask RecoverAsync(IClientSession session, string[] args, ServerState state, CancellationToken ct)
        {
            if (args.Length < 2)
            {
                await ReplyAsync(session, "Syntax: RECOVER <nick> <password>", ct);
                return;
            }

            var targetNick = args[0].Trim();
            var password = args[1];

            if (string.IsNullOrWhiteSpace(targetNick))
            {
                await ReplyAsync(session, "Syntax: RECOVER <nick> <password>", ct);
                return;
            }

            var account = await _repo.GetByNameAsync(targetNick, ct);
            if (account is null)
            {
                await ReplyAsync(session, "That nickname is not registered.", ct);
                return;
            }

            var (master, masterName) = await ResolveMasterAsync(account, ct);

            if (!PasswordHasher.Verify(password, master.PasswordHash))
            {
                await ReplyAsync(session, "Password incorrect.", ct);
                return;
            }

            await _auth.SetIdentifiedAccountAsync(session.ConnectionId, masterName, ct);

            if (!state.TryGetConnectionIdByNick(targetNick, out var targetConn) || targetConn is null)
            {
                await ReplyAsync(session, $"'{targetNick}' is not currently in use. You are now identified for '{masterName}'.", ct);
                return;
            }

            if (string.Equals(targetConn, session.ConnectionId, StringComparison.Ordinal))
            {
                await ReplyAsync(session, "You are already using that nickname.", ct);
                return;
            }

            if (state.TryGetUser(targetConn, out var u) && u is not null && u.IsRemote)
            {
                await ReplyAsync(session, "RECOVER for remote users is not supported.", ct);
                return;
            }

            if (_sessions is null)
            {
                await ReplyAsync(session, "RECOVER is not available on this server.", ct);
                return;
            }

            if (!_sessions.TryGet(targetConn, out var targetSess) || targetSess is null)
            {
                await ReplyAsync(session, "Unable to recover that user.", ct);
                return;
            }

            try
            {
                await targetSess.CloseAsync("Recovered by NickServ", ct);
            }
            catch
            {
                // ignore
            }

            await ReplyAsync(session, $"Recovered '{targetNick}'. You are identified; now change nick to '{targetNick}'.", ct);
        }

        private async ValueTask ReleaseAsync(IClientSession session, string[] args, CancellationToken ct)
        {
            if (args.Length < 2)
            {
                await ReplyAsync(session, "Syntax: RELEASE <nick> <password>", ct);
                return;
            }

            var targetNick = args[0].Trim();
            var password = args[1];

            if (string.IsNullOrWhiteSpace(targetNick))
            {
                await ReplyAsync(session, "Syntax: RELEASE <nick> <password>", ct);
                return;
            }

            var account = await _repo.GetByNameAsync(targetNick, ct);
            if (account is null)
            {
                await ReplyAsync(session, "That nickname is not registered.", ct);
                return;
            }

            var (master, masterName) = await ResolveMasterAsync(account, ct);

            if (!PasswordHasher.Verify(password, master.PasswordHash))
            {
                await ReplyAsync(session, "Password incorrect.", ct);
                return;
            }

            await _auth.SetIdentifiedAccountAsync(session.ConnectionId, masterName, ct);
            await ReplyAsync(session, $"You are now identified for '{masterName}'.", ct);
        }

        private async ValueTask StatusAsync(IClientSession session, string[] args, ServerState state, CancellationToken ct)
        {
            if (args.Length < 1 || string.IsNullOrWhiteSpace(args[0]))
            {
                await ReplyAsync(session, "Syntax: STATUS <nick>", ct);
                return;
            }

            var nick = args[0].Trim();
            if (string.IsNullOrWhiteSpace(nick))
            {
                await ReplyAsync(session, "Syntax: STATUS <nick>", ct);
                return;
            }

            if (!state.TryGetConnectionIdByNick(nick, out var connId) || connId is null)
            {
                await ReplyAsync(session, $"STATUS {nick} 0", ct);
                return;
            }

            var account = await _repo.GetByNameAsync(nick, ct);
            if (account is null)
            {
                await ReplyAsync(session, $"STATUS {nick} 1", ct);
                return;
            }

            var (master, masterName) = await ResolveMasterAsync(account, ct);

            var identified = await _auth.GetIdentifiedAccountAsync(connId, ct);
            var isIdentified = identified is not null && string.Equals(identified, masterName, StringComparison.OrdinalIgnoreCase);

            if (isIdentified)
            {
                await ReplyAsync(session, $"STATUS {nick} 3", ct);
                return;
            }

            if (TryGetUserHostmask(state, connId, out var targetHostmask) && IsOnAccessList(master, targetHostmask))
            {
                await ReplyAsync(session, $"STATUS {nick} 4", ct);
                return;
            }

            await ReplyAsync(session, $"STATUS {nick} 2", ct);
        }

        private async ValueTask AccAsync(IClientSession session, string[] args, ServerState state, CancellationToken ct)
        {
            if (args.Length < 1 || string.IsNullOrWhiteSpace(args[0]))
            {
                await ReplyAsync(session, "Syntax: ACC <nick>", ct);
                return;
            }

            var nick = args[0].Trim();
            if (string.IsNullOrWhiteSpace(nick))
            {
                await ReplyAsync(session, "Syntax: ACC <nick>", ct);
                return;
            }

            if (!state.TryGetConnectionIdByNick(nick, out var connId) || connId is null)
            {
                await ReplyAsync(session, $"ACC {nick} 0", ct);
                return;
            }

            var account = await _repo.GetByNameAsync(nick, ct);
            if (account is null)
            {
                await ReplyAsync(session, $"ACC {nick} 1", ct);
                return;
            }

            var (master, masterName) = await ResolveMasterAsync(account, ct);

            var identified = await _auth.GetIdentifiedAccountAsync(connId, ct);
            var isIdentified = identified is not null && string.Equals(identified, masterName, StringComparison.OrdinalIgnoreCase);

            if (isIdentified)
            {
                await ReplyAsync(session, $"ACC {nick} 3", ct);
                return;
            }

            if (TryGetUserHostmask(state, connId, out var targetHostmask) && IsOnAccessList(master, targetHostmask))
            {
                await ReplyAsync(session, $"ACC {nick} 4", ct);
                return;
            }

            await ReplyAsync(session, $"ACC {nick} 2", ct);
        }

        private async ValueTask InfoAsync(IClientSession session, string[] args, ServerState state, CancellationToken ct)
        {
            var nick = args.Length > 0 && !string.IsNullOrWhiteSpace(args[0]) ? args[0].Trim() : session.Nick;
            if (string.IsNullOrWhiteSpace(nick))
            {
                await ReplyAsync(session, "Syntax: INFO [nick]", ct);
                return;
            }

            var account = await _repo.GetByNameAsync(nick, ct);
            if (account is null)
            {
                await ReplyAsync(session, $"'{nick}' is not registered.", ct);
                return;
            }

            var (master, masterName) = await ResolveMasterAsync(account, ct);

            var identified = await _auth.GetIdentifiedAccountAsync(session.ConnectionId, ct);
            var isMe = identified is not null && string.Equals(identified, masterName, StringComparison.OrdinalIgnoreCase);

            var isOnline = state.TryGetConnectionIdByNick(nick, out var _);
            var onlineText = isOnline ? " << ONLINE >>" : string.Empty;

            await ReplyAsync(session, $"Nickname: {nick}{onlineText}", ct);
            await ReplyAsync(session, $"Registered: {FormatAge(master.RegisteredAtUtc, DateTimeOffset.UtcNow)} ago", ct);

            var expireDays = _options.Value.Services?.NickServ?.AccountExpiryDays ?? 28;
            await ReplyAsync(session, $"Expire Time: {FormatExpiryDays(expireDays)}", ct);

            if (!string.IsNullOrWhiteSpace(master.Email) && (isMe || !master.HideEmail))
            {
                await ReplyAsync(session, $"Email Address: {master.Email}", ct);
            }

            await ReplyAsync(session, $"Nickname Options: {FormatNickOptions(master)}", ct);
        }

        private static string FormatNickOptions(NickAccount account)
        {
            var parts = new System.Collections.Generic.List<string>(8);
            if (account.Secure) parts.Add("Secure");
            if (account.AllowMemos) parts.Add("AllowMemos");
            if (account.MemoNotify) parts.Add("MemoNotify");
            if (account.MemoSignon) parts.Add("MemoSignon");
            if (account.NoLink) parts.Add("NoLink");
            return parts.Count == 0 ? "None" : string.Join(", ", parts);
        }

        private static string FormatExpiryDays(int days)
        {
            if (days <= 0)
            {
                return "Never";
            }

            if (days % 7 == 0)
            {
                var weeks = days / 7;
                return weeks == 1 ? "1 week" : $"{weeks} weeks";
            }

            return days == 1 ? "1 day" : $"{days} days";
        }

        private static string FormatAge(DateTimeOffset thenUtc, DateTimeOffset nowUtc)
        {
            var span = nowUtc - thenUtc;
            if (span < TimeSpan.Zero)
            {
                span = TimeSpan.Zero;
            }

            var days = (int)span.TotalDays;
            var hours = span.Hours;
            var minutes = span.Minutes;
            var seconds = span.Seconds;

            static string Part(int value, string singular, string plural)
                => value == 1 ? $"{value} {singular}" : $"{value} {plural}";

            var parts = new System.Collections.Generic.List<string>(4);
            if (days > 0) parts.Add(Part(days, "day", "days"));
            if (hours > 0) parts.Add(Part(hours, "hour", "hours"));
            if (minutes > 0) parts.Add(Part(minutes, "minute", "minutes"));
            if (seconds > 0 || parts.Count == 0) parts.Add(Part(seconds, "second", "seconds"));

            return string.Join(' ', parts);
        }

        private async ValueTask GroupAsync(IClientSession session, string[] args, CancellationToken ct)
        {
            if (args.Length < 2)
            {
                await ReplyAsync(session, "Syntax: GROUP <nick> <password>", ct);
                return;
            }

            var identified = await RequireIdentifiedAccountAsync(session, ct);
            if (identified is null)
            {
                return;
            }

            var targetNick = args[0].Trim();
            var password = args[1];

            if (string.IsNullOrWhiteSpace(targetNick) || string.Equals(targetNick, "*", StringComparison.Ordinal))
            {
                await ReplyAsync(session, "Syntax: GROUP <nick> <password>", ct);
                return;
            }

            if (string.Equals(targetNick, identified, StringComparison.OrdinalIgnoreCase))
            {
                await ReplyAsync(session, "That nick is already your account name.", ct);
                return;
            }

            var target = await _repo.GetByNameAsync(targetNick, ct);
            if (target is null)
            {
                await ReplyAsync(session, "That nickname is not registered.", ct);
                return;
            }

            if (!string.IsNullOrWhiteSpace(target.GroupedToAccount))
            {
                await ReplyAsync(session, "That nickname is already grouped.", ct);
                return;
            }

            if (!PasswordHasher.Verify(password, target.PasswordHash))
            {
                await ReplyAsync(session, "Password incorrect.", ct);
                return;
            }

            var ok = await _repo.TryUpdateAsync(target with { GroupedToAccount = identified }, ct);
            await ReplyAsync(session, ok ? $"'{targetNick}' is now grouped to '{identified}'." : "GROUP failed.", ct);
        }

        private async ValueTask UngroupAsync(IClientSession session, string[] args, CancellationToken ct)
        {
            if (args.Length < 1)
            {
                await ReplyAsync(session, "Syntax: UNGROUP <nick>", ct);
                return;
            }

            var identified = await RequireIdentifiedAccountAsync(session, ct);
            if (identified is null)
            {
                return;
            }

            var targetNick = args[0].Trim();
            if (string.IsNullOrWhiteSpace(targetNick) || string.Equals(targetNick, "*", StringComparison.Ordinal))
            {
                await ReplyAsync(session, "Syntax: UNGROUP <nick>", ct);
                return;
            }

            var target = await _repo.GetByNameAsync(targetNick, ct);
            if (target is null)
            {
                await ReplyAsync(session, "That nickname is not registered.", ct);
                return;
            }

            if (string.IsNullOrWhiteSpace(target.GroupedToAccount) || !string.Equals(target.GroupedToAccount, identified, StringComparison.OrdinalIgnoreCase))
            {
                await ReplyAsync(session, "That nickname is not grouped to your account.", ct);
                return;
            }

            var ok = await _repo.TryUpdateAsync(target with { GroupedToAccount = null }, ct);
            await ReplyAsync(session, ok ? $"'{targetNick}' is no longer grouped to '{identified}'." : "UNGROUP failed.", ct);
        }

        private async ValueTask ListChansAsync(IClientSession session, string[] args, CancellationToken ct)
        {
            if (_channels is null)
            {
                await ReplyAsync(session, "LISTCHANS is not available on this server.", ct);
                return;
            }

            string? account = null;
            if (args.Length > 0 && !string.IsNullOrWhiteSpace(args[0]))
            {
                account = args[0].Trim();
            }

            account ??= await RequireIdentifiedAccountAsync(session, ct);
            if (account is null)
            {
                await ReplyAsync(session, "You must be identified to use LISTCHANS.", ct);
                return;
            }

            var any = false;
            foreach (var ch in _channels.All())
            {
                if (string.Equals(ch.FounderAccount, account, StringComparison.OrdinalIgnoreCase))
                {
                    any = true;
                    await ReplyAsync(session, ch.Name, ct);
                }
            }

            if (!any)
            {
                await ReplyAsync(session, "No channels found.", ct);
            }
        }

        private async ValueTask<string?> RequireIdentifiedAccountAsync(IClientSession session, CancellationToken ct)
        {
            var identified = await _auth.GetIdentifiedAccountAsync(session.ConnectionId, ct);
            if (string.IsNullOrWhiteSpace(identified))
            {
                await ReplyAsync(session, "You must be identified to use this command.", ct);
                return null;
            }

            return identified;
        }

        private async ValueTask<(NickAccount Master, string MasterName)> ResolveMasterAsync(NickAccount account, CancellationToken ct)
        {
            var current = account;
            var currentName = account.Name;

            for (var i = 0; i < 5; i++)
            {
                if (string.IsNullOrWhiteSpace(current.GroupedToAccount))
                {
                    return (current, currentName);
                }

                var next = await _repo.GetByNameAsync(current.GroupedToAccount, ct);
                if (next is null)
                {
                    return (current, currentName);
                }

                current = next;
                currentName = next.Name;
            }

            return (current, currentName);
        }

        private ValueTask ReplyAsync(IClientSession session, string text, CancellationToken ct)
        {
            var server = _options.Value.ServerInfo?.Name ?? "server";
            var to = session.Nick ?? "*";
            var line = $":{NickServMessages.ServiceName}!services@{server} NOTICE {to} :{text}";
            return session.SendAsync(line, ct);
        }

        private ValueTask ReplyNotRegisteredThrottledAsync(IClientSession session, CancellationToken ct)
        {
            var conn = session.ConnectionId;
            if (string.IsNullOrWhiteSpace(conn))
            {
                return ReplyAsync(session, "This nickname is not registered.", ct);
            }

            var now = Environment.TickCount64;

            if (_notRegisteredUntilTickByConn.TryGetValue(conn, out var until) && now < until)
            {
                return ValueTask.CompletedTask;
            }

            _notRegisteredUntilTickByConn[conn] = now + NotRegisteredCooldownMs;

            if (_notRegisteredUntilTickByConn.Count > 10_000)
            {
                foreach (var kvp in _notRegisteredUntilTickByConn)
                {
                    if (now >= kvp.Value)
                        _notRegisteredUntilTickByConn.TryRemove(kvp.Key, out _);
                }
            }

            return ReplyAsync(session, "This nickname is not registered.", ct);
        }
    }
}
