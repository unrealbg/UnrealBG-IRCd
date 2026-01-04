namespace IRCd.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
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
        private readonly MotdSender _motd;

        public RegistrationService(IOptions<IrcOptions> options, MotdSender motd)
        {
            _options = options;
            _motd = motd;
        }

        public async ValueTask TryCompleteRegistrationAsync(IClientSession session, ServerState state, CancellationToken ct)
        {
            if (session.IsRegistered)
                return;

            if (string.IsNullOrWhiteSpace(session.Nick) || string.IsNullOrWhiteSpace(session.UserName))
                return;

            if (!string.IsNullOrWhiteSpace(_options.Value.ClientPassword) && !session.PassAccepted)
            {
                await session.SendAsync($":server 464 {session.Nick ?? "*"} :Password incorrect", ct);
                await session.CloseAsync("Bad password", ct);
                return;
            }

            session.IsRegistered = true;

            if (state.TryGetUser(session.ConnectionId, out var user) && user is not null)
            {
                user.Nick = session.Nick;
                user.UserName = session.UserName;
                user.IsRegistered = true;

                if (user.IsSecureConnection)
                {
                    user.Modes |= UserModes.Secure;
                }

                user.LastActivityUtc = DateTimeOffset.UtcNow;
            }

            var nick = session.Nick!;

            var serverName = _options.Value.ServerInfo?.Name ?? "server";
            var serverVersion = _options.Value.ServerInfo?.Version ?? "UnrealBG-IRCd";
            var networkName = _options.Value.ServerInfo?.Network ?? "IRCd";

            const string userModeLetters = "iZ";
            const string channelModeLetters = "bklimnpst";

            const string prefix = "(qaohv)~&@%+";
            const string chanTypes = "#";

            const string chanModesIsupport = "b,k,l,imnpst";
            const int nickLen = 20;
            const int chanLen = 50;
            const int topicLen = 300;
            const string caseMapping = "rfc1459";
            const int maxModes = 12;
            const string statusMsg = "~&@%+";
            const string awayLen = "200";
            const string elist = "MNU";

            await session.SendAsync($":{serverName} 001 {nick} :Welcome to the {networkName} IRC Network {nick}!", ct);
            await session.SendAsync($":{serverName} 002 {nick} :Your host is {serverName}, running version {serverVersion}", ct);
            await session.SendAsync($":{serverName} 003 {nick} :This server was created {state.CreatedUtc:O}", ct);

            await session.SendAsync($":{serverName} 004 {nick} {serverName} {serverVersion} {userModeLetters} {channelModeLetters}", ct);

            await session.SendAsync(
                $":{serverName} 005 {nick} CHANTYPES={chanTypes} PREFIX={prefix} CHANMODES={chanModesIsupport} NICKLEN={nickLen} CHANNELLEN={chanLen} TOPICLEN={topicLen} AWAYLEN={awayLen} CASEMAPPING={caseMapping} MODES={maxModes} STATUSMSG={statusMsg} ELIST={elist} NETWORK={networkName} :are supported by this server",
                ct);

            var users = state.UserCount;
            var unknown = 0;
            var ops = 0;
            var channels = state.GetAllChannels().Count();

            await session.SendAsync($":{serverName} 251 {nick} :There are {users} users and {unknown} unknown connections", ct);
            await session.SendAsync($":{serverName} 252 {nick} {ops} :operator(s) online", ct);
            await session.SendAsync($":{serverName} 254 {nick} {channels} :channels formed", ct);
            await session.SendAsync($":{serverName} 255 {nick} :I have {users} clients and 0 servers", ct);

            await _motd.TrySendMotdAsync(session, ct);
        }
    }
}
