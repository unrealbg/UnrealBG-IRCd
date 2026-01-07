namespace IRCd.Services.InfoServ
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Core.Abstractions;
    using IRCd.Core.State;
    using IRCd.Shared.Options;

    using Microsoft.Extensions.Options;

    public sealed class InfoServService
    {
        private readonly IOptions<IrcOptions> _options;

        public InfoServService(IOptions<IrcOptions> options)
        {
            _options = options;
        }

        public async ValueTask HandleAsync(IClientSession session, string text, ServerState state, CancellationToken ct)
        {
            var input = (text ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(input))
            {
                await ReplyAsync(session, InfoServMessages.HelpIntro, ct);
                return;
            }

            var cmd = input.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[0];

            if (cmd.Equals("HELP", StringComparison.OrdinalIgnoreCase))
            {
                await ReplyAsync(session, InfoServMessages.HelpIntro, ct);
                return;
            }

            if (cmd.Equals("INFO", StringComparison.OrdinalIgnoreCase) || cmd.Equals("ABOUT", StringComparison.OrdinalIgnoreCase))
            {
                var si = _options.Value.ServerInfo;
                await ReplyAsync(session, $"Network: {si.Network}", ct);
                await ReplyAsync(session, $"Server: {si.Name} ({si.Description})", ct);
                await ReplyAsync(session, $"Version: {si.Version}", ct);

                if (!string.IsNullOrWhiteSpace(si.AdminEmail))
                {
                    await ReplyAsync(session, $"Admin: {si.AdminEmail}", ct);
                }

                return;
            }

            await ReplyAsync(session, "Unknown command. Try HELP.", ct);
        }

        private ValueTask ReplyAsync(IClientSession session, string text, CancellationToken ct)
        {
            var server = _options.Value.ServerInfo?.Name ?? "server";
            var to = session.Nick ?? "*";
            var line = $":{InfoServMessages.ServiceName}!services@{server} NOTICE {to} :{text}";
            return session.SendAsync(line, ct);
        }
    }
}
