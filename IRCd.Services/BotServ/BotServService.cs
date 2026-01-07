namespace IRCd.Services.BotServ
{
    using System;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Core.Abstractions;
    using IRCd.Core.Protocol;
    using IRCd.Core.Services;
    using IRCd.Core.State;
    using IRCd.Shared.Options;

    using Microsoft.Extensions.Options;

    public sealed class BotServService
    {
        private readonly IOptions<IrcOptions> _options;
        private readonly RoutingService _routing;
        private readonly BotAssignmentService _assignments;

        public BotServService(IOptions<IrcOptions> options, RoutingService routing, BotAssignmentService assignments)
        {
            _options = options;
            _routing = routing;
            _assignments = assignments;
        }

        public async ValueTask HandleAsync(IClientSession session, string text, ServerState state, CancellationToken ct)
        {
            var input = (text ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(input) || input.Equals("HELP", StringComparison.OrdinalIgnoreCase))
            {
                await ReplyAsync(session, BotServMessages.HelpIntro, ct);
                await ReplyAsync(session, BotServMessages.HelpList, ct);
                await ReplyAsync(session, BotServMessages.HelpInfo, ct);
                await ReplyAsync(session, BotServMessages.HelpAssign, ct);
                await ReplyAsync(session, BotServMessages.HelpUnassign, ct);
                await ReplyAsync(session, BotServMessages.HelpJoin, ct);
                await ReplyAsync(session, BotServMessages.HelpPart, ct);
                await ReplyAsync(session, BotServMessages.HelpSay, ct);
                await ReplyAsync(session, BotServMessages.HelpAct, ct);
                return;
            }

            var cmd = input.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[0];

            if (cmd.Equals("LIST", StringComparison.OrdinalIgnoreCase))
            {
                await HandleListAsync(session, ct);
                return;
            }

            if (cmd.Equals("INFO", StringComparison.OrdinalIgnoreCase))
            {
                await HandleInfoAsync(session, input, ct);
                return;
            }

            if (cmd.Equals("ASSIGN", StringComparison.OrdinalIgnoreCase))
            {
                await HandleAssignAsync(session, input, state, ct);
                return;
            }

            if (cmd.Equals("UNASSIGN", StringComparison.OrdinalIgnoreCase))
            {
                await HandleUnassignAsync(session, input, state, ct);
                return;
            }

            if (cmd.Equals("JOIN", StringComparison.OrdinalIgnoreCase))
            {
                await HandleJoinPartAsync(session, input, state, isJoin: true, ct);
                return;
            }

            if (cmd.Equals("PART", StringComparison.OrdinalIgnoreCase))
            {
                await HandleJoinPartAsync(session, input, state, isJoin: false, ct);
                return;
            }

            if (cmd.Equals("SAY", StringComparison.OrdinalIgnoreCase))
            {
                await HandleSayActAsync(session, input, state, isAction: false, ct);
                return;
            }

            if (cmd.Equals("ACT", StringComparison.OrdinalIgnoreCase))
            {
                await HandleSayActAsync(session, input, state, isAction: true, ct);
                return;
            }

            await ReplyAsync(session, "Unknown command. Use HELP.", ct);
        }

        private BotOptions[] GetConfiguredBots()
            => _options.Value.Services?.BotServ?.Bots ?? Array.Empty<BotOptions>();

        private BotOptions? FindBot(string nick)
        {
            if (string.IsNullOrWhiteSpace(nick))
            {
                return null;
            }

            var n = nick.Trim();
            return GetConfiguredBots().FirstOrDefault(b => b is not null && !string.IsNullOrWhiteSpace(b.Nick) && string.Equals(b.Nick, n, StringComparison.OrdinalIgnoreCase));
        }

        private static string GetBotConnectionId(string botNick)
            => $"service:{botNick.Trim().ToLowerInvariant()}";

        private async ValueTask HandleListAsync(IClientSession session, CancellationToken ct)
        {
            var bots = GetConfiguredBots();
            if (bots.Length == 0)
            {
                await ReplyAsync(session, "No bots configured.", ct);
                return;
            }

            var list = string.Join(", ", bots.Where(b => b is not null && !string.IsNullOrWhiteSpace(b.Nick)).Select(b => b.Nick!.Trim()));
            await ReplyAsync(session, $"Bots: {list}", ct);
        }

        private async ValueTask HandleInfoAsync(IClientSession session, string input, CancellationToken ct)
        {
            var parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 2)
            {
                await ReplyAsync(session, "Usage: INFO <botnick>", ct);
                return;
            }

            var botNick = parts[1];
            var bot = FindBot(botNick);
            if (bot is null)
            {
                await ReplyAsync(session, "No such bot.", ct);
                return;
            }

            await ReplyAsync(session, $"Bot {bot.Nick}: {(string.IsNullOrWhiteSpace(bot.RealName) ? "(no realname)" : bot.RealName)}", ct);
        }

        private async ValueTask HandleAssignAsync(IClientSession session, string input, ServerState state, CancellationToken ct)
        {
            var parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 3)
            {
                await ReplyAsync(session, "Usage: ASSIGN <#channel> <botnick>", ct);
                return;
            }

            if (!RequireBotServOper(session, state, ct))
            {
                return;
            }

            var channelName = parts[1];
            var botNick = parts[2];

            if (!IrcValidation.IsValidChannel(channelName, out _))
            {
                await ReplyAsync(session, "Invalid channel.", ct);
                return;
            }

            if (!IrcValidation.IsValidNick(botNick, out _))
            {
                await ReplyAsync(session, "Invalid bot nick.", ct);
                return;
            }

            if (FindBot(botNick) is null)
            {
                await ReplyAsync(session, "No such bot.", ct);
                return;
            }

            _assignments.Assign(channelName, botNick);
            await ReplyAsync(session, $"Assigned {botNick} to {channelName}.", ct);
        }

        private async ValueTask HandleUnassignAsync(IClientSession session, string input, ServerState state, CancellationToken ct)
        {
            var parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 2)
            {
                await ReplyAsync(session, "Usage: UNASSIGN <#channel>", ct);
                return;
            }

            if (!RequireBotServOper(session, state, ct))
            {
                return;
            }

            var channelName = parts[1];
            if (!IrcValidation.IsValidChannel(channelName, out _))
            {
                await ReplyAsync(session, "Invalid channel.", ct);
                return;
            }

            var ok = _assignments.Unassign(channelName);
            await ReplyAsync(session, ok ? $"Unassigned bot from {channelName}." : $"No bot assigned to {channelName}.", ct);
        }

        private async ValueTask HandleJoinPartAsync(IClientSession session, string input, ServerState state, bool isJoin, CancellationToken ct)
        {
            var parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 2)
            {
                await ReplyAsync(session, isJoin ? "Usage: JOIN <#channel> [botnick]" : "Usage: PART <#channel> [botnick]", ct);
                return;
            }

            if (!RequireBotServOper(session, state, ct))
            {
                return;
            }

            var channelName = parts[1];
            if (!IrcValidation.IsValidChannel(channelName, out _))
            {
                await ReplyAsync(session, "Invalid channel.", ct);
                return;
            }

            string? botNick = parts.Length >= 3 ? parts[2] : null;
            if (string.IsNullOrWhiteSpace(botNick))
            {
                _assignments.TryGetAssignedBot(channelName, out botNick);
            }

            if (string.IsNullOrWhiteSpace(botNick) || FindBot(botNick) is null)
            {
                await ReplyAsync(session, "No bot specified/assigned.", ct);
                return;
            }

            var botConn = GetBotConnectionId(botNick);
            if (!state.TryGetUser(botConn, out var botUser) || botUser is null)
            {
                await ReplyAsync(session, "Bot is not present on the network.", ct);
                return;
            }

            if (isJoin)
            {
                var joined = state.TryJoinChannel(botConn, botUser.Nick ?? botNick, channelName);
                await ReplyAsync(session, joined ? $"{botNick} joined {channelName}." : $"{botNick} is already in {channelName}.", ct);
                return;
            }

            var parted = state.TryPartChannel(botConn, channelName, out _);
            await ReplyAsync(session, parted ? $"{botNick} left {channelName}." : $"{botNick} is not in {channelName}.", ct);
        }

        private async ValueTask HandleSayActAsync(IClientSession session, string fullInput, ServerState state, bool isAction, CancellationToken ct)
        {
            if (!RequireBotServOper(session, state, ct))
            {
                return;
            }

            var parts = fullInput.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 3)
            {
                await ReplyAsync(session, isAction ? "Usage: ACT <#channel> <action> [botnick]" : "Usage: SAY <#channel> <text> [botnick]", ct);
                return;
            }

            var channelName = parts[1];
            if (!IrcValidation.IsValidChannel(channelName, out _))
            {
                await ReplyAsync(session, "Invalid channel.", ct);
                return;
            }

            string? explicitBot = null;
            var maybeBot = parts[^1];
            if (FindBot(maybeBot) is not null)
            {
                explicitBot = maybeBot;
            }

            string? botNick = explicitBot;
            if (string.IsNullOrWhiteSpace(botNick))
            {
                _assignments.TryGetAssignedBot(channelName, out botNick);
            }

            if (string.IsNullOrWhiteSpace(botNick) || FindBot(botNick) is null)
            {
                await ReplyAsync(session, "No bot specified/assigned.", ct);
                return;
            }

            var botConn = GetBotConnectionId(botNick);
            if (!state.TryGetUser(botConn, out var botUser) || botUser is null)
            {
                await ReplyAsync(session, "Bot is not present on the network.", ct);
                return;
            }

            if (!state.TryGetChannel(channelName, out var channel) || channel is null)
            {
                await ReplyAsync(session, "No such channel.", ct);
                return;
            }

            if (!channel.Contains(botConn))
            {
                await ReplyAsync(session, "Bot is not in that channel. Use JOIN first.", ct);
                return;
            }

            var start = parts[0].Length + 1 + parts[1].Length + 1;
            var raw = fullInput.Length >= start ? fullInput.Substring(start).Trim() : string.Empty;
            if (!string.IsNullOrWhiteSpace(explicitBot) && raw.EndsWith(" " + explicitBot, StringComparison.OrdinalIgnoreCase))
            {
                raw = raw[..^((" " + explicitBot).Length)].TrimEnd();
            }

            if (string.IsNullOrWhiteSpace(raw))
            {
                await ReplyAsync(session, "No text given.", ct);
                return;
            }

            var host = state.GetHostFor(botConn);
            var ident = string.IsNullOrWhiteSpace(botUser.UserName) ? "services" : botUser.UserName!;
            var msgText = isAction ? $"\u0001ACTION {raw}\u0001" : raw;
            var line = $":{botUser.Nick}!{ident}@{host} PRIVMSG {channelName} :{msgText}";

            await _routing.BroadcastToChannelAsync(channel, line, excludeConnectionId: null, ct);
            await ReplyAsync(session, "OK.", ct);
        }

        private bool RequireBotServOper(IClientSession session, ServerState state, CancellationToken ct)
        {
            _ = ct;
            if (!session.IsRegistered)
            {
                _ = ReplyAsync(session, "You must be registered.", ct);
                return false;
            }

            if (!state.TryGetUser(session.ConnectionId, out var invoker) || invoker is null)
            {
                _ = ReplyAsync(session, "Internal error.", ct);
                return false;
            }

            if (!OperCapabilityService.HasCapability(_options.Value, invoker, "botserv"))
            {
                _ = ReplyAsync(session, "Permission denied.", ct);
                return false;
            }

            return true;
        }

        private ValueTask ReplyAsync(IClientSession session, string text, CancellationToken ct)
        {
            var server = _options.Value.ServerInfo?.Name ?? "server";
            var to = session.Nick ?? "*";
            var line = $":{BotServMessages.ServiceName}!services@{server} NOTICE {to} :{text}";
            return session.SendAsync(line, ct);
        }
    }
}
