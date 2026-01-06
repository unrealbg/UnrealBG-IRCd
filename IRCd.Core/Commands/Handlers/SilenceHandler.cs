namespace IRCd.Core.Commands.Handlers
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Core.Abstractions;
    using IRCd.Core.Commands.Contracts;
    using IRCd.Core.Protocol;
    using IRCd.Core.Services;
    using IRCd.Core.State;
    using IRCd.Shared.Options;

    using Microsoft.Extensions.Options;

    public sealed class SilenceHandler : IIrcCommandHandler
    {
        public string Command => "SILENCE";

        private readonly IOptions<IrcOptions> _options;
        private readonly SilenceService _silence;

        public SilenceHandler(IOptions<IrcOptions> options, SilenceService silence)
        {
            _options = options;
            _silence = silence;
        }

        public async ValueTask HandleAsync(IClientSession session, IrcMessage msg, ServerState state, CancellationToken ct)
        {
            var serverName = _options.Value.ServerInfo?.Name ?? "server";

            if (!session.IsRegistered)
            {
                await session.SendAsync($":{serverName} 451 {(session.Nick ?? "*")} :You have not registered", ct);
                return;
            }

            var me = session.Nick ?? "*";

            if (msg.Params.Count == 0 || string.IsNullOrWhiteSpace(msg.Params[0]))
            {
                foreach (var entryMask in _silence.GetList(session.ConnectionId))
                {
                    await session.SendAsync($":{serverName} 271 {me} {entryMask}", ct);
                }

                await session.SendAsync($":{serverName} 272 {me} :End of SILENCE list", ct);
                return;
            }

            var raw = msg.Params[0].Trim();
            var mode = raw[0];
            var mask = raw;

            if (mode == '+' || mode == '-')
            {
                mask = raw.Length > 1 ? raw[1..] : string.Empty;
            }
            else
            {
                mode = '+';
            }

            mask = mask.Trim();
            if (string.IsNullOrWhiteSpace(mask))
            {
                await session.SendAsync($":{serverName} 461 {me} SILENCE :Not enough parameters", ct);
                return;
            }

            var max = _options.Value.Limits?.MaxSilenceEntries > 0 ? _options.Value.Limits.MaxSilenceEntries : 15;

            if (mode == '-')
            {
                _silence.Remove(session.ConnectionId, mask);
                await session.SendAsync($":{serverName} NOTICE {me} :SILENCE -{mask}", ct);
                return;
            }

            var ok = _silence.TryAdd(session.ConnectionId, mask, max);
            if (!ok)
            {
                await session.SendAsync($":{serverName} NOTICE {me} :SILENCE list is full", ct);
                return;
            }

            await session.SendAsync($":{serverName} NOTICE {me} :SILENCE +{mask}", ct);
        }
    }
}
