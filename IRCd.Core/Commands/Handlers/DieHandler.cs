namespace IRCd.Core.Commands.Handlers
{
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Core.Abstractions;
    using IRCd.Core.Commands.Contracts;
    using IRCd.Core.Services;
    using IRCd.Core.State;
    using IRCd.Shared.Options;

    using Microsoft.Extensions.Hosting;
    using Microsoft.Extensions.Options;

    public sealed class DieHandler : IIrcCommandHandler
    {
        public string Command => "DIE";

        private readonly IOptions<IrcOptions> _options;
        private readonly IHostApplicationLifetime _lifetime;

        public DieHandler(IOptions<IrcOptions> options, IHostApplicationLifetime lifetime)
        {
            _options = options;
            _lifetime = lifetime;
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

            if (!state.TryGetUser(session.ConnectionId, out var user) || user is null || !OperCapabilityService.HasCapability(_options.Value, user, "die"))
            {
                await session.SendAsync($":{serverName} 481 {me} :Permission Denied- You're not an IRC operator", ct);
                return;
            }

            await session.SendAsync($":{serverName} NOTICE {me} :Server is shutting down", ct);
            _lifetime.StopApplication();
        }
    }
}
