namespace IRCd.Core.Commands.Handlers
{
    using IRCd.Core.Abstractions;
    using IRCd.Core.Commands.Contracts;
    using IRCd.Core.Protocol;
    using IRCd.Core.State;

    public sealed class UserHandler : IIrcCommandHandler
    {
        public string Command => "USER";

        public async ValueTask HandleAsync(IClientSession session, IrcMessage msg, ServerState state, CancellationToken ct)
        {
            if (msg.Params.Count < 3)
            {
                await session.SendAsync($":server 461 {(session.Nick ?? "*")} USER :Not enough parameters", ct);
                return;
            }

            var userName = msg.Params[0];
            var realName = msg.Trailing ?? "Unknown";

            session.UserName = userName;

            if (state.TryGetUser(session.ConnectionId, out var user) && user is not null)
            {
                user.UserName = userName;
                user.RealName = realName;
            }

            await TryCompleteRegistrationAsync(session, state, ct);
        }

        private static async ValueTask TryCompleteRegistrationAsync(IClientSession session, ServerState state, CancellationToken ct)
        {
            if (session.IsRegistered) return;

            if (string.IsNullOrWhiteSpace(session.Nick) || string.IsNullOrWhiteSpace(session.UserName))
                return;

            session.IsRegistered = true;

            await session.SendAsync($":server 001 {session.Nick} :Welcome to the IRC network {session.Nick}!", ct);
            await session.SendAsync($":server 002 {session.Nick} :Your host is server", ct);
            await session.SendAsync($":server 003 {session.Nick} :This server was created {DateTimeOffset.UtcNow:O}", ct);

            if (!state.TryGetUser(session.ConnectionId, out var user) || user is null)
            {
                state.TryAddUser(new User
                {
                    ConnectionId = session.ConnectionId,
                    Nick = session.Nick,
                    UserName = session.UserName,
                    RealName = "Unknown",
                    IsRegistered = true
                });
            }
            else
            {
                user.Nick = session.Nick;
                user.IsRegistered = true;
            }
        }
    }
}
