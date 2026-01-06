namespace IRCd.Core.Commands.Handlers
{
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Core.Abstractions;
    using IRCd.Core.Commands.Contracts;
    using IRCd.Core.Protocol;
    using IRCd.Core.Services;
    using IRCd.Core.State;
    using IRCd.Shared.Options;

    using Microsoft.Extensions.Options;

    public sealed class WhowasHandler : IIrcCommandHandler
    {
        public string Command => "WHOWAS";

        private readonly IOptions<IrcOptions> _options;
        private readonly WhowasService _whowas;

        public WhowasHandler(IOptions<IrcOptions> options, WhowasService whowas)
        {
            _options = options;
            _whowas = whowas;
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

            if (msg.Params.Count < 1 || string.IsNullOrWhiteSpace(msg.Params[0]))
            {
                await session.SendAsync($":{serverName} 461 {me} WHOWAS :Not enough parameters", ct);
                return;
            }

            var targetNick = (msg.Params[0] ?? string.Empty).Trim();
            var entries = _whowas.Get(targetNick);

            if (entries.Count == 0)
            {
                await session.SendAsync($":{serverName} 406 {me} {targetNick} :There was no such nickname", ct);
                await session.SendAsync($":{serverName} 369 {me} {targetNick} :End of WHOWAS", ct);
                return;
            }

            foreach (var e in entries)
            {
                await session.SendAsync($":{serverName} 314 {me} {e.Nick} {e.UserName} {e.Host} * :{e.RealName}", ct);
            }

            await session.SendAsync($":{serverName} 369 {me} {targetNick} :End of WHOWAS", ct);
        }
    }
}
