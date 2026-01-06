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

    public sealed class DlineHandler : IIrcCommandHandler
    {
        public string Command => "DLINE";

        private readonly IOptions<IrcOptions> _options;
        private readonly RuntimeDLineService _dlines;
        private readonly ISessionRegistry _sessions;

        public DlineHandler(IOptions<IrcOptions> options, RuntimeDLineService dlines, ISessionRegistry sessions)
        {
            _options = options;
            _dlines = dlines;
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

            if (!state.TryGetUser(session.ConnectionId, out var user) || user is null || !OperCapabilityService.HasCapability(_options.Value, user, "dline"))
            {
                await session.SendAsync($":{serverName} 481 {me} :Permission Denied- You're not an IRC operator", ct);
                return;
            }

            if (msg.Params.Count < 1)
            {
                await session.SendAsync($":{serverName} 461 {me} DLINE :Not enough parameters", ct);
                return;
            }

            var rawMask = msg.Params[0] ?? string.Empty;

            if (rawMask.StartsWith("-", StringComparison.Ordinal))
            {
                var toRemove = rawMask.TrimStart('-').Trim();
                var removed = _dlines.Remove(toRemove);
                await session.SendAsync($":{serverName} NOTICE {me} :UNDLINE {(removed ? "removed" : "not found")} {toRemove}", ct);
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

            _dlines.AddOrReplace(mask, reason);

            foreach (var u in state.GetAllUsers().Where(u => u.IsRegistered && !u.IsRemote).ToArray())
            {
                var remoteIp = u.RemoteIp;
                if (string.IsNullOrWhiteSpace(remoteIp))
                    continue;

                if (_dlines.TryMatch(remoteIp, out var r))
                {
                    var nick = u.Nick ?? "*";

                    if (_sessions.TryGet(u.ConnectionId, out var targetSession) && targetSession is not null)
                    {
                        await targetSession.SendAsync($":{serverName} 465 {nick} :You are banned from this server ({r})", ct);
                        await targetSession.CloseAsync("D-Lined", ct);
                    }

                    state.RemoveUser(u.ConnectionId);
                }
            }

            await session.SendAsync($":{serverName} NOTICE {me} :DLINE added {mask} :{reason}", ct);
        }
    }
}
