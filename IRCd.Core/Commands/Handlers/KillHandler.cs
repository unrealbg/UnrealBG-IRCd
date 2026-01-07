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

    public sealed class KillHandler : IIrcCommandHandler
    {
        public string Command => "KILL";

        private readonly IOptions<IrcOptions> _options;
        private readonly RoutingService _routing;
        private readonly ServerLinkService _links;
        private readonly ISessionRegistry _sessions;
        private readonly SilenceService _silence;
        private readonly WatchService _watch;

        public KillHandler(IOptions<IrcOptions> options, RoutingService routing, ServerLinkService links, ISessionRegistry sessions, SilenceService silence, WatchService watch)
        {
            _options = options;
            _routing = routing;
            _links = links;
            _sessions = sessions;
            _silence = silence;
            _watch = watch;
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

            if (!state.TryGetUser(session.ConnectionId, out var oper) || oper is null || !OperCapabilityService.HasCapability(_options.Value, oper, "kill"))
            {
                await session.SendAsync($":{serverName} 481 {me} :Permission Denied- You're not an IRC operator", ct);
                return;
            }

            if (msg.Params.Count < 1 || string.IsNullOrWhiteSpace(msg.Params[0]))
            {
                await session.SendAsync($":{serverName} 461 {me} KILL :Not enough parameters", ct);
                return;
            }

            var targetNick = (msg.Params[0] ?? string.Empty).Trim();
            var reason = msg.Trailing;
            if (string.IsNullOrWhiteSpace(reason) && msg.Params.Count >= 2)
                reason = msg.Params[1];
            if (string.IsNullOrWhiteSpace(reason))
                reason = "Killed";

            if (!state.TryGetConnectionIdByNick(targetNick, out var targetConnId) || string.IsNullOrWhiteSpace(targetConnId))
            {
                await session.SendAsync($":{serverName} 401 {me} {targetNick} :No such nick", ct);
                return;
            }

            if (!state.TryGetUser(targetConnId!, out var targetUser) || targetUser is null)
            {
                await session.SendAsync($":{serverName} 401 {me} {targetNick} :No such nick", ct);
                return;
            }

            if (targetUser.IsService)
            {
                await session.SendAsync($":{serverName} NOTICE {me} :Cannot KILL services", ct);
                return;
            }

            var killQuit = $"Killed ({me}: {reason})";

            if (targetUser.IsRemote)
            {
                if (string.IsNullOrWhiteSpace(targetUser.Uid) || string.IsNullOrWhiteSpace(targetUser.RemoteSid))
                {
                    await session.SendAsync($":{serverName} NOTICE {me} :Cannot route remote KILL", ct);
                    return;
                }

                var ok = await _links.SendKillAsync(targetUser.RemoteSid!, targetUser.Uid!, killQuit, ct);
                if (!ok)
                {
                    await session.SendAsync($":{serverName} NOTICE {me} :Remote KILL routing failed", ct);
                    return;
                }

                await session.SendAsync($":{serverName} NOTICE {me} :KILL {targetNick} :{reason}", ct);
                return;
            }

            var quitLine = $":{targetUser.Nick}!{(targetUser.UserName ?? "u")}@{(targetUser.Host ?? "localhost")} QUIT :{killQuit}";

            var recipients = new HashSet<string>(StringComparer.Ordinal);
            foreach (var chName in state.GetUserChannels(targetConnId!))
            {
                if (!state.TryGetChannel(chName, out var ch) || ch is null)
                    continue;

                foreach (var member in ch.Members)
                {
                    if (member.ConnectionId == targetConnId)
                        continue;

                    recipients.Add(member.ConnectionId);
                }
            }

            foreach (var connId in recipients)
            {
                await _routing.SendToUserAsync(connId, quitLine, ct);
            }

            if (_sessions.TryGet(targetConnId!, out var targetSession) && targetSession is not null)
            {
                try { await targetSession.SendAsync($":{serverName} NOTICE {targetUser.Nick} :*** You were killed by {me} ({reason})", ct); } catch { }
                try { await targetSession.CloseAsync(killQuit, ct); } catch { }
            }

            _silence.RemoveAll(targetConnId!);
            _watch.RemoveAll(targetConnId!);

            if (!string.IsNullOrWhiteSpace(targetUser.Nick))
            {
                await _watch.NotifyLogoffAsync(state, targetUser.Nick!, targetUser.UserName, targetUser.Host, ct);
            }

            if (!string.IsNullOrWhiteSpace(targetUser.Uid))
            {
                await _links.PropagateQuitAsync(targetUser.Uid!, killQuit, ct);
            }

            state.RemoveUser(targetConnId!);

            await session.SendAsync($":{serverName} NOTICE {me} :KILL {targetNick} :{reason}", ct);
        }
    }
}
