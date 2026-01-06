namespace IRCd.Core.Commands.Handlers
{
    using System;
    using System.Collections.Generic;
    using IRCd.Core.Abstractions;
    using IRCd.Core.Commands.Contracts;
    using IRCd.Core.Protocol;
    using IRCd.Core.Services;
    using IRCd.Core.State;
    using Microsoft.Extensions.Logging;

    public sealed class NickHandler : IIrcCommandHandler
    {
        public string Command => "NICK";
        private readonly RoutingService _routing;
        private readonly RegistrationService _registration;
        private readonly ServerLinkService _links;
        private readonly HostmaskService _hostmask;
        private readonly WhowasService _whowas;
        private readonly WatchService _watch;
        private readonly Microsoft.Extensions.Options.IOptions<IRCd.Shared.Options.IrcOptions> _options;
        private readonly IServiceSessionEvents? _serviceEvents;
        private readonly ISessionRegistry _sessions;
        private readonly Microsoft.Extensions.Logging.ILogger<NickHandler> _logger;

        public NickHandler(RoutingService routing, RegistrationService registration, ServerLinkService links, HostmaskService hostmask, WhowasService whowas, WatchService watch, Microsoft.Extensions.Options.IOptions<IRCd.Shared.Options.IrcOptions> options, ISessionRegistry sessions, Microsoft.Extensions.Logging.ILogger<NickHandler> logger, IServiceSessionEvents? serviceEvents = null)
        {
            _routing = routing;
            _registration = registration;
            _links = links;
            _hostmask = hostmask;
            _whowas = whowas;
            _watch = watch;
            _options = options;
            _sessions = sessions;
            _logger = logger;
            _serviceEvents = serviceEvents;
        }

        public async ValueTask HandleAsync(IClientSession session, IrcMessage msg, ServerState state, CancellationToken ct)
        {
            var newNick = msg.Params.Count > 0 ? msg.Params[0] : msg.Trailing;
            newNick = newNick?.Trim();

            if (string.IsNullOrWhiteSpace(newNick))
            {
                await session.SendAsync(":server 431 * :No nickname given", ct);
                return;
            }

            if (!IrcValidation.IsValidNick(newNick, out _))
            {
                await session.SendAsync($":server 432 * {newNick} :Erroneous nickname", ct);
                return;
            }

            var oldNick = session.Nick;

            if (oldNick is not null && string.Equals(oldNick, newNick, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (state.TryGetConnectionIdByNick(newNick, out var existingConnId) && 
                existingConnId is not null && 
                existingConnId != session.ConnectionId)
            {
                if (!_sessions.TryGet(existingConnId, out var existingSession) || existingSession is null)
                {
                    if (state.TryGetUser(existingConnId, out var ghostUser) && ghostUser is not null)
                    {
                        var ghostChannels = state.GetUserChannels(existingConnId);
                        var ghostNick = ghostUser.Nick ?? newNick;
                        var ghostUserName = ghostUser.UserName ?? "ghost";
                        var ghostHost = _hostmask.GetDisplayedHost((existingSession?.RemoteEndPoint as System.Net.IPEndPoint)?.Address);
                        var quitLine = $":{ghostNick}!{ghostUserName}@{ghostHost} QUIT :Ghost session cleaned up";

                        var recipients = new HashSet<string>();
                        foreach (var chName in ghostChannels)
                        {
                            if (state.TryGetChannel(chName, out var channel) && channel is not null)
                            {
                                foreach (var member in channel.Members)
                                {
                                    if (member.ConnectionId != existingConnId) // Don't send to ghost itself
                                    {
                                        recipients.Add(member.ConnectionId);
                                    }
                                }
                            }
                        }

                        foreach (var recipientConnId in recipients)
                        {
                            await _routing.SendToUserAsync(recipientConnId, quitLine, ct);
                        }
                    }

                    state.RemoveUser(existingConnId);
                }
                else
                {
                    await session.SendAsync($":server 433 * {newNick} :Nickname is already in use", ct);
                    return;
                }
            }

            if (!state.TrySetNick(session.ConnectionId, newNick))
            {
                await session.SendAsync($":server 433 * {newNick} :Nickname is already in use", ct);
                return;
            }

            if (!string.IsNullOrWhiteSpace(oldNick) && state.TryGetUser(session.ConnectionId, out var oldUser) && oldUser is not null)
            {
                _whowas.Record(oldUser, explicitNick: oldNick);
            }

            session.Nick = newNick;
            await _registration.TryCompleteRegistrationAsync(session, state, ct);

            if (_serviceEvents is not null)
            {
                await _serviceEvents.OnNickChangedAsync(session, oldNick, newNick, state, ct);
            }

            if (state.TryGetUser(session.ConnectionId, out var u) && u is not null && string.IsNullOrWhiteSpace(u.Uid))
            {
                var sid = _options.Value.ServerInfo?.Sid ?? "001";
                u.Uid = $"{sid}{session.ConnectionId[..Math.Min(6, session.ConnectionId.Length)].ToUpperInvariant()}";
                u.RemoteSid = sid;
                u.IsRemote = false;
            }

            if (string.IsNullOrWhiteSpace(oldNick))
            {
                return;
            }

            if (state.TryGetUser(session.ConnectionId, out var meUser2) && meUser2 is not null)
            {
                await _watch.NotifyNickChangeAsync(state, meUser2, oldNick, ct);
            }

            var channels = state.UpdateNickInUserChannels(session.ConnectionId, newNick);

            var user = session.UserName ?? "u";
            var host = (_hostmask.GetDisplayedHost((session.RemoteEndPoint as System.Net.IPEndPoint)?.Address));
            var nickLine = $":{oldNick}!{user}@{host} NICK :{newNick}";

            _logger.LogInformation("[NickHandler] Nick change {OldNick} -> {NewNick} for connId {ConnId}, in {ChannelCount} channels", 
                oldNick, newNick, session.ConnectionId, channels.Count);

            var nickRecipients = new HashSet<string>(StringComparer.Ordinal);
            foreach (var ch in channels)
            {
                _logger.LogInformation("[NickHandler] Channel {ChannelName} has {MemberCount} members", ch.Name, ch.Members.Count);
                foreach (var member in ch.Members)
                {
                    if (member.ConnectionId == session.ConnectionId)
                    {
                        continue;
                    }
                    nickRecipients.Add(member.ConnectionId);
                    _logger.LogInformation("[NickHandler] Adding recipient {ConnId} (nick: {Nick})", member.ConnectionId, member.Nick);
                }
            }

            _logger.LogInformation("[NickHandler] Broadcasting NICK change to {RecipientCount} recipients", nickRecipients.Count);

            foreach (var connId in nickRecipients)
            {
                await _routing.SendToUserAsync(connId, nickLine, ct);
            }

            _logger.LogInformation("[NickHandler] Sending NICK change to self: {NickLine}", nickLine);
            await session.SendAsync(nickLine, ct);

            // Force refresh channel member list for self by sending NAMES for each channel
            foreach (var ch in channels)
            {
                var names = new System.Text.StringBuilder();
                foreach (var member in ch.Members)
                {
                    var p = member.Privilege.ToPrefix();
                    var prefix = p is null ? "" : p.Value.ToString();
                    names.Append($"{prefix}{member.Nick} ");
                }
                
                var namesList = names.ToString().TrimEnd();
                if (!string.IsNullOrWhiteSpace(namesList))
                {
                    await session.SendAsync($":server 353 {newNick} = {ch.Name} :{namesList}", ct);
                }
                await session.SendAsync($":server 366 {newNick} {ch.Name} :End of /NAMES list.", ct);
            }

            if (state.TryGetUser(session.ConnectionId, out var meUser) && meUser is not null && !string.IsNullOrWhiteSpace(meUser.Uid))
            {
                meUser.NickTs = ChannelTimestamps.NowTs();
                await _links.PropagateNickAsync(meUser.Uid!, newNick, ct);
            }
        }
    }
}
