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

    public sealed class UserhostHandler : IIrcCommandHandler
    {
        public string Command => "USERHOST";

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
            var nicks = msg.Params.Take(5).ToArray();

            var results = new List<string>(nicks.Length);

            foreach (var nick in nicks)
            {
                if (state.TryGetConnectionIdByNick(nick, out var connId) && connId is not null
                    && state.TryGetUser(connId, out var user) && user is not null)
                {
                    var u = string.IsNullOrWhiteSpace(user.UserName) ? "u" : user.UserName!;
                    var host = "localhost";

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
