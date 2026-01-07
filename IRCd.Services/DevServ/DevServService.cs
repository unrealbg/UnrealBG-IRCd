namespace IRCd.Services.DevServ
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Core.Abstractions;
    using IRCd.Core.State;
    using IRCd.Shared.Options;

    using Microsoft.Extensions.Options;

    public sealed class DevServService
    {
        private readonly IOptions<IrcOptions> _options;

        public DevServService(IOptions<IrcOptions> options)
        {
            _options = options;
        }

        public async ValueTask HandleAsync(IClientSession session, string text, ServerState state, CancellationToken ct)
        {
            var input = (text ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(input) || input.Equals("HELP", StringComparison.OrdinalIgnoreCase))
            {
                await ReplyAsync(session, DevServMessages.HelpIntro, ct);
                return;
            }

            await ReplyAsync(session, "DevServ is not implemented yet. Try HELP.", ct);
        }

        private ValueTask ReplyAsync(IClientSession session, string text, CancellationToken ct)
        {
            var server = _options.Value.ServerInfo?.Name ?? "server";
            var to = session.Nick ?? "*";
            var line = $":{DevServMessages.ServiceName}!services@{server} NOTICE {to} :{text}";
            return session.SendAsync(line, ct);
        }
    }
}
