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

    public sealed class TraceHandler : IIrcCommandHandler
    {
        public string Command => "TRACE";

        private readonly IOptions<IrcOptions> _options;

        public TraceHandler(IOptions<IrcOptions> options)
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

            if (!state.TryGetUser(session.ConnectionId, out var user) || user is null || !OperCapabilityService.HasCapability(_options.Value, user, "trace"))
            {
                await session.SendAsync($":{serverName} 481 {me} :Permission Denied- You're not an IRC operator", ct);
                return;
            }

            var version = _options.Value.ServerInfo?.Version ?? "UnrealBG-IRCd";
            await session.SendAsync($":{serverName} 200 {me} :Link {serverName} {version}", ct);
            await session.SendAsync($":{serverName} 262 {me} :End of TRACE", ct);
        }
    }
}
