namespace IRCd.Services.RootServ
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Core.Abstractions;
    using IRCd.Core.State;
    using IRCd.Shared.Options;

    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;

    public sealed class ChannelSnoopService
    {
        private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _channelToWatchers =
            new(StringComparer.OrdinalIgnoreCase);

        private readonly ISessionRegistry _sessions;
        private readonly IOptions<IrcOptions> _options;
        private readonly ILogger<ChannelSnoopService>? _logger;

        public ChannelSnoopService(ISessionRegistry sessions, IOptions<IrcOptions> options, ILogger<ChannelSnoopService>? logger = null)
        {
            _sessions = sessions;
            _options = options;
            _logger = logger;
        }

        public bool Enable(string channelName, string watcherConnectionId)
        {
            if (string.IsNullOrWhiteSpace(channelName) || string.IsNullOrWhiteSpace(watcherConnectionId))
            {
                return false;
            }

            var set = _channelToWatchers.GetOrAdd(channelName.Trim(), _ => new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase));
            set[watcherConnectionId] = 0;
            return true;
        }

        public bool Disable(string channelName, string watcherConnectionId)
        {
            if (string.IsNullOrWhiteSpace(channelName) || string.IsNullOrWhiteSpace(watcherConnectionId))
            {
                return false;
            }

            if (!_channelToWatchers.TryGetValue(channelName.Trim(), out var set))
            {
                return false;
            }

            var ok = set.TryRemove(watcherConnectionId, out _);
            if (set.IsEmpty)
            {
                _channelToWatchers.TryRemove(channelName.Trim(), out _);
            }

            return ok;
        }

        public string[] ListForWatcher(string watcherConnectionId)
        {
            if (string.IsNullOrWhiteSpace(watcherConnectionId))
            {
                return Array.Empty<string>();
            }

            return _channelToWatchers
                .Where(kvp => kvp.Value.ContainsKey(watcherConnectionId))
                .Select(kvp => kvp.Key)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        public async ValueTask OnChannelMessageAsync(IClientSession fromSession, Channel channel, string text, ServerState state, CancellationToken ct)
        {
            _ = state;
            if (channel is null || string.IsNullOrWhiteSpace(channel.Name) || string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            if (!_channelToWatchers.TryGetValue(channel.Name, out var watchers) || watchers.IsEmpty)
            {
                return;
            }

            var fromNick = fromSession.Nick ?? "*";
            var server = _options.Value.ServerInfo?.Name ?? "server";

            _logger?.LogInformation("CHANSNOOP {Channel} <{Nick}> {Text}", channel.Name, fromNick, text);

            foreach (var watcherId in watchers.Keys.ToArray())
            {
                if (!_sessions.TryGet(watcherId, out var watcher) || watcher is null)
                {
                    watchers.TryRemove(watcherId, out _);
                    continue;
                }

                var to = watcher.Nick ?? "*";
                var safeText = text.Replace('\r', ' ').Replace('\n', ' ');
                var line = $":{RootServMessages.ServiceName}!services@{server} NOTICE {to} :[snoop {channel.Name}] <{fromNick}> {safeText}";
                await watcher.SendAsync(line, ct);
            }
        }
    }
}
