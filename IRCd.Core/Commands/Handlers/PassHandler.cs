namespace IRCd.Core.Commands.Handlers
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Core.Abstractions;
    using IRCd.Core.Commands.Contracts;
    using IRCd.Core.Protocol;
    using IRCd.Core.State;
    using IRCd.Shared.Options;

    using Microsoft.Extensions.Options;

    public sealed class PassHandler : IIrcCommandHandler
    {
        public string Command => "PASS";

        private readonly IOptions<IrcOptions> _options;

        public PassHandler(IOptions<IrcOptions> options)
        {
            _options = options;
        }

        public async ValueTask HandleAsync(IClientSession session, IrcMessage msg, ServerState state, CancellationToken ct)
        {
            if (session.IsRegistered)
            {
                await session.SendAsync($":server 462 {session.Nick ?? "*"} :You may not reregister", ct);
                return;
            }

            if (msg.Params.Count < 1)
            {
                await session.SendAsync($":server 461 {(session.Nick ?? "*")} PASS :Not enough parameters", ct);
                return;
            }

            var provided = (msg.Params[0] ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(provided))
            {
                await session.SendAsync($":server 461 {(session.Nick ?? "*")} PASS :Not enough parameters", ct);
                return;
            }

            var expected = _options.Value.ClientPassword;

            if (!string.IsNullOrWhiteSpace(expected) && !string.Equals(expected, provided, StringComparison.Ordinal))
            {
                await session.SendAsync($":server 464 {(session.Nick ?? "*")} :Password incorrect", ct);
                await session.CloseAsync("Bad password", ct);
                return;
            }

            if (!string.IsNullOrWhiteSpace(expected))
            {
                session.PassAccepted = true;
            }
        }
    }
}
