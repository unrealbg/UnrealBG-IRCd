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

        public CommandDispatcher(IEnumerable<IIrcCommandHandler> handlers, RateLimitService rateLimit)
        {
            _handlers = handlers.ToDictionary(h => h.Command, StringComparer.OrdinalIgnoreCase);
            _rateLimit = rateLimit;
        }

        public async ValueTask DispatchAsync(IClientSession session, IrcMessage msg, ServerState state, CancellationToken ct)
        {
            if (state.TryGetUser(session.ConnectionId, out var user) && user is not null)
            {
                user.LastActivityUtc = DateTimeOffset.UtcNow;
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
                            var quit = _rateLimit.GetDisconnectQuitMessage();
                            await session.CloseAsync(quit, ct);
                        }

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
    }
}
