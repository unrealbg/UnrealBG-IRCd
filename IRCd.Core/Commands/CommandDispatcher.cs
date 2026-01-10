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
        private readonly AutoDlineService? _autoDline;
        private readonly IMetrics _metrics;
        private readonly IServerClock? _clock;

        public CommandDispatcher(
            IEnumerable<IIrcCommandHandler> handlers,
            RateLimitService rateLimit,
            IMetrics metrics,
            FloodService? flood = null,
            IServerClock? clock = null,
            AutoDlineService? autoDline = null)
        {
            _handlers = handlers.ToDictionary(h => h.Command, StringComparer.OrdinalIgnoreCase);
            _rateLimit = rateLimit;
            _metrics = metrics;
            _flood = flood;
            _clock = clock;
            _autoDline = autoDline;
        }

        public async ValueTask DispatchAsync(IClientSession session, IrcMessage msg, ServerState state, CancellationToken ct)
        {
            _metrics.CommandProcessed(msg.Command);

            if (state.TryGetUser(session.ConnectionId, out var user) && user is not null)
            {
                if (UpdatesIdleForWhois(msg.Command))
                {
                    user.LastActivityUtc = _clock?.UtcNow ?? DateTimeOffset.UtcNow;
                }
            }

            if (_handlers.TryGetValue(msg.Command, out var handler))
            {
                if (_rateLimit.Enabled && IsRateLimitedCommand(msg.Command))
                {
                    var key = msg.Command.ToUpperInvariant();

                    if (!_rateLimit.TryConsume(session.ConnectionId, key, out var retryAfterSeconds))
                    {
                        if (_autoDline is not null)
                        {
                            var applied = await _autoDline.ObserveRateLimitAsync(session, state, ct);
                            if (applied)
                            {
                                try { await session.SendAsync("ERROR :D-Lined", ct); } catch { }
                                await session.CloseAsync("D-Lined", ct);
                                return;
                            }
                        }

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
                    var floodResult = await _flood.CheckCommandAsync(session, msg, state, ct);
                    if (floodResult.IsFlooding)
                    {
                        if (_autoDline is not null)
                        {
                            var applied = await _autoDline.ObserveFloodAsync(session, state, ct);
                            if (applied)
                            {
                                try { await session.SendAsync("ERROR :D-Lined", ct); } catch { }
                                await session.CloseAsync("D-Lined", ct);
                                return;
                            }
                        }

                        var nick = session.Nick ?? "*";

                        if (floodResult.ShouldWarn)
                        {
                            await session.SendAsync($":server NOTICE {nick} :Flood detected. Try again in {floodResult.CooldownSeconds}s", ct);
                        }

                        if (floodResult.ShouldDisconnect)
                        {
                            try { await session.SendAsync("ERROR :Excess Flood", ct); } catch { }
                            _metrics.FloodKick();
                            await session.CloseAsync("Excess Flood", ct);
                        }

                        return;
                    }

                    if (floodResult.ShouldWarn)
                    {
                        var nick = session.Nick ?? "*";
                        await session.SendAsync($":server NOTICE {nick} :Approaching flood limit", ct);
                    }
                }

                await handler.HandleAsync(session, msg, state, ct);
            }
            else
            {
                await session.SendAsync($":server 421 {session.Nick ?? "*"} {msg.Command} :Unknown command", ct);
            }
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
