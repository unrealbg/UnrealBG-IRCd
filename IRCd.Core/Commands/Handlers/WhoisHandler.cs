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

    using IRCd.Shared.Options;

    using Microsoft.Extensions.Options;

    public sealed class WhoisHandler : IIrcCommandHandler
    {
        public string Command => "WHOIS";

        private readonly ISessionRegistry _sessions;

        private readonly IOptions<IrcOptions> _options;

        public WhoisHandler(ISessionRegistry sessions, IOptions<IrcOptions> options)
        {
            _sessions = sessions;
            _options = options;
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
                await session.SendAsync($":server 461 {session.Nick} WHOIS :Not enough parameters", ct);
                return;
            }

            var me = session.Nick!;

            var maxTargets = _options.Value.Limits?.MaxWhoisTargets > 0 ? _options.Value.Limits.MaxWhoisTargets : 5;

            var raw = msg.Params.Count >= 2 ? msg.Params[1] : msg.Params[0];
            var targets = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (targets.Length > maxTargets)
            {
                await session.SendAsync($":server 407 {me} :Too many targets", ct);
                return;
            }

            var targetNick = targets[0];

            if (!IrcValidation.IsValidNick(targetNick, out _))
            {
                await session.SendAsync($":server 401 {me} {targetNick} :No such nick", ct);
                await session.SendAsync($":server 318 {me} {targetNick} :End of /WHOIS list.", ct);
                return;
            }

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
            var host = state.GetHostFor(targetConn);
            var realName = string.IsNullOrWhiteSpace(targetUser.RealName) ? "Unknown" : targetUser.RealName!;

            await session.SendAsync($":server 311 {me} {targetUser.Nick} {userName} {host} * :{realName}", ct);

            if (!string.IsNullOrWhiteSpace(targetUser.AwayMessage))
            {
                await session.SendAsync($":server 301 {me} {targetUser.Nick} :{targetUser.AwayMessage}", ct);
            }

            var secure = targetUser.IsSecureConnection;

            if (_sessions.TryGet(targetConn, out var targetSession) && targetSession is not null)
            {
                secure = targetSession.IsSecureConnection;
            }

            if (secure)
            {
                await session.SendAsync($":server 671 {me} {targetUser.Nick} :is using a secure connection", ct);
            }

            if (targetUser.Modes.HasFlag(UserModes.Operator))
            {
                await session.SendAsync($":server 313 {me} {targetUser.Nick} :is an IRC operator", ct);
            }

            var localServerName = _options.Value.ServerInfo?.Name ?? "server";
            var localServerDesc = _options.Value.ServerInfo?.Description ?? "IRCd";

            var shownServer = localServerName;
            if (targetUser.IsRemote && !string.IsNullOrWhiteSpace(targetUser.RemoteSid))
            {
                if (state.TryGetRemoteServerBySid(targetUser.RemoteSid!, out var rs) && rs is not null && !string.IsNullOrWhiteSpace(rs.Name))
                {
                    shownServer = rs.Name!;
                }
            }

            await session.SendAsync($":{localServerName} 312 {me} {targetUser.Nick} {shownServer} :{localServerDesc}", ct);

            await session.SendAsync($":{localServerName} 378 {me} {targetUser.Nick} :is connecting from *@{host}", ct);

            var chanList = BuildWhoisChannelList(requesterConnId: session.ConnectionId, targetConnId: targetConn, state);
            if (chanList.Count > 0)
            {
                await session.SendAsync($":server 319 {me} {targetUser.Nick} :{string.Join(' ', chanList)}", ct);
            }

            var modeLetters = new List<char>();
            if (targetUser.Modes.HasFlag(UserModes.Invisible)) modeLetters.Add('i');
            if (targetUser.Modes.HasFlag(UserModes.Secure)) modeLetters.Add('Z');

            if (modeLetters.Count > 0)
            {
                await session.SendAsync($":server 379 {me} {targetUser.Nick} :is using modes +{new string(modeLetters.ToArray())}", ct);
            }

            var now = DateTimeOffset.UtcNow;

            var connectedAt = targetUser.ConnectedAtUtc;
            if (connectedAt == default)
                connectedAt = now;

            var lastActivity = targetUser.LastActivityUtc;
            if (lastActivity == default)
                lastActivity = connectedAt;

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
                    continue;

                if (ch.Modes.HasFlag(ChannelModes.Secret) && !ch.Contains(requesterConnId))
                    continue;

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
