namespace IRCd.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Net;
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
        private readonly IMetrics _metrics;
        private readonly WatchService _watch;
        private readonly ConnectionAuthService? _auth;
        private readonly BanService _banService;

        public RegistrationService(IOptions<IrcOptions> options, MotdSender motd, IMetrics metrics, WatchService watch, BanService banService, ConnectionAuthService? auth = null)
        {
            _options = options;
            _motd = motd;
            _metrics = metrics;
            _watch = watch;
            _banService = banService;
            _auth = auth;
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

            _metrics.UserRegistered();

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

                var remoteIp = session.RemoteEndPoint is System.Net.IPEndPoint ipEndPoint ? ipEndPoint.Address : null;
                var bans = await _banService.TryMatchSessionAsync(session.Nick!, session.UserName!, user.Host ?? "localhost", remoteIp, ct);
                
                if (bans.Count > 0)
                {
                    var ban = bans[0]; // Use first matching ban
                    var serverName2 = _options.Value.ServerInfo?.Name ?? "server";
                    var banTypeText = ban.Type switch
                    {
                        State.BanType.KLINE => "K-Lined",
                        State.BanType.DLINE => "D-Lined",
                        State.BanType.ZLINE => "Z-Lined",
                        State.BanType.QLINE => "Q-Lined",
                        State.BanType.AKILL => "AKilled",
                        _ => "Banned"
                    };
                    await session.SendAsync($":{serverName2} 465 {session.Nick} :You are banned from this server ({ban.Reason})", ct);
                    await session.CloseAsync(banTypeText, ct);
                    return;
                }

                await _watch.NotifyLogonAsync(state, user, ct);
            }

            var nick = session.Nick!;

            var serverName = _options.Value.ServerInfo?.Name ?? "server";
            var serverVersion = _options.Value.ServerInfo?.Version ?? "UnrealBG-IRCd";
            var networkName = _options.Value.ServerInfo?.Network ?? "IRCd";

            var clientPort = 6667;
            if (state.TryGetUser(session.ConnectionId, out var usr) && usr is not null && usr.IsSecureConnection)
            {
                clientPort = 6697;
            }

            const string userModeLetters = "ioZ";
            const string channelModeLetters = "beIklimnpst";

            var isupport = _options.Value.Isupport ?? new IsupportOptions();

            var prefix = string.IsNullOrWhiteSpace(isupport.Prefix) ? "(ov)@+" : isupport.Prefix;
            var chanTypes = string.IsNullOrWhiteSpace(isupport.ChanTypes) ? "#" : isupport.ChanTypes;
            var chanModesIsupport = string.IsNullOrWhiteSpace(isupport.ChanModes) ? "beI,k,l,imnpst" : isupport.ChanModes;
            var nickLen = isupport.NickLen > 0 ? isupport.NickLen : 20;
            var chanLen = isupport.ChanLen > 0 ? isupport.ChanLen : 50;
            var topicLen = isupport.TopicLen > 0 ? isupport.TopicLen : 300;
            var caseMapping = string.IsNullOrWhiteSpace(isupport.CaseMapping) ? "rfc1459" : isupport.CaseMapping;
            var maxModes = isupport.MaxModes > 0 ? isupport.MaxModes : 12;
            var statusMsg = string.IsNullOrWhiteSpace(isupport.StatusMsg) ? "@+" : isupport.StatusMsg;
            var awayLen = (isupport.AwayLen > 0 ? isupport.AwayLen : 200).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var elist = string.IsNullOrWhiteSpace(isupport.EList) ? "MNU" : isupport.EList;
            var kickLen = isupport.KickLen > 0 ? isupport.KickLen : 160;

            if (_auth is not null)
            {
                await _auth.AwaitAuthChecksAsync(session.ConnectionId, ct);
            }

            await session.SendAsync($":{serverName} 001 {nick} :Welcome to the {networkName} Internet Relay Chat Network {nick}", ct);
            await session.SendAsync($":{serverName} 002 {nick} :Your host is {serverName}[{serverName}/{clientPort}], running version {serverVersion}", ct);
            await session.SendAsync($":{serverName} 003 {nick} :This server was created {state.CreatedUtc:MMM d yyyy} at {state.CreatedUtc:HH:mm:ss}", ct);

            await session.SendAsync($":{serverName} 004 {nick} {serverName} {serverVersion} {userModeLetters} {channelModeLetters}", ct);

            await session.SendAsync(
                $":{serverName} 005 {nick} CALLERID CASEMAPPING={caseMapping} DEAF=D KICKLEN={kickLen} MODES={maxModes} NICKLEN={nickLen} PREFIX={prefix} STATUSMSG={statusMsg} TOPICLEN={topicLen} NETWORK={networkName} MAXLIST=beI:60 CHANTYPES={chanTypes} :are supported by this server",
                ct);
            await session.SendAsync(
                $":{serverName} 005 {nick} CHANLIMIT={chanTypes}:25 CHANNELLEN={chanLen} CHANMODES={chanModesIsupport} AWAYLEN={awayLen} ELIST={elist} SAFELIST KNOCK :are supported by this server",
                ct);

            var users = state.UserCount;
            var unknown = 0;
            var ops = 0;
            var channels = state.GetAllChannels().Count();

            await session.SendAsync($":{serverName} 251 {nick} :There are {users} users and {unknown} invisible on 1 servers", ct);
            await session.SendAsync($":{serverName} 252 {nick} {ops} :IRC Operators online", ct);
            await session.SendAsync($":{serverName} 254 {nick} {channels} :channels formed", ct);
            await session.SendAsync($":{serverName} 255 {nick} :I have {users} clients and 0 servers", ct);
            await session.SendAsync($":{serverName} 265 {nick} {users} {users} :Current local users: {users}  Max: {users}", ct);
            await session.SendAsync($":{serverName} 266 {nick} {users} {users} :Current global users: {users}  Max: {users}", ct);
            await session.SendAsync($":{serverName} 250 {nick} :Highest connection count: {users} ({users} clients) ({users} connections received)", ct);

            await _motd.TrySendMotdAsync(session, ct);
        }
    }
}
