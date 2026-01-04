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

    public sealed class LinksHandler : IIrcCommandHandler
    {
        public string Command => "LINKS";

        private readonly IOptions<IrcOptions> _options;

        public LinksHandler(IOptions<IrcOptions> options)
        {
            _options = options;
        }

        public async ValueTask HandleAsync(IClientSession session, IrcMessage msg, ServerState state, CancellationToken ct)
        {
            if (!session.IsRegistered)
            {
                var serverName = _options.Value.ServerInfo?.Name ?? "server";
                await session.SendAsync($":{serverName} 451 {(session.Nick ?? "*")} :You have not registered", ct);
                return;
            }

            var me = session.Nick ?? "*";

            var srv = _options.Value.ServerInfo?.Name ?? "server";

            foreach (var s in state.GetRemoteServers())
            {
                await session.SendAsync($":{srv} 364 {me} {s.Name} {s.Name} :0 {s.Description}", ct);
            }

            await session.SendAsync($":{srv} 365 {me} * :End of /LINKS list.", ct);
        }
    }
}
