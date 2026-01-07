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

            if (channel.Modes.HasFlag(ChannelModes.Secret) && !channel.Contains(session.ConnectionId))
            {
                await session.SendAsync($":server 366 {session.Nick} {channelName} :End of /NAMES list.", ct);
                return;
            }

            var names = channel.Members
                .OrderByDescending(m => m.Privilege)
                .ThenBy(m => m.Nick, StringComparer.OrdinalIgnoreCase)
                .Select(m =>
                {
                    var useMultiPrefix = session.EnabledCapabilities.Contains("multi-prefix");
                    var useUserhostInNames = session.EnabledCapabilities.Contains("userhost-in-names");
                    
                    string prefix;
                    if (useMultiPrefix)
                    {
                        prefix = m.Privilege.ToAllPrefixes();
                    }
                    else
                    {
                        var p = m.Privilege.ToPrefix();
                        prefix = p.HasValue ? p.Value.ToString() : string.Empty;
                    }
                    
                    if (useUserhostInNames)
                    {
                        var userName = "user";
                        var host = "host";
                        
                        if (state.TryGetUser(m.ConnectionId, out var memberUser) && memberUser is not null)
                        {
                            userName = memberUser.UserName ?? "user";
                            host = state.GetHostFor(m.ConnectionId);
                        }
                        
                        return $"{prefix}{m.Nick}!{userName}@{host}";
                    }
                    
                    return prefix + m.Nick;
                })
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToList();

            const int maxPayloadChars = 400;

            if (names.Count == 0)
            {
                await session.SendAsync($":server 353 {session.Nick} = {channelName} :", ct);
                await session.SendAsync($":server 366 {session.Nick} {channelName} :End of /NAMES list.", ct);
                return;
            }

            var current = new System.Collections.Generic.List<string>();
            var len = 0;

            foreach (var n in names)
            {
                if (current.Count == 0)
                {
                    current.Add(n);
                    len = n.Length;
                    continue;
                }

                if (len + 1 + n.Length > maxPayloadChars)
                {
                    await session.SendAsync($":server 353 {session.Nick} = {channelName} :{string.Join(' ', current)}", ct);
                    current.Clear();
                    current.Add(n);
                    len = n.Length;
                }
                else
                {
                    current.Add(n);
                    len += 1 + n.Length;
                }
            }

            if (current.Count > 0)
            {
                await session.SendAsync($":server 353 {session.Nick} = {channelName} :{string.Join(' ', current)}", ct);
            }

            await session.SendAsync($":server 366 {session.Nick} {channelName} :End of /NAMES list.", ct);
        }
    }
}
