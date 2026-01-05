namespace IRCd.Core.Commands.Handlers
{
    using Microsoft.Extensions.Options;

    using IRCd.Core.Abstractions;
    using IRCd.Core.Commands.Contracts;
    using IRCd.Core.Protocol;
    using IRCd.Core.State;

    using IRCd.Shared.Options;

    public sealed class NamesHandler : IIrcCommandHandler
    {
        public string Command => "NAMES";

        private readonly IOptions<IrcOptions> _options;

        public NamesHandler(IOptions<IrcOptions> options)
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

            if (msg.Params.Count == 0)
            {
                var myChannels = state.GetUserChannels(session.ConnectionId);
                foreach (var chName in myChannels)
                {
                    await SendNamesForChannel(session, chName, state, ct);
                }

                if (myChannels.Count == 0)
                {
                    await session.SendAsync($":server 366 {session.Nick} * :End of /NAMES list.", ct);
                }

                return;
            }

            var targets = msg.Params[0]
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            var max = _options.Value.Limits?.MaxNamesChannels > 0 ? _options.Value.Limits.MaxNamesChannels : 10;

            foreach (var chName in targets.Take(max))
            {
                await SendNamesForChannel(session, chName, state, ct);
            }
        }

        private static async ValueTask SendNamesForChannel(IClientSession session, string channelName, ServerState state, CancellationToken ct)
        {
            if (!IrcValidation.IsValidChannel(channelName, out _))
            {
                await session.SendAsync($":server 366 {session.Nick} {channelName} :End of /NAMES list.", ct);
                return;
            }

            if (!state.TryGetChannel(channelName, out var channel) || channel is null)
            {
                await session.SendAsync($":server 366 {session.Nick} {channelName} :End of /NAMES list.", ct);
                return;
            }

            var nicks = channel.Members
                               .OrderByDescending(m => m.Privilege)
                               .ThenBy(m => m.Nick, StringComparer.OrdinalIgnoreCase)
                               .Select(m =>
            {
                var p = m.Privilege.ToPrefix();
                return p is null ? m.Nick : $"{p}{m.Nick}";
            });

            var namesLine = string.Join(' ', nicks);

            await session.SendAsync($":server 353 {session.Nick} = {channelName} :{namesLine}", ct);
            await session.SendAsync($":server 366 {session.Nick} {channelName} :End of /NAMES list.", ct);
        }
    }
}
