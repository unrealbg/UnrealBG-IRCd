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

    using IRCd.Shared.Options;

    using Microsoft.Extensions.Options;

    public sealed class ListHandler : IIrcCommandHandler
    {
        public string Command => "LIST";

        private readonly IOptions<IrcOptions> _options;

        public ListHandler(IOptions<IrcOptions> options)
        {
            _options = options;
        }

        public async ValueTask HandleAsync(IClientSession session, IrcMessage msg, ServerState state, CancellationToken ct)
        {
            if (!session.IsRegistered)
            {
                await session.SendAsync($":server 451 {(session.Nick ?? "*")} :You have not registered", ct);
                return;
            }

            var me = session.Nick!;

            await session.SendAsync($":server 321 {me} Channel :Users  Name", ct);

            string[]? requested = null;
            if (msg.Params.Count > 0 && !string.IsNullOrWhiteSpace(msg.Params[0]))
            {
                var max = _options.Value.Limits?.MaxListTargets > 0 ? _options.Value.Limits.MaxListTargets : 20;
                requested = msg.Params[0]
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(n => IrcValidation.IsValidChannel(n, out _))
                    .Take(max)
                    .ToArray();
            }

            foreach (var ch in state.GetAllChannels())
            {
                if (requested is not null &&
                    !requested.Contains(ch.Name, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                var isMember = ch.Contains(session.ConnectionId);
                if (ch.Modes.HasFlag(ChannelModes.Secret) && !isMember)
                {
                    continue;
                }

                var users = ch.Members.Count;
                var topic = ch.Topic ?? string.Empty;

                await session.SendAsync($":server 322 {me} {ch.Name} {users} :{topic}", ct);
            }

            await session.SendAsync($":server 323 {me} :End of /LIST", ct);
        }
    }
}
