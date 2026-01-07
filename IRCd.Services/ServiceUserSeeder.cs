namespace IRCd.Services
{
    using System;

    using IRCd.Core.State;
    using IRCd.Services.Agent;
    using IRCd.Services.AdminServ;
    using IRCd.Services.BotServ;
    using IRCd.Services.ChanServ;
    using IRCd.Services.DevServ;
    using IRCd.Services.HelpServ;
    using IRCd.Services.HostServ;
    using IRCd.Services.InfoServ;
    using IRCd.Services.MemoServ;
    using IRCd.Services.NickServ;
    using IRCd.Services.OperServ;
    using IRCd.Services.RootServ;
    using IRCd.Services.StatServ;
    using IRCd.Services.SeenServ;
    using IRCd.Shared.Options;

    using Microsoft.Extensions.Logging;

    public static class ServiceUserSeeder
    {
        public static void EnsureServiceUsers(ServerState state, IrcOptions options, ILogger? logger = null)
        {
            if (state is null) throw new ArgumentNullException(nameof(state));
            if (options is null) throw new ArgumentNullException(nameof(options));

            var host = BuildServicesHost(options);

            EnsureOne(state, NickServMessages.ServiceName, host, "Nickname Services", logger);
            EnsureOne(state, ChanServMessages.ServiceName, host, "Channel Services", logger);
            EnsureOne(state, OperServMessages.ServiceName, host, "Operator Services", logger);
            EnsureOne(state, MemoServMessages.ServiceName, host, "Memo Services", logger);
            EnsureOne(state, SeenServMessages.ServiceName, host, "Seen Services", logger);
            EnsureOne(state, InfoServMessages.ServiceName, host, "Information Services", logger);
            EnsureOne(state, StatServMessages.ServiceName, host, "Statistics Services", logger);
            EnsureOne(state, AdminServMessages.ServiceName, host, "Admin Services", logger);
            EnsureOne(state, DevServMessages.ServiceName, host, "Developer Services", logger);
            EnsureOne(state, HelpServMessages.ServiceName, host, "Help Services", logger);
            EnsureOne(state, RootServMessages.ServiceName, host, "Root Services", logger);
            EnsureOne(state, HostServMessages.ServiceName, host, "Host Services", logger);
            EnsureOne(state, BotServMessages.ServiceName, host, "Bot Services", logger);
            EnsureOne(state, AgentMessages.ServiceName, host, "Agent Services", logger);

            var bots = options.Services?.BotServ?.Bots;
            if (bots is not null)
            {
                foreach (var b in bots)
                {
                    if (b is null || string.IsNullOrWhiteSpace(b.Nick))
                    {
                        continue;
                    }

                    var nick = b.Nick.Trim();
                    if (!IRCd.Core.Protocol.IrcValidation.IsValidNick(nick, out _))
                    {
                        logger?.LogWarning("BotServ: skipping invalid bot nick {Nick}", nick);
                        continue;
                    }

                    var real = string.IsNullOrWhiteSpace(b.RealName) ? "Service Bot" : b.RealName.Trim();

                    EnsureBot(state, nick, host, real, logger);
                }
            }
        }

        private static void EnsureBot(ServerState state, string nick, string host, string realName, ILogger? logger)
        {
            if (string.IsNullOrWhiteSpace(nick))
            {
                return;
            }

            if (state.TryGetConnectionIdByNick(nick, out var _))
            {
                return;
            }

            var connId = $"service:{nick.ToLowerInvariant()}";

            var user = new User
            {
                ConnectionId = connId,
                IsService = true,
                IsRemote = false,
                Nick = nick,
                UserName = "bot",
                Host = host,
                RealName = realName,
                IsRegistered = true,
                IsSecureConnection = false,
            };

            if (!state.TryAddUser(user))
            {
                logger?.LogWarning("Failed to register bot user {Nick} (nick already in use?)", nick);
            }
        }

        private static void EnsureOne(ServerState state, string nick, string host, string realName, ILogger? logger)
        {
            if (string.IsNullOrWhiteSpace(nick))
            {
                return;
            }

            if (state.TryGetConnectionIdByNick(nick, out var _))
            {
                return;
            }

            var connId = $"service:{nick.ToLowerInvariant()}";

            var user = new User
            {
                ConnectionId = connId,
                IsService = true,
                IsRemote = false,
                Nick = nick,
                UserName = "services",
                Host = host,
                RealName = realName,
                IsRegistered = true,
                IsSecureConnection = false,
            };

            if (!state.TryAddUser(user))
            {
                logger?.LogWarning("Failed to register service user {Nick} (nick already in use?)", nick);
            }
        }

        private static string BuildServicesHost(IrcOptions options)
        {
            var baseName = options.ServerInfo?.Network;
            if (string.IsNullOrWhiteSpace(baseName))
            {
                baseName = options.ServerInfo?.Name;
            }

            if (string.IsNullOrWhiteSpace(baseName))
            {
                return "services.localhost";
            }

            baseName = SanitizeHostLabel(baseName);
            if (string.IsNullOrWhiteSpace(baseName))
            {
                return "services.localhost";
            }

            return "services." + baseName;
        }

        private static string SanitizeHostLabel(string input)
        {
            Span<char> buf = stackalloc char[input.Length];
            var j = 0;

            foreach (var c in input)
            {
                var ok =
                    (c >= 'a' && c <= 'z') ||
                    (c >= 'A' && c <= 'Z') ||
                    (c >= '0' && c <= '9') ||
                    c == '-' || c == '.';

                buf[j++] = ok ? c : '-';
            }

            var s = new string(buf[..j]);
            return s.Trim('.').Trim('-');
        }
    }
}
