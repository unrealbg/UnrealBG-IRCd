namespace IRCd.Core.Commands.Handlers
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Core.Abstractions;
    using IRCd.Core.Commands.Contracts;
    using IRCd.Core.Protocol;
    using IRCd.Core.Services;
    using IRCd.Core.State;
    using IRCd.Shared.Options;
    using Microsoft.Extensions.Options;

    public sealed class ModeHandler : IIrcCommandHandler
    {
        public string Command => "MODE";

        private readonly RoutingService _routing;
        private readonly ServerLinkService _links;
        private readonly HostmaskService _hostmask;
        private readonly IServiceChannelEvents? _channelEvents;
        private readonly IOptions<IrcOptions> _options;

        public ModeHandler(RoutingService routing, ServerLinkService links, HostmaskService hostmask, IOptions<IrcOptions> options, IServiceChannelEvents? channelEvents = null)
        {
            _routing = routing;
            _links = links;
            _hostmask = hostmask;
            _options = options;
            _channelEvents = channelEvents;
        }

        public async ValueTask HandleAsync(IClientSession session, IrcMessage msg, ServerState state, CancellationToken ct)
        {
            if (!session.IsRegistered)
            {
                await session.SendAsync($":server 451 {(session.Nick ?? "*")} :You have not registered", ct);
                return;
            }

            if (msg.Params.Count < 1)
            {
                await session.SendAsync($":server 461 {session.Nick} MODE :Not enough parameters", ct);
                return;
            }

            var target = msg.Params[0]?.Trim();

            if (string.IsNullOrWhiteSpace(target))
            {
                await session.SendAsync($":server 461 {session.Nick} MODE :Not enough parameters", ct);
                return;
            }

            if (!target.StartsWith("#", StringComparison.Ordinal))
            {
                await HandleUserModeAsync(session, msg, state, ct);
                return;
            }

            if (!IrcValidation.IsValidChannel(target, out _))
            {
                await session.SendAsync($":server 403 {session.Nick} {target} :No such channel", ct);
                return;
            }

            if (msg.Params.Count == 1)
            {
                if (!state.TryGetChannel(target, out var ch) || ch is null)
                {
                    await session.SendAsync($":server 403 {session.Nick} {target} :No such channel", ct);
                    return;
                }

                await session.SendAsync($":server 324 {session.Nick} {target} {ch.FormatModeString()}", ct);
                return;
            }

            var modeToken = msg.Params[1];
            if (string.IsNullOrWhiteSpace(modeToken) || (modeToken[0] != '+' && modeToken[0] != '-'))
            {
                await session.SendAsync($":server 501 {session.Nick} :Unknown MODE flags", ct);
                return;
            }

            var providedArgs = msg.Params.Count - 2;
            var requiredUserArgs = modeToken.Count(ch => ch is 'q' or 'a' or 'o' or 'h' or 'v');
            var allowImplicitSelfTarget = providedArgs == 0 && requiredUserArgs == 1;

            var argIndex = 2;

            var appliedModes = new List<char>();
            var appliedArgs = new List<string>();

            char? lastAppliedSign = null;
            string? deferredError = null;

            void RecordAppliedMode(char signChar, char modeChar)
            {
                if (lastAppliedSign != signChar)
                {
                    appliedModes.Add(signChar);
                    lastAppliedSign = signChar;
                }

                appliedModes.Add(modeChar);
            }

            char sign = modeToken[0];

            var channelModeChanges = ParseChannelModeChanges(modeToken);
            if (channelModeChanges.Count > 0)
            {
                var changed = state.TryApplyChannelModes(target, session.ConnectionId, channelModeChanges, out var channel, out var error);
                if (channel is null)
                {
                    await SendModeError(session, target, error, ct);
                    return;
                }

                if (changed)
                {
                    var tmpSign = modeToken[0];
                    for (int j = 1; j < modeToken.Length; j++)
                    {
                        var mc = modeToken[j];
                        if (mc == '+' || mc == '-')
                        {
                            tmpSign = mc;
                            continue;
                        }

                        if (mc is 'n' or 't')
                        {
                            RecordAppliedMode(tmpSign, mc);
                        }
                    }
                }
            }

            var currentSign = sign;

            for (int i = 1; i < modeToken.Length; i++)
            {
                var c = modeToken[i];

                if (c == '+' || c == '-')
                {
                    currentSign = c;
                    continue;
                }

                if (c is 'n' or 't')
                {
                    continue;
                }

                if (c is not ('q' or 'a' or 'o' or 'h' or 'v' or 'b' or 'e' or 'I' or 'i' or 'k' or 'l' or 'm' or 'p' or 's'))
                {
                    continue;
                }

                if (c is 'i' or 'k' or 'l' or 'm' or 'p' or 's')
                {
                    if (!state.TryGetChannel(target, out var ch) || ch is null)
                    {
                        deferredError = $":server 403 {session.Nick} {target} :No such channel";
                        break;
                    }

                    if (!ch.Contains(session.ConnectionId))
                    {
                        deferredError = $":server 442 {session.Nick} {target} :You're not on that channel";
                        break;
                    }

                    if (!ch.HasPrivilege(session.ConnectionId, ChannelPrivilege.Op))
                    {
                        deferredError = $":server 482 {session.Nick} {target} :You're not channel operator";
                        break;
                    }

                    var setOn = currentSign == '+';

                    if (c == 'i')
                    {
                        var changed = ch.ApplyModeChange(ChannelModes.InviteOnly, setOn);
                        if (!changed) continue;

                        RecordAppliedMode(currentSign, 'i');
                        continue;
                    }

                    if (c == 'k')
                    {
                        if (setOn)
                        {
                            if (msg.Params.Count <= argIndex)
                            {
                                deferredError = $":server 461 {session.Nick} MODE :Not enough parameters";
                                break;
                            }

                            var keyArg = msg.Params[argIndex++];
                            ch.SetKey(keyArg);

                            RecordAppliedMode(currentSign, 'k');
                            appliedArgs.Add(keyArg);
                        }
                        else
                        {
                            ch.SetKey(null);

                            RecordAppliedMode(currentSign, 'k');
                        }

                        continue;
                    }

                    if (c == 'l')
                    {
                        if (setOn)
                        {
                            if (msg.Params.Count <= argIndex)
                            {
                                deferredError = $":server 461 {session.Nick} MODE :Not enough parameters";
                                break;
                            }

                            var raw = msg.Params[argIndex++];
                            if (!int.TryParse(raw, out var limitArg) || limitArg <= 0)
                            {
                                deferredError = $":server 461 {session.Nick} MODE :Invalid limit";
                                break;
                            }

                            ch.SetLimit(limitArg);

                            RecordAppliedMode(currentSign, 'l');
                            appliedArgs.Add(raw);
                        }
                        else
                        {
                            ch.SetLimit(null);

                            RecordAppliedMode(currentSign, 'l');
                        }

                        continue;
                    }

                    if (c == 'm')
                    {
                        var changed = ch.ApplyModeChange(ChannelModes.Moderated, setOn);
                        if (!changed) continue;

                        RecordAppliedMode(currentSign, 'm');
                        continue;
                    }

                    if (c == 'p')
                    {
                        var changed = ch.ApplyModeChange(ChannelModes.Private, setOn);
                        if (!changed) continue;

                        RecordAppliedMode(currentSign, 'p');
                        continue;
                    }

                    if (c == 's')
                    {
                        var changed = ch.ApplyModeChange(ChannelModes.Secret, setOn);
                        if (!changed) continue;

                        RecordAppliedMode(currentSign, 's');
                        continue;
                    }
                }

                if (c == 'b')
                {
                    if (!state.TryGetChannel(target, out var ch) || ch is null)
                    {
                        await session.SendAsync($":server 403 {session.Nick} {target} :No such channel", ct);
                        return;
                    }

                    if (!ch.Contains(session.ConnectionId))
                    {
                        await session.SendAsync($":server 442 {session.Nick} {target} :You're not on that channel", ct);
                        return;
                    }

                    if (!ch.HasPrivilege(session.ConnectionId, ChannelPrivilege.Op))
                    {
                        await session.SendAsync($":server 482 {session.Nick} {target} :You're not channel operator", ct);
                        return;
                    }

                    var banEnable = currentSign == '+';

                    if (msg.Params.Count <= argIndex)
                    {
                        foreach (var ban in ch.Bans)
                        {
                            await session.SendAsync(
                                $":server 367 {session.Nick} {target} {ban.Mask} {ban.SetBy} {ban.SetAtUtc.ToUnixTimeSeconds()}",
                                ct);
                        }

                        await session.SendAsync($":server 368 {session.Nick} {target} :End of Channel Ban List", ct);
                        continue;
                    }

                    var mask = msg.Params[argIndex++];
                    var setter = session.Nick ?? "*";

                    if (banEnable)
                    {
                        var maxList = _options.Value.Limits?.MaxListModes > 0 ? _options.Value.Limits.MaxListModes : 60;
                        if (ch.Bans.Count >= maxList)
                        {                            await session.SendAsync($":server 478 {session.Nick} {target} {mask} :Channel ban list is full", ct);
                            continue;
                        }
                    }

                    var changedBan = banEnable ? ch.AddBan(mask, setter) : ch.RemoveBan(mask);
                    if (!changedBan) continue;

                    if (!appliedModes.Contains(currentSign)) appliedModes.Insert(0, currentSign);
                    appliedModes.Add('b');
                    appliedArgs.Add(mask);
                    continue;
                }

                if (c == 'e')
                {
                    if (!state.TryGetChannel(target, out var ch) || ch is null)
                    {
                        await session.SendAsync($":server 403 {session.Nick} {target} :No such channel", ct);
                        return;
                    }

                    if (!ch.Contains(session.ConnectionId))
                    {
                        await session.SendAsync($":server 442 {session.Nick} {target} :You're not on that channel", ct);
                        return;
                    }

                    if (!ch.HasPrivilege(session.ConnectionId, ChannelPrivilege.Op))
                    {
                        await session.SendAsync($":server 482 {session.Nick} {target} :You're not channel operator", ct);
                        return;
                    }

                    var exceptEnable = currentSign == '+';

                    if (msg.Params.Count <= argIndex)
                    {
                        foreach (var except in ch.ExceptBans)
                        {
                            await session.SendAsync(
                                $":server 348 {session.Nick} {target} {except.Mask} {except.SetBy} {except.SetAtUtc.ToUnixTimeSeconds()}",
                                ct);
                        }

                        await session.SendAsync($":server 349 {session.Nick} {target} :End of Channel Exception List", ct);
                        continue;
                    }

                    var mask = msg.Params[argIndex++];
                    var setter = session.Nick ?? "*";

                    if (exceptEnable)
                    {
                        var maxList = _options.Value.Limits?.MaxListModes > 0 ? _options.Value.Limits.MaxListModes : 60;
                        if (ch.ExceptBans.Count >= maxList)
                        {
                            await session.SendAsync($":server 478 {session.Nick} {target} {mask} :Channel exception list is full", ct);
                            continue;
                        }
                    }

                    var changedExcept = exceptEnable ? ch.AddExceptBan(mask, setter) : ch.RemoveExceptBan(mask);
                    if (!changedExcept) continue;

                    if (!appliedModes.Contains(currentSign)) appliedModes.Insert(0, currentSign);
                    appliedModes.Add('e');
                    appliedArgs.Add(mask);
                    continue;
                }

                if (c == 'I')
                {
                    if (!state.TryGetChannel(target, out var ch) || ch is null)
                    {
                        await session.SendAsync($":server 403 {session.Nick} {target} :No such channel", ct);
                        return;
                    }

                    if (!ch.Contains(session.ConnectionId))
                    {
                        await session.SendAsync($":server 442 {session.Nick} {target} :You're not on that channel", ct);
                        return;
                    }

                    if (!ch.HasPrivilege(session.ConnectionId, ChannelPrivilege.Op))
                    {
                        await session.SendAsync($":server 482 {session.Nick} {target} :You're not channel operator", ct);
                        return;
                    }

                    var inviteExceptEnable = currentSign == '+';

                    if (msg.Params.Count <= argIndex)
                    {
                        foreach (var inviteExcept in ch.InviteExceptions)
                        {
                            await session.SendAsync(
                                $":server 346 {session.Nick} {target} {inviteExcept.Mask} {inviteExcept.SetBy} {inviteExcept.SetAtUtc.ToUnixTimeSeconds()}",
                                ct);
                        }

                        await session.SendAsync($":server 347 {session.Nick} {target} :End of Channel Invite Exception List", ct);
                        continue;
                    }

                    var mask = msg.Params[argIndex++];
                    var setter = session.Nick ?? "*";

                    if (inviteExceptEnable)
                    {
                        var maxList = _options.Value.Limits?.MaxListModes > 0 ? _options.Value.Limits.MaxListModes : 60;
                        if (ch.InviteExceptions.Count >= maxList)
                        {
                            await session.SendAsync($":server 478 {session.Nick} {target} {mask} :Channel invite exception list is full", ct);
                            continue;
                        }
                    }

                    var changedInviteExcept = inviteExceptEnable ? ch.AddInviteException(mask, setter) : ch.RemoveInviteException(mask);
                    if (!changedInviteExcept) continue;

                    if (!appliedModes.Contains(currentSign)) appliedModes.Insert(0, currentSign);
                    appliedModes.Add('I');
                    appliedArgs.Add(mask);
                    continue;
                }

                string nickArg;
                if (msg.Params.Count <= argIndex)
                {
                    if (!allowImplicitSelfTarget)
                    {
                        deferredError = $":server 461 {session.Nick} MODE :Not enough parameters";
                        break;
                    }

                    nickArg = session.Nick ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(nickArg))
                    {
                        deferredError = $":server 461 {session.Nick} MODE :Not enough parameters";
                        break;
                    }
                }
                else
                {
                    nickArg = msg.Params[argIndex++];
                }
                var userModeEnable = currentSign == '+';

                if (!userModeEnable && (c == 'o' || c == 'v'))
                {
                    if (state.TryGetConnectionIdByNick(nickArg, out var targetConnId) && targetConnId is not null
                        && state.TryGetUser(targetConnId, out var targetUser) && targetUser is not null
                        && targetUser.IsService)
                    {
                        continue;
                    }
                }

                var ok = state.TrySetChannelPrivilege(target, session.ConnectionId, c, userModeEnable, nickArg, out var updatedChannel, out var error2);
                if (!ok || updatedChannel is null)
                {
                    if (error2 == "No such nick")
                    {
                        deferredError = $":server 401 {session.Nick} {nickArg} :No such nick";
                        break;
                    }

                    if (error2 == "They aren't on that channel")
                    {
                        deferredError = $":server 441 {session.Nick} {nickArg} {target} :They aren't on that channel";
                        break;
                    }

                    deferredError = null;
                    await SendModeError(session, target, error2, ct);
                    break;
                }

                RecordAppliedMode(currentSign, c);
                appliedArgs.Add(nickArg);
            }

            if (deferredError is not null && appliedModes.Count == 0 && appliedArgs.Count == 0)
            {
                await session.SendAsync(deferredError, ct);
                return;
            }

            if (appliedModes.Count == 0 && appliedArgs.Count == 0)
                return;

            if (!state.TryGetChannel(target, out var finalChannel) || finalChannel is null)
                return;

            var nick = session.Nick!;
            var userName2 = session.UserName ?? "u";

            var modeOut = BuildModeOut(appliedModes);
            var argsOut = appliedArgs.Count > 0 ? " " + string.Join(' ', appliedArgs) : string.Empty;

            var host = state.GetHostFor(session.ConnectionId);
            var line = $":{nick}!{userName2}@{host} MODE {target} {modeOut}{argsOut}";
            await _routing.BroadcastToChannelAsync(finalChannel, line, excludeConnectionId: null, ct);

            if (deferredError is not null)
            {
                if (!deferredError.Contains(" 461 ", StringComparison.Ordinal))
                {
                    await session.SendAsync(deferredError, ct);
                }
            }

            if (_channelEvents is not null)
            {
                await _channelEvents.OnChannelModeChangedAsync(session, finalChannel, state, ct);
            }

            if (!state.TryGetChannel(target, out finalChannel) || finalChannel is null)
            {
                return;
            }

            await _links.PropagateChannelModesAsync(target, finalChannel.CreatedTs, finalChannel.FormatModeString(), ct);

            var key = finalChannel.Key ?? string.Empty;
            var limit = finalChannel.UserLimit?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
            await _links.PropagateChannelMetaAsync(target, finalChannel.CreatedTs, key, limit, ct);

            if (appliedModes.Contains('b') && appliedArgs.Count > 0)
            {
                var lastMask = appliedArgs[^1];
                if (modeOut.Contains("+b", StringComparison.Ordinal))
                {
                    var ban = finalChannel.Bans.LastOrDefault(b => string.Equals(b.Mask, lastMask, StringComparison.OrdinalIgnoreCase));
                    if (ban is not null)
                    {
                        await _links.PropagateBanAsync(target, finalChannel.CreatedTs, ban.Mask, ban.SetBy, ban.SetAtUtc.ToUnixTimeSeconds(), ct);
                    }
                }
                else if (modeOut.Contains("-b", StringComparison.Ordinal))
                {
                    await _links.PropagateBanDelAsync(target, finalChannel.CreatedTs, lastMask, ct);
                }
            }

            for (var i = 0; i < appliedModes.Count; i++)
            {
                var m = appliedModes[i];
                if (m is 'o' or 'v')
                {
                    var nickArg = appliedArgs.Count > 0 ? appliedArgs[^1] : null;
                    if (!string.IsNullOrWhiteSpace(nickArg) && state.TryGetConnectionIdByNick(nickArg, out var connId) && connId is not null)
                    {
                        if (state.TryGetUser(connId, out var uu) && uu is not null && !string.IsNullOrWhiteSpace(uu.Uid))
                        {
                            var priv = finalChannel.GetPrivilege(connId);
                            await _links.PropagateMemberPrivilegeAsync(target, uu.Uid!, priv, ct);
                        }
                    }
                }
            }
        }

        private static async ValueTask HandleUserModeAsync(IClientSession session, IrcMessage msg, ServerState state, CancellationToken ct)
        {
            var me = session.Nick!;
            var targetNick = msg.Params[0];

            if (!string.Equals(targetNick, me, StringComparison.OrdinalIgnoreCase))
            {
                await session.SendAsync($":server 502 {me} :Can't change mode for other users", ct);
                return;
            }

            if (msg.Params.Count == 1)
            {
                if (!state.TryGetUser(session.ConnectionId, out var u) || u is null)
                {
                    await session.SendAsync($":server 401 {me} {targetNick} :No such nick", ct);
                    return;
                }

                await session.SendAsync($":server 221 {me} {FormatUserModes(u.Modes)}", ct);
                return;
            }

            var modeToken = msg.Params[1];
            if (string.IsNullOrWhiteSpace(modeToken) || (modeToken[0] != '+' && modeToken[0] != '-'))
            {
                await session.SendAsync($":server 501 {me} :Unknown MODE flags", ct);
                return;
            }

            var sign = modeToken[0];
            var changed = false;

            for (int i = 1; i < modeToken.Length; i++)
            {
                var c = modeToken[i];

                if (c == '+' || c == '-')
                {
                    sign = c;
                    continue;
                }

                if (c == 'z' || c == 'Z')
                {
                    continue;
                }

                if (c != 'i')
                {
                    continue;
                }

                var enable = sign == '+';
                if (state.TrySetUserMode(session.ConnectionId, UserModes.Invisible, enable))
                {
                    changed = true;
                }
            }

            if (!changed)
            {
                return;
            }

            var userName = session.UserName ?? "u";
            var host = "localhost";
            await session.SendAsync($":{me}!{userName}@{host} MODE {me} :{ExtractAppliedUserModes(modeToken)}", ct);
        }

        private static string FormatUserModes(UserModes modes)
        {
            var letters = new List<char>();
            if (modes.HasFlag(UserModes.Invisible))
            {
                letters.Add('i');
            }

            if (modes.HasFlag(UserModes.Secure))
            {
                letters.Add('Z');
            }

            if (modes.HasFlag(UserModes.Operator))
            {
                letters.Add('o');
            }

            return "+" + new string(letters.ToArray());
        }

        private static string ExtractAppliedUserModes(string token)
        {
            var sign = token.Length > 0 && (token[0] == '+' || token[0] == '-') ? token[0] : '+';
            return token.Contains('i') ? $"{sign}i" : $"{sign}";
        }

        private static List<(ChannelModes Mode, bool Enable)> ParseChannelModeChanges(string token)
        {
            var enable = token[0] == '+';
            var list = new List<(ChannelModes, bool)>();

            for (int i = 1; i < token.Length; i++)
            {
                var c = token[i];
                if (c == '+' || c == '-')
                {
                    enable = c == '+';
                    continue;
                }

                switch (c)
                {
                    case 'n':
                        list.Add((ChannelModes.NoExternalMessages, enable));
                        break;
                    case 't':
                        list.Add((ChannelModes.TopicOpsOnly, enable));
                        break;
                }
            }

            return list;
        }

        private static string BuildModeOut(List<char> appliedModes) => new string(appliedModes.ToArray());

        private static async ValueTask SendModeError(IClientSession session, string channel, string? error, CancellationToken ct)
        {
            if (error == "No such channel")
            {
                await session.SendAsync($":server 403 {session.Nick} {channel} :No such channel", ct);
                return;
            }

            if (error == "You're not on that channel")
            {
                await session.SendAsync($":server 442 {session.Nick} {channel} :You're not on that channel", ct);
                return;
            }

            await session.SendAsync($":server 482 {session.Nick} {channel} :You're not channel operator", ct);
        }
    }
}
