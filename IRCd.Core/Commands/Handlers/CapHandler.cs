namespace IRCd.Core.Commands.Handlers
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Core.Abstractions;
    using IRCd.Core.Commands.Contracts;
    using IRCd.Core.Protocol;
    using IRCd.Core.State;
    using IRCd.Shared.Options;

    using Microsoft.Extensions.Options;

    public sealed class CapHandler : IIrcCommandHandler
    {
        public string Command => "CAP";

        private static readonly string[] SupportedCaps = ["server-time", "message-tags"]; 

        private readonly IOptions<IrcOptions> _options;

        public CapHandler(IOptions<IrcOptions> options)
        {
            _options = options;
        }

        public async ValueTask HandleAsync(IClientSession session, IrcMessage msg, ServerState state, CancellationToken ct)
        {
            var sub = msg.Params.Count > 0 ? msg.Params[0] : null;
            if (string.IsNullOrWhiteSpace(sub))
                return;

            var serverName = _options.Value.ServerInfo?.Name ?? "server";
            var nick = session.Nick ?? "*";

            if (sub.Equals("LS", StringComparison.OrdinalIgnoreCase))
            {
                await session.SendAsync($":{serverName} CAP {nick} LS :{string.Join(' ', SupportedCaps)}", ct);
                return;
            }

            if (sub.Equals("END", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (sub.Equals("LIST", StringComparison.OrdinalIgnoreCase))
            {
                var enabled = session.EnabledCapabilities.Count == 0
                    ? string.Empty
                    : string.Join(' ', session.EnabledCapabilities);

                await session.SendAsync($":{serverName} CAP {nick} LIST :{enabled}", ct);
                return;
            }

            if (sub.Equals("REQ", StringComparison.OrdinalIgnoreCase))
            {
                if (msg.Params.Count >= 2)
                {
                    var req = msg.Params[1] ?? string.Empty;
                    var requested = req.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

                    var ack = new List<string>();
                    var nak = new List<string>();

                    foreach (var cap in requested)
                    {
                        var normalized = cap.StartsWith('-') ? cap[1..] : cap;

                        if (!SupportedCaps.Contains(normalized, StringComparer.OrdinalIgnoreCase))
                        {
                            nak.Add(cap);
                            continue;
                        }

                        if (cap.StartsWith('-'))
                        {
                            session.EnabledCapabilities.Remove(normalized);
                            ack.Add(cap);
                        }
                        else
                        {
                            session.EnabledCapabilities.Add(normalized);
                            ack.Add(cap);
                        }
                    }

                    if (nak.Count > 0)
                    {
                        await session.SendAsync($":{serverName} CAP {nick} NAK :{string.Join(' ', nak)}", ct);
                        return;
                    }

                    await session.SendAsync($":{serverName} CAP {nick} ACK :{string.Join(' ', ack)}", ct);
                }
                return;
            }
        }
    }
}
