namespace IRCd.Core.Commands.Handlers
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Core.Abstractions;
    using IRCd.Core.Commands.Contracts;
    using IRCd.Core.Protocol;
    using IRCd.Core.Services;
    using IRCd.Core.State;

    public sealed class UserHandler : IIrcCommandHandler
    {
        public string Command => "USER";

        private readonly RegistrationService _registration;

        public UserHandler(RegistrationService registration)
            => _registration = registration;

        public async ValueTask HandleAsync(IClientSession session, IrcMessage msg, ServerState state, CancellationToken ct)
        {
            if (session.IsRegistered)
            {
                await session.SendAsync($":server 462 {session.Nick ?? "*"} :You may not reregister", ct);
                return;
            }

            if (msg.Params.Count < 3)
            {
                await session.SendAsync($":server 461 {(session.Nick ?? "*")} USER :Not enough parameters", ct);
                return;
            }

            var userName = (msg.Params[0] ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(userName))
            {
                await session.SendAsync($":server 461 {(session.Nick ?? "*")} USER :Not enough parameters", ct);
                return;
            }

            var realName = msg.Trailing;
            if (string.IsNullOrWhiteSpace(realName) && msg.Params.Count >= 4)
            {
                realName = msg.Params[3];
            }

            realName = string.IsNullOrWhiteSpace(realName) ? "Unknown" : realName.Trim();
            if (realName.Length > 100) realName = realName[..100];

            session.UserName = userName;

            if (state.TryGetUser(session.ConnectionId, out var user) && user is not null)
            {
                user.UserName = userName;
                user.RealName = realName;
                user.LastActivityUtc = DateTimeOffset.UtcNow;
            }

            await session.SendAsync($":server NOTICE * :USER set to {userName}", ct);

            await _registration.TryCompleteRegistrationAsync(session, state, ct);
        }
    }
}
