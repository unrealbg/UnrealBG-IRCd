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

    public sealed class StatsHandler : IIrcCommandHandler
    {
        public string Command => "STATS";

        private readonly IOptions<IrcOptions> _options;

        public StatsHandler(IOptions<IrcOptions> options)
        {
            _options = options;
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

            if (!state.TryGetUser(session.ConnectionId, out var user) || user is null || !OperCapabilityService.HasCapability(_options.Value, user, "stats"))
            {
                await session.SendAsync($":{serverName} 481 {me} :Permission Denied- You're not an IRC operator", ct);
                return;
            }

            var query = msg.Params.Count > 0 ? (msg.Params[0] ?? string.Empty) : string.Empty;
            query = query.Trim();

            var uptime = DateTimeOffset.UtcNow - state.CreatedUtc;
            await session.SendAsync($":{serverName} 242 {me} :Server Up {uptime}", ct);
            await session.SendAsync($":{serverName} 250 {me} :Users {state.UserCount}", ct);

            await session.SendAsync($":{serverName} 219 {me} {query} :End of STATS report", ct);
        }
    }
}
