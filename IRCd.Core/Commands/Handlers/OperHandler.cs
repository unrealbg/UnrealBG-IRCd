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

    public sealed class OperHandler : IIrcCommandHandler
    {
        public string Command => "OPER";

        private readonly IOptions<IrcOptions> _options;

        public OperHandler(IOptions<IrcOptions> options)
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

            if (msg.Params.Count < 2)
            {
                await session.SendAsync($":server 461 {session.Nick} OPER :Not enough parameters", ct);
                return;
            }

            var me = session.Nick!;

            var expected = _options.Value.OperPassword;
            if (string.IsNullOrWhiteSpace(expected))
            {
                await session.SendAsync($":server 464 {me} :Password incorrect", ct);
                return;
            }

            var provided = msg.Params[1] ?? string.Empty;
            if (!string.Equals(expected, provided, StringComparison.Ordinal))
            {
                await session.SendAsync($":server 464 {me} :Password incorrect", ct);
                return;
            }

            if (state.TrySetUserMode(session.ConnectionId, UserModes.Operator, enable: true))
            {
                await session.SendAsync($":server 381 {me} :You are now an IRC operator", ct);
            }
        }
    }
}
