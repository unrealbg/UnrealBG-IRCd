namespace IRCd.Services.StatServ
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Core.Abstractions;
    using IRCd.Core.State;
    using IRCd.Shared.Options;

    using Microsoft.Extensions.Options;

    public sealed class StatServService
    {
        private readonly IOptions<IrcOptions> _options;

        public StatServService(IOptions<IrcOptions> options)
        {
            _options = options;
        }

        public async ValueTask HandleAsync(IClientSession session, string text, ServerState state, CancellationToken ct)
        {
            var input = (text ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(input))
            {
                await ReplyAsync(session, StatServMessages.HelpIntro, ct);
                return;
            }

            var cmd = input.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[0];

            if (cmd.Equals("HELP", StringComparison.OrdinalIgnoreCase))
            {
                await ReplyAsync(session, StatServMessages.HelpIntro, ct);
                return;
            }

            if (cmd.Equals("STATS", StringComparison.OrdinalIgnoreCase))
            {
                var users = state.GetUsersSnapshot();
                var totalUsers = users.Count;
                var totalChannels = state.GetAllChannelNames().Count;
                var totalOpers = 0;
                var totalServices = 0;

                foreach (var u in users)
                {
                    if (u.IsService) totalServices++;
                    if (u.Modes.HasFlag(UserModes.Operator)) totalOpers++;
                }

                await ReplyAsync(session, $"Users: {totalUsers} (opers: {totalOpers}, services: {totalServices})", ct);
                await ReplyAsync(session, $"Channels: {totalChannels}", ct);
                return;
            }

            await ReplyAsync(session, "Unknown command. Try HELP.", ct);
        }

        private ValueTask ReplyAsync(IClientSession session, string text, CancellationToken ct)
        {
            var server = _options.Value.ServerInfo?.Name ?? "server";
            var to = session.Nick ?? "*";
            var line = $":{StatServMessages.ServiceName}!services@{server} NOTICE {to} :{text}";
            return session.SendAsync(line, ct);
        }
    }
}
