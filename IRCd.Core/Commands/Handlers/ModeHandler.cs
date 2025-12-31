namespace IRCd.Core.Commands.Handlers
{
    using IRCd.Core.Abstractions;
    using IRCd.Core.Commands.Contracts;
    using IRCd.Core.Protocol;
    using IRCd.Core.Services;
    using IRCd.Core.State;

    using System;
    using System.Collections.Generic;

    public sealed class ModeHandler : IIrcCommandHandler
    {
        public string Command => "MODE";
        private readonly RoutingService _routing;

        public ModeHandler(RoutingService routing) => _routing = routing;

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

            var changes = ParseModeChanges(modeToken);
            if (changes.Count == 0)
            {
                await session.SendAsync($":server 501 {session.Nick} :Unknown MODE flags", ct);
                return;
            }

            var changed = state.TryApplyChannelModes(target, session.ConnectionId, changes, out var channel, out var error);
            if (channel is null)
            {
                if (error == "No such channel")
                {
                    await session.SendAsync($":server 403 {session.Nick} {target} :No such channel", ct);
                }
                else if (error == "You're not on that channel")
                {
                    await session.SendAsync($":server 442 {session.Nick} {target} :You're not on that channel", ct);
                }
                else if (error == "You're not channel operator")
                {
                    await session.SendAsync($":server 482 {session.Nick} {target} :You're not channel operator", ct);
                }
                else
                {
                    await session.SendAsync($":server NOTICE * :MODE failed", ct);
                }

                return;
            }

            if (!changed)
            {
                return;
            }

            var nick = session.Nick!;
            var user = session.UserName ?? "u";
            var line = $":{nick}!{user}@localhost MODE {target} {modeToken}";

            await _routing.BroadcastToChannelAsync(channel, line, excludeConnectionId: null, ct);
        }

        private static List<(ChannelModes Mode, bool Enable)> ParseModeChanges(string token)
        {
            var enable = token[0] == '+';
            var list = new List<(ChannelModes, bool)>();

            for (int i = 1; i < token.Length; i++)
            {
                switch (token[i])
                {
                    case 'n': list.Add((ChannelModes.NoExternalMessages, enable)); break;
                    case 't': list.Add((ChannelModes.TopicOpsOnly, enable)); break;
                }
            }

            return list;
        }
    }
}
