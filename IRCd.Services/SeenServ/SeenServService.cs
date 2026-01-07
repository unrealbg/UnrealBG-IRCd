namespace IRCd.Services.SeenServ
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Core.Abstractions;
    using IRCd.Core.State;
    using IRCd.Services.Storage;
    using IRCd.Shared.Options;

    using Microsoft.Extensions.Options;

    public sealed class SeenServService
    {
        private readonly IOptions<IrcOptions> _options;
        private readonly ISeenRepository _seen;

        public SeenServService(IOptions<IrcOptions> options, ISeenRepository seen)
        {
            _options = options;
            _seen = seen;
        }

        public async ValueTask HandleAsync(IClientSession session, string text, ServerState state, CancellationToken ct)
        {
            var input = (text ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(input) || input.Equals("HELP", StringComparison.OrdinalIgnoreCase))
            {
                await ReplyAsync(session, SeenServMessages.HelpIntro, ct);
                return;
            }

            var nick = input.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[0];
            if (string.IsNullOrWhiteSpace(nick))
            {
                await ReplyAsync(session, SeenServMessages.HelpIntro, ct);
                return;
            }

            if (state.TryGetConnectionIdByNick(nick, out var connId) && connId is not null)
            {
                await ReplyAsync(session, $"{nick} is currently online.", ct);
                return;
            }

            var rec = await _seen.GetAsync(nick, ct);
            if (rec is null)
            {
                await ReplyAsync(session, $"I have not seen {nick}.", ct);
                return;
            }

            var age = DateTimeOffset.UtcNow - rec.WhenUtc;
            var ago = FormatAge(age);
            await ReplyAsync(session, $"{rec.Nick} was last seen {ago} ago: {rec.Message}", ct);
        }

        private static string FormatAge(TimeSpan age)
        {
            if (age < TimeSpan.Zero)
            {
                age = TimeSpan.Zero;
            }

            if (age.TotalDays >= 2)
            {
                return $"{(int)age.TotalDays}d";
            }

            if (age.TotalHours >= 2)
            {
                return $"{(int)age.TotalHours}h";
            }

            if (age.TotalMinutes >= 2)
            {
                return $"{(int)age.TotalMinutes}m";
            }

            return $"{Math.Max(0, (int)age.TotalSeconds)}s";
        }

        private ValueTask ReplyAsync(IClientSession session, string text, CancellationToken ct)
        {
            var server = _options.Value.ServerInfo?.Name ?? "server";
            var to = session.Nick ?? "*";
            var line = $":{SeenServMessages.ServiceName}!services@{server} NOTICE {to} :{text}";
            return session.SendAsync(line, ct);
        }
    }
}
