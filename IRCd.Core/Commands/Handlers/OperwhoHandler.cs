namespace IRCd.Core.Commands.Handlers
{
    using System;
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

    public sealed class OperwhoHandler : IIrcCommandHandler
    {
        public string Command => "OPERWHO";

        private readonly IOptions<IrcOptions> _options;

        public OperwhoHandler(IOptions<IrcOptions> options)
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

            var opers = state.GetAllUsers()
                .Where(u => u.IsRegistered && u.Modes.HasFlag(UserModes.Operator) && !string.IsNullOrWhiteSpace(u.Nick))
                .OrderBy(u => u.Nick, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            foreach (var u in opers)
            {
                var cls = string.IsNullOrWhiteSpace(u.OperClass) ? "*" : u.OperClass;
                var operName = string.IsNullOrWhiteSpace(u.OperName) ? "*" : u.OperName;
                var where = !u.IsRemote ? serverName : (state.TryGetRemoteServerBySid(u.RemoteSid ?? string.Empty, out var rs) && rs is not null ? rs.Name : (u.RemoteSid ?? "remote"));

                await session.SendAsync($":{serverName} NOTICE {me} :OPER {u.Nick} {operName} {cls} ({where})", ct);
            }

            await session.SendAsync($":{serverName} NOTICE {me} :End of /OPERWHO", ct);
        }
    }
}
