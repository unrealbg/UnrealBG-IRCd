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

    public sealed class WallopsHandler : IIrcCommandHandler
    {
        public string Command => "WALLOPS";

        private readonly IOptions<IrcOptions> _options;
        private readonly RoutingService _routing;

        public WallopsHandler(IOptions<IrcOptions> options, RoutingService routing)
        {
            _options = options;
            _routing = routing;
        }

        public async ValueTask HandleAsync(IClientSession session, IrcMessage msg, ServerState state, CancellationToken ct)
        {
            if (!session.IsRegistered)
            {
                await session.SendAsync($":server 451 {(session.Nick ?? "*")} :You have not registered", ct);
                return;
            }

            var me = session.Nick!;
            var serverName = _options.Value.ServerInfo?.Name ?? "server";

            if (!state.TryGetUser(session.ConnectionId, out var user) || user is null || !OperCapabilityService.HasCapability(_options.Value, user, "wallops"))
            {
                await session.SendAsync($":{serverName} 481 {me} :Permission Denied- You're not an IRC operator", ct);
                return;
            }

            var text = msg.Trailing;
            if (string.IsNullOrWhiteSpace(text) && msg.Params.Count > 0)
                text = msg.Params[0];

            if (string.IsNullOrWhiteSpace(text))
            {
                await session.SendAsync($":{serverName} 461 {me} WALLOPS :Not enough parameters", ct);
                return;
            }

            var host = state.GetHostFor(session.ConnectionId);
            var prefix = $":{me}!{(session.UserName ?? "u")}@{host}";
            var line = $"{prefix} WALLOPS :{text}";

            foreach (var u in state.GetAllUsers().Where(u => u.IsRegistered && u.Modes.HasFlag(UserModes.Operator) && !u.IsRemote).ToArray())
            {
                if (string.Equals(u.ConnectionId, session.ConnectionId, StringComparison.OrdinalIgnoreCase))
                    continue;

                await _routing.SendToUserAsync(u.ConnectionId, line, ct);
            }

            await session.SendAsync(line, ct);
        }
    }
}
