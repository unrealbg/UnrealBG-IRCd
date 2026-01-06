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

    public sealed class QuitHandler : IIrcCommandHandler
    {
        public string Command => "QUIT";

        private readonly RoutingService _routing;
        private readonly ServerLinkService _links;
        private readonly WhowasService _whowas;
        private readonly SilenceService _silence;
        private readonly WatchService _watch;
        private readonly IServiceSessionEvents? _serviceEvents;

        public QuitHandler(RoutingService routing, ServerLinkService links, WhowasService whowas, SilenceService silence, WatchService watch, IServiceSessionEvents? serviceEvents = null)
        {
            _routing = routing;
            _links = links;
            _whowas = whowas;
            _silence = silence;
            _watch = watch;
            _serviceEvents = serviceEvents;
        }

        public async ValueTask HandleAsync(IClientSession session, IrcMessage msg, ServerState state, CancellationToken ct)
        {
            var reason = msg.Trailing;
            if (string.IsNullOrWhiteSpace(reason))
            {
                reason = "Client Quit";
            }

            if (session.IsRegistered && !string.IsNullOrWhiteSpace(session.Nick))
            {
                var nick = session.Nick!;
                var user = session.UserName ?? "u";
                var host = state.GetHostFor(session.ConnectionId);

                var quitLine = $":{nick}!{user}@{host} QUIT :{reason}";

                var recipients = new HashSet<string>(StringComparer.Ordinal);

                foreach (var chName in state.GetUserChannels(session.ConnectionId))
                {
                    if (!state.TryGetChannel(chName, out var ch) || ch is null)
                    {
                        continue;
                    }

                    foreach (var member in ch.Members)
                    {
                        if (member.ConnectionId == session.ConnectionId)
                        {
                            continue;
                        }

                        recipients.Add(member.ConnectionId);
                    }
                }

                foreach (var connId in recipients)
                {
                    await _routing.SendToUserAsync(connId, quitLine, ct);
                }

                if (state.TryGetUser(session.ConnectionId, out var u) && u is not null && !string.IsNullOrWhiteSpace(u.Uid))
                {
                    _whowas.Record(u, explicitNick: nick, signoff: reason);
                    await _links.PropagateQuitAsync(u.Uid!, reason, ct);
                }

                await _watch.NotifyLogoffAsync(state, nick, user, host, ct);
            }

            _silence.RemoveAll(session.ConnectionId);
            _watch.RemoveAll(session.ConnectionId);

            if (_serviceEvents is not null)
            {
                await _serviceEvents.OnQuitAsync(session.ConnectionId, ct);
            }

            await session.CloseAsync(reason, ct);
        }
    }
}
