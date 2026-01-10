namespace IRCd.Services.ChanServ
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Core.Abstractions;
    using IRCd.Core.State;
    using IRCd.Core.Protocol;
    using IRCd.Core.Services;
    using IRCd.Services.NickServ;
    using IRCd.Services.Storage;
    using IRCd.Services.SeenServ;
    using IRCd.Shared.Options;

    using Microsoft.Extensions.Options;

    public sealed class ChanServService
    {
        private readonly IOptions<IrcOptions> _options;
        private readonly INickAccountRepository _nickAccounts;
        private readonly IAuthState _auth;
        private readonly IChanServChannelRepository _channels;
        private readonly RoutingService _routing;
        private readonly ServerLinkService? _links;

        private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _identifiedChannelsByConnection = new(StringComparer.OrdinalIgnoreCase);

        public ChanServService(
            IOptions<IrcOptions> options,
            INickAccountRepository nickAccounts,
            IAuthState auth,
            IChanServChannelRepository channels,
            RoutingService routing,
            ServerLinkService? links = null)
        {
            _options = options;
            _nickAccounts = nickAccounts;
            _auth = auth;
            _channels = channels;
            _routing = routing;
            _links = links;
        }

        public async ValueTask HandleAsync(IClientSession session, string text, ServerState state, CancellationToken ct)
        {
            var cmdLine = (text ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(cmdLine))
            {
                await ReplyAsync(session, ChanServMessages.HelpIntro, ct);
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

                case "LIST":
                    await ListAsync(session, args, ct);
                    return;

                case "IDENTIFY":
                    await IdentifyAsync(session, args, ct);
                    return;

                case "REGISTER":
                    await RegisterAsync(session, args, state, ct);
                    return;

                case "DROP":
                    await DropAsync(session, args, ct);
                    return;

                case "UNREGISTER":
                case "UNREG":
                    await DropAsync(session, args, ct);
                    return;

                case "INFO":
                    await InfoAsync(session, args, ct);
                    return;

                case "SUSPEND":
                    await SuspendAsync(session, args, state, ct);
                    return;

                case "UNSUSPEND":
                case "UNSUSP":
                    await UnsuspendAsync(session, args, state, ct);
                    return;

                case "SET":
                    await SetAsync(session, args, state, ct);
                    return;

                case "AKICK":
                    await AkickAsync(session, args, ct);
                    return;

                case "FLAGS":
                    await FlagsAsync(session, args, ct);
                    return;

                case "ACCESS":
                    await AccessAsync(session, args, ct);
                    return;

                case "SOP":
                    await XopAsync(session, args, ct, label: "SOP", entryFlags: ChanServFlags.Op | ChanServFlags.Invite | ChanServFlags.Kick | ChanServFlags.Ban | ChanServFlags.Flags);
                    return;

                case "AOP":
                    await XopAsync(session, args, ct, label: "AOP", entryFlags: ChanServFlags.Op);
                    return;

                case "VOP":
                    await XopAsync(session, args, ct, label: "VOP", entryFlags: ChanServFlags.Voice);
                    return;

                case "STATUS":
                    await StatusAsync(session, args, state, ct);
                    return;

                case "ENFORCE":
                    await EnforceAsync(session, args, state, ct);
                    return;

                case "OP":
                    if (TryNormalizePlusMinusNick(args, out var opArgs, out var opEnable))
                    {
                        await ModePrivilegeAsync(session, opArgs, state, ct, enable: opEnable, modeChar: 'o', required: ChanServFlags.Op, label: opEnable ? "OP" : "DEOP");
                        return;
                    }

                    await ModePrivilegeAsync(session, args, state, ct, enable: true, modeChar: 'o', required: ChanServFlags.Op, label: "OP");
                    return;

                case "DEOP":
                    await ModePrivilegeAsync(session, args, state, ct, enable: false, modeChar: 'o', required: ChanServFlags.Op, label: "DEOP");
                    return;

                case "VOICE":
                    if (TryNormalizePlusMinusNick(args, out var voiceArgs, out var voiceEnable))
                    {
                        await ModePrivilegeAsync(session, voiceArgs, state, ct, enable: voiceEnable, modeChar: 'v', required: ChanServFlags.Voice, label: voiceEnable ? "VOICE" : "DEVOICE");
                        return;
                    }

                    await ModePrivilegeAsync(session, args, state, ct, enable: true, modeChar: 'v', required: ChanServFlags.Voice, label: "VOICE");
                    return;

                case "DEVOICE":
                    await ModePrivilegeAsync(session, args, state, ct, enable: false, modeChar: 'v', required: ChanServFlags.Voice, label: "DEVOICE");
                    return;

                case "INVITE":
                    await InviteAsync(session, args, state, ct);
                    return;

                case "KICK":
                    await KickAsync(session, args, state, ct);
                    return;

                case "RECOVER":
                    await RecoverAsync(session, args, state, ct);
                    return;

                case "BAN":
                    await BanAsync(session, args, state, ct);
                    return;

                case "UNBAN":
                    await UnbanAsync(session, args, state, ct);
                    return;

                case "TOPIC":
                    await TopicAsync(session, args, state, ct);
                    return;

                case "CLEAR":
                    await ClearAsync(session, args, state, ct);
                    return;

                default:
                    await ReplyAsync(session, "Unknown command. Try: HELP", ct);
                    return;
            }
        }

        private static bool TryNormalizePlusMinusNick(string[] args, out string[] normalizedArgs, out bool enable)
        {
            enable = true;
            normalizedArgs = args;

            if (args is null || args.Length < 2)
            {
                return false;
            }

            var chan = args[0];
            var token = args[1];
            if (string.IsNullOrWhiteSpace(chan) || string.IsNullOrWhiteSpace(token))
            {
                return false;
            }

            token = token.Trim();
            if (token == "+" || token == "-")
            {
                if (args.Length < 3 || string.IsNullOrWhiteSpace(args[2]))
                {
                    return false;
                }

                enable = token == "+";
                normalizedArgs = new[] { chan, args[2].Trim() };
                return true;
            }

            if ((token[0] == '+' || token[0] == '-') && token.Length > 1)
            {
                enable = token[0] == '+';
                var nick = token[1..].Trim();
                if (string.IsNullOrWhiteSpace(nick))
                {
                    return false;
                }

                normalizedArgs = new[] { chan, nick };
                return true;
            }

            return false;
        }

        private ValueTask HelpAsync(IClientSession session, string[] args, CancellationToken ct)
        {
            if (args.Length == 0)
            {
                return ReplyManyAsync(session, ct,
                    ChanServMessages.HelpIntro,
                    ChanServMessages.HelpList,
                    ChanServMessages.HelpIdentify,
                    ChanServMessages.HelpRegister,
                    ChanServMessages.HelpDrop,
                    ChanServMessages.HelpInfo,
                    ChanServMessages.HelpSet,
                    ChanServMessages.HelpSetMlock,
                    ChanServMessages.HelpSetTopicLock,
                    ChanServMessages.HelpFlags,
                    ChanServMessages.HelpAccess,
                    ChanServMessages.HelpSop,
                    ChanServMessages.HelpAop,
                    ChanServMessages.HelpVop,
                    ChanServMessages.HelpStatus,
                    ChanServMessages.HelpEnforce,
                    ChanServMessages.HelpOp,
                    ChanServMessages.HelpDeop,
                    ChanServMessages.HelpVoice,
                    ChanServMessages.HelpDevoice,
                    ChanServMessages.HelpInvite,
                    ChanServMessages.HelpKick,
                    ChanServMessages.HelpAkick,
                    ChanServMessages.HelpRecover,
                    ChanServMessages.HelpTopic,
                    ChanServMessages.HelpBan,
                    ChanServMessages.HelpUnban,
                        ChanServMessages.HelpSuspend,
                        ChanServMessages.HelpUnsuspend,
                    ChanServMessages.HelpClear);
            }

            var sub = args[0].ToUpperInvariant();
            var line = sub switch
            {
                "LIST" => ChanServMessages.HelpList,
                "IDENTIFY" => ChanServMessages.HelpIdentify,
                "REGISTER" => ChanServMessages.HelpRegister,
                "DROP" => ChanServMessages.HelpDrop,
                "UNREGISTER" => ChanServMessages.HelpDrop,
                "UNREG" => ChanServMessages.HelpDrop,
                "INFO" => ChanServMessages.HelpInfo,
                "SET" => ChanServMessages.HelpSet,
                "MLOCK" => ChanServMessages.HelpSetMlock,
                "TOPICLOCK" => ChanServMessages.HelpSetTopicLock,
                "AKICK" => ChanServMessages.HelpAkick,
                "FLAGS" => ChanServMessages.HelpFlags,
                "ACCESS" => ChanServMessages.HelpAccess,
                "SOP" => ChanServMessages.HelpSop,
                "AOP" => ChanServMessages.HelpAop,
                "VOP" => ChanServMessages.HelpVop,
                "STATUS" => ChanServMessages.HelpStatus,
                "ENFORCE" => ChanServMessages.HelpEnforce,
                "OP" => ChanServMessages.HelpOp,
                "DEOP" => ChanServMessages.HelpDeop,
                "VOICE" => ChanServMessages.HelpVoice,
                "DEVOICE" => ChanServMessages.HelpDevoice,
                "INVITE" => ChanServMessages.HelpInvite,
                "KICK" => ChanServMessages.HelpKick,
                "RECOVER" => ChanServMessages.HelpRecover,
                "TOPIC" => ChanServMessages.HelpTopic,
                "BAN" => ChanServMessages.HelpBan,
                "UNBAN" => ChanServMessages.HelpUnban,
                "SUSPEND" => ChanServMessages.HelpSuspend,
                "UNSUSPEND" => ChanServMessages.HelpUnsuspend,
                "UNSUSP" => ChanServMessages.HelpUnsuspend,
                "CLEAR" => ChanServMessages.HelpClear,
                _ => ChanServMessages.HelpIntro
            };

            return ReplyAsync(session, line, ct);
        }

        private async ValueTask EnforceAsync(IClientSession session, string[] args, ServerState state, CancellationToken ct)
        {
            if (args.Length < 1)
            {
                await ReplyAsync(session, "Syntax: ENFORCE <#channel>", ct);
                return;
            }

            var channelName = args[0];
            var (reg, flags) = await RequireAccessAsync(session, channelName, ct);
            if (reg is null)
            {
                return;
            }

            if (!flags.HasFlag(ChanServFlags.Op) && !flags.HasFlag(ChanServFlags.Founder) && !flags.HasFlag(ChanServFlags.Flags))
            {
                await ReplyAsync(session, "Insufficient privileges.", ct);
                return;
            }

            if (!state.TryGetChannel(channelName, out var live) || live is null)
            {
                await ReplyAsync(session, "No such channel.", ct);
                return;
            }

            if (!live.Contains(session.ConnectionId))
            {
                await ReplyAsync(session, "You must be in the channel.", ct);
                return;
            }

            if (reg.Mlock is not null)
            {
                await EnforceMlockAsync(live, reg.Mlock, state, ct);
            }

            if (reg.TopicLock is not null && reg.TopicLock.Enabled)
            {
                await EnforceTopicLockAsync(session, live, reg.TopicLock, state, ct);
            }

            foreach (var member in live.Members.ToArray())
            {
                var memberConn = member.ConnectionId;
                if (!state.TryGetUser(memberConn, out var u) || u is null)
                {
                    continue;
                }

                if (u.IsService)
                {
                    continue;
                }

                var account = await _auth.GetIdentifiedAccountAsync(memberConn, ct);
                var desired = ChannelPrivilege.Normal;

                if (!string.IsNullOrWhiteSpace(account))
                {
                    var f = reg.GetFlagsFor(account);
                    if (f.HasFlag(ChanServFlags.Founder) || f.HasFlag(ChanServFlags.Op))
                    {
                        desired = ChannelPrivilege.Op;
                    }
                    else if (f.HasFlag(ChanServFlags.Voice))
                    {
                        desired = ChannelPrivilege.Voice;
                    }
                }

                var current = live.GetPrivilege(memberConn);
                if (current == desired)
                {
                    continue;
                }

                if (current == ChannelPrivilege.Op && desired == ChannelPrivilege.Voice)
                {
                    if (live.TryUpdateMemberPrivilege(memberConn, ChannelPrivilege.Normal))
                    {
                        await BroadcastMemberPrivilegeAsync(live, state, u, enable: false, modeChar: 'o', ct);
                    }

                    if (live.TryUpdateMemberPrivilege(memberConn, ChannelPrivilege.Voice))
                    {
                        await BroadcastMemberPrivilegeAsync(live, state, u, enable: true, modeChar: 'v', ct);
                    }

                    continue;
                }

                if (desired == ChannelPrivilege.Normal)
                {
                    var modeChar = current == ChannelPrivilege.Op ? 'o' : (current == ChannelPrivilege.Voice ? 'v' : '\0');
                    if (modeChar != '\0' && live.TryUpdateMemberPrivilege(memberConn, ChannelPrivilege.Normal))
                    {
                        await BroadcastMemberPrivilegeAsync(live, state, u, enable: false, modeChar: modeChar, ct);
                    }

                    continue;
                }

                var upChar = desired == ChannelPrivilege.Op ? 'o' : 'v';
                if (live.TryUpdateMemberPrivilege(memberConn, desired))
                {
                    await BroadcastMemberPrivilegeAsync(live, state, u, enable: true, modeChar: upChar, ct);
                }
            }

            await ReplyAsync(session, "Updated.", ct);
        }

        private async ValueTask EnforceTopicLockAsync(IClientSession session, Channel channel, ChannelTopicLock topicLock, ServerState state, CancellationToken ct)
        {
            _ = session;

            var desired = string.IsNullOrWhiteSpace(topicLock.LockedTopic) ? null : topicLock.LockedTopic;
            if (string.Equals(channel.Topic, desired, StringComparison.Ordinal))
            {
                return;
            }

            var server = _options.Value.ServerInfo?.Name ?? "server";
            var setBy = $"{ChanServMessages.ServiceName}!services@{server}";
            channel.TrySetTopicWithTs(desired, setBy, ChannelTimestamps.NowTs());

            var topicLine = $":{setBy} TOPIC {channel.Name} :{channel.Topic ?? string.Empty}";
            await _routing.BroadcastToChannelAsync(channel, topicLine, excludeConnectionId: null, ct);
        }

        private async ValueTask BroadcastMemberPrivilegeAsync(Channel channel, ServerState state, User user, bool enable, char modeChar, CancellationToken ct)
        {
            var sign = enable ? "+" : "-";

            var csHost = "localhost";
            if (state.TryGetConnectionIdByNick(ChanServMessages.ServiceName, out var csConn) && csConn is not null)
            {
                csHost = state.GetHostFor(csConn);
            }

            var targetNick = user.Nick ?? "*";
            var modeLine = $":{ChanServMessages.ServiceName}!services@{csHost} MODE {channel.Name} {sign}{modeChar} {targetNick}";
            await _routing.BroadcastToChannelAsync(channel, modeLine, excludeConnectionId: null, ct);

            if (_links is not null && !string.IsNullOrWhiteSpace(user.Uid))
            {
                var priv = channel.GetPrivilege(user.ConnectionId);
                await _links.PropagateMemberPrivilegeAsync(channel.Name, user.Uid!, priv, ct);
            }
        }

        private async ValueTask StatusAsync(IClientSession session, string[] args, ServerState state, CancellationToken ct)
        {
            if (args.Length < 1)
            {
                await ReplyAsync(session, "Syntax: STATUS <#channel> [nick]", ct);
                return;
            }

            var channelName = args[0];
            var reg = await _channels.GetByNameAsync(channelName, ct);
            if (reg is null)
            {
                await ReplyAsync(session, "That channel is not registered.", ct);
                return;
            }

            string targetConn;
            string targetNick;

            if (args.Length >= 2 && !string.IsNullOrWhiteSpace(args[1]))
            {
                targetNick = args[1].Trim();
                if (!state.TryGetConnectionIdByNick(targetNick, out var conn) || conn is null)
                {
                    await ReplyAsync(session, "No such nick.", ct);
                    return;
                }

                targetConn = conn;
            }
            else
            {
                targetConn = session.ConnectionId;
                targetNick = session.Nick ?? "*";
            }

            var account = await _auth.GetIdentifiedAccountAsync(targetConn, ct);
            var status = 0;

            if (!string.IsNullOrWhiteSpace(account))
            {
                var flags = reg.GetFlagsFor(account);

                if (flags.HasFlag(ChanServFlags.Founder))
                {
                    status = 3;
                }
                else if (flags.HasFlag(ChanServFlags.Op))
                {
                    status = 2;
                }
                else if (flags.HasFlag(ChanServFlags.Voice))
                {
                    status = 1;
                }
            }

            await ReplyAsync(session, $"STATUS {channelName} {targetNick}: {status}", ct);
        }

        private async ValueTask XopAsync(IClientSession session, string[] args, CancellationToken ct, string label, ChanServFlags entryFlags)
        {
            if (args.Length < 1)
            {
                await ReplyAsync(session, $"Syntax: {label} <#channel> LIST | ADD <account> | DEL <account> | CLEAR", ct);
                return;
            }

            var channelName = args[0];
            var reg = await _channels.GetByNameAsync(channelName, ct);
            if (reg is null)
            {
                await ReplyAsync(session, "That channel is not registered.", ct);
                return;
            }

            // Default to LIST when no subcommand is provided.
            var sub = args.Length >= 2 ? args[1].ToUpperInvariant() : "LIST";

            if (sub == "LIST")
            {
                var matches = reg.Access
                    .Where(kv => !string.Equals(kv.Key, reg.FounderAccount, StringComparison.OrdinalIgnoreCase)
                        && (kv.Value & entryFlags) == entryFlags)
                    .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                if (matches.Length == 0)
                {
                    await ReplyAsync(session, "No access entries.", ct);
                    return;
                }

                foreach (var kv in matches)
                {
                    await ReplyAsync(session, $"{kv.Key}: {ChanServFlagParser.FormatFlags(kv.Value)}", ct);
                }

                return;
            }

            var account = await RequireIdentifiedAccountAsync(session, ct);
            if (account is null)
            {
                return;
            }

            var actorFlags = reg.GetFlagsFor(account);
            if (!actorFlags.HasFlag(ChanServFlags.Founder) && !actorFlags.HasFlag(ChanServFlags.Flags))
            {
                await ReplyAsync(session, "Insufficient privileges.", ct);
                return;
            }

            if (sub == "ADD")
            {
                if (args.Length < 3 || string.IsNullOrWhiteSpace(args[2]))
                {
                    await ReplyAsync(session, $"Syntax: {label} <#channel> ADD <account>", ct);
                    return;
                }

                var targetAccount = args[2].Trim();
                if (string.Equals(targetAccount, reg.FounderAccount, StringComparison.OrdinalIgnoreCase))
                {
                    await ReplyAsync(session, "Cannot change founder flags.", ct);
                    return;
                }

                var map = new Dictionary<string, ChanServFlags>(reg.Access, StringComparer.OrdinalIgnoreCase);
                map.TryGetValue(targetAccount, out var existing);
                map[targetAccount] = existing | entryFlags;

                var updated = reg with { Access = map };
                var ok = await _channels.TryUpdateAsync(updated, ct);
                await ReplyAsync(session, ok ? "Updated." : "Update failed.", ct);
                return;
            }

            if (sub is "DEL" or "DELETE" or "REM" or "REMOVE")
            {
                if (args.Length < 3 || string.IsNullOrWhiteSpace(args[2]))
                {
                    await ReplyAsync(session, $"Syntax: {label} <#channel> DEL <account>", ct);
                    return;
                }

                var targetAccount = args[2].Trim();
                if (string.Equals(targetAccount, reg.FounderAccount, StringComparison.OrdinalIgnoreCase))
                {
                    await ReplyAsync(session, "Cannot change founder flags.", ct);
                    return;
                }

                var map = new Dictionary<string, ChanServFlags>(reg.Access, StringComparer.OrdinalIgnoreCase);
                if (!map.TryGetValue(targetAccount, out var existing))
                {
                    await ReplyAsync(session, "No such entry.", ct);
                    return;
                }

                var updatedFlags = existing & ~entryFlags;
                if (updatedFlags == ChanServFlags.None)
                {
                    map.Remove(targetAccount);
                }
                else
                {
                    map[targetAccount] = updatedFlags;
                }

                var updated = reg with { Access = map };
                var ok = await _channels.TryUpdateAsync(updated, ct);
                await ReplyAsync(session, ok ? "Updated." : "Update failed.", ct);
                return;
            }

            if (sub == "CLEAR")
            {
                var map = new Dictionary<string, ChanServFlags>(reg.Access, StringComparer.OrdinalIgnoreCase);
                var changed = false;

                foreach (var key in map.Keys.ToArray())
                {
                    if (string.Equals(key, reg.FounderAccount, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var before = map[key];
                    var after = before & ~entryFlags;
                    if (after == before)
                    {
                        continue;
                    }

                    changed = true;
                    if (after == ChanServFlags.None)
                    {
                        map.Remove(key);
                    }
                    else
                    {
                        map[key] = after;
                    }
                }

                if (!changed)
                {
                    await ReplyAsync(session, "No access entries.", ct);
                    return;
                }

                var updated = reg with { Access = map };
                var ok = await _channels.TryUpdateAsync(updated, ct);
                await ReplyAsync(session, ok ? "Updated." : "Update failed.", ct);
                return;
            }

            await ReplyAsync(session, $"Syntax: {label} <#channel> LIST | ADD <account> | DEL <account> | CLEAR", ct);
        }

        private async ValueTask ListAsync(IClientSession session, string[] args, CancellationToken ct)
        {
            var mask = args.Length > 0 && !string.IsNullOrWhiteSpace(args[0]) ? args[0].Trim() : "*";

            var matches = _channels.All()
                .Where(ch => ch is not null && !string.IsNullOrWhiteSpace(ch.Name) && IRCd.Core.Services.MaskMatcher.IsMatch(mask, ch.Name))
                .OrderBy(ch => ch.Name, StringComparer.OrdinalIgnoreCase)
                .Take(50)
                .ToArray();

            if (matches.Length == 0)
            {
                await ReplyAsync(session, "No matching registered channels.", ct);
                return;
            }

            await ReplyAsync(session, $"Registered channels matching '{mask}':", ct);
            foreach (var ch in matches)
            {
                var desc = string.IsNullOrWhiteSpace(ch.Description) ? "" : $" - {ch.Description}";
                await ReplyAsync(session, $"{ch.Name}{desc}", ct);
            }
        }

        private async ValueTask IdentifyAsync(IClientSession session, string[] args, CancellationToken ct)
        {
            if (args.Length < 2)
            {
                await ReplyAsync(session, "Syntax: IDENTIFY <#channel> <password>", ct);
                return;
            }

            var channelName = args[0];
            var password = args[1];

            if (!IrcValidation.IsValidChannel(channelName, out _))
            {
                await ReplyAsync(session, "Invalid channel name.", ct);
                return;
            }

            var reg = await _channels.GetByNameAsync(channelName, ct);
            if (reg is null)
            {
                await ReplyAsync(session, "That channel is not registered.", ct);
                return;
            }

            if (!PasswordHasher.Verify(password, reg.PasswordHash))
            {
                await ReplyAsync(session, "Password incorrect.", ct);
                return;
            }

            MarkChannelIdentified(session.ConnectionId, reg.Name);
            await ReplyAsync(session, $"You are now identified for {reg.Name}.", ct);
        }

        private async ValueTask RegisterAsync(IClientSession session, string[] args, ServerState state, CancellationToken ct)
        {
            if (args.Length < 2)
            {
                await ReplyAsync(session, "Syntax: REGISTER <#channel> <password> [description]", ct);
                return;
            }

            var channelName = args[0];
            if (!IrcValidation.IsValidChannel(channelName, out _))
            {
                await ReplyAsync(session, "Invalid channel name.", ct);
                return;
            }

            var password = args[1];
            if (string.IsNullOrWhiteSpace(password))
            {
                await ReplyAsync(session, "Syntax: REGISTER <#channel> <password> [description]", ct);
                return;
            }

            var description = args.Length > 2 ? string.Join(' ', args.Skip(2)) : null;

            var account = await RequireIdentifiedAccountAsync(session, ct);
            if (account is null)
            {
                return;
            }

            if (!state.TryGetChannel(channelName, out var live) || live is null || !live.Contains(session.ConnectionId))
            {
                await ReplyAsync(session, "You must be in the channel to register it.", ct);
                return;
            }

            if (!live.HasPrivilege(session.ConnectionId, ChannelPrivilege.Op))
            {
                await ReplyAsync(session, "You must be channel operator to register it.", ct);
                return;
            }

            var existing = await _channels.GetByNameAsync(channelName, ct);
            if (existing is not null)
            {
                await ReplyAsync(session, "That channel is already registered.", ct);
                return;
            }

            var reg = new RegisteredChannel
            {
                Name = channelName,
                FounderAccount = account,
                PasswordHash = PasswordHasher.Hash(password),
                Description = string.IsNullOrWhiteSpace(description) ? null : description,
                RegisteredAtUtc = DateTimeOffset.UtcNow,
                Access = new Dictionary<string, ChanServFlags>(StringComparer.OrdinalIgnoreCase),
                Akicks = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            };

            var ok = await _channels.TryCreateAsync(reg, ct);
            await ReplyAsync(session, ok ? $"Channel '{channelName}' registered." : "Registration failed.", ct);

            if (ok && _options.Value.Services.ChanServ.AutoJoinRegisteredChannels && reg.GuardEnabled && state.TryGetChannel(channelName, out var active) && active is not null)
            {
                await EnsureChanServJoinedAndOppedAsync(active, state, ct);
            }
        }

        private async ValueTask EnsureChanServJoinedAndOppedAsync(Channel channel, ServerState state, CancellationToken ct)
        {
            if (state.TryGetConnectionIdByNick(ChanServMessages.ServiceName, out var csConn) && csConn is not null)
            {
                if (!channel.Contains(csConn))
                {
                    state.TryJoinChannel(csConn, ChanServMessages.ServiceName, channel.Name);

                    var host = state.GetHostFor(csConn);
                    var joinLine = $":{ChanServMessages.ServiceName}!services@{host} JOIN {channel.Name}";
                    await _routing.BroadcastToChannelAsync(channel, joinLine, excludeConnectionId: null, ct);

                    if (channel.TryUpdateMemberPrivilege(csConn, ChannelPrivilege.Op))
                    {
                        var modeLine = $":{ChanServMessages.ServiceName}!services@{host} MODE {channel.Name} +o {ChanServMessages.ServiceName}";
                        await _routing.BroadcastToChannelAsync(channel, modeLine, excludeConnectionId: null, ct);
                    }
                }
            }
        }

        private async ValueTask DropAsync(IClientSession session, string[] args, CancellationToken ct)
        {
            if (args.Length < 2)
            {
                await ReplyAsync(session, "Syntax: DROP <#channel> <password>", ct);
                return;
            }

            var channelName = args[0];
            var password = args[1];

            var account = await RequireIdentifiedAccountAsync(session, ct);
            if (account is null)
            {
                return;
            }

            var reg = await _channels.GetByNameAsync(channelName, ct);
            if (reg is null)
            {
                await ReplyAsync(session, "That channel is not registered.", ct);
                return;
            }

            if (!string.Equals(reg.FounderAccount, account, StringComparison.OrdinalIgnoreCase))
            {
                await ReplyAsync(session, "Only the founder can drop the channel.", ct);
                return;
            }

            if (!PasswordHasher.Verify(password, reg.PasswordHash))
            {
                await ReplyAsync(session, "Password incorrect.", ct);
                return;
            }

            var ok = await _channels.TryDeleteAsync(reg.Name, ct);
            await ReplyAsync(session, ok ? $"Channel '{reg.Name}' dropped." : "Drop failed.", ct);
        }

        private async ValueTask TopicAsync(IClientSession session, string[] args, ServerState state, CancellationToken ct)
        {
            if (args.Length < 1)
            {
                await ReplyAsync(session, "Syntax: TOPIC <#channel> [topic]", ct);
                return;
            }

            var channelName = args[0];
            if (!state.TryGetChannel(channelName, out var channel) || channel is null)
            {
                await ReplyAsync(session, "No such channel.", ct);
                return;
            }

            if (args.Length == 1)
            {
                if (string.IsNullOrWhiteSpace(channel.Topic))
                {
                    await ReplyAsync(session, "No topic is set.", ct);
                }
                else
                {
                    await ReplyAsync(session, $"Topic: {channel.Topic}", ct);
                }

                return;
            }

            var (reg, flags) = await RequireAccessAsync(session, channelName, ct);
            if (reg is null)
            {
                return;
            }

            if (!flags.HasFlag(ChanServFlags.Op))
            {
                await ReplyAsync(session, "Insufficient privileges.", ct);
                return;
            }

            if (reg.TopicLock is not null && reg.TopicLock.Enabled)
            {
                await ReplyAsync(session, "Topic is locked.", ct);
                return;
            }

            var newTopic = string.Join(' ', args.Skip(1)).Trim();
            if (string.Equals(newTopic, "CLEAR", StringComparison.OrdinalIgnoreCase))
            {
                newTopic = string.Empty;
            }

            var server = _options.Value.ServerInfo?.Name ?? "server";
            var setBy = $"{ChanServMessages.ServiceName}!services@{server}";
            channel.TrySetTopicWithTs(string.IsNullOrWhiteSpace(newTopic) ? null : newTopic, setBy, ChannelTimestamps.NowTs());

            var topicLine = $":{setBy} TOPIC {channel.Name} :{channel.Topic ?? string.Empty}";
            await _routing.BroadcastToChannelAsync(channel, topicLine, excludeConnectionId: null, ct);

            await ReplyAsync(session, "Updated.", ct);
        }

        private async ValueTask InfoAsync(IClientSession session, string[] args, CancellationToken ct)
        {
            if (args.Length < 1)
            {
                await ReplyAsync(session, "Syntax: INFO <#channel>", ct);
                return;
            }

            var channelName = args[0];
            var reg = await _channels.GetByNameAsync(channelName, ct);
            if (reg is null)
            {
                await ReplyAsync(session, $"'{channelName}' is not registered.", ct);
                return;
            }

            await ReplyAsync(session, $"Channel: {reg.Name}", ct);
            await ReplyAsync(session, $"Founder: {reg.FounderAccount}", ct);
            if (!string.IsNullOrWhiteSpace(reg.Description))
            {
                await ReplyAsync(session, $"Description: {reg.Description}", ct);
            }

            if (!string.IsNullOrWhiteSpace(reg.Url))
            {
                await ReplyAsync(session, $"URL: {reg.Url}", ct);
            }

            if (!string.IsNullOrWhiteSpace(reg.Email))
            {
                await ReplyAsync(session, $"Email: {reg.Email}", ct);
            }

            if (!string.IsNullOrWhiteSpace(reg.SuccessorAccount))
            {
                await ReplyAsync(session, $"Successor: {reg.SuccessorAccount}", ct);
            }

            if (!string.IsNullOrWhiteSpace(reg.EntryMessage))
            {
                await ReplyAsync(session, $"EntryMsg: {reg.EntryMessage}", ct);
            }

            if (reg.Mlock is not null)
            {
                var pm = reg.Mlock.SetModes.HasFlag(ChannelModes.Private) ? "ON"
                    : reg.Mlock.ClearModes.HasFlag(ChannelModes.Private) ? "OFF"
                    : null;

                if (!string.IsNullOrWhiteSpace(pm))
                {
                    await ReplyAsync(session, $"Private: {pm}", ct);
                }
            }

            await ReplyAsync(session, $"Restricted: {(reg.RestrictedEnabled ? "ON" : "OFF")}", ct);

            if (reg.SuspendedEnabled)
            {
                var reason = string.IsNullOrWhiteSpace(reg.SuspendedReason) ? "(no reason)" : reg.SuspendedReason;
                await ReplyAsync(session, $"Suspended: ON - {reason}", ct);
            }
            else
            {
                await ReplyAsync(session, "Suspended: OFF", ct);
            }
        }

        private async ValueTask SetAsync(IClientSession session, string[] args, ServerState state, CancellationToken ct)
        {
            if (args.Length < 3)
            {
                await ReplyAsync(session, "Syntax: SET <#channel> DESC <text> | SET <#channel> PASSWORD <newpass> | SET <#channel> MLOCK <modes> | SET <#channel> KEY <key|OFF> | SET <#channel> LIMIT <n|OFF> | SET <#channel> TOPICLOCK ON|OFF [topic] | SET <#channel> GUARD ON|OFF | SET <#channel> SEENSERV ON|OFF | SET <#channel> PRIVATE ON|OFF | SET <#channel> SECRET ON|OFF | SET <#channel> MODERATED ON|OFF | SET <#channel> NOEXTERNAL ON|OFF | SET <#channel> TOPICOPS ON|OFF | SET <#channel> INVITEONLY ON|OFF | SET <#channel> RESTRICTED ON|OFF | SET <#channel> URL <url> | SET <#channel> EMAIL <email> | SET <#channel> SUCCESSOR <account|OFF> | SET <#channel> ENTRYMSG <text|OFF> | SET <#channel> FOUNDER <account>", ct);
                return;
            }

            var channelName = args[0];
            var what = args[1].ToUpperInvariant();
            var rest = args.Skip(2).ToArray();
            var value = string.Join(' ', rest);

            var account = await RequireIdentifiedAccountAsync(session, ct);
            if (account is null)
            {
                return;
            }

            var reg = await _channels.GetByNameAsync(channelName, ct);
            if (reg is null)
            {
                await ReplyAsync(session, "That channel is not registered.", ct);
                return;
            }

            var flags = reg.GetFlagsFor(account);
            if (!flags.HasFlag(ChanServFlags.Founder))
            {
                await ReplyAsync(session, "Insufficient privileges.", ct);
                return;
            }

            RegisteredChannel updated;
            switch (what)
            {
                case "DESC":
                case "DESCRIPTION":
                    updated = reg with { Description = string.IsNullOrWhiteSpace(value) ? null : value };
                    break;

                case "PASSWORD":
                    if (string.IsNullOrWhiteSpace(value))
                    {
                        await ReplyAsync(session, "Syntax: SET <#channel> PASSWORD <newpass>", ct);
                        return;
                    }

                    updated = reg with { PasswordHash = PasswordHasher.Hash(value) };
                    break;

                case "MLOCK":
                    if (!ChanServMlockParser.TryParse(value, out var mlock, out var parseErr))
                    {
                        await ReplyAsync(session, $"Invalid MLOCK: {parseErr}", ct);
                        return;
                    }

                    updated = reg with { Mlock = mlock };
                    break;

                case "KEY":
                    if (rest.Length < 1)
                    {
                        await ReplyAsync(session, "Syntax: SET <#channel> KEY <key|OFF>", ct);
                        return;
                    }

                    var keyValue = string.Join(' ', rest).Trim();
                    if (keyValue.Equals("OFF", StringComparison.OrdinalIgnoreCase) || keyValue.Equals("NONE", StringComparison.OrdinalIgnoreCase) || keyValue.Equals("0", StringComparison.OrdinalIgnoreCase))
                    {
                        updated = reg with
                        {
                            Mlock = new ChannelMlock
                            {
                                SetModes = reg.Mlock?.SetModes ?? ChannelModes.None,
                                ClearModes = (reg.Mlock?.ClearModes ?? ChannelModes.None) | ChannelModes.Key,
                                KeyLocked = false,
                                Key = null,
                                LimitLocked = reg.Mlock?.LimitLocked ?? false,
                                Limit = reg.Mlock?.Limit,
                                Raw = (reg.Mlock?.Raw ?? string.Empty).Trim()
                            }
                        };
                        break;
                    }

                    updated = reg with
                    {
                        Mlock = new ChannelMlock
                        {
                            SetModes = (reg.Mlock?.SetModes ?? ChannelModes.None) | ChannelModes.Key,
                            ClearModes = (reg.Mlock?.ClearModes ?? ChannelModes.None) & ~ChannelModes.Key,
                            KeyLocked = true,
                            Key = keyValue,
                            LimitLocked = reg.Mlock?.LimitLocked ?? false,
                            Limit = reg.Mlock?.Limit,
                            Raw = $"{(reg.Mlock?.Raw ?? string.Empty).Trim()} +k"
                        }
                    };
                    break;

                case "LIMIT":
                    if (rest.Length < 1)
                    {
                        await ReplyAsync(session, "Syntax: SET <#channel> LIMIT <n|OFF>", ct);
                        return;
                    }

                    var limitToken = rest[0].Trim();
                    if (limitToken.Equals("OFF", StringComparison.OrdinalIgnoreCase) || limitToken.Equals("NONE", StringComparison.OrdinalIgnoreCase) || limitToken.Equals("0", StringComparison.OrdinalIgnoreCase))
                    {
                        updated = reg with
                        {
                            Mlock = new ChannelMlock
                            {
                                SetModes = reg.Mlock?.SetModes ?? ChannelModes.None,
                                ClearModes = (reg.Mlock?.ClearModes ?? ChannelModes.None) | ChannelModes.Limit,
                                KeyLocked = reg.Mlock?.KeyLocked ?? false,
                                Key = reg.Mlock?.Key,
                                LimitLocked = false,
                                Limit = null,
                                Raw = (reg.Mlock?.Raw ?? string.Empty).Trim()
                            }
                        };
                        break;
                    }

                    if (!int.TryParse(limitToken, out var limit) || limit < 1)
                    {
                        await ReplyAsync(session, "Syntax: SET <#channel> LIMIT <n|OFF>", ct);
                        return;
                    }

                    updated = reg with
                    {
                        Mlock = new ChannelMlock
                        {
                            SetModes = (reg.Mlock?.SetModes ?? ChannelModes.None) | ChannelModes.Limit,
                            ClearModes = (reg.Mlock?.ClearModes ?? ChannelModes.None) & ~ChannelModes.Limit,
                            KeyLocked = reg.Mlock?.KeyLocked ?? false,
                            Key = reg.Mlock?.Key,
                            LimitLocked = true,
                            Limit = limit,
                            Raw = $"{(reg.Mlock?.Raw ?? string.Empty).Trim()} +l"
                        }
                    };
                    break;

                case "PRIVATE":
                    if (rest.Length < 1)
                    {
                        await ReplyAsync(session, "Syntax: SET <#channel> PRIVATE ON|OFF", ct);
                        return;
                    }

                    var pv = rest[0].ToUpperInvariant();
                    if (pv is not ("ON" or "OFF" or "TRUE" or "FALSE" or "1" or "0"))
                    {
                        await ReplyAsync(session, "Syntax: SET <#channel> PRIVATE ON|OFF", ct);
                        return;
                    }

                    var privateEnable = pv is "ON" or "TRUE" or "1";
                    updated = reg with { Mlock = UpdateMlockFlag(reg.Mlock, ChannelModes.Private, privateEnable) };
                    break;

                case "SECRET":
                    if (rest.Length < 1)
                    {
                        await ReplyAsync(session, "Syntax: SET <#channel> SECRET ON|OFF", ct);
                        return;
                    }

                    var scv = rest[0].ToUpperInvariant();
                    if (scv is not ("ON" or "OFF" or "TRUE" or "FALSE" or "1" or "0"))
                    {
                        await ReplyAsync(session, "Syntax: SET <#channel> SECRET ON|OFF", ct);
                        return;
                    }

                    var secretEnable = scv is "ON" or "TRUE" or "1";
                    updated = reg with { Mlock = UpdateMlockFlag(reg.Mlock, ChannelModes.Secret, secretEnable) };
                    break;

                case "MODERATED":
                    if (rest.Length < 1)
                    {
                        await ReplyAsync(session, "Syntax: SET <#channel> MODERATED ON|OFF", ct);
                        return;
                    }

                    var mv = rest[0].ToUpperInvariant();
                    if (mv is not ("ON" or "OFF" or "TRUE" or "FALSE" or "1" or "0"))
                    {
                        await ReplyAsync(session, "Syntax: SET <#channel> MODERATED ON|OFF", ct);
                        return;
                    }

                    var moderatedEnable = mv is "ON" or "TRUE" or "1";
                    updated = reg with { Mlock = UpdateMlockFlag(reg.Mlock, ChannelModes.Moderated, moderatedEnable) };
                    break;

                case "NOEXTERNAL":
                case "NOEXTERNALMESSAGES":
                    if (rest.Length < 1)
                    {
                        await ReplyAsync(session, "Syntax: SET <#channel> NOEXTERNAL ON|OFF", ct);
                        return;
                    }

                    var nv = rest[0].ToUpperInvariant();
                    if (nv is not ("ON" or "OFF" or "TRUE" or "FALSE" or "1" or "0"))
                    {
                        await ReplyAsync(session, "Syntax: SET <#channel> NOEXTERNAL ON|OFF", ct);
                        return;
                    }

                    var noExternalEnable = nv is "ON" or "TRUE" or "1";
                    updated = reg with { Mlock = UpdateMlockFlag(reg.Mlock, ChannelModes.NoExternalMessages, noExternalEnable) };
                    break;

                case "TOPICOPS":
                case "TOPICOPSONLY":
                    if (rest.Length < 1)
                    {
                        await ReplyAsync(session, "Syntax: SET <#channel> TOPICOPS ON|OFF", ct);
                        return;
                    }

                    var tv = rest[0].ToUpperInvariant();
                    if (tv is not ("ON" or "OFF" or "TRUE" or "FALSE" or "1" or "0"))
                    {
                        await ReplyAsync(session, "Syntax: SET <#channel> TOPICOPS ON|OFF", ct);
                        return;
                    }

                    var topicOpsEnable = tv is "ON" or "TRUE" or "1";
                    updated = reg with { Mlock = UpdateMlockFlag(reg.Mlock, ChannelModes.TopicOpsOnly, topicOpsEnable) };
                    break;

                case "INVITEONLY":
                    if (rest.Length < 1)
                    {
                        await ReplyAsync(session, "Syntax: SET <#channel> INVITEONLY ON|OFF", ct);
                        return;
                    }

                    var iv = rest[0].ToUpperInvariant();
                    if (iv is not ("ON" or "OFF" or "TRUE" or "FALSE" or "1" or "0"))
                    {
                        await ReplyAsync(session, "Syntax: SET <#channel> INVITEONLY ON|OFF", ct);
                        return;
                    }

                    var inviteEnable = iv is "ON" or "TRUE" or "1";
                    updated = reg with { Mlock = UpdateMlockFlag(reg.Mlock, ChannelModes.InviteOnly, inviteEnable) };
                    break;

                case "RESTRICTED":
                    if (rest.Length < 1)
                    {
                        await ReplyAsync(session, "Syntax: SET <#channel> RESTRICTED ON|OFF", ct);
                        return;
                    }

                    var rv = rest[0].ToUpperInvariant();
                    if (rv is not ("ON" or "OFF" or "TRUE" or "FALSE" or "1" or "0"))
                    {
                        await ReplyAsync(session, "Syntax: SET <#channel> RESTRICTED ON|OFF", ct);
                        return;
                    }

                    var restrictedEnable = rv is "ON" or "TRUE" or "1";
                    updated = reg with { RestrictedEnabled = restrictedEnable };
                    break;

                case "URL":
                    if (string.IsNullOrWhiteSpace(value))
                    {
                        await ReplyAsync(session, "Syntax: SET <#channel> URL <url>", ct);
                        return;
                    }

                    updated = reg with { Url = value.Trim() };
                    break;

                case "EMAIL":
                    if (string.IsNullOrWhiteSpace(value))
                    {
                        await ReplyAsync(session, "Syntax: SET <#channel> EMAIL <email>", ct);
                        return;
                    }

                    updated = reg with { Email = value.Trim() };
                    break;

                case "SUCCESSOR":
                    if (rest.Length < 1)
                    {
                        await ReplyAsync(session, "Syntax: SET <#channel> SUCCESSOR <account|OFF>", ct);
                        return;
                    }

                    var succ = rest[0].Trim();
                    if (succ.Equals("OFF", StringComparison.OrdinalIgnoreCase) || succ.Equals("NONE", StringComparison.OrdinalIgnoreCase) || succ.Equals("0", StringComparison.OrdinalIgnoreCase))
                    {
                        updated = reg with { SuccessorAccount = null };
                        break;
                    }

                    var succAcc = await _nickAccounts.GetByNameAsync(succ, ct);
                    if (succAcc is null)
                    {
                        await ReplyAsync(session, "No such account.", ct);
                        return;
                    }

                    updated = reg with { SuccessorAccount = succAcc.Name };
                    break;

                case "ENTRYMSG":
                    if (rest.Length < 1)
                    {
                        await ReplyAsync(session, "Syntax: SET <#channel> ENTRYMSG <text|OFF>", ct);
                        return;
                    }

                    var em = string.Join(' ', rest).Trim();
                    if (em.Equals("OFF", StringComparison.OrdinalIgnoreCase) || em.Equals("NONE", StringComparison.OrdinalIgnoreCase) || em.Equals("0", StringComparison.OrdinalIgnoreCase))
                    {
                        updated = reg with { EntryMessage = null };
                        break;
                    }

                    updated = reg with { EntryMessage = string.IsNullOrWhiteSpace(em) ? null : em };
                    break;

                case "FOUNDER":
                    if (rest.Length < 1)
                    {
                        await ReplyAsync(session, "Syntax: SET <#channel> FOUNDER <account>", ct);
                        return;
                    }

                    var newFounder = rest[0].Trim();
                    var newFounderAcc = await _nickAccounts.GetByNameAsync(newFounder, ct);
                    if (newFounderAcc is null)
                    {
                        await ReplyAsync(session, "No such account.", ct);
                        return;
                    }

                    var accessCopy = new Dictionary<string, ChanServFlags>(reg.Access, StringComparer.OrdinalIgnoreCase);
                    accessCopy.Remove(newFounderAcc.Name);
                    updated = reg with { FounderAccount = newFounderAcc.Name, Access = accessCopy };
                    break;

                case "TOPICLOCK":
                    if (rest.Length < 1)
                    {
                        await ReplyAsync(session, "Syntax: SET <#channel> TOPICLOCK ON|OFF [topic]", ct);
                        return;
                    }

                    var mode = rest[0].ToUpperInvariant();
                    if (mode == "OFF")
                    {
                        updated = reg with { TopicLock = new ChannelTopicLock { Enabled = false, LockedTopic = null } };
                        break;
                    }

                    if (mode == "ON")
                    {
                        string? locked;
                        if (rest.Length > 1)
                        {
                            locked = string.Join(' ', rest.Skip(1));
                        }
                        else if (state.TryGetChannel(channelName, out var live) && live is not null)
                        {
                            locked = live.Topic;
                        }
                        else
                        {
                            locked = reg.TopicLock?.LockedTopic;
                        }

                        if (string.IsNullOrWhiteSpace(locked) && (reg.TopicLock is null || string.IsNullOrWhiteSpace(reg.TopicLock.LockedTopic)))
                        {
                            await ReplyAsync(session, "No current topic to lock; specify one: SET <#channel> TOPICLOCK ON <topic>", ct);
                            return;
                        }

                        locked = string.IsNullOrWhiteSpace(locked) ? null : locked;
                        updated = reg with { TopicLock = new ChannelTopicLock { Enabled = true, LockedTopic = locked } };
                        break;
                    }

                    await ReplyAsync(session, "Syntax: SET <#channel> TOPICLOCK ON|OFF [topic]", ct);
                    return;

                case "GUARD":
                    if (rest.Length < 1)
                    {
                        await ReplyAsync(session, "Syntax: SET <#channel> GUARD ON|OFF", ct);
                        return;
                    }

                    var gv = rest[0].ToUpperInvariant();
                    if (gv is not ("ON" or "OFF" or "TRUE" or "FALSE" or "1" or "0"))
                    {
                        await ReplyAsync(session, "Syntax: SET <#channel> GUARD ON|OFF", ct);
                        return;
                    }

                    var enable = gv is "ON" or "TRUE" or "1";
                    updated = reg with { GuardEnabled = enable };
                    break;

                case "SEENSERV":
                    if (rest.Length < 1)
                    {
                        await ReplyAsync(session, "Syntax: SET <#channel> SEENSERV ON|OFF", ct);
                        return;
                    }

                    var sv = rest[0].ToUpperInvariant();
                    if (sv is not ("ON" or "OFF" or "TRUE" or "FALSE" or "1" or "0"))
                    {
                        await ReplyAsync(session, "Syntax: SET <#channel> SEENSERV ON|OFF", ct);
                        return;
                    }

                    var seenEnable = sv is "ON" or "TRUE" or "1";
                    updated = reg with { SeenServEnabled = seenEnable };
                    break;

                default:
                    await ReplyAsync(session, "Unknown SET option.", ct);
                    return;
            }

            var ok = await _channels.TryUpdateAsync(updated, ct);
            await ReplyAsync(session, ok ? "Updated." : "Update failed.", ct);

            if (ok && what is ("MLOCK" or "PRIVATE" or "INVITEONLY" or "KEY" or "LIMIT" or "SECRET" or "MODERATED" or "NOEXTERNAL" or "NOEXTERNALMESSAGES" or "TOPICOPS" or "TOPICOPSONLY"))
            {
                Channel? active = null;
                if (!state.TryGetChannel(updated.Name, out active) || active is null)
                {
                    state.TryGetChannel(channelName, out active);
                }

                if (active is not null && updated.Mlock is not null)
                {
                    await EnforceMlockAsync(active, updated.Mlock, state, ct);
                }
            }

            if (ok && what == "TOPICLOCK")
            {
                Channel? active = null;
                if (!state.TryGetChannel(updated.Name, out active) || active is null)
                {
                    state.TryGetChannel(channelName, out active);
                }

                if (active is not null && updated.TopicLock is not null && updated.TopicLock.Enabled)
                {
                    var server = _options.Value.ServerInfo?.Name ?? "server";
                    var setBy = $"{ChanServMessages.ServiceName}!services@{server}";
                    active.TrySetTopicWithTs(updated.TopicLock.LockedTopic, setBy, ChannelTimestamps.NowTs());
                    var topicLine = $":{setBy} TOPIC {active.Name} :{active.Topic ?? string.Empty}";
                    await _routing.BroadcastToChannelAsync(active, topicLine, excludeConnectionId: null, ct);
                }
            }

            if (ok && what == "SEENSERV")
            {
                Channel? active = null;

                if (!state.TryGetChannel(updated.Name, out active) || active is null)
                {
                    state.TryGetChannel(channelName, out active);
                }

                if (active is null)
                {
                    var liveName = state.GetUserChannels(session.ConnectionId)
                        .FirstOrDefault(n => string.Equals(n, updated.Name, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(n, channelName, StringComparison.OrdinalIgnoreCase));

                    if (!string.IsNullOrWhiteSpace(liveName))
                    {
                        state.TryGetChannel(liveName, out active);
                    }
                }

                if (active is not null)
                {
                    if (updated.SeenServEnabled)
                    {
                        await EnsureSeenServJoinedAndVoicedAsync(active, state, ct);
                    }
                    else
                    {
                        await EnsureSeenServPartedAsync(active, state, ct);
                    }
                }
            }
        }

        private static ChannelMlock UpdateMlockFlag(ChannelMlock? existing, ChannelModes mode, bool enable)
        {
            var set = existing?.SetModes ?? ChannelModes.None;
            var clear = existing?.ClearModes ?? ChannelModes.None;

            if (enable)
            {
                set |= mode;
                clear &= ~mode;
            }
            else
            {
                clear |= mode;
                set &= ~mode;
            }

            var raw = existing?.Raw;
            if (string.IsNullOrWhiteSpace(raw))
            {
                var modeChar = mode switch
                {
                    ChannelModes.Private => 'p',
                    ChannelModes.Secret => 's',
                    ChannelModes.InviteOnly => 'i',
                    ChannelModes.Moderated => 'm',
                    ChannelModes.NoExternalMessages => 'n',
                    ChannelModes.TopicOpsOnly => 't',
                    _ => 'p'
                };

                raw = enable ? $"+{modeChar}" : $"-{modeChar}";
            }

            return new ChannelMlock
            {
                SetModes = set,
                ClearModes = clear,
                KeyLocked = existing?.KeyLocked ?? false,
                Key = existing?.Key,
                LimitLocked = existing?.LimitLocked ?? false,
                Limit = existing?.Limit,
                Raw = raw.Trim()
            };
        }

        private async ValueTask EnsureSeenServJoinedAndVoicedAsync(Channel channel, ServerState state, CancellationToken ct)
        {
            if (!state.TryGetConnectionIdByNick(SeenServMessages.ServiceName, out var ssConn) || ssConn is null)
            {
                return;
            }

            if (!channel.Contains(ssConn))
            {
                state.TryJoinChannel(ssConn, SeenServMessages.ServiceName, channel.Name);

                var ssHost = state.GetHostFor(ssConn);
                var joinLine = $":{SeenServMessages.ServiceName}!services@{ssHost} JOIN {channel.Name}";
                await _routing.BroadcastToChannelAsync(channel, joinLine, excludeConnectionId: null, ct);
            }

            string actorNick;
            string actorHost;
            if (state.TryGetConnectionIdByNick(ChanServMessages.ServiceName, out var csConn) && csConn is not null)
            {
                await EnsureChanServJoinedAndOppedAsync(channel, state, ct);
                actorNick = ChanServMessages.ServiceName;
                actorHost = state.GetHostFor(csConn);
            }
            else
            {
                actorNick = SeenServMessages.ServiceName;
                actorHost = state.GetHostFor(ssConn);
            }

            var current = channel.GetPrivilege(ssConn);
            if (current < ChannelPrivilege.Voice)
            {
                if (channel.TryUpdateMemberPrivilege(ssConn, ChannelPrivilege.Voice))
                {
                    var modeLine = $":{actorNick}!services@{actorHost} MODE {channel.Name} +v {SeenServMessages.ServiceName}";
                    await _routing.BroadcastToChannelAsync(channel, modeLine, excludeConnectionId: null, ct);
                }
            }
        }

        private async ValueTask EnsureSeenServPartedAsync(Channel channel, ServerState state, CancellationToken ct)
        {
            if (!state.TryGetConnectionIdByNick(SeenServMessages.ServiceName, out var ssConn) || ssConn is null)
            {
                return;
            }

            if (!channel.Contains(ssConn))
            {
                return;
            }

            if (state.TryPartChannel(ssConn, channel.Name, out _))
            {
                var ssHost = state.GetHostFor(ssConn);
                var partLine = $":{SeenServMessages.ServiceName}!services@{ssHost} PART {channel.Name}";
                await _routing.BroadcastToChannelAsync(channel, partLine, excludeConnectionId: null, ct);
            }
        }

        private async ValueTask AkickAsync(IClientSession session, string[] args, CancellationToken ct)
        {
            if (args.Length < 2)
            {
                await ReplyAsync(session, "Syntax: AKICK <#channel> ADD <account> [reason] | DEL <account> | LIST", ct);
                return;
            }

            var channelName = args[0];
            var sub = args[1].ToUpperInvariant();

            var account = await RequireIdentifiedAccountAsync(session, ct);
            if (account is null)
            {
                return;
            }

            var reg = await _channels.GetByNameAsync(channelName, ct);
            if (reg is null)
            {
                await ReplyAsync(session, "That channel is not registered.", ct);
                return;
            }

            var actorFlags = reg.GetFlagsFor(account);
            if (!actorFlags.HasFlag(ChanServFlags.Founder) && !actorFlags.HasFlag(ChanServFlags.Flags))
            {
                await ReplyAsync(session, "Insufficient privileges.", ct);
                return;
            }

            if (sub == "LIST")
            {
                if (reg.Akicks.Count == 0)
                {
                    await ReplyAsync(session, "AKICK list is empty.", ct);
                    return;
                }

                foreach (var kv in reg.Akicks.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
                {
                    var r = string.IsNullOrWhiteSpace(kv.Value) ? "(no reason)" : kv.Value;
                    await ReplyAsync(session, $"{kv.Key}: {r}", ct);
                }

                return;
            }

            if (sub == "ADD")
            {
                if (args.Length < 3 || string.IsNullOrWhiteSpace(args[2]))
                {
                    await ReplyAsync(session, "Syntax: AKICK <#channel> ADD <account> [reason]", ct);
                    return;
                }

                var targetAccount = args[2].Trim();
                var reason = args.Length > 3 ? string.Join(' ', args.Skip(3)) : null;

                var map = new Dictionary<string, string?>(reg.Akicks, StringComparer.OrdinalIgnoreCase)
                {
                    [targetAccount] = string.IsNullOrWhiteSpace(reason) ? null : reason
                };

                var updated = reg with { Akicks = map };
                var ok = await _channels.TryUpdateAsync(updated, ct);
                await ReplyAsync(session, ok ? "Updated." : "Update failed.", ct);
                return;
            }

            if (sub == "DEL" || sub == "DELETE" || sub == "REMOVE")
            {
                if (args.Length < 3 || string.IsNullOrWhiteSpace(args[2]))
                {
                    await ReplyAsync(session, "Syntax: AKICK <#channel> DEL <account>", ct);
                    return;
                }

                var targetAccount = args[2].Trim();
                var map = new Dictionary<string, string?>(reg.Akicks, StringComparer.OrdinalIgnoreCase);
                var removed = map.Remove(targetAccount);
                var updated = reg with { Akicks = map };
                var ok = removed && await _channels.TryUpdateAsync(updated, ct);
                await ReplyAsync(session, ok ? "Updated." : "No such entry.", ct);
                return;
            }

            await ReplyAsync(session, "Syntax: AKICK <#channel> ADD <account> [reason] | DEL <account> | LIST", ct);
        }

        public async ValueTask OnUserJoinedAsync(IClientSession session, Channel channel, ServerState state, CancellationToken ct)
        {
            var reg = await _channels.GetByNameAsync(channel.Name, ct);
            if (reg is null)
            {
                return;
            }

            if (reg.SuspendedEnabled)
            {
                var suspendReason = string.IsNullOrWhiteSpace(reg.SuspendedReason) ? "SUSPENDED" : reg.SuspendedReason;
                await EnforceKickAsync(channel.Name, session.ConnectionId, session.Nick ?? "*", state, suspendReason, ct);
                return;
            }

            async ValueTask SendEntryMsgIfConfiguredAsync()
            {
                if (string.IsNullOrWhiteSpace(reg.EntryMessage))
                {
                    return;
                }

                if (!channel.Contains(session.ConnectionId) || string.IsNullOrWhiteSpace(session.Nick))
                {
                    return;
                }

                var host = "localhost";
                if (state.TryGetConnectionIdByNick(ChanServMessages.ServiceName, out var csConn) && csConn is not null)
                {
                    host = state.GetHostFor(csConn);
                }

                var line = $":{ChanServMessages.ServiceName}!services@{host} NOTICE {session.Nick} :{reg.EntryMessage}";
                await _routing.SendToUserAsync(session.ConnectionId, line, ct);
            }

            if (_options.Value.Services.ChanServ.AutoJoinRegisteredChannels && reg.GuardEnabled)
            {
                await EnsureChanServJoinedAndOppedAsync(channel, state, ct);
            }

            if (reg.SeenServEnabled)
            {
                await EnsureSeenServJoinedAndVoicedAsync(channel, state, ct);
            }

            var account = await _auth.GetIdentifiedAccountAsync(session.ConnectionId, ct);
            if (string.IsNullOrWhiteSpace(account))
            {
                if (reg.RestrictedEnabled)
                {
                    await EnforceKickAsync(channel.Name, session.ConnectionId, session.Nick ?? "*", state, "RESTRICTED", ct);
                    return;
                }

                var currentPriv = channel.GetPrivilege(session.ConnectionId);
                if (currentPriv != ChannelPrivilege.Normal)
                {
                    channel.TryUpdateMemberPrivilege(session.ConnectionId, ChannelPrivilege.Normal);
                }

                await SendEntryMsgIfConfiguredAsync();

                return;
            }

            if (reg.TryGetAkickReason(account, out var reason))
            {
                await EnforceKickAsync(channel.Name, session.ConnectionId, session.Nick ?? "*", state, reason, ct);
                return;
            }

            var flags = reg.GetFlagsFor(account);
            if (reg.RestrictedEnabled && flags == ChanServFlags.None)
            {
                await EnforceKickAsync(channel.Name, session.ConnectionId, session.Nick ?? "*", state, "RESTRICTED", ct);
                return;
            }

            if (reg.Mlock is not null)
            {
                await EnforceMlockAsync(channel, reg.Mlock, state, ct);
            }

            var desiredPrivilege = ChannelPrivilege.Normal;
            if (flags.HasFlag(ChanServFlags.Founder) || flags.HasFlag(ChanServFlags.Op))
            {
                desiredPrivilege = ChannelPrivilege.Op;
            }
            else if (flags.HasFlag(ChanServFlags.Voice))
            {
                desiredPrivilege = ChannelPrivilege.Voice;
            }

            var current = channel.GetPrivilege(session.ConnectionId);
            if (desiredPrivilege == ChannelPrivilege.Normal && current != ChannelPrivilege.Normal)
            {
                channel.TryUpdateMemberPrivilege(session.ConnectionId, ChannelPrivilege.Normal);
                await SendEntryMsgIfConfiguredAsync();
                return;
            }

            if (desiredPrivilege == ChannelPrivilege.Normal)
            {
                await SendEntryMsgIfConfiguredAsync();
                return;
            }

            if (current >= desiredPrivilege)
            {
                await SendEntryMsgIfConfiguredAsync();
                return;
            }

            if (!channel.TryUpdateMemberPrivilege(session.ConnectionId, desiredPrivilege))
            {
                await SendEntryMsgIfConfiguredAsync();
                return;
            }

            var nick = session.Nick ?? "*";
            var modeChar = desiredPrivilege switch
            {
                ChannelPrivilege.Op => 'o',
                ChannelPrivilege.Voice => 'v',
                _ => '\0'
            };
            
            if (modeChar != '\0')
            {
                var host = "localhost";
                if (state.TryGetConnectionIdByNick(ChanServMessages.ServiceName, out var csConn) && csConn is not null)
                {
                    host = state.GetHostFor(csConn);
                }

                var modeLine = $":{ChanServMessages.ServiceName}!services@{host} MODE {channel.Name} +{modeChar} {nick}";
                await _routing.BroadcastToChannelAsync(channel, modeLine, excludeConnectionId: null, ct);
            }

            await SendEntryMsgIfConfiguredAsync();
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

        private async ValueTask SuspendAsync(IClientSession session, string[] args, ServerState state, CancellationToken ct)
        {
            if (!await RequireOperCapabilityAsync(session, state, "cs_suspend", ct))
            {
                return;
            }

            if (args.Length < 1)
            {
                await ReplyAsync(session, "Syntax: SUSPEND <#channel> [reason]", ct);
                return;
            }

            var channelName = args[0];
            var reg = await _channels.GetByNameAsync(channelName, ct);
            if (reg is null)
            {
                await ReplyAsync(session, "That channel is not registered.", ct);
                return;
            }

            var reason = args.Length > 1 ? string.Join(' ', args.Skip(1)) : null;
            var by = session.Nick;
            var updated = reg with
            {
                SuspendedEnabled = true,
                SuspendedReason = string.IsNullOrWhiteSpace(reason) ? null : reason,
                SuspendedBy = string.IsNullOrWhiteSpace(by) ? null : by,
                SuspendedAtUtc = DateTimeOffset.UtcNow
            };

            var ok = await _channels.TryUpdateAsync(updated, ct);
            await ReplyAsync(session, ok ? "Updated." : "Update failed.", ct);
        }

        private async ValueTask UnsuspendAsync(IClientSession session, string[] args, ServerState state, CancellationToken ct)
        {
            if (!await RequireOperCapabilityAsync(session, state, "cs_suspend", ct))
            {
                return;
            }

            if (args.Length < 1)
            {
                await ReplyAsync(session, "Syntax: UNSUSPEND <#channel>", ct);
                return;
            }

            var channelName = args[0];
            var reg = await _channels.GetByNameAsync(channelName, ct);
            if (reg is null)
            {
                await ReplyAsync(session, "That channel is not registered.", ct);
                return;
            }

            var updated = reg with
            {
                SuspendedEnabled = false,
                SuspendedReason = null,
                SuspendedBy = null,
                SuspendedAtUtc = null
            };

            var ok = await _channels.TryUpdateAsync(updated, ct);
            await ReplyAsync(session, ok ? "Updated." : "Update failed.", ct);
        }

        public async ValueTask OnChannelModeChangedAsync(IClientSession session, Channel channel, ServerState state, CancellationToken ct)
        {
            _ = session;

            var reg = await _channels.GetByNameAsync(channel.Name, ct);
            if (reg?.Mlock is null)
            {
                return;
            }

            await EnforceMlockAsync(channel, reg.Mlock, state, ct);
        }

        public async ValueTask OnChannelTopicChangedAsync(IClientSession session, Channel channel, string? previousTopic, ServerState state, CancellationToken ct)
        {
            _ = previousTopic;
            _ = state;

            var reg = await _channels.GetByNameAsync(channel.Name, ct);
            if (reg?.TopicLock is null || !reg.TopicLock.Enabled)
            {
                return;
            }

            var desired = string.IsNullOrWhiteSpace(reg.TopicLock.LockedTopic) ? null : reg.TopicLock.LockedTopic;
            if (string.Equals(channel.Topic, desired, StringComparison.Ordinal))
            {
                return;
            }

            var server = _options.Value.ServerInfo?.Name ?? "server";
            var setBy = $"{ChanServMessages.ServiceName}!services@{server}";
            channel.TrySetTopicWithTs(desired, setBy, ChannelTimestamps.NowTs());
            await ReplyAsync(session, "Topic is locked.", ct);
        }

        private async ValueTask EnforceKickAsync(string channelName, string targetConn, string targetNick, ServerState state, string? reason, CancellationToken ct)
        {
            if (!state.TryGetChannel(channelName, out var ch) || ch is null)
            {
                return;
            }

            if (!ch.Contains(targetConn))
            {
                return;
            }

            var msg = string.IsNullOrWhiteSpace(reason) ? "AKICK" : reason;
            var line = $":{ChanServMessages.ServiceName}!services@localhost KICK {channelName} {targetNick} :{msg}";

            if (!state.TryPartChannel(targetConn, channelName, out var updated) || updated is null)
            {
                return;
            }

            await _routing.BroadcastToChannelAsync(updated, line, excludeConnectionId: null, ct);
            await _routing.SendToUserAsync(targetConn, line, ct);
        }

        private async ValueTask EnforceMlockAsync(Channel channel, ChannelMlock mlock, ServerState state, CancellationToken ct)
        {
            _ = state;

            var beforeModes = channel.Modes;
            var beforeKey = channel.Key;
            var beforeLimit = channel.UserLimit;

            ApplyMlock(channel, mlock);

            var changed = beforeModes != channel.Modes || !string.Equals(beforeKey, channel.Key, StringComparison.Ordinal) || beforeLimit != channel.UserLimit;
            if (!changed)
            {
                return;
            }

            var csHost = "localhost";
            if (state.TryGetConnectionIdByNick(ChanServMessages.ServiceName, out var csConn) && csConn is not null)
            {
                csHost = state.GetHostFor(csConn);
            }

            var mode = channel.FormatModeString();
            var modeArgs = new List<string>();

            if (channel.Modes.HasFlag(ChannelModes.Key))
            {
                modeArgs.Add("*");
            }

            if (channel.Modes.HasFlag(ChannelModes.Limit) && channel.UserLimit.HasValue)
            {
                modeArgs.Add(channel.UserLimit.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            var modeLine = modeArgs.Count == 0
                ? $":{ChanServMessages.ServiceName}!services@{csHost} MODE {channel.Name} {mode}"
                : $":{ChanServMessages.ServiceName}!services@{csHost} MODE {channel.Name} {mode} {string.Join(' ', modeArgs)}";
            await _routing.BroadcastToChannelAsync(channel, modeLine, excludeConnectionId: null, ct);
        }

        private static void ApplyMlock(Channel channel, ChannelMlock mlock)
        {
            foreach (var (mode, enable) in EnumerateModeBits(mlock.SetModes, true).Concat(EnumerateModeBits(mlock.ClearModes, false)))
            {
                channel.ApplyModeChange(mode, enable);
            }

            if (mlock.KeyLocked)
            {
                channel.SetKey(mlock.Key);
            }

            if (mlock.LimitLocked)
            {
                channel.SetLimit(mlock.Limit);
            }
        }

        private static IEnumerable<(ChannelModes Mode, bool Enable)> EnumerateModeBits(ChannelModes modes, bool enable)
        {
            if (modes.HasFlag(ChannelModes.NoExternalMessages)) yield return (ChannelModes.NoExternalMessages, enable);
            if (modes.HasFlag(ChannelModes.TopicOpsOnly)) yield return (ChannelModes.TopicOpsOnly, enable);
            if (modes.HasFlag(ChannelModes.InviteOnly)) yield return (ChannelModes.InviteOnly, enable);
            if (modes.HasFlag(ChannelModes.Moderated)) yield return (ChannelModes.Moderated, enable);
            if (modes.HasFlag(ChannelModes.Private)) yield return (ChannelModes.Private, enable);
            if (modes.HasFlag(ChannelModes.Secret)) yield return (ChannelModes.Secret, enable);
        }

        private async ValueTask FlagsAsync(IClientSession session, string[] args, CancellationToken ct)
        {
            if (args.Length < 1)
            {
                await ReplyAsync(session, "Syntax: FLAGS <#channel> [account] [flags]", ct);
                return;
            }

            var channelName = args[0];
            var reg = await _channels.GetByNameAsync(channelName, ct);
            if (reg is null)
            {
                await ReplyAsync(session, "That channel is not registered.", ct);
                return;
            }

            if (args.Length == 1)
            {
                if (reg.Access.Count == 0)
                {
                    await ReplyAsync(session, "No access entries.", ct);
                    return;
                }

                foreach (var kv in reg.Access.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
                {
                    await ReplyAsync(session, $"{kv.Key}: {ChanServFlagParser.FormatFlags(kv.Value)}", ct);
                }

                return;
            }

            var account = await RequireIdentifiedAccountAsync(session, ct);
            if (account is null)
            {
                return;
            }

            var actorFlags = reg.GetFlagsFor(account);
            if (!actorFlags.HasFlag(ChanServFlags.Founder) && !actorFlags.HasFlag(ChanServFlags.Flags))
            {
                await ReplyAsync(session, "Insufficient privileges.", ct);
                return;
            }

            var targetAccount = args[1];
            if (string.IsNullOrWhiteSpace(targetAccount))
            {
                await ReplyAsync(session, "Syntax: FLAGS <#channel> <account> <flags>", ct);
                return;
            }

            if (args.Length < 3)
            {
                var f = reg.GetFlagsFor(targetAccount);
                await ReplyAsync(session, $"{targetAccount}: {ChanServFlagParser.FormatFlags(f)}", ct);
                return;
            }

            var flagsText = string.Join(' ', args.Skip(2));
            if (!ChanServFlagParser.TryParse(flagsText, out var newFlags))
            {
                await ReplyAsync(session, "Invalid flags.", ct);
                return;
            }

            if (string.Equals(targetAccount, reg.FounderAccount, StringComparison.OrdinalIgnoreCase))
            {
                await ReplyAsync(session, "Cannot change founder flags.", ct);
                return;
            }

            var map = new Dictionary<string, ChanServFlags>(reg.Access, StringComparer.OrdinalIgnoreCase)
            {
                [targetAccount] = newFlags
            };

            var updated = reg with { Access = map };
            var ok = await _channels.TryUpdateAsync(updated, ct);
            await ReplyAsync(session, ok ? "Updated." : "Update failed.", ct);
        }

        private async ValueTask AccessAsync(IClientSession session, string[] args, CancellationToken ct)
        {
            if (args.Length < 1)
            {
                await ReplyAsync(session, "Syntax: ACCESS <#channel> LIST | ACCESS <#channel> ADD <account> <flags> | ACCESS <#channel> DEL <account> | ACCESS <#channel> CLEAR", ct);
                return;
            }

            var channelName = args[0];
            var reg = await _channels.GetByNameAsync(channelName, ct);
            if (reg is null)
            {
                await ReplyAsync(session, "That channel is not registered.", ct);
                return;
            }

            var sub = args.Length >= 2 ? args[1].ToUpperInvariant() : "LIST";

            if (sub == "LIST")
            {
                if (reg.Access.Count == 0)
                {
                    await ReplyAsync(session, "No access entries.", ct);
                    return;
                }

                foreach (var kv in reg.Access.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
                {
                    await ReplyAsync(session, $"{kv.Key}: {ChanServFlagParser.FormatFlags(kv.Value)}", ct);
                }

                return;
            }

            var account = await RequireIdentifiedAccountAsync(session, ct);
            if (account is null)
            {
                return;
            }

            var actorFlags = reg.GetFlagsFor(account);
            if (!actorFlags.HasFlag(ChanServFlags.Founder) && !actorFlags.HasFlag(ChanServFlags.Flags))
            {
                await ReplyAsync(session, "Insufficient privileges.", ct);
                return;
            }

            if (sub == "ADD")
            {
                if (args.Length < 4)
                {
                    await ReplyAsync(session, "Syntax: ACCESS <#channel> ADD <account> <flags>", ct);
                    return;
                }

                var targetAccount = args[2];
                if (string.IsNullOrWhiteSpace(targetAccount))
                {
                    await ReplyAsync(session, "Syntax: ACCESS <#channel> ADD <account> <flags>", ct);
                    return;
                }

                if (string.Equals(targetAccount, reg.FounderAccount, StringComparison.OrdinalIgnoreCase))
                {
                    await ReplyAsync(session, "Cannot change founder flags.", ct);
                    return;
                }

                var flagsText = string.Join(' ', args.Skip(3));
                if (!ChanServFlagParser.TryParse(flagsText, out var newFlags))
                {
                    await ReplyAsync(session, "Invalid flags.", ct);
                    return;
                }

                var map = new Dictionary<string, ChanServFlags>(reg.Access, StringComparer.OrdinalIgnoreCase)
                {
                    [targetAccount] = newFlags
                };

                var updated = reg with { Access = map };
                var ok = await _channels.TryUpdateAsync(updated, ct);
                await ReplyAsync(session, ok ? "Updated." : "Update failed.", ct);
                return;
            }

            if (sub == "DEL")
            {
                if (args.Length < 3)
                {
                    await ReplyAsync(session, "Syntax: ACCESS <#channel> DEL <account>", ct);
                    return;
                }

                var targetAccount = args[2];
                if (string.IsNullOrWhiteSpace(targetAccount))
                {
                    await ReplyAsync(session, "Syntax: ACCESS <#channel> DEL <account>", ct);
                    return;
                }

                if (string.Equals(targetAccount, reg.FounderAccount, StringComparison.OrdinalIgnoreCase))
                {
                    await ReplyAsync(session, "Cannot change founder flags.", ct);
                    return;
                }

                if (!reg.Access.ContainsKey(targetAccount))
                {
                    await ReplyAsync(session, "No such access entry.", ct);
                    return;
                }

                var map = new Dictionary<string, ChanServFlags>(reg.Access, StringComparer.OrdinalIgnoreCase);
                map.Remove(targetAccount);

                var updated = reg with { Access = map };
                var ok = await _channels.TryUpdateAsync(updated, ct);
                await ReplyAsync(session, ok ? "Updated." : "Update failed.", ct);
                return;
            }

            if (sub == "CLEAR")
            {
                if (reg.Access.Count == 0)
                {
                    await ReplyAsync(session, "No access entries.", ct);
                    return;
                }

                var updated = reg with { Access = new Dictionary<string, ChanServFlags>(StringComparer.OrdinalIgnoreCase) };
                var ok = await _channels.TryUpdateAsync(updated, ct);
                await ReplyAsync(session, ok ? "Updated." : "Update failed.", ct);
                return;
            }

            await ReplyAsync(session, "Syntax: ACCESS <#channel> LIST | ACCESS <#channel> ADD <account> <flags> | ACCESS <#channel> DEL <account> | ACCESS <#channel> CLEAR", ct);
        }

        private async ValueTask InviteAsync(IClientSession session, string[] args, ServerState state, CancellationToken ct)
        {
            if (args.Length < 2)
            {
                await ReplyAsync(session, "Syntax: INVITE <#channel> <nick>", ct);
                return;
            }

            var channelName = args[0];
            var targetNick = args[1];

            var (reg, flags) = await RequireAccessAsync(session, channelName, ct);
            if (reg is null)
            {
                return;
            }

            if (!flags.HasFlag(ChanServFlags.Invite))
            {
                await ReplyAsync(session, "Insufficient privileges.", ct);
                return;
            }

            if (!state.TryGetChannel(channelName, out var live) || live is null)
            {
                await ReplyAsync(session, "No such channel.", ct);
                return;
            }

            if (!state.TryGetConnectionIdByNick(targetNick, out var targetConn) || targetConn is null)
            {
                await ReplyAsync(session, "No such nick.", ct);
                return;
            }

            live.AddInvite(targetNick);

            var fromNick = ChanServMessages.ServiceName;
            var fromUser = "services";
            var inviteLine = $":{fromNick}!{fromUser}@localhost INVITE {targetNick} :{channelName}";
            await _routing.SendToUserAsync(targetConn, inviteLine, ct);
            await ReplyAsync(session, $"Invited {targetNick} to {channelName}.", ct);
        }

        private async ValueTask RecoverAsync(IClientSession session, string[] args, ServerState state, CancellationToken ct)
        {
            if (args.Length < 1)
            {
                await ReplyAsync(session, "Syntax: RECOVER <#channel> [nick]", ct);
                return;
            }

            var channelName = args[0];
            var targetNick = args.Length > 1 && !string.IsNullOrWhiteSpace(args[1])
                ? args[1].Trim()
                : (session.Nick ?? string.Empty);

            var (reg, flags) = await RequireAccessAsync(session, channelName, ct);
            if (reg is null)
            {
                return;
            }

            if (!flags.HasFlag(ChanServFlags.Op))
            {
                await ReplyAsync(session, "Insufficient privileges.", ct);
                return;
            }

            if (!state.TryGetChannel(channelName, out var live) || live is null)
            {
                await ReplyAsync(session, "No such channel.", ct);
                return;
            }

            if (!state.TryGetConnectionIdByNick(targetNick, out var targetConn) || targetConn is null)
            {
                await ReplyAsync(session, "No such nick.", ct);
                return;
            }

            if (!live.Contains(targetConn))
            {
                await ReplyAsync(session, "They aren't on that channel.", ct);
                return;
            }

            if (_options.Value.Services.ChanServ.AutoJoinRegisteredChannels && reg.GuardEnabled)
            {
                await EnsureChanServJoinedAndOppedAsync(live, state, ct);
            }

            if (reg.Mlock is not null)
            {
                await EnforceMlockAsync(live, reg.Mlock, state, ct);
            }

            var current = live.GetPrivilege(targetConn);
            if (current < ChannelPrivilege.Op)
            {
                live.TryUpdateMemberPrivilege(targetConn, ChannelPrivilege.Op);

                var csHost = "localhost";
                if (state.TryGetConnectionIdByNick(ChanServMessages.ServiceName, out var csConn) && csConn is not null)
                {
                    csHost = state.GetHostFor(csConn);
                }

                var modeLine = $":{ChanServMessages.ServiceName}!services@{csHost} MODE {channelName} +o {targetNick}";
                await _routing.BroadcastToChannelAsync(live, modeLine, excludeConnectionId: null, ct);
            }

            await ReplyAsync(session, "Updated.", ct);
        }

        private async ValueTask KickAsync(IClientSession session, string[] args, ServerState state, CancellationToken ct)
        {
            if (args.Length < 2)
            {
                await ReplyAsync(session, "Syntax: KICK <#channel> <nick> [reason]", ct);
                return;
            }

            var channelName = args[0];
            var targetNick = args[1];
            var reason = args.Length > 2 ? string.Join(' ', args.Skip(2)) : targetNick;

            var (reg, flags) = await RequireAccessAsync(session, channelName, ct);
            if (reg is null)
            {
                return;
            }

            if (!flags.HasFlag(ChanServFlags.Kick))
            {
                await ReplyAsync(session, "Insufficient privileges.", ct);
                return;
            }

            if (!state.TryGetChannel(channelName, out var live) || live is null)
            {
                await ReplyAsync(session, "No such channel.", ct);
                return;
            }

            if (!state.TryGetConnectionIdByNick(targetNick, out var targetConn) || targetConn is null)
            {
                await ReplyAsync(session, "No such nick.", ct);
                return;
            }

            if (!live.Contains(targetConn))
            {
                await ReplyAsync(session, "They aren't on that channel.", ct);
                return;
            }

            var line = $":{ChanServMessages.ServiceName}!services@localhost KICK {channelName} {targetNick} :{reason}";

            if (!state.TryPartChannel(targetConn, channelName, out var updatedChannel) || updatedChannel is null)
            {
                await ReplyAsync(session, "KICK failed unexpectedly.", ct);
                return;
            }

            await _routing.BroadcastToChannelAsync(updatedChannel, line, excludeConnectionId: null, ct);
            if (!updatedChannel.Contains(targetConn))
            {
                await _routing.SendToUserAsync(targetConn, line, ct);
            }

            await ReplyAsync(session, $"Kicked {targetNick} from {channelName}.", ct);
        }

        private async ValueTask ModePrivilegeAsync(
            IClientSession session,
            string[] args,
            ServerState state,
            CancellationToken ct,
            bool enable,
            char modeChar,
            ChanServFlags required,
            string label)
        {
            if (args.Length < 1)
            {
                await ReplyAsync(session, $"Syntax: {label} <#channel> [nick]", ct);
                return;
            }

            var channelName = args[0];
            var targetNick = args.Length > 1 && !string.IsNullOrWhiteSpace(args[1]) ? args[1] : (session.Nick ?? "");
            if (string.IsNullOrWhiteSpace(targetNick))
            {
                await ReplyAsync(session, $"Syntax: {label} <#channel> [nick]", ct);
                return;
            }

            var (reg, flags) = await RequireAccessAsync(session, channelName, ct);
            if (reg is null)
            {
                return;
            }

            if (!flags.HasFlag(required))
            {
                await ReplyAsync(session, "Insufficient privileges.", ct);
                return;
            }

            if (!state.TryGetChannel(channelName, out var live) || live is null)
            {
                await ReplyAsync(session, "No such channel.", ct);
                return;
            }

            if (!live.Contains(session.ConnectionId))
            {
                await ReplyAsync(session, "You must be in the channel.", ct);
                return;
            }

            if (!state.TryGetConnectionIdByNick(targetNick, out var targetConn) || targetConn is null)
            {
                await ReplyAsync(session, "No such nick.", ct);
                return;
            }

            if (!live.Contains(targetConn))
            {
                await ReplyAsync(session, "They aren't on that channel.", ct);
                return;
            }

            var desired = modeChar switch
            {
                'v' => ChannelPrivilege.Voice,
                'o' => ChannelPrivilege.Op,
                _ => ChannelPrivilege.Normal
            };

            if (desired == ChannelPrivilege.Normal)
            {
                await ReplyAsync(session, "Unsupported mode.", ct);
                return;
            }

            if (enable)
            {
                var current = live.GetPrivilege(targetConn);
                if (current < desired)
                {
                    live.TryUpdateMemberPrivilege(targetConn, desired);
                }
            }
            else
            {
                var current = live.GetPrivilege(targetConn);
                if (current == desired)
                {
                    live.TryUpdateMemberPrivilege(targetConn, ChannelPrivilege.Normal);
                }
            }

            var sign = enable ? "+" : "-";
            var csHost = "localhost";
            if (state.TryGetConnectionIdByNick(ChanServMessages.ServiceName, out var csConn) && csConn is not null)
            {
                csHost = state.GetHostFor(csConn);
            }

            var modeLine = $":{ChanServMessages.ServiceName}!services@{csHost} MODE {channelName} {sign}{modeChar} {targetNick}";
            await _routing.BroadcastToChannelAsync(live, modeLine, excludeConnectionId: null, ct);

            if (_links is not null && state.TryGetUser(targetConn, out var u) && u is not null && !string.IsNullOrWhiteSpace(u.Uid))
            {
                var priv = live.GetPrivilege(targetConn);
                await _links.PropagateMemberPrivilegeAsync(channelName, u.Uid!, priv, ct);
            }
        }

        private async ValueTask BanAsync(IClientSession session, string[] args, ServerState state, CancellationToken ct)
        {
            if (args.Length < 2)
            {
                await ReplyAsync(session, "Syntax: BAN <#channel> <nick|mask>", ct);
                return;
            }

            var channelName = args[0];
            var who = args[1];

            var (reg, flags) = await RequireAccessAsync(session, channelName, ct);
            if (reg is null)
            {
                return;
            }

            if (!flags.HasFlag(ChanServFlags.Ban))
            {
                await ReplyAsync(session, "Insufficient privileges.", ct);
                return;
            }

            if (!state.TryGetChannel(channelName, out var live) || live is null)
            {
                await ReplyAsync(session, "No such channel.", ct);
                return;
            }

            if (!live.Contains(session.ConnectionId))
            {
                await ReplyAsync(session, "You must be in the channel.", ct);
                return;
            }

            var mask = ResolveNickOrMaskToBanMask(state, who);
            if (string.IsNullOrWhiteSpace(mask))
            {
                await ReplyAsync(session, "Invalid nick or mask.", ct);
                return;
            }

            var setBy = $":{ChanServMessages.ServiceName}!services@localhost";
            var setAt = DateTimeOffset.UtcNow;

            if (!live.AddBan(mask, setBy, setAt))
            {
                await ReplyAsync(session, "Ban already exists.", ct);
                return;
            }

            var modeLine = $":{ChanServMessages.ServiceName}!services@localhost MODE {channelName} +b {mask}";
            await _routing.BroadcastToChannelAsync(live, modeLine, excludeConnectionId: null, ct);

            if (_links is not null)
            {
                await _links.PropagateBanAsync(channelName, live.CreatedTs, mask, setBy.TrimStart(':'), setAt.ToUnixTimeSeconds(), ct);
            }
        }

        private async ValueTask UnbanAsync(IClientSession session, string[] args, ServerState state, CancellationToken ct)
        {
            if (args.Length < 1)
            {
                await ReplyAsync(session, "Syntax: UNBAN <#channel> [nick|mask]", ct);
                return;
            }

            var channelName = args[0];

            if (args.Length == 1)
            {
                await ClearAsync(session, new[] { channelName, "BANS" }, state, ct);
                return;
            }

            var who = args[1];

            var (reg, flags) = await RequireAccessAsync(session, channelName, ct);
            if (reg is null)
            {
                return;
            }

            if (!flags.HasFlag(ChanServFlags.Ban))
            {
                await ReplyAsync(session, "Insufficient privileges.", ct);
                return;
            }

            if (!state.TryGetChannel(channelName, out var live) || live is null)
            {
                await ReplyAsync(session, "No such channel.", ct);
                return;
            }

            if (!live.Contains(session.ConnectionId))
            {
                await ReplyAsync(session, "You must be in the channel.", ct);
                return;
            }

            var mask = ResolveNickOrMaskToBanMask(state, who);
            if (string.IsNullOrWhiteSpace(mask))
            {
                await ReplyAsync(session, "Invalid nick or mask.", ct);
                return;
            }

            if (!live.RemoveBan(mask))
            {
                await ReplyAsync(session, "No such ban.", ct);
                return;
            }

            var modeLine = $":{ChanServMessages.ServiceName}!services@localhost MODE {channelName} -b {mask}";
            await _routing.BroadcastToChannelAsync(live, modeLine, excludeConnectionId: null, ct);

            if (_links is not null)
            {
                await _links.PropagateBanDelAsync(channelName, live.CreatedTs, mask, ct);
            }
        }

        private async ValueTask ClearAsync(IClientSession session, string[] args, ServerState state, CancellationToken ct)
        {
            if (args.Length < 2)
            {
                await ReplyAsync(session, "Syntax: CLEAR <#channel> BANS|OPS|VOICES|TOPIC|MODES|USERS", ct);
                return;
            }

            var channelName = args[0];
            var what = args[1].ToUpperInvariant();

            if (what is not ("BANS" or "OPS" or "VOICES" or "TOPIC" or "MODES" or "USERS"))
            {
                await ReplyAsync(session, "Syntax: CLEAR <#channel> BANS|OPS|VOICES|TOPIC|MODES|USERS", ct);
                return;
            }

            var (reg, flags) = await RequireAccessAsync(session, channelName, ct);
            if (reg is null)
            {
                return;
            }

            var required = what switch
            {
                "BANS" => ChanServFlags.Ban,
                "OPS" => ChanServFlags.Op,
                "VOICES" => ChanServFlags.Voice,
                "TOPIC" => ChanServFlags.Op,
                "MODES" => ChanServFlags.Flags,
                "USERS" => ChanServFlags.Kick,
                _ => ChanServFlags.None
            };

            if (required != ChanServFlags.None && !flags.HasFlag(required))
            {
                await ReplyAsync(session, "Insufficient privileges.", ct);
                return;
            }

            if (!state.TryGetChannel(channelName, out var live) || live is null)
            {
                await ReplyAsync(session, "No such channel.", ct);
                return;
            }

            if (!live.Contains(session.ConnectionId))
            {
                await ReplyAsync(session, "You must be in the channel.", ct);
                return;
            }

            var csHost = "localhost";
            if (state.TryGetConnectionIdByNick(ChanServMessages.ServiceName, out var csConn) && csConn is not null)
            {
                csHost = state.GetHostFor(csConn);
            }

            if (what == "TOPIC")
            {
                if (string.IsNullOrWhiteSpace(live.Topic))
                {
                    await ReplyAsync(session, "No topic to clear.", ct);
                    return;
                }

                var setBy = $"{ChanServMessages.ServiceName}!services@{csHost}";
                live.SetTopic(null, setBy);

                var topicLine = $":{ChanServMessages.ServiceName}!services@{csHost} TOPIC {channelName} :";
                await _routing.BroadcastToChannelAsync(live, topicLine, excludeConnectionId: null, ct);

                await ReplyAsync(session, "Topic cleared.", ct);
                return;
            }

            if (what == "MODES")
            {
                live.ApplyModeChange(ChannelModes.NoExternalMessages, enable: true);
                live.ApplyModeChange(ChannelModes.TopicOpsOnly, enable: true);

                live.ApplyModeChange(ChannelModes.InviteOnly, enable: false);
                live.ApplyModeChange(ChannelModes.Moderated, enable: false);
                live.ApplyModeChange(ChannelModes.Private, enable: false);
                live.ApplyModeChange(ChannelModes.Secret, enable: false);

                live.ClearKey();
                live.ClearLimit();

                var modeLine = $":{ChanServMessages.ServiceName}!services@{csHost} MODE {channelName} {live.FormatModeString()}";
                await _routing.BroadcastToChannelAsync(live, modeLine, excludeConnectionId: null, ct);

                await ReplyAsync(session, "Modes cleared.", ct);
                return;
            }

            if (what == "USERS")
            {
                var targets = live.Members
                    .Where(m => m is not null
                        && !string.IsNullOrWhiteSpace(m.ConnectionId)
                        && m.ConnectionId != session.ConnectionId
                        && !string.Equals(m.Nick, ChanServMessages.ServiceName, StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(m.Nick, SeenServMessages.ServiceName, StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(m.Nick, NickServ.NickServMessages.ServiceName, StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(m.Nick, MemoServ.MemoServMessages.ServiceName, StringComparison.OrdinalIgnoreCase))
                    .ToArray();

                if (targets.Length == 0)
                {
                    await ReplyAsync(session, "No users to clear.", ct);
                    return;
                }

                foreach (var m in targets)
                {
                    var nick = string.IsNullOrWhiteSpace(m.Nick) ? "*" : m.Nick;
                    var line = $":{ChanServMessages.ServiceName}!services@{csHost} KICK {channelName} {nick} :CLEARED";

                    if (!state.TryPartChannel(m.ConnectionId, channelName, out var updated) || updated is null)
                    {
                        continue;
                    }

                    await _routing.BroadcastToChannelAsync(updated, line, excludeConnectionId: null, ct);
                    await _routing.SendToUserAsync(m.ConnectionId, line, ct);
                }

                await ReplyAsync(session, "Users cleared.", ct);
                return;
            }

            if (what == "OPS")
            {
                var targets = live.Members
                    .Where(m => m is not null
                        && !string.IsNullOrWhiteSpace(m.Nick)
                        && !string.Equals(m.Nick, ChanServMessages.ServiceName, StringComparison.OrdinalIgnoreCase)
                        && (m.Privilege.ToPrefix() == '@'))
                    .ToArray();

                if (targets.Length == 0)
                {
                    await ReplyAsync(session, "No ops to clear.", ct);
                    return;
                }

                foreach (var m in targets)
                {
                    if (!live.TryUpdateMemberPrivilege(m.ConnectionId, ChannelPrivilege.Normal))
                    {
                        continue;
                    }

                    var modeLine = $":{ChanServMessages.ServiceName}!services@{csHost} MODE {channelName} -o {m.Nick}";
                    await _routing.BroadcastToChannelAsync(live, modeLine, excludeConnectionId: null, ct);

                    if (_links is not null && state.TryGetUser(m.ConnectionId, out var u) && u is not null && !string.IsNullOrWhiteSpace(u.Uid))
                    {
                        await _links.PropagateMemberPrivilegeAsync(channelName, u.Uid!, ChannelPrivilege.Normal, ct);
                    }
                }

                await ReplyAsync(session, "Ops cleared.", ct);
                return;
            }

            if (what == "VOICES")
            {
                var targets = live.Members
                    .Where(m => m is not null
                        && !string.IsNullOrWhiteSpace(m.Nick)
                        && !string.Equals(m.Nick, ChanServMessages.ServiceName, StringComparison.OrdinalIgnoreCase)
                        && m.Privilege == ChannelPrivilege.Voice)
                    .ToArray();

                if (targets.Length == 0)
                {
                    await ReplyAsync(session, "No voices to clear.", ct);
                    return;
                }

                foreach (var m in targets)
                {
                    if (!live.TryUpdateMemberPrivilege(m.ConnectionId, ChannelPrivilege.Normal))
                    {
                        continue;
                    }

                    var modeLine = $":{ChanServMessages.ServiceName}!services@{csHost} MODE {channelName} -v {m.Nick}";
                    await _routing.BroadcastToChannelAsync(live, modeLine, excludeConnectionId: null, ct);

                    if (_links is not null && state.TryGetUser(m.ConnectionId, out var u) && u is not null && !string.IsNullOrWhiteSpace(u.Uid))
                    {
                        await _links.PropagateMemberPrivilegeAsync(channelName, u.Uid!, ChannelPrivilege.Normal, ct);
                    }
                }

                await ReplyAsync(session, "Voices cleared.", ct);
                return;
            }

            var bans = live.Bans.ToArray();
            if (bans.Length == 0)
            {
                await ReplyAsync(session, "No bans to clear.", ct);
                return;
            }

            foreach (var b in bans)
            {
                if (!live.RemoveBan(b.Mask))
                {
                    continue;
                }

                var modeLine = $":{ChanServMessages.ServiceName}!services@localhost MODE {channelName} -b {b.Mask}";
                await _routing.BroadcastToChannelAsync(live, modeLine, excludeConnectionId: null, ct);

                if (_links is not null)
                {
                    await _links.PropagateBanDelAsync(channelName, live.CreatedTs, b.Mask, ct);
                }
            }

            await ReplyAsync(session, "Bans cleared.", ct);
        }

        private static string? ResolveNickOrMaskToBanMask(ServerState state, string input)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                return null;
            }

            if (input.Contains('!') || input.Contains('@') || input.Contains('*') || input.Contains('?'))
            {
                return input.Trim();
            }

            if (!state.TryGetConnectionIdByNick(input.Trim(), out var connId) || string.IsNullOrWhiteSpace(connId))
            {
                return null;
            }

            var host = state.GetHostFor(connId!);
            return $"*!*@{host}";
        }

        private async ValueTask<string?> RequireIdentifiedAccountAsync(IClientSession session, CancellationToken ct)
        {
            var account = await _auth.GetIdentifiedAccountAsync(session.ConnectionId, ct);
            if (string.IsNullOrWhiteSpace(account))
            {
                await ReplyAsync(session, "You must be identified to use ChanServ.", ct);
                return null;
            }

            var exists = await _nickAccounts.GetByNameAsync(account, ct);
            if (exists is null)
            {
                await ReplyAsync(session, "You must be identified to a registered account.", ct);
                return null;
            }

            return account;
        }

        private async ValueTask<(RegisteredChannel? Channel, ChanServFlags Flags)> RequireAccessAsync(IClientSession session, string channelName, CancellationToken ct)
        {
            var reg = await _channels.GetByNameAsync(channelName, ct);
            if (reg is null)
            {
                await ReplyAsync(session, "That channel is not registered.", ct);
                return (null, ChanServFlags.None);
            }

            var account = await _auth.GetIdentifiedAccountAsync(session.ConnectionId, ct);
            if (!string.IsNullOrWhiteSpace(account))
            {
                var exists = await _nickAccounts.GetByNameAsync(account, ct);
                if (exists is not null)
                {
                    return (reg, reg.GetFlagsFor(account));
                }
            }

            if (IsChannelIdentified(session.ConnectionId, reg.Name))
            {
                return (reg, ChanServFlags.All);
            }

            await ReplyAsync(session, "You must be identified to a registered account, or IDENTIFY to the channel.", ct);
            return (null, ChanServFlags.None);
        }

        private void MarkChannelIdentified(string connectionId, string channelName)
        {
            if (string.IsNullOrWhiteSpace(connectionId) || string.IsNullOrWhiteSpace(channelName))
            {
                return;
            }

            var set = _identifiedChannelsByConnection.GetOrAdd(connectionId, _ => new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase));
            set[channelName] = 0;
        }

        private bool IsChannelIdentified(string connectionId, string channelName)
        {
            if (string.IsNullOrWhiteSpace(connectionId) || string.IsNullOrWhiteSpace(channelName))
            {
                return false;
            }

            return _identifiedChannelsByConnection.TryGetValue(connectionId, out var set) && set.ContainsKey(channelName);
        }

        private async ValueTask ReplyManyAsync(IClientSession session, CancellationToken ct, params string[] lines)
        {
            foreach (var l in lines)
            {
                if (!string.IsNullOrWhiteSpace(l))
                {
                    await ReplyAsync(session, l, ct);
                }
            }
        }

        private ValueTask ReplyAsync(IClientSession session, string text, CancellationToken ct)
        {
            var server = _options.Value.ServerInfo?.Name ?? "server";
            var to = session.Nick ?? "*";
            var line = $":{ChanServMessages.ServiceName}!services@{server} NOTICE {to} :{text}";
            return session.SendAsync(line, ct);
        }
    }
}
