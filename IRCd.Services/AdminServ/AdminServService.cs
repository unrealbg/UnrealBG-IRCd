namespace IRCd.Services.AdminServ
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Core.Abstractions;
    using IRCd.Core.Protocol;
    using IRCd.Core.Services;
    using IRCd.Core.State;
    using IRCd.Services.Storage;
    using IRCd.Shared.Options;

    using Microsoft.Extensions.Options;

    public sealed class AdminServService
    {
        private readonly IOptions<IrcOptions> _options;
        private readonly IAdminStaffRepository _staff;
        private readonly IAuthState _auth;

        public AdminServService(IOptions<IrcOptions> options, IAdminStaffRepository staff, IAuthState auth)
        {
            _options = options;
            _staff = staff;
            _auth = auth;
        }

        public async ValueTask HandleAsync(IClientSession session, string text, ServerState state, CancellationToken ct)
        {
            var input = (text ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(input))
            {
                await HelpAsync(session, Array.Empty<string>(), ct);
                return;
            }

            var parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var cmd = parts.Length > 0 ? parts[0].ToUpperInvariant() : "HELP";
            var args = parts.Skip(1).ToArray();

            switch (cmd)
            {
                case "HELP":
                    await HelpAsync(session, args, ct);
                    return;

                case "OPER":
                    await OperAsync(session, args, state, ct);
                    return;

                case "FLAGS":
                    await FlagsAsync(session, args, state, ct);
                    return;

                case "OPERSET":
                    await OperSetAsync(session, args, state, ct);
                    return;

                case "WHOIS":
                    await WhoisAsync(session, args, state, ct);
                    return;

                default:
                    await ReplyAsync(session, "Unknown command. Try HELP.", ct);
                    return;
            }
        }

        private async ValueTask HelpAsync(IClientSession session, string[] args, CancellationToken ct)
        {
            if (args.Length == 0)
            {
                await ReplyAsync(session, AdminServMessages.HelpIntro, ct);
                await ReplyAsync(session, AdminServMessages.HelpOper, ct);
                await ReplyAsync(session, AdminServMessages.HelpFlags, ct);
                await ReplyAsync(session, AdminServMessages.HelpOperset, ct);
                await ReplyAsync(session, AdminServMessages.HelpWhois, ct);
                return;
            }

            var sub = args[0].ToUpperInvariant();
            var line = sub switch
            {
                "OPER" => AdminServMessages.HelpOper,
                "FLAGS" => AdminServMessages.HelpFlags,
                "OPERSET" => AdminServMessages.HelpOperset,
                "WHOIS" => AdminServMessages.HelpWhois,
                _ => AdminServMessages.HelpIntro
            };

            await ReplyAsync(session, line, ct);
        }

        private async ValueTask<bool> RequireAdminAccessAsync(IClientSession session, ServerState state, CancellationToken ct)
        {
            if (!session.IsRegistered)
            {
                await ReplyAsync(session, "You have not registered.", ct);
                return false;
            }

            var identified = await _auth.GetIdentifiedAccountAsync(session.ConnectionId, ct);
            if (!string.IsNullOrWhiteSpace(identified))
            {
                var existing = await _staff.GetByAccountAsync(identified, ct);
                if (existing is not null)
                {
                    return true;
                }
            }

            if (!state.TryGetUser(session.ConnectionId, out var user) || user is null)
            {
                await ReplyAsync(session, "Permission denied.", ct);
                return false;
            }

            if (!OperCapabilityService.HasCapability(_options.Value, user, "adminserv"))
            {
                await ReplyAsync(session, "Permission denied.", ct);
                return false;
            }

            return true;
        }

        private async ValueTask OperAsync(IClientSession session, string[] args, ServerState state, CancellationToken ct)
        {
            if (!await RequireAdminAccessAsync(session, state, ct))
            {
                return;
            }

            if (args.Length == 0)
            {
                await ReplyAsync(session, AdminServMessages.HelpOper, ct);
                return;
            }

            var sub = args[0].ToUpperInvariant();
            switch (sub)
            {
                case "ADD":
                    await OperAddAsync(session, args.Skip(1).ToArray(), ct);
                    return;

                case "DEL":
                case "DELETE":
                    await OperDelAsync(session, args.Skip(1).ToArray(), ct);
                    return;

                case "LIST":
                    await OperListAsync(session, ct);
                    return;

                default:
                    await ReplyAsync(session, AdminServMessages.HelpOper, ct);
                    return;
            }
        }

        private async ValueTask OperAddAsync(IClientSession session, string[] args, CancellationToken ct)
        {
            if (args.Length < 1)
            {
                await ReplyAsync(session, "Syntax: OPER ADD <account> [operclass]", ct);
                return;
            }

            var account = args[0];
            if (!IrcValidation.IsValidNick(account, out var err))
            {
                await ReplyAsync(session, $"Invalid account: {err}", ct);
                return;
            }

            var operClass = args.Length > 1 ? args[1].Trim() : null;
            if (string.IsNullOrWhiteSpace(operClass))
            {
                operClass = null;
            }

            var entry = new AdminStaffEntry
            {
                Account = account.Trim(),
                Flags = Array.Empty<string>(),
                OperClass = operClass,
            };

            var ok = await _staff.TryUpsertAsync(entry, ct);
            await ReplyAsync(session, ok ? $"Added staff account {entry.Account}." : "Failed to add staff account.", ct);
        }

        private async ValueTask OperDelAsync(IClientSession session, string[] args, CancellationToken ct)
        {
            if (args.Length < 1)
            {
                await ReplyAsync(session, "Syntax: OPER DEL <account>", ct);
                return;
            }

            var account = args[0];
            if (!IrcValidation.IsValidNick(account, out var err))
            {
                await ReplyAsync(session, $"Invalid account: {err}", ct);
                return;
            }

            var ok = await _staff.TryDeleteAsync(account.Trim(), ct);
            await ReplyAsync(session, ok ? $"Deleted staff account {account.Trim()}." : "Staff account not found.", ct);
        }

        private async ValueTask OperListAsync(IClientSession session, CancellationToken ct)
        {
            var all = _staff.All().OrderBy(e => e.Account, StringComparer.OrdinalIgnoreCase).ToArray();
            if (all.Length == 0)
            {
                await ReplyAsync(session, "No staff entries.", ct);
                return;
            }

            await ReplyAsync(session, $"Staff entries ({all.Length}):", ct);
            foreach (var e in all)
            {
                await ReplyAsync(session, FormatEntry(e), ct);
            }
        }

        private async ValueTask FlagsAsync(IClientSession session, string[] args, ServerState state, CancellationToken ct)
        {
            if (!await RequireAdminAccessAsync(session, state, ct))
            {
                return;
            }

            if (args.Length < 1)
            {
                await ReplyAsync(session, AdminServMessages.HelpFlags, ct);
                return;
            }

            var sub = args[0].ToUpperInvariant();
            switch (sub)
            {
                case "ADD":
                    await FlagsAddDelAsync(session, args.Skip(1).ToArray(), add: true, ct);
                    return;

                case "DEL":
                case "DELETE":
                    await FlagsAddDelAsync(session, args.Skip(1).ToArray(), add: false, ct);
                    return;

                default:
                    await ReplyAsync(session, AdminServMessages.HelpFlags, ct);
                    return;
            }
        }

        private async ValueTask FlagsAddDelAsync(IClientSession session, string[] args, bool add, CancellationToken ct)
        {
            if (args.Length < 2)
            {
                await ReplyAsync(session, add
                    ? "Syntax: FLAGS ADD <account> <flag> [flag...]"
                    : "Syntax: FLAGS DEL <account> <flag> [flag...]", ct);
                return;
            }

            var account = args[0];
            if (!IrcValidation.IsValidNick(account, out var err))
            {
                await ReplyAsync(session, $"Invalid account: {err}", ct);
                return;
            }

            var flags = args.Skip(1)
                .Select(f => (f ?? string.Empty).Trim())
                .Where(f => !string.IsNullOrWhiteSpace(f))
                .ToArray();

            if (flags.Length == 0)
            {
                await ReplyAsync(session, "No flags given.", ct);
                return;
            }

            var existing = await _staff.GetByAccountAsync(account.Trim(), ct);
            if (existing is null)
            {
                await ReplyAsync(session, "Staff account not found.", ct);
                return;
            }

            var set = new HashSet<string>(existing.Flags ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            foreach (var f in flags)
            {
                if (add)
                {
                    set.Add(f);
                }
                else
                {
                    set.Remove(f);
                }
            }

            var updated = existing with
            {
                Flags = set.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray()
            };

            var ok = await _staff.TryUpsertAsync(updated, ct);
            await ReplyAsync(session, ok ? FormatEntry(updated) : "Failed to update flags.", ct);
        }

        private async ValueTask OperSetAsync(IClientSession session, string[] args, ServerState state, CancellationToken ct)
        {
            if (!await RequireAdminAccessAsync(session, state, ct))
            {
                return;
            }

            if (args.Length < 2)
            {
                await ReplyAsync(session, AdminServMessages.HelpOperset, ct);
                return;
            }

            var account = args[0];
            if (!IrcValidation.IsValidNick(account, out var err))
            {
                await ReplyAsync(session, $"Invalid account: {err}", ct);
                return;
            }

            var existing = await _staff.GetByAccountAsync(account.Trim(), ct);
            if (existing is null)
            {
                await ReplyAsync(session, "Staff account not found.", ct);
                return;
            }

            var raw = args[1].Trim();
            var operClass = raw == "-" ? null : raw;
            if (string.IsNullOrWhiteSpace(operClass))
            {
                operClass = null;
            }

            var updated = existing with { OperClass = operClass };
            var ok = await _staff.TryUpsertAsync(updated, ct);
            await ReplyAsync(session, ok ? FormatEntry(updated) : "Failed to update oper class.", ct);
        }

        private async ValueTask WhoisAsync(IClientSession session, string[] args, ServerState state, CancellationToken ct)
        {
            if (!await RequireAdminAccessAsync(session, state, ct))
            {
                return;
            }

            if (args.Length < 1)
            {
                await ReplyAsync(session, AdminServMessages.HelpWhois, ct);
                return;
            }

            var token = args[0].Trim();
            if (string.IsNullOrWhiteSpace(token))
            {
                await ReplyAsync(session, AdminServMessages.HelpWhois, ct);
                return;
            }

            if (state.TryGetConnectionIdByNick(token, out var cid) && !string.IsNullOrWhiteSpace(cid))
            {
                var account = await _auth.GetIdentifiedAccountAsync(cid, ct);
                if (string.IsNullOrWhiteSpace(account))
                {
                    await ReplyAsync(session, $"{token} is not identified.", ct);
                    return;
                }

                await ReplyAsync(session, $"Account for {token}: {account}", ct);
                var staff = await _staff.GetByAccountAsync(account, ct);
                await ReplyAsync(session, staff is null ? $"No staff entry for {account}." : FormatEntry(staff), ct);
                return;
            }

            if (!IrcValidation.IsValidNick(token, out var err))
            {
                await ReplyAsync(session, $"Invalid nick/account: {err}", ct);
                return;
            }

            var entry = await _staff.GetByAccountAsync(token, ct);
            await ReplyAsync(session, entry is null ? $"No staff entry for {token}." : FormatEntry(entry), ct);
        }

        private static string FormatEntry(AdminStaffEntry e)
        {
            var flags = (e.Flags ?? Array.Empty<string>()).Length == 0
                ? "(none)"
                : string.Join(',', e.Flags ?? Array.Empty<string>());

            var cls = string.IsNullOrWhiteSpace(e.OperClass) ? "(none)" : e.OperClass;
            return $"Staff {e.Account}: flags={flags} operclass={cls}";
        }

        private ValueTask ReplyAsync(IClientSession session, string text, CancellationToken ct)
        {
            var server = _options.Value.ServerInfo?.Name ?? "server";
            var to = session.Nick ?? "*";
            var line = $":{AdminServMessages.ServiceName}!services@{server} NOTICE {to} :{text}";
            return session.SendAsync(line, ct);
        }
    }
}
