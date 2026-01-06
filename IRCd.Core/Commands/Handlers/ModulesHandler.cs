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

    public sealed class ModulesHandler : IIrcCommandHandler
    {
        public string Command => "MODULES";

        private readonly IOptions<IrcOptions> _options;

        public ModulesHandler(IOptions<IrcOptions> options)
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

            await session.SendAsync($":{serverName} 702 {me} :core (built-in)", ct);
            await session.SendAsync($":{serverName} 703 {me} :End of /MODULES list", ct);
        }
    }
}
