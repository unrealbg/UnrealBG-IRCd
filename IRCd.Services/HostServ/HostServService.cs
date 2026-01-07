namespace IRCd.Services.HostServ
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Core.Abstractions;
    using IRCd.Core.Protocol;
    using IRCd.Core.Services;
    using IRCd.Core.State;
    using IRCd.Services.Storage;
    using IRCd.Shared.Options;

    using Microsoft.Extensions.Options;

    public sealed class HostServService
    {
        private const int MaxVHostLen = 120;

        private readonly IOptions<IrcOptions> _options;
        private readonly IVHostRepository _vhosts;
        private readonly RoutingService _routing;

        public HostServService(IOptions<IrcOptions> options, IVHostRepository vhosts, RoutingService routing)
        {
            _options = options;
            _vhosts = vhosts;
            _routing = routing;
        }

        public async ValueTask HandleAsync(IClientSession session, string text, ServerState state, CancellationToken ct)
        {
            var input = (text ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(input) || input.Equals("HELP", StringComparison.OrdinalIgnoreCase))
            {
                await ReplyAsync(session, HostServMessages.HelpIntro, ct);
                await ReplyAsync(session, HostServMessages.HelpSetHost, ct);
                await ReplyAsync(session, HostServMessages.HelpSetHost2, ct);
                await ReplyAsync(session, HostServMessages.HelpAdd, ct);
                await ReplyAsync(session, HostServMessages.HelpDel, ct);
                await ReplyAsync(session, HostServMessages.HelpChange, ct);
                return;
            }

            var parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var cmd = parts.Length > 0 ? parts[0] : "HELP";

            if (cmd.Equals("SETHOST", StringComparison.OrdinalIgnoreCase))
            {
                await HandleSetHostAsync(session, parts, state, ct);
                return;
            }

            if (cmd.Equals("ADD", StringComparison.OrdinalIgnoreCase))
            {
                await HandleAddOrChangeAsync(session, parts, state, isChange: false, ct);
                return;
            }

            if (cmd.Equals("CHANGE", StringComparison.OrdinalIgnoreCase))
            {
                await HandleAddOrChangeAsync(session, parts, state, isChange: true, ct);
                return;
            }

            if (cmd.Equals("DEL", StringComparison.OrdinalIgnoreCase) || cmd.Equals("DELETE", StringComparison.OrdinalIgnoreCase))
            {
                await HandleDelAsync(session, parts, state, ct);
                return;
            }

            await ReplyAsync(session, "Unknown command. Use HELP.", ct);
        }

        private async ValueTask HandleSetHostAsync(IClientSession session, string[] parts, ServerState state, CancellationToken ct)
        {
            var serverName = _options.Value.ServerInfo?.Name ?? "server";
            var me = session.Nick ?? "*";

            if (!session.IsRegistered)
            {
                await session.SendAsync($":{serverName} 451 {me} :You have not registered", ct);
                return;
            }

            if (!state.TryGetUser(session.ConnectionId, out var invoker) || invoker is null)
            {
                await ReplyAsync(session, "Internal error.", ct);
                return;
            }

            if (parts.Length == 2)
            {
                if (invoker.IsService)
                {
                    await ReplyAsync(session, "Services cannot set vHosts.", ct);
                    return;
                }

                if (invoker.IsRemote)
                {
                    await ReplyAsync(session, "Cannot set vHosts for remote users.", ct);
                    return;
                }

                var vhost = parts[1];
                if (!IsValidVHost(vhost))
                {
                    await ReplyAsync(session, "Invalid vHost.", ct);
                    return;
                }

                var nick = invoker.Nick ?? string.Empty;
                var grant = await _vhosts.GetAsync(nick, ct);
                if (grant is null || !string.Equals(grant.VHost, vhost, StringComparison.OrdinalIgnoreCase))
                {
                    await ReplyAsync(session, "You are not authorized for that vHost.", ct);
                    return;
                }

                await ApplyHostAsync(invoker.ConnectionId, invoker, vhost, state, ct);
                await ReplyAsync(session, $"vHost set to {vhost}.", ct);
                return;
            }

            if (parts.Length == 3)
            {
                if (!OperCapabilityService.HasCapability(_options.Value, invoker, "hostserv"))
                {
                    await ReplyAsync(session, "Permission denied.", ct);
                    return;
                }

                var targetNick = parts[1];
                var vhost = parts[2];

                if (!IrcValidation.IsValidNick(targetNick, out _))
                {
                    await ReplyAsync(session, "Invalid nick.", ct);
                    return;
                }

                if (!IsValidVHost(vhost))
                {
                    await ReplyAsync(session, "Invalid vHost.", ct);
                    return;
                }

                await _vhosts.TryUpsertAsync(new VHostRecord { Nick = targetNick, VHost = vhost }, ct);

                if (!state.TryGetConnectionIdByNick(targetNick, out var targetConn) || string.IsNullOrWhiteSpace(targetConn))
                {
                    await ReplyAsync(session, $"vHost assigned to {targetNick}. They can apply it with /msg HostServ SETHOST {vhost}.", ct);
                    return;
                }

                if (!state.TryGetUser(targetConn!, out var targetUser) || targetUser is null)
                {
                    await ReplyAsync(session, "No such nick.", ct);
                    return;
                }

                if (targetUser.IsService)
                {
                    await ReplyAsync(session, "Cannot set vHosts for services.", ct);
                    return;
                }

                if (targetUser.IsRemote)
                {
                    await ReplyAsync(session, "Cannot set vHosts for remote users.", ct);
                    return;
                }

                await ApplyHostAsync(targetConn!, targetUser, vhost, state, ct);
                await ReplyAsync(session, $"vHost set for {targetNick} to {vhost}.", ct);
                return;
            }

            await ReplyAsync(session, "Usage: SETHOST <vhost> OR SETHOST <nick> <vhost>", ct);
        }

        private async ValueTask HandleAddOrChangeAsync(IClientSession session, string[] parts, ServerState state, bool isChange, CancellationToken ct)
        {
            if (!session.IsRegistered)
            {
                await ReplyAsync(session, "You must be registered.", ct);
                return;
            }

            if (!state.TryGetUser(session.ConnectionId, out var invoker) || invoker is null)
            {
                await ReplyAsync(session, "Internal error.", ct);
                return;
            }

            if (!OperCapabilityService.HasCapability(_options.Value, invoker, "hostserv"))
            {
                await ReplyAsync(session, "Permission denied.", ct);
                return;
            }

            if (parts.Length < 3)
            {
                await ReplyAsync(session, "Not enough parameters.", ct);
                return;
            }

            var nick = parts[1];
            var vhost = parts[2];

            if (!IrcValidation.IsValidNick(nick, out _))
            {
                await ReplyAsync(session, "Invalid nick.", ct);
                return;
            }

            if (!IsValidVHost(vhost))
            {
                await ReplyAsync(session, "Invalid vHost.", ct);
                return;
            }

            var ok = await _vhosts.TryUpsertAsync(new VHostRecord { Nick = nick, VHost = vhost }, ct);
            if (!ok)
            {
                await ReplyAsync(session, "Failed to save vHost assignment.", ct);
                return;
            }

            await ReplyAsync(session, isChange ? $"vHost updated for {nick}." : $"vHost assigned to {nick}.", ct);
        }

        private async ValueTask HandleDelAsync(IClientSession session, string[] parts, ServerState state, CancellationToken ct)
        {
            if (!session.IsRegistered)
            {
                await ReplyAsync(session, "You must be registered.", ct);
                return;
            }

            if (!state.TryGetUser(session.ConnectionId, out var invoker) || invoker is null)
            {
                await ReplyAsync(session, "Internal error.", ct);
                return;
            }

            if (!OperCapabilityService.HasCapability(_options.Value, invoker, "hostserv"))
            {
                await ReplyAsync(session, "Permission denied.", ct);
                return;
            }

            if (parts.Length < 2)
            {
                await ReplyAsync(session, "Not enough parameters.", ct);
                return;
            }

            var nick = parts[1];
            if (!IrcValidation.IsValidNick(nick, out _))
            {
                await ReplyAsync(session, "Invalid nick.", ct);
                return;
            }

            var ok = await _vhosts.TryDeleteAsync(nick, ct);
            await ReplyAsync(session, ok ? $"vHost removed for {nick}." : $"No vHost entry for {nick}.", ct);
        }

        private async ValueTask ApplyHostAsync(string connectionId, User user, string newHost, ServerState state, CancellationToken ct)
        {
            var nick = user.Nick ?? "*";
            var ident = string.IsNullOrWhiteSpace(user.UserName) ? "u" : user.UserName!;
            var oldHost = state.GetHostFor(connectionId);

            user.Host = newHost;

            var line = $":{nick}!{ident}@{oldHost} CHGHOST {ident} {newHost}";
            var recipients = new HashSet<string>(StringComparer.Ordinal) { connectionId };

            foreach (var chName in state.GetUserChannels(connectionId))
            {
                if (!state.TryGetChannel(chName, out var ch) || ch is null)
                    continue;

                foreach (var member in ch.Members)
                {
                    recipients.Add(member.ConnectionId);
                }
            }

            foreach (var connId in recipients)
            {
                await _routing.SendToUserAsync(connId, line, ct);
            }
        }

        private static bool IsValidVHost(string vhost)
        {
            if (string.IsNullOrWhiteSpace(vhost))
            {
                return false;
            }

            vhost = vhost.Trim();
            if (vhost.Length > MaxVHostLen)
            {
                return false;
            }

            return vhost.IndexOfAny([' ', '\r', '\n', '\0', '\t', ':']) < 0;
        }

        private ValueTask ReplyAsync(IClientSession session, string text, CancellationToken ct)
        {
            var server = _options.Value.ServerInfo?.Name ?? "server";
            var to = session.Nick ?? "*";
            var line = $":{HostServMessages.ServiceName}!services@{server} NOTICE {to} :{text}";
            return session.SendAsync(line, ct);
        }
    }
}
