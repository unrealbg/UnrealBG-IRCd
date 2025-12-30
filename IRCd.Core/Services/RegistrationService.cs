namespace IRCd.Core.Services
{
    using IRCd.Core.Abstractions;
    using IRCd.Core.State;

    public sealed class RegistrationService
    {
        public async ValueTask TryCompleteRegistrationAsync(IClientSession session, ServerState state, CancellationToken ct)
        {
            if (session.IsRegistered)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(session.Nick) || string.IsNullOrWhiteSpace(session.UserName))
            {
                return;
            }

            session.IsRegistered = true;

            await session.SendAsync($":server 001 {session.Nick} :Welcome to the IRC network {session.Nick}!", ct);
            await session.SendAsync($":server 002 {session.Nick} :Your host is server", ct);
            await session.SendAsync($":server 003 {session.Nick} :This server was created {DateTimeOffset.UtcNow:O}", ct);

            if (state.TryGetUser(session.ConnectionId, out var user) && user is not null)
            {
                user.Nick = session.Nick;
                user.UserName = session.UserName;
                user.IsRegistered = true;
            }
        }
    }
}
