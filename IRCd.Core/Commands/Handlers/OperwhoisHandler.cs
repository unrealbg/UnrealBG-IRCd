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

    public sealed class OperwhoisHandler : IIrcCommandHandler
    {
        public string Command => "OPERWHOIS";

        private readonly IOptions<IrcOptions> _options;

        public OperwhoisHandler(IOptions<IrcOptions> options)
        {
            _options = options;
        }

        public async ValueTask HandleAsync(IClientSession session, IrcMessage msg, ServerState state, CancellationToken ct)
        {
            var serverName = _options.Value.ServerInfo?.Name ?? "server";

            if (!session.IsRegistered)
            {
                await session.SendAsync($":{serverName} 451 {(session.Nick ?? "*")} :You have not registered", ct);
                return;
            }

            var me = session.Nick ?? "*";

            if (!state.TryGetUser(session.ConnectionId, out var user) || user is null || !OperCapabilityService.HasCapability(_options.Value, user, "operwho"))
            {
                await session.SendAsync($":{serverName} 481 {me} :Permission Denied- You're not an IRC operator", ct);
                return;
            }

            if (msg.Params.Count < 1 || string.IsNullOrWhiteSpace(msg.Params[0]))
            {
                await session.SendAsync($":{serverName} 461 {me} OPERWHOIS :Not enough parameters", ct);
                return;
            }

            var targetNick = msg.Params[0].Trim();

            if (!state.TryGetConnectionIdByNick(targetNick, out var targetConn) || targetConn is null || !state.TryGetUser(targetConn, out var targetUser) || targetUser is null)
            {
                await session.SendAsync($":{serverName} 401 {me} {targetNick} :No such nick", ct);
                return;
            }

            if (!targetUser.Modes.HasFlag(UserModes.Operator))
            {
                await session.SendAsync($":{serverName} 401 {me} {targetNick} :No such nick", ct);
                return;
            }

            var cls = string.IsNullOrWhiteSpace(targetUser.OperClass) ? "*" : targetUser.OperClass;
            var operName = string.IsNullOrWhiteSpace(targetUser.OperName) ? "*" : targetUser.OperName;

            var where = !targetUser.IsRemote ? serverName : (state.TryGetRemoteServerBySid(targetUser.RemoteSid ?? string.Empty, out var rs) && rs is not null ? rs.Name : (targetUser.RemoteSid ?? "remote"));

            await session.SendAsync($":{serverName} NOTICE {me} :OPERWHOIS {targetUser.Nick} {operName} {cls} ({where})", ct);
        }
    }
}
