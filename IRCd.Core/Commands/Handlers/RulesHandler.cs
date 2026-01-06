namespace IRCd.Core.Commands.Handlers
{
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Core.Abstractions;
    using IRCd.Core.Commands.Contracts;
    using IRCd.Core.Protocol;
    using IRCd.Core.State;
    using IRCd.Shared.Options;

    using Microsoft.Extensions.Options;

    public sealed class RulesHandler : IIrcCommandHandler
    {
        public string Command => "RULES";

        private readonly IOptions<IrcOptions> _options;

        public RulesHandler(IOptions<IrcOptions> options)
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

            await session.SendAsync($":{serverName} NOTICE {me} :Rules are not configured.", ct);
            await session.SendAsync($":{serverName} NOTICE {me} :Be respectful and do not abuse the network.", ct);
            await session.SendAsync($":{serverName} NOTICE {me} :End of /RULES.", ct);
        }
    }
}
