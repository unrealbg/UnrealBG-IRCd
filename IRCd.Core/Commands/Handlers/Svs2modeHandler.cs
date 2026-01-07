namespace IRCd.Core.Commands.Handlers
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Core.Abstractions;
    using IRCd.Core.Commands.Contracts;
    using IRCd.Core.Protocol;
    using IRCd.Core.Services;
    using IRCd.Core.State;
    using IRCd.Shared.Options;

    using Microsoft.Extensions.Options;

    public sealed class Svs2modeHandler : IIrcCommandHandler
    {
        public string Command => "SVS2MODE";

        private readonly IOptions<IrcOptions> _options;
        private readonly ISessionRegistry _sessions;
        private readonly ServerLinkService _links;

        public Svs2modeHandler(IOptions<IrcOptions> options, ISessionRegistry sessions, ServerLinkService links)
        {
            _options = options;
            _sessions = sessions;
            _links = links;
        }

        public async ValueTask HandleAsync(IClientSession session, IrcMessage msg, ServerState state, CancellationToken ct)
        {
            var server = _options.Value.ServerInfo?.Name ?? "server";

            if (!session.IsRegistered)
            {
                await session.SendAsync($":{server} 451 {(session.Nick ?? "*")} :You have not registered", ct);
                return;
            }

            var me = session.Nick ?? "*";

            if (!state.TryGetUser(session.ConnectionId, out var oper) || oper is null || !OperCapabilityService.HasCapability(_options.Value, oper, "svs2mode"))
            {
                await session.SendAsync($":{server} 481 {me} :Permission Denied- You're not an IRC operator", ct);
                return;
            }

            if (msg.Params.Count < 2)
            {
                await session.SendAsync($":{server} 461 {me} SVS2MODE :Not enough parameters", ct);
                return;
            }

            var targetNick = (msg.Params[0] ?? string.Empty).Trim();
            var modeToken = (msg.Params[1] ?? string.Empty).Trim();

            if (!IrcValidation.IsValidNick(targetNick, out _))
            {
                await session.SendAsync($":{server} 401 {me} {targetNick} :No such nick", ct);
                return;
            }

            if (string.IsNullOrWhiteSpace(modeToken) || (modeToken[0] != '+' && modeToken[0] != '-'))
            {
                await session.SendAsync($":{server} NOTICE {me} :Invalid mode string", ct);
                return;
            }

            if (!state.TryGetConnectionIdByNick(targetNick, out var targetConn) || targetConn is null || !state.TryGetUser(targetConn, out var targetUser) || targetUser is null)
            {
                await session.SendAsync($":{server} 401 {me} {targetNick} :No such nick", ct);
                return;
            }

            if (targetUser.IsService)
            {
                await session.SendAsync($":{server} NOTICE {me} :Cannot SVS2MODE services", ct);
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

                if (c != 'i')
                    continue;

                var enable = sign == '+';
                if (state.TrySetUserMode(targetConn, UserModes.Invisible, enable))
                    changed = true;
            }

            if (!changed)
            {
                await session.SendAsync($":{server} NOTICE {me} :No changes", ct);
                return;
            }

            if (!targetUser.IsRemote && _sessions.TryGet(targetConn, out var targetSession) && targetSession is not null)
            {
                targetSession.TryApplyUserModes(modeToken, out _);
                var userName = targetSession.UserName ?? (targetUser.UserName ?? "u");
                var host = state.GetHostFor(targetConn);
                await targetSession.SendAsync($":{targetNick}!{userName}@{host} MODE {targetNick} :{ExtractAppliedUserModes(modeToken)}", ct);
            }

            if (!string.IsNullOrWhiteSpace(targetUser.Uid))
            {
                await _links.PropagateUserModeAsync(targetUser.Uid!, ExtractAppliedUserModes(modeToken), ct);
            }

            await session.SendAsync($":{server} NOTICE {me} :SVS2MODE {targetNick} {ExtractAppliedUserModes(modeToken)}", ct);
        }

        private static string ExtractAppliedUserModes(string token)
        {
            var sign = token.Length > 0 && (token[0] == '+' || token[0] == '-') ? token[0] : '+';
            return token.Contains('i') ? $"{sign}i" : $"{sign}";
        }
    }
}
