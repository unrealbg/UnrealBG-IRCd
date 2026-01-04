namespace IRCd.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Core.Abstractions;
    using IRCd.Core.State;
    using IRCd.Shared.Options;

    using Microsoft.Extensions.Options;

    public sealed class RegistrationService
    {
        private const string ServerName = "server";
        private const string ServerVersion = "UnrealBG-IRCd";

        private const string UserModeLetters = "i";

        private const string ChannelModeLetters = "bklimnpst";

        private const string ChanModesIsupport = "b,k,l,imnpst";
        private const string ChanTypes = "#";
        private const string Prefix = "(qaohv)*&@%+";
        private const string NetworkName = "IRCd";

        private readonly IOptions<IrcOptions> _options;

        public RegistrationService(IOptions<IrcOptions> options)
        {
            _options = options;
        }

        public async ValueTask TryCompleteRegistrationAsync(IClientSession session, ServerState state, CancellationToken ct)
        {
            if (session.IsRegistered)
                return;

            if (string.IsNullOrWhiteSpace(session.Nick) || string.IsNullOrWhiteSpace(session.UserName))
                return;

            session.IsRegistered = true;

            if (state.TryGetUser(session.ConnectionId, out var user) && user is not null)
            {
                user.Nick = session.Nick;
                user.UserName = session.UserName;
                user.IsRegistered = true;

                user.LastActivityUtc = DateTimeOffset.UtcNow;
            }

            var nick = session.Nick!;

            await session.SendAsync($":{ServerName} 001 {nick} :Welcome to the {NetworkName} IRC Network {nick}!", ct);
            await session.SendAsync($":{ServerName} 002 {nick} :Your host is {ServerName}, running version {ServerVersion}", ct);
            await session.SendAsync($":{ServerName} 003 {nick} :This server was created {state.CreatedUtc:O}", ct);

            await session.SendAsync($":{ServerName} 004 {nick} {ServerName} {ServerVersion} {UserModeLetters} {ChannelModeLetters}", ct);

            await session.SendAsync(
                $":{ServerName} 005 {nick} CHANTYPES={ChanTypes} PREFIX={Prefix} CHANMODES={ChanModesIsupport} NETWORK={NetworkName} :are supported by this server",
                ct);

            var users = state.UserCount;
            var unknown = 0;
            var ops = 0;
            var channels = state.GetAllChannels().Count();

            await session.SendAsync($":{ServerName} 251 {nick} :There are {users} users and {unknown} unknown connections", ct);
            await session.SendAsync($":{ServerName} 252 {nick} {ops} :operator(s) online", ct);
            await session.SendAsync($":{ServerName} 254 {nick} {channels} :channels formed", ct);
            await session.SendAsync($":{ServerName} 255 {nick} :I have {users} clients and 0 servers", ct);

            await SendMotdAsync(session, nick, ct);
        }

        private async Task SendMotdAsync(IClientSession session, string nick, CancellationToken ct)
        {
            var motd = _options.Value.Motd;
            var lines = await LoadMotdLinesAsync(motd, ct);

            if (lines.Count == 0)
            {
                await session.SendAsync($":{ServerName} 422 {nick} :MOTD File is missing", ct);
                return;
            }

            await session.SendAsync($":{ServerName} 375 {nick} :- {ServerName} Message of the Day -", ct);

            foreach (var line in lines)
            {
                await session.SendAsync($":{ServerName} 372 {nick} :- {line}", ct);
            }

            await session.SendAsync($":{ServerName} 376 {nick} :End of /MOTD command.", ct);
        }

        private static async Task<List<string>> LoadMotdLinesAsync(MotdOptions motd, CancellationToken ct)
        {
            if (motd.Lines is { Length: > 0 })
            {
                return motd.Lines
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Select(s => s!.TrimEnd('\r', '\n'))
                    .ToList();
            }

            if (string.IsNullOrWhiteSpace(motd.FilePath))
                return new List<string>();

            try
            {
                var path = motd.FilePath;

                if (!Path.IsPathRooted(path))
                {
                    path = Path.Combine(AppContext.BaseDirectory, path);
                }

                if (!File.Exists(path))
                    return new List<string>();

                var fileLines = await File.ReadAllLinesAsync(path, ct);

                return fileLines
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Select(s => s.TrimEnd('\r', '\n'))
                    .ToList();
            }
            catch
            {
                return new List<string>();
            }
        }
    }
}
