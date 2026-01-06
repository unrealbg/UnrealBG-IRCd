namespace IRCd.Core.Commands.Handlers
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Core.Abstractions;
    using IRCd.Core.Commands.Contracts;
    using IRCd.Core.Protocol;
    using IRCd.Core.State;
    using IRCd.Shared.Options;

    using Microsoft.Extensions.Options;

    public sealed class InfoHandler : IIrcCommandHandler
    {
        public string Command => "INFO";

        private readonly IOptions<IrcOptions> _options;

        public InfoHandler(IOptions<IrcOptions> options)
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

            var me = session.Nick ?? "*";
            var serverName = _options.Value.ServerInfo?.Name ?? "server";
            var version = _options.Value.ServerInfo?.Version ?? "UnrealBG-IRCd";

            await session.SendAsync($":{serverName} 371 {me} :{serverName} ({version})", ct);
            await session.SendAsync($":{serverName} 371 {me} :Created {state.CreatedUtc:O}", ct);
            await session.SendAsync($":{serverName} 371 {me} :Now {DateTimeOffset.UtcNow:O}", ct);
            await session.SendAsync($":{serverName} 374 {me} :End of INFO", ct);
        }
    }
}
