namespace IRCd.Core.Commands.Handlers
{
    using System;
    using System.Linq;
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

            var operName = msg.Params[0] ?? string.Empty;
            var provided = msg.Params[1] ?? string.Empty;

            var cfg = _options.Value;

            string? operClass = null;

            if (cfg.Opers is { Length: > 0 })
            {
                var match = cfg.Opers.FirstOrDefault(o =>
                    o is not null
                    && !string.IsNullOrWhiteSpace(o.Name)
                    && string.Equals(o.Name, operName, StringComparison.OrdinalIgnoreCase));

                if (match is null || string.IsNullOrWhiteSpace(match.Password) || !string.Equals(match.Password, provided, StringComparison.Ordinal))
                {
                    await session.SendAsync($":server 464 {me} :Password incorrect", ct);
                    return;
                }

                operClass = string.IsNullOrWhiteSpace(match.Class) ? null : match.Class;
            }
            else
            {
                var expected = cfg.OperPassword;
                if (string.IsNullOrWhiteSpace(expected) || !string.Equals(expected, provided, StringComparison.Ordinal))
                {
                    await session.SendAsync($":server 464 {me} :Password incorrect", ct);
                    return;
                }
            }

            if (state.TrySetUserMode(session.ConnectionId, UserModes.Operator, enable: true))
            {
                state.TrySetOperInfo(session.ConnectionId, operName, operClass);
                await session.SendAsync($":server 381 {me} :You are now an IRC operator", ct);
            }
        }
    }
}
