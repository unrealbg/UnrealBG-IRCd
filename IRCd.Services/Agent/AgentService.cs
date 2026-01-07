namespace IRCd.Services.Agent
{
    using System;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Core.Abstractions;
    using IRCd.Core.Services;
    using IRCd.Core.State;
    using IRCd.Shared.Options;

    using Microsoft.Extensions.Options;

    public sealed class AgentService
    {
        private const int MaxLogonLen = 300;
        private const int MaxBadwordLen = 64;

        private readonly IOptions<IrcOptions> _options;
        private readonly AgentStateService _state;

        public AgentService(IOptions<IrcOptions> options, AgentStateService state)
        {
            _options = options;
            _state = state;
        }

        public async ValueTask HandleAsync(IClientSession session, string text, ServerState serverState, CancellationToken ct)
        {
            _ = serverState;

            var input = (text ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(input) || input.Equals("HELP", StringComparison.OrdinalIgnoreCase))
            {
                await ReplyAsync(session, AgentMessages.HelpIntro, ct);
                await ReplyAsync(session, AgentMessages.HelpInfo, ct);
                await ReplyAsync(session, AgentMessages.HelpUpdate, ct);
                await ReplyAsync(session, AgentMessages.HelpBadwords, ct);
                await ReplyAsync(session, AgentMessages.HelpLogon, ct);
                return;
            }

            if (!session.IsRegistered)
            {
                await ReplyAsync(session, "You have not registered.", ct);
                return;
            }

            var parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var cmd = parts.Length > 0 ? parts[0] : "HELP";

            if (cmd.Equals("INFO", StringComparison.OrdinalIgnoreCase))
            {
                await HandleInfoAsync(session, ct);
                return;
            }

            if (cmd.Equals("UPDATE", StringComparison.OrdinalIgnoreCase))
            {
                if (!HasAgentCapability(session, serverState))
                {
                    await ReplyAsync(session, "Permission denied.", ct);
                    return;
                }

                _state.ReloadFrom(_options.Value);
                await ReplyAsync(session, "Agent settings reloaded from config.", ct);
                return;
            }

            if (cmd.Equals("BADWORDS", StringComparison.OrdinalIgnoreCase))
            {
                await HandleBadwordsAsync(session, parts, serverState, ct);
                return;
            }

            if (cmd.Equals("LOGON", StringComparison.OrdinalIgnoreCase))
            {
                await HandleLogonAsync(session, input, parts, serverState, ct);
                return;
            }

            await ReplyAsync(session, "Unknown command. Use HELP.", ct);
        }

        private async ValueTask HandleInfoAsync(IClientSession session, CancellationToken ct)
        {
            var msg = _state.GetLogonMessage();
            await ReplyAsync(session, string.IsNullOrWhiteSpace(msg) ? "LOGON: (none)" : $"LOGON: {msg}", ct);

            var bad = _state.GetBadwords();
            await ReplyAsync(session, $"BADWORDS: {bad.Count} word(s)", ct);
        }

        private async ValueTask HandleBadwordsAsync(IClientSession session, string[] parts, ServerState serverState, CancellationToken ct)
        {
            var sub = parts.Length >= 2 ? parts[1] : "LIST";

            if (sub.Equals("LIST", StringComparison.OrdinalIgnoreCase))
            {
                var words = _state.GetBadwords().OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
                if (words.Length == 0)
                {
                    await ReplyAsync(session, "BADWORDS: (empty)", ct);
                    return;
                }

                await ReplyAsync(session, "BADWORDS: " + string.Join(", ", words), ct);
                return;
            }

            if (!HasAgentCapability(session, serverState))
            {
                await ReplyAsync(session, "Permission denied.", ct);
                return;
            }

            if (sub.Equals("CLEAR", StringComparison.OrdinalIgnoreCase))
            {
                _state.ClearBadwords();
                await ReplyAsync(session, "BADWORDS cleared.", ct);
                return;
            }

            if ((sub.Equals("ADD", StringComparison.OrdinalIgnoreCase) || sub.Equals("DEL", StringComparison.OrdinalIgnoreCase)) && parts.Length < 3)
            {
                await ReplyAsync(session, "Usage: BADWORDS ADD <word> | BADWORDS DEL <word>", ct);
                return;
            }

            if (sub.Equals("ADD", StringComparison.OrdinalIgnoreCase))
            {
                var word = parts[2];
                if (!IsValidBadword(word))
                {
                    await ReplyAsync(session, "Invalid badword.", ct);
                    return;
                }

                var ok = _state.AddBadword(word);
                await ReplyAsync(session, ok ? "BADWORDS updated." : "Already present.", ct);
                return;
            }

            if (sub.Equals("DEL", StringComparison.OrdinalIgnoreCase) || sub.Equals("REMOVE", StringComparison.OrdinalIgnoreCase))
            {
                var ok = _state.RemoveBadword(parts[2]);
                await ReplyAsync(session, ok ? "BADWORDS updated." : "Not found.", ct);
                return;
            }

            await ReplyAsync(session, "Usage: BADWORDS [LIST|ADD <word>|DEL <word>|CLEAR]", ct);
        }

        private async ValueTask HandleLogonAsync(IClientSession session, string fullInput, string[] parts, ServerState serverState, CancellationToken ct)
        {
            if (parts.Length == 1)
            {
                var msg = _state.GetLogonMessage();
                await ReplyAsync(session, string.IsNullOrWhiteSpace(msg) ? "LOGON: (none)" : $"LOGON: {msg}", ct);
                return;
            }

            var sub = parts[1];
            if (sub.Equals("CLEAR", StringComparison.OrdinalIgnoreCase))
            {
                if (!HasAgentCapability(session, serverState))
                {
                    await ReplyAsync(session, "Permission denied.", ct);
                    return;
                }

                _state.SetLogonMessage(null);
                await ReplyAsync(session, "LOGON cleared.", ct);
                return;
            }

            if (sub.Equals("SET", StringComparison.OrdinalIgnoreCase))
            {
                if (!HasAgentCapability(session, serverState))
                {
                    await ReplyAsync(session, "Permission denied.", ct);
                    return;
                }

                var start = parts[0].Length + 1 + parts[1].Length + 1;
                var msg = fullInput.Length >= start ? fullInput.Substring(start) : string.Empty;
                msg = SanitizeLogon(msg);
                if (string.IsNullOrWhiteSpace(msg))
                {
                    await ReplyAsync(session, "Invalid message.", ct);
                    return;
                }

                _state.SetLogonMessage(msg);
                await ReplyAsync(session, "LOGON updated.", ct);
                return;
            }

            await ReplyAsync(session, "Usage: LOGON [SET <message>|CLEAR]", ct);
        }

        private bool HasAgentCapability(IClientSession session, ServerState serverState)
        {
            if (!serverState.TryGetUser(session.ConnectionId, out var u) || u is null)
            {
                return false;
            }

            return OperCapabilityService.HasCapability(_options.Value, u, "agent");
        }

        private static bool IsValidBadword(string word)
        {
            if (string.IsNullOrWhiteSpace(word))
            {
                return false;
            }

            word = word.Trim();
            if (word.Length > MaxBadwordLen)
            {
                return false;
            }

            return word.IndexOfAny([' ', '\r', '\n', '\0', '\t', ':']) < 0;
        }

        private static string SanitizeLogon(string msg)
        {
            if (string.IsNullOrWhiteSpace(msg))
            {
                return string.Empty;
            }

            msg = msg.Replace("\r", string.Empty).Replace("\n", string.Empty).Replace("\0", string.Empty);
            msg = msg.Trim();
            if (msg.Length > MaxLogonLen)
            {
                msg = msg[..MaxLogonLen];
            }

            return msg;
        }

        private ValueTask ReplyAsync(IClientSession session, string text, CancellationToken ct)
        {
            var server = _options.Value.ServerInfo?.Name ?? "server";
            var to = session.Nick ?? "*";
            var line = $":{AgentMessages.ServiceName}!services@{server} NOTICE {to} :{text}";
            return session.SendAsync(line, ct);
        }
    }
}
