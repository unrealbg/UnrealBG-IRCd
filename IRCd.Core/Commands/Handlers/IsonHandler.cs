namespace IRCd.Core.Commands.Handlers
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Core.Abstractions;
    using IRCd.Core.Commands.Contracts;
    using IRCd.Core.Protocol;
    using IRCd.Core.State;
    using IRCd.Shared.Options;

    using Microsoft.Extensions.Options;

    public sealed class IsonHandler : IIrcCommandHandler
    {
        public string Command => "ISON";

        private readonly IOptions<IrcOptions> _options;

        public IsonHandler(IOptions<IrcOptions> options)
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

            var me = session.Nick ?? "*";

            var maxNames = _options.Value.Limits?.MaxIsonNames > 0 ? _options.Value.Limits.MaxIsonNames : 128;

            var online = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void AddTokens(string? s)
            {
                if (string.IsNullOrWhiteSpace(s))
                    return;

                foreach (var n in s.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (online.Count >= maxNames)
                        return;

                    if (!IrcValidation.IsValidNick(n, out _))
                        continue;

                    if (state.TryGetConnectionIdByNick(n, out _))
                        online.Add(n);
                }
            }

            foreach (var p in msg.Params)
            {
                AddTokens(p);
                if (online.Count >= maxNames)
                    break;
            }

            if (online.Count < maxNames)
                AddTokens(msg.Trailing);

            await session.SendAsync($":server 303 {me} :{string.Join(' ', online)}", ct);
        }
    }
}
