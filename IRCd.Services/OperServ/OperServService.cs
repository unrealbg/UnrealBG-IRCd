namespace IRCd.Services.OperServ
{
    using System;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Core.Abstractions;
    using IRCd.Core.Config;
    using IRCd.Core.Protocol;
    using IRCd.Core.Services;
    using IRCd.Core.State;
    using IRCd.Shared.Options;

    using Microsoft.Extensions.Hosting;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;

    public sealed class OperServService
    {
        private readonly ILogger<OperServService> _logger;
        private readonly IOptions<IrcOptions> _options;
        private readonly IrcConfigManager _config;
        private readonly BanService _bans;
        private readonly IBanEnforcer? _banEnforcer;
        private readonly RuntimeDenyService _denies;
        private readonly RuntimeWarnService _warns;
        private readonly RuntimeTriggerService _triggers;
        private readonly ISessionRegistry _sessions;
        private readonly RoutingService _routing;
        private readonly IHostApplicationLifetime? _lifetime;

        public OperServService(
            ILogger<OperServService> logger,
            IOptions<IrcOptions> options,
            IrcConfigManager config,
            BanService bans,
            RuntimeDenyService denies,
            RuntimeWarnService warns,
            RuntimeTriggerService triggers,
            ISessionRegistry sessions,
            RoutingService routing,
            IBanEnforcer? banEnforcer = null,
            IHostApplicationLifetime? lifetime = null)
        {
            _logger = logger;
            _options = options;
            _config = config;
            _bans = bans;
            _denies = denies;
            _warns = warns;
            _triggers = triggers;
            _sessions = sessions;
            _routing = routing;
            _banEnforcer = banEnforcer;
            _lifetime = lifetime;
        }

        public async ValueTask HandleAsync(IClientSession session, string text, ServerState state, CancellationToken ct)
        {
            var cmdLine = (text ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(cmdLine))
            {
                await ReplyAsync(session, OperServMessages.HelpIntro, ct);
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

                case "KLINE":
                    await KlineAsync(session, args, state, ct);
                    return;

                case "AKILL":
                    await KlineAsync(session, args, state, ct);
                    return;

                case "DLINE":
                    await DlineAsync(session, args, state, BanType.DLINE, ct);
                    return;

                case "ZLINE":
                    await DlineAsync(session, args, state, BanType.ZLINE, ct);
                    return;

                case "DENY":
                    await DenyAsync(session, args, state, ct);
                    return;

                case "WARN":
                    await WarnAsync(session, args, state, ct);
                    return;

                case "TRIGGER":
                    await TriggerAsync(session, args, state, ct);
                    return;

                case "GLOBAL":
                    await GlobalAsync(session, args, state, ct);
                    return;

                case "FJOIN":
                    await FjoinAsync(session, args, state, ct);
                    return;

                case "FPART":
                    await FpartAsync(session, args, state, ct);
                    return;

                case "UINFO":
                    await UinfoAsync(session, args, state, ct);
                    return;

                case "STATS":
                    await StatsAsync(session, args, state, ct);
                    return;

                case "CLEAR":
                    await ClearAsync(session, args, state, ct);
                    return;

                case "REHASH":
                    await RehashAsync(session, state, ct);
                    return;

                case "DIE":
                    await DieAsync(session, state, ct);
                    return;

                case "RESTART":
                    await RestartAsync(session, state, ct);
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
                await ReplyAsync(session, OperServMessages.HelpIntro, ct);
                await ReplyAsync(session, OperServMessages.HelpKline, ct);
                await ReplyAsync(session, OperServMessages.HelpAkill, ct);
                await ReplyAsync(session, OperServMessages.HelpDline, ct);
                await ReplyAsync(session, OperServMessages.HelpZline, ct);
                await ReplyAsync(session, OperServMessages.HelpDeny, ct);
                await ReplyAsync(session, OperServMessages.HelpWarn, ct);
                await ReplyAsync(session, OperServMessages.HelpTrigger, ct);
                await ReplyAsync(session, OperServMessages.HelpGlobal, ct);
                await ReplyAsync(session, OperServMessages.HelpFjoin, ct);
                await ReplyAsync(session, OperServMessages.HelpFpart, ct);
                await ReplyAsync(session, OperServMessages.HelpUinfo, ct);
                await ReplyAsync(session, OperServMessages.HelpStats, ct);
                await ReplyAsync(session, OperServMessages.HelpClear, ct);
                await ReplyAsync(session, OperServMessages.HelpRehash, ct);
                await ReplyAsync(session, OperServMessages.HelpRestart, ct);
                await ReplyAsync(session, OperServMessages.HelpDie, ct);
                return;
            }

            var sub = args[0].ToUpperInvariant();
            var line = sub switch
            {
                "KLINE" => OperServMessages.HelpKline,
                "AKILL" => OperServMessages.HelpAkill,
                "DLINE" => OperServMessages.HelpDline,
                "ZLINE" => OperServMessages.HelpZline,
                "DENY" => OperServMessages.HelpDeny,
                "WARN" => OperServMessages.HelpWarn,
                "TRIGGER" => OperServMessages.HelpTrigger,
                "GLOBAL" => OperServMessages.HelpGlobal,
                "FJOIN" => OperServMessages.HelpFjoin,
                "FPART" => OperServMessages.HelpFpart,
                "UINFO" => OperServMessages.HelpUinfo,
                "STATS" => OperServMessages.HelpStats,
                "CLEAR" => OperServMessages.HelpClear,
                "REHASH" => OperServMessages.HelpRehash,
                "RESTART" => OperServMessages.HelpRestart,
                "DIE" => OperServMessages.HelpDie,
                _ => OperServMessages.HelpIntro
            };

            await ReplyAsync(session, line, ct);
        }

        private async ValueTask<bool> RequireOperCapabilityAsync(IClientSession session, ServerState state, string capability, CancellationToken ct)
        {
            if (!session.IsRegistered)
            {
                await ReplyAsync(session, "You have not registered.", ct);
                return false;
            }

            if (!state.TryGetUser(session.ConnectionId, out var user) || user is null || !OperCapabilityService.HasCapability(_options.Value, user, capability))
            {
                await ReplyAsync(session, "Permission denied.", ct);
                return false;
            }

            return true;
        }

        private async ValueTask KlineAsync(IClientSession session, string[] args, ServerState state, CancellationToken ct)
        {
            if (!await RequireOperCapabilityAsync(session, state, "kline", ct))
            {
                return;
            }

            if (args.Length < 1)
            {
                await ReplyAsync(session, "Syntax: KLINE <mask> [reason] | KLINE -<mask>", ct);
                return;
            }

            var rawMask = args[0];
            if (rawMask.StartsWith("-", StringComparison.Ordinal))
            {
                var toRemove = rawMask.TrimStart('-').Trim();
                var removed = await _bans.RemoveAsync(BanType.KLINE, toRemove, ct);
                await ReplyAsync(session, $"UNKLINE {(removed ? "removed" : "not found")} {toRemove}", ct);
                return;
            }

            var mask = rawMask.Trim();
            var reason = args.Length > 1 ? string.Join(' ', args.Skip(1)) : "Banned";
            if (string.IsNullOrWhiteSpace(reason))
            {
                reason = "Banned";
            }

            var entry = await _bans.AddAsync(new BanEntry
            {
                Type = BanType.KLINE,
                Mask = mask,
                Reason = reason,
                SetBy = session.Nick ?? "OperServ"
            }, ct);

            if (_banEnforcer is not null)
            {
                await _banEnforcer.EnforceBanImmediatelyAsync(entry, ct);
            }
            else
            {
                foreach (var u in state.GetAllUsers().Where(u => u.IsRegistered && !u.IsRemote).ToArray())
                {
                    var nick = u.Nick ?? "*";
                    var userName = u.UserName ?? "user";
                    var host = u.Host ?? "localhost";

                    var match = await _bans.TryMatchUserAsync(nick, userName, host, ct);
                    if (match is null || match.Id != entry.Id)
                        continue;

                    if (_sessions.TryGet(u.ConnectionId, out var targetSession) && targetSession is not null)
                    {
                        await targetSession.SendAsync($":{ServerName} 465 {nick} :You are banned from this server ({match.Reason})", ct);
                        await targetSession.CloseAsync("K-Lined", ct);
                    }

                    state.RemoveUser(u.ConnectionId);
                }
            }

            await ReplyAsync(session, $"KLINE added {mask} :{reason}", ct);
        }

        private async ValueTask DlineAsync(IClientSession session, string[] args, ServerState state, BanType type, CancellationToken ct)
        {
            if (!await RequireOperCapabilityAsync(session, state, "dline", ct))
            {
                return;
            }

            if (args.Length < 1)
            {
                await ReplyAsync(session, "Syntax: DLINE <mask> [reason] | DLINE -<mask>", ct);
                return;
            }

            var rawMask = args[0];
            if (rawMask.StartsWith("-", StringComparison.Ordinal))
            {
                var toRemove = rawMask.TrimStart('-').Trim();
                var removed = await _bans.RemoveAsync(type, toRemove, ct);
                var verb = type == BanType.ZLINE ? "UNZLINE" : "UNDLINE";
                await ReplyAsync(session, $"{verb} {(removed ? "removed" : "not found")} {toRemove}", ct);
                return;
            }

            var mask = rawMask.Trim();
            var reason = args.Length > 1 ? string.Join(' ', args.Skip(1)) : "Banned";
            if (string.IsNullOrWhiteSpace(reason))
            {
                reason = "Banned";
            }

            var entry = await _bans.AddAsync(new BanEntry
            {
                Type = type,
                Mask = mask,
                Reason = reason,
                SetBy = session.Nick ?? "OperServ"
            }, ct);

            if (_banEnforcer is not null)
            {
                await _banEnforcer.EnforceBanImmediatelyAsync(entry, ct);
            }
            else
            {
                foreach (var u in state.GetAllUsers().Where(u => u.IsRegistered && !u.IsRemote).ToArray())
                {
                    var remoteIpString = u.RemoteIp;
                    if (string.IsNullOrWhiteSpace(remoteIpString))
                        continue;

                    if (!System.Net.IPAddress.TryParse(remoteIpString, out var ip))
                        continue;

                    var match = await _bans.TryMatchIpAsync(ip, ct);
                    if (match is null || match.Id != entry.Id)
                        continue;

                    var nick = u.Nick ?? "*";
                    var banText = type == BanType.ZLINE ? "Z-Lined" : "D-Lined";

                    if (_sessions.TryGet(u.ConnectionId, out var targetSession) && targetSession is not null)
                    {
                        await targetSession.SendAsync($":{ServerName} 465 {nick} :You are banned from this server ({match.Reason})", ct);
                        await targetSession.CloseAsync(banText, ct);
                    }

                    state.RemoveUser(u.ConnectionId);
                }
            }

            var opText = type == BanType.ZLINE ? "ZLINE" : "DLINE";
            await ReplyAsync(session, $"{opText} added {mask} :{reason}", ct);
        }

        private async ValueTask DenyAsync(IClientSession session, string[] args, ServerState state, CancellationToken ct)
        {
            if (!await RequireOperCapabilityAsync(session, state, "deny", ct))
            {
                return;
            }

            if (args.Length == 0)
            {
                await ReplyAsync(session, "Syntax: DENY <mask> [reason] | DENY -<mask> | DENY LIST", ct);
                return;
            }

            var sub = args[0];
            if (sub.Equals("LIST", StringComparison.OrdinalIgnoreCase))
            {
                var items = _options.Value.Denies ?? Array.Empty<DenyOptions>();
                if (items.Length == 0)
                {
                    await ReplyAsync(session, "DENY list is empty.", ct);
                    return;
                }

                foreach (var d in items.Where(d => d is not null && !string.IsNullOrWhiteSpace(d.Mask)).OrderBy(d => d!.Mask, StringComparer.OrdinalIgnoreCase))
                {
                    var reason = string.IsNullOrWhiteSpace(d!.Reason) ? "Denied" : d.Reason;
                    await ReplyAsync(session, $"DENY {d.Mask} :{reason}", ct);
                }

                return;
            }

            if (sub.StartsWith("-", StringComparison.Ordinal))
            {
                var toRemove = sub.TrimStart('-').Trim();
                var removed = _denies.Remove(toRemove);
                await ReplyAsync(session, $"DENY {(removed ? "removed" : "not found")} {toRemove}", ct);
                return;
            }

            var mask = sub.Trim();
            var reason2 = args.Length > 1 ? string.Join(' ', args.Skip(1)) : "Denied";
            _denies.AddOrReplace(mask, reason2);
            await ReplyAsync(session, $"DENY added {mask} :{reason2}", ct);
        }

        private async ValueTask WarnAsync(IClientSession session, string[] args, ServerState state, CancellationToken ct)
        {
            if (!await RequireOperCapabilityAsync(session, state, "warn", ct))
            {
                return;
            }

            if (args.Length == 0)
            {
                await ReplyAsync(session, "Syntax: WARN <mask> [message] | WARN -<mask> | WARN LIST", ct);
                return;
            }

            var sub = args[0];
            if (sub.Equals("LIST", StringComparison.OrdinalIgnoreCase))
            {
                var items = _options.Value.Warns ?? Array.Empty<WarnOptions>();
                if (items.Length == 0)
                {
                    await ReplyAsync(session, "WARN list is empty.", ct);
                    return;
                }

                foreach (var w in items.Where(w => w is not null && !string.IsNullOrWhiteSpace(w.Mask)).OrderBy(w => w!.Mask, StringComparer.OrdinalIgnoreCase))
                {
                    var msg = string.IsNullOrWhiteSpace(w!.Message) ? "Warning" : w.Message;
                    await ReplyAsync(session, $"WARN {w.Mask} :{msg}", ct);
                }

                return;
            }

            if (sub.StartsWith("-", StringComparison.Ordinal))
            {
                var toRemove = sub.TrimStart('-').Trim();
                var removed = _warns.Remove(toRemove);
                await ReplyAsync(session, $"WARN {(removed ? "removed" : "not found")} {toRemove}", ct);
                return;
            }

            var mask = sub.Trim();
            var message = args.Length > 1 ? string.Join(' ', args.Skip(1)) : "Warning";
            _warns.AddOrReplace(mask, message);
            await ReplyAsync(session, $"WARN added {mask} :{message}", ct);
        }

        private async ValueTask TriggerAsync(IClientSession session, string[] args, ServerState state, CancellationToken ct)
        {
            if (!await RequireOperCapabilityAsync(session, state, "trigger", ct))
            {
                return;
            }

            if (args.Length == 0)
            {
                await ReplyAsync(session, "Syntax: TRIGGER <pattern> [response] | TRIGGER -<pattern> | TRIGGER LIST", ct);
                return;
            }

            var sub = args[0];
            if (sub.Equals("LIST", StringComparison.OrdinalIgnoreCase))
            {
                var items = _options.Value.Triggers ?? Array.Empty<TriggerOptions>();
                if (items.Length == 0)
                {
                    await ReplyAsync(session, "TRIGGER list is empty.", ct);
                    return;
                }

                foreach (var t in items.Where(t => t is not null && !string.IsNullOrWhiteSpace(t.Pattern)).OrderBy(t => t!.Pattern, StringComparer.OrdinalIgnoreCase))
                {
                    var resp = t!.Response ?? string.Empty;
                    await ReplyAsync(session, $"TRIGGER {t.Pattern} :{resp}", ct);
                }

                return;
            }

            if (sub.StartsWith("-", StringComparison.Ordinal))
            {
                var toRemove = sub.TrimStart('-').Trim();
                var removed = _triggers.Remove(toRemove);
                await ReplyAsync(session, $"TRIGGER {(removed ? "removed" : "not found")} {toRemove}", ct);
                return;
            }

            var pattern = sub.Trim();
            var response = args.Length > 1 ? string.Join(' ', args.Skip(1)) : string.Empty;
            _triggers.AddOrReplace(pattern, response);
            await ReplyAsync(session, $"TRIGGER added {pattern}", ct);
        }

        private async ValueTask GlobalAsync(IClientSession session, string[] args, ServerState state, CancellationToken ct)
        {
            if (!await RequireOperCapabilityAsync(session, state, "global", ct))
            {
                return;
            }

            if (args.Length == 0)
            {
                await ReplyAsync(session, "Syntax: GLOBAL <message>", ct);
                return;
            }

            var message = string.Join(' ', args).Trim();
            if (string.IsNullOrWhiteSpace(message))
            {
                await ReplyAsync(session, "Syntax: GLOBAL <message>", ct);
                return;
            }

            var from = $":{OperServMessages.ServiceName}!services@{ServerName} NOTICE";

            var delivered = 0;
            foreach (var targetSession in _sessions.All())
            {
                if (targetSession is null)
                {
                    continue;
                }

                if (!state.TryGetUser(targetSession.ConnectionId, out var u) || u is null || !u.IsRegistered || u.IsService)
                {
                    continue;
                }

                var toNick = u.Nick ?? targetSession.Nick;
                if (string.IsNullOrWhiteSpace(toNick) || string.Equals(toNick, "*", StringComparison.Ordinal))
                {
                    continue;
                }

                await targetSession.SendAsync($"{from} {toNick} :{message}", ct);
                delivered++;
            }

            await ReplyAsync(session, $"GLOBAL sent to {delivered} users", ct);
        }

        private async ValueTask FjoinAsync(IClientSession session, string[] args, ServerState state, CancellationToken ct)
        {
            if (!await RequireOperCapabilityAsync(session, state, "svsjoin", ct))
            {
                return;
            }

            if (args.Length < 2)
            {
                await ReplyAsync(session, "Syntax: FJOIN <nick> <#channel>", ct);
                return;
            }

            var targetNick = args[0].Trim();
            var channelName = args[1].Trim();

            if (!IrcValidation.IsValidNick(targetNick, out _) || !IrcValidation.IsValidChannel(channelName, out _))
            {
                await ReplyAsync(session, "FJOIN invalid nick/channel", ct);
                return;
            }

            if (!state.TryGetConnectionIdByNick(targetNick, out var targetConn) || targetConn is null || !state.TryGetUser(targetConn, out var targetUser) || targetUser is null)
            {
                await ReplyAsync(session, "FJOIN no such nick", ct);
                return;
            }

            if (targetUser.IsService)
            {
                await ReplyAsync(session, "Cannot FJOIN services", ct);
                return;
            }

            if (targetUser.IsRemote)
            {
                await ReplyAsync(session, "Cannot FJOIN remote users", ct);
                return;
            }

            if (!state.TryJoinChannel(targetConn, targetNick, channelName))
            {
                await ReplyAsync(session, "FJOIN no-op", ct);
                return;
            }

            if (!state.TryGetChannel(channelName, out var channel) || channel is null)
            {
                await ReplyAsync(session, "FJOIN failed", ct);
                return;
            }

            var userName = targetUser.UserName ?? "u";
            var host = state.GetHostFor(targetConn);
            var joinLine = $":{targetNick}!{userName}@{host} JOIN :{channelName}";
            await _routing.BroadcastToChannelAsync(channel, joinLine, excludeConnectionId: null, ct);

            await ReplyAsync(session, $"FJOIN {targetNick} {channelName}", ct);
        }

        private async ValueTask FpartAsync(IClientSession session, string[] args, ServerState state, CancellationToken ct)
        {
            if (!await RequireOperCapabilityAsync(session, state, "svspart", ct))
            {
                return;
            }

            if (args.Length < 2)
            {
                await ReplyAsync(session, "Syntax: FPART <nick> <#channel> [reason]", ct);
                return;
            }

            var targetNick = args[0].Trim();
            var channelName = args[1].Trim();
            var reason = args.Length > 2 ? string.Join(' ', args.Skip(2)).Trim() : string.Empty;

            if (!IrcValidation.IsValidNick(targetNick, out _) || !IrcValidation.IsValidChannel(channelName, out _))
            {
                await ReplyAsync(session, "FPART invalid nick/channel", ct);
                return;
            }

            if (!state.TryGetConnectionIdByNick(targetNick, out var targetConn) || targetConn is null || !state.TryGetUser(targetConn, out var targetUser) || targetUser is null)
            {
                await ReplyAsync(session, "FPART no such nick", ct);
                return;
            }

            if (targetUser.IsService)
            {
                await ReplyAsync(session, "Cannot FPART services", ct);
                return;
            }

            if (targetUser.IsRemote)
            {
                await ReplyAsync(session, "Cannot FPART remote users", ct);
                return;
            }

            if (!state.TryPartChannel(targetConn, channelName, out var channel) || channel is null)
            {
                await ReplyAsync(session, "FPART no-op", ct);
                return;
            }

            var userName = targetUser.UserName ?? "u";
            var host = state.GetHostFor(targetConn);
            var partLine = $":{targetNick}!{userName}@{host} PART {channelName}";
            if (!string.IsNullOrWhiteSpace(reason))
            {
                partLine += $" :{reason}";
            }

            await _routing.BroadcastToChannelAsync(channel, partLine, excludeConnectionId: null, ct);
            await _routing.SendToUserAsync(targetConn, partLine, ct);

            await ReplyAsync(session, $"FPART {targetNick} {channelName}", ct);
        }

        private async ValueTask UinfoAsync(IClientSession session, string[] args, ServerState state, CancellationToken ct)
        {
            if (!await RequireOperCapabilityAsync(session, state, "uinfo", ct))
            {
                return;
            }

            if (args.Length < 1)
            {
                await ReplyAsync(session, "Syntax: UINFO <nick>", ct);
                return;
            }

            var targetNick = args[0].Trim();
            if (!state.TryGetConnectionIdByNick(targetNick, out var conn) || conn is null || !state.TryGetUser(conn, out var u) || u is null)
            {
                await ReplyAsync(session, "No such nick", ct);
                return;
            }

            var chCount = state.GetUserChannels(conn).Count;
            var flags = u.Modes.ToString();
            var remote = u.IsRemote ? "remote" : "local";
            var oper = u.Modes.HasFlag(UserModes.Operator) ? "oper" : "user";
            var host = u.Host ?? state.GetHostFor(conn);
            var ip = u.RemoteIp ?? "";
            var operInfo = string.IsNullOrWhiteSpace(u.OperName) && string.IsNullOrWhiteSpace(u.OperClass)
                ? ""
                : $" oper={u.OperName ?? ""}/{u.OperClass ?? ""}";

            await ReplyAsync(session, $"UINFO {u.Nick} {u.UserName}@{host} ip={ip} {remote} {oper} modes={flags} channels={chCount}{operInfo}", ct);
        }

        private async ValueTask StatsAsync(IClientSession session, string[] args, ServerState state, CancellationToken ct)
        {
            if (!await RequireOperCapabilityAsync(session, state, "stats", ct))
            {
                return;
            }

            _ = args;

            var uptime = DateTimeOffset.UtcNow - state.CreatedUtc;
            var k = (await _bans.GetActiveByTypeAsync(BanType.KLINE, ct)).Count;
            var d = (await _bans.GetActiveByTypeAsync(BanType.DLINE, ct)).Count + (await _bans.GetActiveByTypeAsync(BanType.ZLINE, ct)).Count;
            var deny = _options.Value.Denies?.Length ?? 0;
            var warn = _options.Value.Warns?.Length ?? 0;
            var trig = _options.Value.Triggers?.Length ?? 0;

            await ReplyAsync(session, $"STATS uptime={uptime:dd\\:hh\\:mm\\:ss} users={state.UserCount} sessions={_sessions.All().Count()} channels={state.GetAllChannelNames().Count}", ct);
            await ReplyAsync(session, $"STATS klines={k} dlines={d} denies={deny} warns={warn} triggers={trig}", ct);
        }

        private async ValueTask ClearAsync(IClientSession session, string[] args, ServerState state, CancellationToken ct)
        {
            if (!await RequireOperCapabilityAsync(session, state, "clear", ct))
            {
                return;
            }

            var what = args.Length > 0 ? args[0].Trim().ToUpperInvariant() : "ALL";
            if (string.IsNullOrWhiteSpace(what))
            {
                what = "ALL";
            }

            switch (what)
            {
                case "ALL":
                    _denies.Clear();
                    _warns.Clear();
                    _triggers.Clear();
                    await ClearBansAsync(BanType.KLINE, ct);
                    await ClearBansAsync(BanType.DLINE, ct);
                    await ClearBansAsync(BanType.ZLINE, ct);
                    await ReplyAsync(session, "CLEAR ALL done", ct);
                    return;

                case "KLINE":
                case "KLINES":
                    await ClearBansAsync(BanType.KLINE, ct);
                    await ReplyAsync(session, "CLEAR KLINE done", ct);
                    return;

                case "DLINE":
                case "DLINES":
                    await ClearBansAsync(BanType.DLINE, ct);
                    await ClearBansAsync(BanType.ZLINE, ct);
                    await ReplyAsync(session, "CLEAR DLINE done", ct);
                    return;

                case "DENY":
                case "DENIES":
                    _denies.Clear();
                    await ReplyAsync(session, "CLEAR DENY done", ct);
                    return;

                case "WARN":
                case "WARNS":
                    _warns.Clear();
                    await ReplyAsync(session, "CLEAR WARN done", ct);
                    return;

                case "TRIGGER":
                case "TRIGGERS":
                    _triggers.Clear();
                    await ReplyAsync(session, "CLEAR TRIGGER done", ct);
                    return;

                default:
                    await ReplyAsync(session, "Syntax: CLEAR [ALL|KLINE|DLINE|DENY|WARN|TRIGGER]", ct);
                    return;
            }
        }

        private async ValueTask ClearBansAsync(BanType type, CancellationToken ct)
        {
            var bans = await _bans.GetActiveByTypeAsync(type, ct);
            foreach (var b in bans)
            {
                await _bans.RemoveByIdAsync(b.Id, ct);
            }
        }

        private async ValueTask RehashAsync(IClientSession session, ServerState state, CancellationToken ct)
        {
            if (!await RequireOperCapabilityAsync(session, state, "rehash", ct))
            {
                return;
            }

            try
            {
                var result = _config.TryRehashFromConfiguredPath();
                if (!result.Success)
                {
                    foreach (var e in result.Errors.Take(10))
                    {
                        await ReplyAsync(session, e, ct);
                    }
                    return;
                }

                await ReplyAsync(session, "Rehashing ircd.conf", ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "OperServ REHASH failed");
                await ReplyAsync(session, $"REHASH failed: {ex.Message}", ct);
            }
        }

        private async ValueTask DieAsync(IClientSession session, ServerState state, CancellationToken ct)
        {
            if (!await RequireOperCapabilityAsync(session, state, "die", ct))
            {
                return;
            }

            if (_lifetime is null)
            {
                await ReplyAsync(session, "DIE is not available in this host.", ct);
                return;
            }

            await ReplyAsync(session, "Server is shutting down", ct);
            _lifetime.StopApplication();
        }

        private async ValueTask RestartAsync(IClientSession session, ServerState state, CancellationToken ct)
        {
            if (!await RequireOperCapabilityAsync(session, state, "restart", ct))
            {
                return;
            }

            if (_lifetime is null)
            {
                await ReplyAsync(session, "RESTART is not available in this host.", ct);
                return;
            }

            await ReplyAsync(session, "Server is restarting", ct);
            _lifetime.StopApplication();
        }

        private string ServerName => _options.Value.ServerInfo?.Name ?? "server";

        private async ValueTask ReplyAsync(IClientSession session, string text, CancellationToken ct)
        {
            var to = session.Nick ?? "*";
            var server = ServerName;
            var line = $":{OperServMessages.ServiceName}!services@{server} NOTICE {to} :{text}";
            await session.SendAsync(line, ct);
        }
    }
}
