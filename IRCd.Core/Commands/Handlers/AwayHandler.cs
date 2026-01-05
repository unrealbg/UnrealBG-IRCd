namespace IRCd.Core.Commands.Handlers
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Core.Abstractions;
    using IRCd.Core.Commands.Contracts;
    using IRCd.Core.Protocol;
    using IRCd.Core.State;

    public sealed class AwayHandler : IIrcCommandHandler
    {
        public string Command => "AWAY";

        public async ValueTask HandleAsync(IClientSession session, IrcMessage msg, ServerState state, CancellationToken ct)
        {
            if (!session.IsRegistered)
            {
                await session.SendAsync($":server 451 {(session.Nick ?? "*")} :You have not registered", ct);
                return;
            }

            if (!state.TryGetUser(session.ConnectionId, out var u) || u is null)
            {
                await session.SendAsync($":server 401 {session.Nick} {session.Nick} :No such nick", ct);
                return;
            }

            var away = msg.Trailing;
            if (string.IsNullOrWhiteSpace(away) && msg.Params.Count > 0)
            {
                away = msg.Params[0];
            }

            away = away?.Trim();

            if (string.IsNullOrWhiteSpace(away))
            {
                u.AwayMessage = null;
                await session.SendAsync($":server 305 {session.Nick} :You are no longer marked as being away", ct);
                return;
            }

            if (away.Length > 200)
                away = away[..200];

            u.AwayMessage = away;
            await session.SendAsync($":server 306 {session.Nick} :You have been marked as being away", ct);
        }
    }
}
