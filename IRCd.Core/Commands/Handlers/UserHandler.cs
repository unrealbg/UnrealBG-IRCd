namespace IRCd.Core.Commands.Handlers
{
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
            if (msg.Params.Count < 3)
            {
                await session.SendAsync($":server 461 {(session.Nick ?? "*")} USER :Not enough parameters", ct);
                return;
            }

            var userName = msg.Params[0];
            session.UserName = userName;

            if (state.TryGetUser(session.ConnectionId, out var user) && user is not null)
            {
                user.UserName = userName;
            }

            await _registration.TryCompleteRegistrationAsync(session, state, ct);
        }
    }
}
