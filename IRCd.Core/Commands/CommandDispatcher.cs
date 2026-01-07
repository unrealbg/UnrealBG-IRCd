namespace IRCd.Core.Commands
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Core.Abstractions;
    using IRCd.Core.Commands.Contracts;
    using IRCd.Core.Protocol;
    using IRCd.Core.Services;
    using IRCd.Core.State;

    public sealed class CommandDispatcher
    {
        private readonly Dictionary<string, IIrcCommandHandler> _handlers;
        private readonly RateLimitService _rateLimit;
        private readonly FloodService? _flood;
        private readonly IMetrics _metrics;

        public CommandDispatcher(IEnumerable<IIrcCommandHandler> handlers, RateLimitService rateLimit, IMetrics metrics, FloodService? flood = null)
        {
            _handlers = handlers.ToDictionary(h => h.Command, StringComparer.OrdinalIgnoreCase);
            _rateLimit = rateLimit;
            _metrics = metrics;
            _flood = flood;
        }

        public async ValueTask DispatchAsync(IClientSession session, IrcMessage msg, ServerState state, CancellationToken ct)
        {
            _metrics.CommandProcessed(msg.Command);

            if (state.TryGetUser(session.ConnectionId, out var user) && user is not null)
            {
                if (UpdatesIdleForWhois(msg.Command))
                {
                    user.LastActivityUtc = DateTimeOffset.UtcNow;
                }
            }

            if (_handlers.TryGetValue(msg.Command, out var handler))
            {
                if (_rateLimit.Enabled && IsRateLimitedCommand(msg.Command))
                {
                    var key = msg.Command.ToUpperInvariant();

                    if (!_rateLimit.TryConsume(session.ConnectionId, key, out var retryAfterSeconds))
                    {
                        var nick = session.Nick ?? "*";
                        await session.SendAsync($":server NOTICE {nick} :Flood detected. Try again in {retryAfterSeconds}s", ct);

                        if (_rateLimit.RegisterViolationAndShouldDisconnect(session.ConnectionId))
                        {
                            _metrics.FloodKick();
                            var quit = _rateLimit.GetDisconnectQuitMessage();
                            await session.CloseAsync(quit, ct);
                        }

                        return;
                    }
                }

                if (_flood is not null)
                {
                    var floodResult = CheckFlood(session, msg, state);
                    if (floodResult.IsFlooding)
                    {
                        var nick = session.Nick ?? "*";
                        await session.SendAsync($":server NOTICE {nick} :Flood detected. Try again in {floodResult.CooldownSeconds}s", ct);
                        return;
                    }
                }

                await handler.HandleAsync(session, msg, state, ct);
            }
            else
            {
                await session.SendAsync($":server 421 {session.Nick ?? "*"} {msg.Command} :Unknown command", ct);
            }
        }

        private FloodCheckResult CheckFlood(IClientSession session, IrcMessage msg, ServerState state)
        {
            state.TryGetUser(session.ConnectionId, out var user);

            if (msg.Command.Equals("PRIVMSG", StringComparison.OrdinalIgnoreCase)
                || msg.Command.Equals("NOTICE", StringComparison.OrdinalIgnoreCase))
            {
                var rawTargets = msg.Params is not null && msg.Params.Count > 0 ? msg.Params[0] : null;
                if (string.IsNullOrWhiteSpace(rawTargets))
                {
                    return default;
                }

                FloodCheckResult worst = default;
                foreach (var target in rawTargets.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var r = _flood!.CheckMessageFlood(session.ConnectionId, target, user);
                    if (r.IsFlooding)
                    {
                        return r;
                    }

                    if (r.ShouldWarn)
                    {
                        worst = r;
                    }
                }

                return worst;
            }

            if (msg.Command.Equals("JOIN", StringComparison.OrdinalIgnoreCase)
                || msg.Command.Equals("PART", StringComparison.OrdinalIgnoreCase))
            {
                return _flood!.CheckJoinPartFlood(session.ConnectionId, user);
            }

            if (msg.Command.Equals("NICK", StringComparison.OrdinalIgnoreCase))
            {
                return _flood!.CheckNickFlood(session.ConnectionId, user);
            }

            return default;
        }

        private static bool IsRateLimitedCommand(string command)
            => command.Equals("PRIVMSG", StringComparison.OrdinalIgnoreCase)
            || command.Equals("NOTICE", StringComparison.OrdinalIgnoreCase)
            || command.Equals("JOIN", StringComparison.OrdinalIgnoreCase)
            || command.Equals("WHO", StringComparison.OrdinalIgnoreCase)
            || command.Equals("WHOIS", StringComparison.OrdinalIgnoreCase)
            || command.Equals("NAMES", StringComparison.OrdinalIgnoreCase)
            || command.Equals("LIST", StringComparison.OrdinalIgnoreCase)
            || command.Equals("MODE", StringComparison.OrdinalIgnoreCase)
            || command.Equals("TOPIC", StringComparison.OrdinalIgnoreCase);

        private static bool UpdatesIdleForWhois(string command)
            => !command.Equals("PING", StringComparison.OrdinalIgnoreCase)
               && !command.Equals("PONG", StringComparison.OrdinalIgnoreCase)
               && !command.Equals("WHO", StringComparison.OrdinalIgnoreCase)
               && !command.Equals("WHOIS", StringComparison.OrdinalIgnoreCase);
    }
}
