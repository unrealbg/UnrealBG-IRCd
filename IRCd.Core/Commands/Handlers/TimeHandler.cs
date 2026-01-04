namespace IRCd.Core.Commands.Handlers
{
    using System;
    using System.Globalization;
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Core.Abstractions;
    using IRCd.Core.Commands.Contracts;
    using IRCd.Core.Protocol;
    using IRCd.Core.State;

    using Microsoft.Extensions.Options;

    using IRCd.Shared.Options;

    public sealed class TimeHandler : IIrcCommandHandler
    {
        public string Command => "TIME";

        private readonly IOptions<IrcOptions> _options;

        public TimeHandler(IOptions<IrcOptions> options)
        {
            _options = options;
        }

        public async ValueTask HandleAsync(IClientSession session, IrcMessage msg, ServerState state, CancellationToken ct)
        {
            if (!session.IsRegistered)
            {
                await session.SendAsync($":server 451 {(session.Nick ?? "*")} :You have not registered", ct);
                return;
            }

            var me = session.Nick!;
            var serverName = _options.Value.ServerInfo?.Name ?? "server";

            var ts = DateTimeOffset.UtcNow.ToString("ddd MMM dd yyyy HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);
            await session.SendAsync($":{serverName} 391 {me} {serverName} :{ts}", ct);
        }
    }
}
