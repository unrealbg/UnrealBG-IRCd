namespace IRCd.Core.Commands.Handlers
{
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Core.Abstractions;
    using IRCd.Core.Commands.Contracts;
    using IRCd.Core.Protocol;
    using IRCd.Core.State;

    using IRCd.Shared.Options;

    using Microsoft.Extensions.Options;

    public sealed class UserhostHandler : IIrcCommandHandler
    {
        public string Command => "USERHOST";

        private readonly IOptions<IrcOptions> _options;

        public UserhostHandler(IOptions<IrcOptions> options)
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

            if (msg.Params.Count < 1)
            {
                await session.SendAsync($":server 461 {session.Nick} USERHOST :Not enough parameters", ct);
                return;
            }

            var me = session.Nick!;
            var max = _options.Value.Limits?.MaxUserhostTargets > 0 ? _options.Value.Limits.MaxUserhostTargets : 10;
            var nicks = msg.Params.Take(max).ToArray();

            var results = new List<string>(nicks.Length);

            foreach (var nick in nicks)
            {
                if (!IrcValidation.IsValidNick(nick, out _))
                {
                    continue;
                }

                if (state.TryGetConnectionIdByNick(nick, out var connId) && connId is not null
                    && state.TryGetUser(connId, out var user) && user is not null)
                {
                    var u = string.IsNullOrWhiteSpace(user.UserName) ? "u" : user.UserName!;
                    var host = state.GetHostFor(connId);

                    results.Add($"{user.Nick}=+{u}@{host}");
                }
                else
                {
                    // Omit to avoid confusing clients.
                }
            }

            await session.SendAsync($":server 302 {me} :{string.Join(' ', results)}", ct);
        }
    }
}
