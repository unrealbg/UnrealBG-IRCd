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
    using IRCd.Core.State;

    public sealed class WhoisHandler : IIrcCommandHandler
    {
        public string Command => "WHOIS";

        public async ValueTask HandleAsync(IClientSession session, IrcMessage msg, ServerState state, CancellationToken ct)
        {
            if (!session.IsRegistered)
            {
                await session.SendAsync($":server 451 {(session.Nick ?? "*")} :You have not registered", ct);
                return;
            }

            if (msg.Params.Count < 1)
            {
                await session.SendAsync($":server 461 {session.Nick} WHOIS :Not enough parameters", ct);
                return;
            }

            var me = session.Nick!;
            var targetNick = msg.Params[0];

            if (!state.TryGetConnectionIdByNick(targetNick, out var targetConn) || targetConn is null)
            {
                await session.SendAsync($":server 401 {me} {targetNick} :No such nick", ct);
                await session.SendAsync($":server 318 {me} {targetNick} :End of /WHOIS list.", ct);
                return;
            }

            if (!state.TryGetUser(targetConn, out var targetUser) || targetUser is null)
            {
                await session.SendAsync($":server 401 {me} {targetNick} :No such nick", ct);
                await session.SendAsync($":server 318 {me} {targetNick} :End of /WHOIS list.", ct);
                return;
            }

            var userName = string.IsNullOrWhiteSpace(targetUser.UserName) ? "u" : targetUser.UserName!;
            var host = "localhost";
            var realName = string.IsNullOrWhiteSpace(targetUser.RealName) ? "Unknown" : targetUser.RealName!;
            await session.SendAsync($":server 311 {me} {targetUser.Nick} {userName} {host} * :{realName}", ct);

            await session.SendAsync($":server 312 {me} {targetUser.Nick} server :IRCd (.NET)", ct);

            var chanList = BuildWhoisChannelList(requesterConnId: session.ConnectionId, targetConnId: targetConn, state);
            if (chanList.Count > 0)
            {
                await session.SendAsync($":server 319 {me} {targetUser.Nick} :{string.Join(' ', chanList)}", ct);
            }

            var now = DateTimeOffset.UtcNow;

            var connectedAt = targetUser.ConnectedAtUtc;
            if (connectedAt == default)
            {
                connectedAt = now;
            }

            var lastActivity = targetUser.LastActivityUtc;
            if (lastActivity == default)
            {
                lastActivity = connectedAt;
            }

            var idleSeconds = (long)Math.Max(0, (now - lastActivity).TotalSeconds);

            await session.SendAsync(
                $":server 317 {me} {targetUser.Nick} {idleSeconds} {connectedAt.ToUnixTimeSeconds()} :seconds idle, signon time",
                ct);

            await session.SendAsync($":server 318 {me} {targetUser.Nick} :End of /WHOIS list.", ct);
        }

        private static List<string> BuildWhoisChannelList(string requesterConnId, string targetConnId, ServerState state)
        {
            var result = new List<string>();

            foreach (var ch in state.GetAllChannels())
            {
                if (!ch.Contains(targetConnId))
                {
                    continue;
                }

                if (ch.Modes.HasFlag(ChannelModes.Secret) && !ch.Contains(requesterConnId))
                {
                    continue;
                }

                var priv = ch.GetPrivilege(targetConnId);
                var pfx = priv.ToPrefix();

                result.Add(pfx is null ? ch.Name : $"{pfx}{ch.Name}");
            }

            return result
                .OrderBy(s => s[0] == '#' ? 1 : 0)
                .ThenBy(s => s.TrimStart('*', '&', '@', '%', '+', '~'), StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }
}
