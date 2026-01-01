namespace IRCd.Core.Commands.Handlers
{
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Core.Abstractions;
    using IRCd.Core.Commands.Contracts;
    using IRCd.Core.Protocol;
    using IRCd.Core.Services;
    using IRCd.Core.State;

    public sealed class ModeHandler : IIrcCommandHandler
    {
        public string Command => "MODE";

        private readonly RoutingService _routing;

        public ModeHandler(RoutingService routing)
        {
            _routing = routing;
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

            var target = msg.Params[0];
            if (!target.StartsWith('#'))
            {
                await session.SendAsync($":server 501 {session.Nick} :Unknown MODE flags", ct);
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

            var argIndex = 2;

            var appliedModes = new List<char>();
            var appliedArgs = new List<string>();

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
                    appliedModes.AddRange(modeToken.Where(c => c is 'n' or 't' || c is '+' or '-'));
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

                if (c is not ('q' or 'a' or 'o' or 'h' or 'v' or 'b' or 'i' or 'k' or 'l' or 'm' or 's'))
                {
                    continue;
                }

                if (c is 'i' or 'k' or 'l' or 'm' or 's')
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

                    var setOn = currentSign == '+';

                    if (c == 'i')
                    {
                        var changed = ch.ApplyModeChange(ChannelModes.InviteOnly, setOn);
                        if (!changed) continue;

                        if (!appliedModes.Contains(currentSign)) appliedModes.Insert(0, currentSign);
                        appliedModes.Add('i');
                        continue;
                    }

                    if (c == 'k')
                    {
                        if (setOn)
                        {
                            if (msg.Params.Count <= argIndex)
                            {
                                await session.SendAsync($":server 461 {session.Nick} MODE :Not enough parameters", ct);
                                return;
                            }

                            var key = msg.Params[argIndex++];
                            ch.SetKey(key);

                            if (!appliedModes.Contains(currentSign)) appliedModes.Insert(0, currentSign);
                            appliedModes.Add('k');
                            appliedArgs.Add(key);
                        }
                        else
                        {
                            ch.SetKey(null);

                            if (!appliedModes.Contains(currentSign)) appliedModes.Insert(0, currentSign);
                            appliedModes.Add('k');
                        }

                        continue;
                    }

                    if (c == 'l')
                    {
                        if (setOn)
                        {
                            if (msg.Params.Count <= argIndex)
                            {
                                await session.SendAsync($":server 461 {session.Nick} MODE :Not enough parameters", ct);
                                return;
                            }

                            var raw = msg.Params[argIndex++];
                            if (!int.TryParse(raw, out var limit) || limit <= 0)
                            {
                                await session.SendAsync($":server NOTICE * :Invalid limit", ct);
                                return;
                            }

                            ch.SetLimit(limit);

                            if (!appliedModes.Contains(currentSign))
                            {
                                appliedModes.Insert(0, currentSign);
                            }

                            appliedModes.Add('l');
                            appliedArgs.Add(raw);
                        }
                        else
                        {
                            ch.SetLimit(null);

                            if (!appliedModes.Contains(currentSign))
                            {
                                appliedModes.Insert(0, currentSign);
                            }

                            appliedModes.Add('l');
                        }

                        continue;
                    }

                    if (c == 'm')
                    {
                        var changed = ch.ApplyModeChange(ChannelModes.Moderated, setOn);
                        if (!changed)
                        {
                            continue;
                        }

                        if (!appliedModes.Contains(currentSign))
                        {
                            appliedModes.Insert(0, currentSign);
                        }

                        appliedModes.Add('m');
                        continue;
                    }

                    if (c == 's')
                    {
                        var changed = ch.ApplyModeChange(ChannelModes.Secret, setOn);
                        if (!changed)
                        {
                            continue;
                        }

                        if (!appliedModes.Contains(currentSign))
                        {
                            appliedModes.Insert(0, currentSign);
                        }

                        appliedModes.Add('s');
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

                    var changedBan = banEnable ? ch.AddBan(mask, setter) : ch.RemoveBan(mask);
                    if (!changedBan)
                    {
                        continue;
                    }

                    if (!appliedModes.Contains(currentSign))
                    {
                        appliedModes.Insert(0, currentSign);
                    }

                    appliedModes.Add('b');
                    appliedArgs.Add(mask);

                    continue;
                }

                if (msg.Params.Count <= argIndex)
                {
                    await session.SendAsync($":server 461 {session.Nick} MODE :Not enough parameters", ct);
                    return;
                }

                var nickArg = msg.Params[argIndex++];
                var userModeEnable = currentSign == '+';

                var ok = state.TrySetChannelPrivilege(target, session.ConnectionId, c, userModeEnable, nickArg, out var updatedChannel, out var error2);
                if (!ok || updatedChannel is null)
                {
                    if (error2 == "No such nick")
                    {
                        await session.SendAsync($":server 401 {session.Nick} {nickArg} :No such nick", ct);
                    }
                    else if (error2 == "They aren't on that channel")
                    {
                        await session.SendAsync($":server 441 {session.Nick} {nickArg} {target} :They aren't on that channel", ct);
                    }
                    else
                    {
                        await SendModeError(session, target, error2, ct);
                    }

                    return;
                }

                if (!appliedModes.Contains(currentSign))
                    appliedModes.Insert(0, currentSign);

                appliedModes.Add(c);
                appliedArgs.Add(nickArg);
            }

            if (appliedModes.Count == 0 && appliedArgs.Count == 0)
                return;

            if (!state.TryGetChannel(target, out var finalChannel) || finalChannel is null)
                return;

            var nick = session.Nick!;
            var userName = session.UserName ?? "u";

            var modeOut = BuildModeOut(modeToken[0], appliedModes);
            var argsOut = appliedArgs.Count > 0 ? " " + string.Join(' ', appliedArgs) : string.Empty;

            var line = $":{nick}!{userName}@localhost MODE {target} {modeOut}{argsOut}";
            await _routing.BroadcastToChannelAsync(finalChannel, line, excludeConnectionId: null, ct);
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

        private static string BuildModeOut(char defaultSign, List<char> appliedModes)
        {
            var letters = appliedModes.Where(c => c is not ('+' or '-')).ToArray();
            return defaultSign + new string(letters);
        }

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
