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

    public sealed class AdminHandler : IIrcCommandHandler
    {
        public string Command => "ADMIN";

        private readonly IOptions<IrcOptions> _options;

        public AdminHandler(IOptions<IrcOptions> options)
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
            var server = _options.Value.ServerInfo;

            var serverName = server?.Name ?? "server";

            await session.SendAsync($":{serverName} 256 {me} :Administrative info about {serverName}", ct);
            await session.SendAsync($":{serverName} 257 {me} :{server?.AdminLocation1 ?? server?.Description ?? "IRCd"}", ct);
            await session.SendAsync($":{serverName} 258 {me} :{server?.AdminEmail ?? "admin@localhost"}", ct);
        }
    }
}
