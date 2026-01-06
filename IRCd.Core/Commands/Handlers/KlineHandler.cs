namespace IRCd.Core.Commands.Handlers
{
    using System;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Core.Abstractions;
    using IRCd.Core.Commands.Contracts;
    using IRCd.Core.Services;
    using IRCd.Core.State;
    using IRCd.Shared.Options;

    using Microsoft.Extensions.Options;

    public sealed class KlineHandler : IIrcCommandHandler
    {
        public string Command => "KLINE";

        private readonly IOptions<IrcOptions> _options;
        private readonly RuntimeKLineService _klines;
        private readonly ISessionRegistry _sessions;

        public KlineHandler(IOptions<IrcOptions> options, RuntimeKLineService klines, ISessionRegistry sessions)
        {
            _options = options;
            _klines = klines;
            _sessions = sessions;
        }

        public async ValueTask HandleAsync(IClientSession session, Protocol.IrcMessage msg, ServerState state, CancellationToken ct)
        {
            if (!session.IsRegistered)
            {
                await session.SendAsync($":server 451 {(session.Nick ?? "*")} :You have not registered", ct);
                return;
            }

            var me = session.Nick!;
            var serverName = _options.Value.ServerInfo?.Name ?? "server";

            if (!state.TryGetUser(session.ConnectionId, out var user) || user is null || !OperCapabilityService.HasCapability(_options.Value, user, "kline"))
            {
                await session.SendAsync($":{serverName} 481 {me} :Permission Denied- You're not an IRC operator", ct);
                return;
            }

            if (msg.Params.Count < 1)
            {
                await session.SendAsync($":{serverName} 461 {me} KLINE :Not enough parameters", ct);
                return;
            }

            var rawMask = msg.Params[0] ?? string.Empty;

            if (rawMask.StartsWith("-", StringComparison.Ordinal))
            {
                var toRemove = rawMask.TrimStart('-').Trim();
                var removed = _klines.Remove(toRemove);
                await session.SendAsync($":{serverName} NOTICE {me} :UNKLINE {(removed ? "removed" : "not found")} {toRemove}", ct);
                return;
            }

            var mask = rawMask.Trim();
            var reason = msg.Trailing;
            if (string.IsNullOrWhiteSpace(reason) && msg.Params.Count >= 2)
            {
                reason = msg.Params[1];
            }

            if (string.IsNullOrWhiteSpace(reason))
                reason = "Banned";

            _klines.AddOrReplace(mask, reason);

            foreach (var u in state.GetAllUsers().Where(u => u.IsRegistered && !u.IsRemote).ToArray())
            {
                var nick = u.Nick ?? "*";
                var userName = u.UserName ?? "user";
                var host = u.Host ?? "localhost";

                if (_klines.TryMatch(nick, userName, host, out var r))
                {
                    if (_sessions.TryGet(u.ConnectionId, out var targetSession) && targetSession is not null)
                    {
                        await targetSession.SendAsync($":{serverName} 465 {nick} :You are banned from this server ({r})", ct);
                        await targetSession.CloseAsync("K-Lined", ct);
                    }

                    state.RemoveUser(u.ConnectionId);
                }
            }

            await session.SendAsync($":{serverName} NOTICE {me} :KLINE added {mask} :{reason}", ct);
        }
    }
}
