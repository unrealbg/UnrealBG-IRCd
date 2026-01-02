namespace IRCd.Core.Services
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Core.Abstractions;
    using IRCd.Core.Protocol;
    using IRCd.Core.State;
    using IRCd.Shared.Options;

    using Microsoft.Extensions.Options;

    public sealed class RegistrationService
    {
        private readonly IOptions<IrcOptions> _options;
        private readonly LusersService _lusers;

        public RegistrationService(IOptions<IrcOptions> options, LusersService lusers)
        {
            _options = options;
            _lusers = lusers;
        }

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

            if (state.TryGetUser(session.ConnectionId, out var user) && user is not null)
            {
                user.Nick = session.Nick;
                user.UserName = session.UserName;
                user.IsRegistered = true;
            }

            var me = session.Nick!;
            var serverName = "server";
            var networkName = "IRCd";
            var version = "UnrealBG-IRCd";

            await session.SendAsync($":{serverName} 001 {me} :Welcome to the {networkName} IRC Network {me}!", ct);
            await session.SendAsync($":{serverName} 002 {me} :Your host is {serverName}, running version {version}", ct);
            await session.SendAsync($":{serverName} 003 {me} :This server was created {state.CreatedUtc:O}", ct);

            await session.SendAsync($":{serverName} 004 {me} {serverName} {version} oiwsz biklmnopstv", ct);

            await session.SendAsync($":{serverName} 005 {me} CHANTYPES=# PREFIX=(qaohv)~&@%+ CHANMODES=b,k,l,imnpst NETWORK={networkName} :are supported by this server", ct);

            await _lusers.SendOnConnectAsync(session, state, ct);

            await MotdSender.SendMotdAsync(session, _options.Value, ct);
        }
    }
}
