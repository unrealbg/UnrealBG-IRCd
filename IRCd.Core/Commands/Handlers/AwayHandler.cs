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

    public sealed class AwayHandler : IIrcCommandHandler
    {
        public string Command => "AWAY";

        private readonly ISessionRegistry _sessions;

        public AwayHandler(ISessionRegistry sessions)
        {
            _sessions = sessions;
        }

        public async ValueTask HandleAsync(IClientSession session, IrcMessage msg, ServerState state, CancellationToken ct)
        {
            if (!session.IsRegistered)
            {
                await session.SendAsync($":server 451 {(session.Nick ?? "*")} :You have not registered", ct);
                return;
            }

            if (!state.TryGetUser(session.ConnectionId, out var u) || u is null)
            {
                await session.SendAsync($":server 401 {session.Nick} {session.Nick} :No such nick", ct);
                return;
            }

            var away = msg.Trailing;
            if (string.IsNullOrWhiteSpace(away) && msg.Params.Count > 0)
            {
                away = msg.Params[0];
            }

            away = away?.Trim();

            if (string.IsNullOrWhiteSpace(away))
            {
                u.AwayMessage = null;
                await session.SendAsync($":server 305 {session.Nick} :You are no longer marked as being away", ct);
                
                await BroadcastAwayNotifyAsync(session, state, null, ct);
                return;
            }

            if (away.Length > 200)
                away = away[..200];

            u.AwayMessage = away;
            await session.SendAsync($":server 306 {session.Nick} :You have been marked as being away", ct);
            
            await BroadcastAwayNotifyAsync(session, state, away, ct);
        }

        private async ValueTask BroadcastAwayNotifyAsync(IClientSession session, ServerState state, string? awayMessage, CancellationToken ct)
        {
            var userChannels = state.GetUserChannels(session.ConnectionId);
            var notifiedConnIds = new System.Collections.Generic.HashSet<string>();

            foreach (var channelName in userChannels)
            {
                if (!state.TryGetChannel(channelName, out var channel) || channel is null)
                    continue;

                foreach (var member in channel.Members)
                {
                    if (member.ConnectionId == session.ConnectionId)
                        continue;

                    if (notifiedConnIds.Contains(member.ConnectionId))
                        continue;

                    if (!_sessions.TryGet(member.ConnectionId, out var memberSession) || memberSession is null)
                        continue;

                    if (!memberSession.EnabledCapabilities.Contains("away-notify"))
                        continue;

                    notifiedConnIds.Add(member.ConnectionId);

                    var nick = session.Nick ?? "*";
                    var userName = session.UserName ?? "u";
                    var host = state.GetHostFor(session.ConnectionId);
                    
                    if (string.IsNullOrWhiteSpace(awayMessage))
                    {
                        await memberSession.SendAsync($":{nick}!{userName}@{host} AWAY", ct);
                    }
                    else
                    {
                        await memberSession.SendAsync($":{nick}!{userName}@{host} AWAY :{awayMessage}", ct);
                    }
                }
            }
        }
    }
}
