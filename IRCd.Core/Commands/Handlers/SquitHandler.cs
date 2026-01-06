namespace IRCd.Core.Commands.Handlers
{
    using System;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Core.Abstractions;
    using IRCd.Core.Commands.Contracts;
    using IRCd.Core.Services;
    using IRCd.Core.State;
    using IRCd.Shared.Options;

    using Microsoft.Extensions.Options;

    public sealed class SquitHandler : IIrcCommandHandler
    {
        public string Command => "SQUIT";

        private readonly IOptions<IrcOptions> _options;
        private readonly ServerLinkService _links;

        public SquitHandler(IOptions<IrcOptions> options, ServerLinkService links)
        {
            _options = options;
            _links = links;
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

            if (!state.TryGetUser(session.ConnectionId, out var user) || user is null || !OperCapabilityService.HasCapability(_options.Value, user, "squit"))
            {
                await session.SendAsync($":{serverName} 481 {me} :Permission Denied- You're not an IRC operator", ct);
                return;
            }

            if (msg.Params.Count < 1)
            {
                await session.SendAsync($":{serverName} 461 {me} SQUIT :Not enough parameters", ct);
                return;
            }

            var target = msg.Params[0] ?? string.Empty;
            var reason = msg.Trailing;
            if (string.IsNullOrWhiteSpace(reason) && msg.Params.Count >= 2)
            {
                reason = msg.Params[1];
            }

            if (string.IsNullOrWhiteSpace(reason))
                reason = "Requested";

            string? sid = null;

            if (target.Length == 3 && target.All(char.IsLetterOrDigit))
            {
                sid = target;
            }
            else
            {
                sid = state.GetRemoteServers()
                    .FirstOrDefault(s => s is not null && string.Equals(s.Name, target, StringComparison.OrdinalIgnoreCase))
                    ?.Sid;
            }

            if (string.IsNullOrWhiteSpace(sid))
            {
                await session.SendAsync($":{serverName} 402 {me} {target} :No such server", ct);
                return;
            }

            var ok = await _links.LocalSquitAsync(sid, reason, ct);
            if (!ok)
            {
                await session.SendAsync($":{serverName} 402 {me} {target} :No such server", ct);
                return;
            }

            await session.SendAsync($":{serverName} NOTICE {me} :SQUIT {sid} :{reason}", ct);
        }
    }
}
