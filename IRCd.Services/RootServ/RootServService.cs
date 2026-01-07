namespace IRCd.Services.RootServ
{
    using System;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Core.Abstractions;
    using IRCd.Core.Protocol;
    using IRCd.Core.Services;
    using IRCd.Core.State;
    using IRCd.Shared.Options;

    using Microsoft.Extensions.Hosting;
    using Microsoft.Extensions.Options;

    public sealed class RootServService
    {
        private readonly IOptions<IrcOptions> _options;
        private readonly ISessionRegistry _sessions;
        private readonly ChannelSnoopService _snoop;
        private readonly IHostApplicationLifetime? _lifetime;

        public RootServService(
            IOptions<IrcOptions> options,
            ISessionRegistry sessions,
            ChannelSnoopService snoop,
            IHostApplicationLifetime? lifetime = null)
        {
            _options = options;
            _sessions = sessions;
            _snoop = snoop;
            _lifetime = lifetime;
        }

        public async ValueTask HandleAsync(IClientSession session, string text, ServerState state, CancellationToken ct)
        {
            var input = (text ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(input))
            {
                await HelpAsync(session, Array.Empty<string>(), ct);
                return;
            }

            var parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var cmd = parts.Length > 0 ? parts[0].ToUpperInvariant() : "HELP";
            var args = parts.Skip(1).ToArray();

            switch (cmd)
            {
                case "HELP":
                    await HelpAsync(session, args, ct);
                    return;

                case "REFERENCE":
                    await ReplyAsync(session, RootServMessages.ReferenceIndex, ct);
                    return;

                case "SHUTDOWN":
                    if (!await RequireRootAsync(session, state, ct)) return;
                    await ShutdownAsync(session, ct);
                    return;

                case "RESTART":
                    if (!await RequireRootAsync(session, state, ct)) return;
                    await RestartAsync(session, ct);
                    return;

                case "RAW":
                    if (!await RequireRootAsync(session, state, ct)) return;
                    await RawAsync(session, args, ct);
                    return;

                case "INJECT":
                    if (!await RequireRootAsync(session, state, ct)) return;
                    await InjectAsync(session, args, ct);
                    return;

                case "QUIT":
                    if (!await RequireRootAsync(session, state, ct)) return;
                    await QuitAsync(session, args, state, ct);
                    return;

                case "CHANSNOOP":
                    if (!await RequireRootAsync(session, state, ct)) return;
                    await ChanSnoopAsync(session, args, ct);
                    return;

                default:
                    await ReplyAsync(session, "Unknown command. Try HELP.", ct);
                    return;
            }
        }

        private async ValueTask HelpAsync(IClientSession session, string[] args, CancellationToken ct)
        {
            if (args.Length == 0)
            {
                await ReplyAsync(session, RootServMessages.HelpIntro, ct);
                await ReplyAsync(session, RootServMessages.HelpReference, ct);
                await ReplyAsync(session, RootServMessages.HelpShutdown, ct);
                await ReplyAsync(session, RootServMessages.HelpRestart, ct);
                await ReplyAsync(session, RootServMessages.HelpRaw, ct);
                await ReplyAsync(session, RootServMessages.HelpInject, ct);
                await ReplyAsync(session, RootServMessages.HelpQuit, ct);
                await ReplyAsync(session, RootServMessages.HelpChanSnoop, ct);
                return;
            }

            var sub = args[0].ToUpperInvariant();
            var line = sub switch
            {
                "REFERENCE" => RootServMessages.HelpReference,
                "SHUTDOWN" => RootServMessages.HelpShutdown,
                "RESTART" => RootServMessages.HelpRestart,
                "RAW" => RootServMessages.HelpRaw,
                "INJECT" => RootServMessages.HelpInject,
                "QUIT" => RootServMessages.HelpQuit,
                "CHANSNOOP" => RootServMessages.HelpChanSnoop,
                _ => RootServMessages.HelpIntro
            };

            await ReplyAsync(session, line, ct);
        }

        private async ValueTask<bool> RequireRootAsync(IClientSession session, ServerState state, CancellationToken ct)
        {
            if (!session.IsRegistered)
            {
                await ReplyAsync(session, "You have not registered.", ct);
                return false;
            }

            if (!state.TryGetUser(session.ConnectionId, out var user) || user is null)
            {
                await ReplyAsync(session, "Permission denied.", ct);
                return false;
            }

            if (!user.Modes.HasFlag(UserModes.Operator) || string.IsNullOrWhiteSpace(user.OperClass))
            {
                await ReplyAsync(session, "Permission denied.", ct);
                return false;
            }

            if (!OperCapabilityService.HasCapability(_options.Value, user, "rootserv"))
            {
                await ReplyAsync(session, "Permission denied.", ct);
                return false;
            }

            return true;
        }

        private async ValueTask ShutdownAsync(IClientSession session, CancellationToken ct)
        {
            if (_lifetime is null)
            {
                await ReplyAsync(session, "SHUTDOWN is not available in this host.", ct);
                return;
            }

            await ReplyAsync(session, "Services host is shutting down", ct);
            _lifetime.StopApplication();
        }

        private async ValueTask RestartAsync(IClientSession session, CancellationToken ct)
        {
            if (_lifetime is null)
            {
                await ReplyAsync(session, "RESTART is not available in this host.", ct);
                return;
            }

            await ReplyAsync(session, "Services host is restarting", ct);
            _lifetime.StopApplication();
        }

        private async ValueTask RawAsync(IClientSession session, string[] args, CancellationToken ct)
        {
            if (args.Length < 1)
            {
                await ReplyAsync(session, "Syntax: RAW <line>", ct);
                return;
            }

            var line = string.Join(' ', args).Replace('\r', ' ').Replace('\n', ' ');
            foreach (var s in _sessions.All())
            {
                await s.SendAsync(line, ct);
            }

            await ReplyAsync(session, "RAW sent.", ct);
        }

        private async ValueTask InjectAsync(IClientSession session, string[] args, CancellationToken ct)
        {
            if (args.Length < 1)
            {
                await ReplyAsync(session, "Syntax: INJECT <line>", ct);
                return;
            }

            var payload = string.Join(' ', args).Replace('\r', ' ').Replace('\n', ' ');
            var server = _options.Value.ServerInfo?.Name ?? "server";
            var line = payload.StartsWith(":", StringComparison.Ordinal) ? payload : $":{server} {payload}";

            foreach (var s in _sessions.All())
            {
                await s.SendAsync(line, ct);
            }

            await ReplyAsync(session, "INJECT sent.", ct);
        }

        private async ValueTask QuitAsync(IClientSession session, string[] args, ServerState state, CancellationToken ct)
        {
            var nick = args.Length > 0 ? args[0].Trim() : RootServMessages.ServiceName;
            if (string.IsNullOrWhiteSpace(nick) || !IrcValidation.IsValidNick(nick, out _))
            {
                await ReplyAsync(session, "Syntax: QUIT [serviceNick]", ct);
                return;
            }

            if (!state.TryGetConnectionIdByNick(nick, out var cid) || string.IsNullOrWhiteSpace(cid))
            {
                await ReplyAsync(session, $"No such service user '{nick}'.", ct);
                return;
            }

            if (!state.TryGetUser(cid, out var u) || u is null || !u.IsService)
            {
                await ReplyAsync(session, $"'{nick}' is not a services pseudo-user.", ct);
                return;
            }

            state.RemoveUser(cid);

            if (_sessions.TryGet(cid, out var sess) && sess is not null)
            {
                await sess.CloseAsync("Services QUIT", ct);
            }

            await ReplyAsync(session, $"Service user '{nick}' removed.", ct);
        }

        private async ValueTask ChanSnoopAsync(IClientSession session, string[] args, CancellationToken ct)
        {
            if (args.Length == 0)
            {
                await ReplyAsync(session, RootServMessages.HelpChanSnoop, ct);
                return;
            }

            var sub = args[0].ToUpperInvariant();
            if (sub == "LIST")
            {
                var chans = _snoop.ListForWatcher(session.ConnectionId);
                if (chans.Length == 0)
                {
                    await ReplyAsync(session, "CHANSNOOP: no channels.", ct);
                    return;
                }

                await ReplyAsync(session, "CHANSNOOP: " + string.Join(' ', chans), ct);
                return;
            }

            if (args.Length < 2)
            {
                await ReplyAsync(session, RootServMessages.HelpChanSnoop, ct);
                return;
            }

            var channel = args[1].Trim();
            if (!IrcValidation.IsValidChannel(channel, out _))
            {
                await ReplyAsync(session, "Invalid channel.", ct);
                return;
            }

            if (sub == "ON")
            {
                _snoop.Enable(channel, session.ConnectionId);
                await ReplyAsync(session, $"CHANSNOOP enabled for {channel}.", ct);
                return;
            }

            if (sub == "OFF")
            {
                var ok = _snoop.Disable(channel, session.ConnectionId);
                await ReplyAsync(session, ok ? $"CHANSNOOP disabled for {channel}." : "CHANSNOOP was not enabled.", ct);
                return;
            }

            await ReplyAsync(session, RootServMessages.HelpChanSnoop, ct);
        }

        private ValueTask ReplyAsync(IClientSession session, string text, CancellationToken ct)
        {
            var server = _options.Value.ServerInfo?.Name ?? "server";
            var to = session.Nick ?? "*";
            var line = $":{RootServMessages.ServiceName}!services@{server} NOTICE {to} :{text}";
            return session.SendAsync(line, ct);
        }
    }
}
