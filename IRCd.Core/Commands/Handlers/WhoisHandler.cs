namespace IRCd.Core.Commands.Handlers
{
    using IRCd.Core.Abstractions;
    using IRCd.Core.Commands.Contracts;
    using IRCd.Core.Protocol;
    using IRCd.Core.State;

    using System;

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

            var nick = msg.Params.Count == 1 ? msg.Params[0] : msg.Params[^1];

            if (!state.TryGetConnectionIdByNick(nick, out var connId) || connId is null ||
                !state.TryGetUser(connId, out var user) || user is null)
            {
                await session.SendAsync($":server 401 {session.Nick} {nick} :No such nick", ct);
                await session.SendAsync($":server 318 {session.Nick} {nick} :End of /WHOIS list.", ct);
                return;
            }

            var uName = user.UserName ?? "u";
            var host = "localhost";
            var real = user.RealName ?? "Unknown";

            await session.SendAsync($":server 311 {session.Nick} {user.Nick} {uName} {host} * :{real}", ct);
            await session.SendAsync($":server 312 {session.Nick} {user.Nick} server :IRCd", ct);

            var chs = state.GetUserChannels(connId);
            if (chs.Count > 0)
            {
                var list = string.Join(' ', chs.Select(chName =>
                {
                    var p = state.GetUserPrivilegeInChannel(connId, chName).ToPrefix();
                    return p is null ? chName : $"{p}{chName}";
                }));

                await session.SendAsync($":server 319 {session.Nick} {user.Nick} :{list}", ct);
            }

            var idleSecs = (int)Math.Max(0, (DateTimeOffset.UtcNow - user.LastActivityUtc).TotalSeconds);
            var signonTs = user.ConnectedAtUtc.ToUnixTimeSeconds();
            await session.SendAsync($":server 317 {session.Nick} {user.Nick} {idleSecs} {signonTs} :seconds idle, signon time", ct);

            await session.SendAsync($":server 318 {session.Nick} {user.Nick} :End of /WHOIS list.", ct);
        }
    }
}
