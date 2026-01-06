namespace IRCd.Core.Commands.Handlers
{
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Core.Abstractions;
    using IRCd.Core.Commands.Contracts;
    using IRCd.Core.Services;
    using IRCd.Core.State;
    using IRCd.Shared.Options;

    using Microsoft.Extensions.Options;

    public sealed class UndlineHandler : IIrcCommandHandler
    {
        public string Command => "UNDLINE";

        private readonly IOptions<IrcOptions> _options;
        private readonly RuntimeDLineService _dlines;

        public UndlineHandler(IOptions<IrcOptions> options, RuntimeDLineService dlines)
        {
            _options = options;
            _dlines = dlines;
        }

        public async ValueTask HandleAsync(IClientSession session, Protocol.IrcMessage msg, ServerState state, CancellationToken ct)
        {
            if (!session.IsRegistered)
            {
                await session.SendAsync($":server 451 {(session.Nick ?? "*")} :You have not registered", ct);
                return;
            }

            var me = session.Nick!;
            var serverName = _options.Value.ServerInfo?.Name ?? "server";

            if (!state.TryGetUser(session.ConnectionId, out var user) || user is null || !OperCapabilityService.HasCapability(_options.Value, user, "dline"))
            {
                await session.SendAsync($":{serverName} 481 {me} :Permission Denied- You're not an IRC operator", ct);
                return;
            }

            if (msg.Params.Count < 1)
            {
                await session.SendAsync($":{serverName} 461 {me} UNDLINE :Not enough parameters", ct);
                return;
            }

            var mask = (msg.Params[0] ?? string.Empty).Trim();
            var removed = _dlines.Remove(mask);

            await session.SendAsync($":{serverName} NOTICE {me} :UNDLINE {(removed ? "removed" : "not found")} {mask}", ct);
        }
    }
}
