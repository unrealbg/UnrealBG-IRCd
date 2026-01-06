namespace IRCd.Core.Commands.Handlers
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Core.Abstractions;
    using IRCd.Core.Commands.Contracts;
    using IRCd.Core.Protocol;
    using IRCd.Core.Services;
    using IRCd.Core.State;
    using IRCd.Shared.Options;

    using Microsoft.Extensions.Options;

    public sealed class WatchHandler : IIrcCommandHandler
    {
        public string Command => "WATCH";

        private readonly IOptions<IrcOptions> _options;
        private readonly IOptions<CommandLimitsOptions> _limits;
        private readonly WatchService _watch;

        public WatchHandler(IOptions<IrcOptions> options, IOptions<CommandLimitsOptions> limits, WatchService watch)
        {
            _options = options;
            _limits = limits;
            _watch = watch;
        }

        public async ValueTask HandleAsync(IClientSession session, IrcMessage msg, ServerState state, CancellationToken ct)
        {
            var server = _options.Value.ServerInfo?.Name ?? "server";

            if (!session.IsRegistered)
            {
                await session.SendAsync($":{server} 451 {(session.Nick ?? "*")} :You have not registered", ct);
                return;
            }

            var me = session.Nick ?? "*";

            if (msg.Params.Count == 0 && string.IsNullOrWhiteSpace(msg.Trailing))
            {
                await _watch.SendListAsync(state, session.ConnectionId, ct);
                return;
            }

            var tokens = new List<string>();
            foreach (var p in msg.Params)
            {
                if (string.IsNullOrWhiteSpace(p))
                    continue;

                foreach (var t in p.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    tokens.Add(t);
            }

            if (!string.IsNullOrWhiteSpace(msg.Trailing))
            {
                foreach (var t in msg.Trailing.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    tokens.Add(t);
                }
            }

            foreach (var raw in tokens)
            {
                var t = raw.Trim();
                if (t.Length == 0)
                {
                    continue;
                }

                if (string.Equals(t, "L", StringComparison.OrdinalIgnoreCase) || string.Equals(t, "S", StringComparison.OrdinalIgnoreCase))
                {
                    await _watch.SendListAsync(state, session.ConnectionId, ct);
                    continue;
                }

                if (string.Equals(t, "C", StringComparison.OrdinalIgnoreCase))
                {
                    _watch.Clear(session.ConnectionId);
                    await session.SendAsync($":{server} 606 {me} :End of WATCH list", ct);
                    continue;
                }

                var adding = true;
                var nick = t;

                if (t[0] == '+' || t[0] == '-')
                {
                    adding = t[0] == '+';
                    nick = t.Substring(1);
                }

                nick = nick.Trim();
                if (nick.Length == 0)
                {
                    continue;
                }

                if (adding)
                {
                    var ok = _watch.TryAdd(session.ConnectionId, nick, _limits.Value.MaxWatchEntries);
                    if (!ok)
                    {
                        await session.SendAsync($":{server} NOTICE {me} :WATCH list is full", ct);
                        continue;
                    }

                    await _watch.SendImmediateStatusAsync(state, session.ConnectionId, nick, ct);
                }
                else
                {
                    _watch.Remove(session.ConnectionId, nick);
                    await session.SendAsync($":{server} 602 {me} {nick} :Stopped watching", ct);
                }
            }
        }
    }
}
