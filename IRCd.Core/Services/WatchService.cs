namespace IRCd.Core.Services
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Core.State;
    using IRCd.Shared.Options;

    using Microsoft.Extensions.Options;

    public sealed class WatchService
    {
        private readonly IOptions<IrcOptions> _options;
        private readonly RoutingService _routing;

        private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, string>> _watchedByConn = new(StringComparer.Ordinal);

        private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _watchersByNick =
            new(StringComparer.OrdinalIgnoreCase);

        public WatchService(IOptions<IrcOptions> options, RoutingService routing)
        {
            _options = options;
            _routing = routing;
        }

        public IReadOnlyCollection<string> GetList(string connectionId)
        {
            if (string.IsNullOrWhiteSpace(connectionId))
            {
                return Array.Empty<string>();
            }

            if (_watchedByConn.TryGetValue(connectionId, out var set))
            {
                return set.Values.OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToArray();
            }

            return Array.Empty<string>();
        }

        public bool TryAdd(string connectionId, string nick, int maxEntries)
        {
            if (string.IsNullOrWhiteSpace(connectionId) || string.IsNullOrWhiteSpace(nick))
            {
                return false;
            }

            nick = nick.Trim();
            if (nick.Length == 0)
            {
                 return false;
            }

            var set = _watchedByConn.GetOrAdd(connectionId, _ => new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase));

            if (set.Count >= maxEntries && !set.ContainsKey(nick))
            {
                return false;
            }

            set[nick] = nick;

            var watchers = _watchersByNick.GetOrAdd(nick, _ => new ConcurrentDictionary<string, byte>(StringComparer.Ordinal));
            watchers[connectionId] = 0;

            return true;
        }

        public bool Remove(string connectionId, string nick)
        {
            if (string.IsNullOrWhiteSpace(connectionId) || string.IsNullOrWhiteSpace(nick))
            {
                return false;
            }

            if (_watchedByConn.TryGetValue(connectionId, out var set))
            {
                if (!set.TryRemove(nick, out _))
                {
                    return false;
                }

                if (_watchersByNick.TryGetValue(nick, out var watchers))
                {
                    watchers.TryRemove(connectionId, out _);
                    if (watchers.IsEmpty)
                    {
                        _watchersByNick.TryRemove(nick, out _);
                    }
                }

                if (set.IsEmpty)
                {
                    _watchedByConn.TryRemove(connectionId, out _);
                }

                return true;
            }

            return false;
        }

        public void Clear(string connectionId)
        {
            if (string.IsNullOrWhiteSpace(connectionId))
            {
                return;
            }

            if (_watchedByConn.TryRemove(connectionId, out var set))
            {
                foreach (var nick in set.Keys)
                {
                    if (_watchersByNick.TryGetValue(nick, out var watchers))
                    {
                        watchers.TryRemove(connectionId, out _);
                        if (watchers.IsEmpty)
                        {
                            _watchersByNick.TryRemove(nick, out _);
                        }
                    }
                }
            }
        }

        public void RemoveAll(string connectionId) => Clear(connectionId);

        public async ValueTask NotifyLogonAsync(ServerState state, User user, CancellationToken ct)
        {
            if (user is null || string.IsNullOrWhiteSpace(user.Nick))
            {
                return;
            }

            var server = _options.Value.ServerInfo?.Name ?? "server";
            var nick = user.Nick!;

            if (!_watchersByNick.TryGetValue(nick, out var watchers) || watchers.IsEmpty)
            {
                return;
            }

            var when = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var u = user.UserName ?? "u";
            var host = user.Host ?? "localhost";

            foreach (var watcherConnId in watchers.Keys)
            {
                if (!state.TryGetUser(watcherConnId, out var watcher) || watcher is null || !watcher.IsRegistered || string.IsNullOrWhiteSpace(watcher.Nick))
                {
                    continue;
                }

                var me = watcher.Nick!;
                await _routing.SendToUserAsync(watcherConnId, $":{server} 600 {me} {nick} {u} {host} {when} :logged on", ct);
            }
        }

        public async ValueTask NotifyLogoffAsync(ServerState state, string nick, string? userName, string? host, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(nick))
            {
                return;
            }

            var server = _options.Value.ServerInfo?.Name ?? "server";

            if (!_watchersByNick.TryGetValue(nick, out var watchers) || watchers.IsEmpty)
            {
                return;
            }

            var when = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var u = string.IsNullOrWhiteSpace(userName) ? "*" : userName;
            var h = string.IsNullOrWhiteSpace(host) ? "localhost" : host;

            foreach (var watcherConnId in watchers.Keys)
            {
                if (!state.TryGetUser(watcherConnId, out var watcher) || watcher is null || !watcher.IsRegistered || string.IsNullOrWhiteSpace(watcher.Nick))
                {
                    continue;
                }

                var me = watcher.Nick!;
                await _routing.SendToUserAsync(watcherConnId, $":{server} 601 {me} {nick} {u} {h} {when} :logged off", ct);
            }
        }

        public async ValueTask NotifyNickChangeAsync(ServerState state, User user, string oldNick, CancellationToken ct)
        {
            if (user is null)
            {
                return;
            }

            var newNick = user.Nick ?? string.Empty;

            if (!string.IsNullOrWhiteSpace(oldNick))
            {
                await NotifyLogoffAsync(state, oldNick, user.UserName, user.Host, ct);
            }

            if (!string.IsNullOrWhiteSpace(newNick))
            {
                await NotifyLogonAsync(state, user, ct);
            }
        }

        public async ValueTask SendImmediateStatusAsync(ServerState state, string watcherConnId, string watchedNick, CancellationToken ct)
        {
            var server = _options.Value.ServerInfo?.Name ?? "server";

            if (!state.TryGetUser(watcherConnId, out var watcher) || watcher is null || !watcher.IsRegistered || string.IsNullOrWhiteSpace(watcher.Nick))
            {
                return;
            }

            var me = watcher.Nick!;

            if (state.TryGetConnectionIdByNick(watchedNick, out var targetConnId) && targetConnId is not null && state.TryGetUser(targetConnId, out var target) && target is not null && target.IsRegistered)
            {
                await _routing.SendToUserAsync(watcherConnId,
                    $":{server} 604 {me} {target.Nick} {(target.UserName ?? "u")} {(target.Host ?? "localhost")} :is online",
                    ct);
            }
            else
            {
                await _routing.SendToUserAsync(watcherConnId, $":{server} 605 {me} {watchedNick} * * :is offline", ct);
            }
        }

        public async ValueTask SendListAsync(ServerState state, string watcherConnId, CancellationToken ct)
        {
            var server = _options.Value.ServerInfo?.Name ?? "server";

            if (!state.TryGetUser(watcherConnId, out var watcher) || watcher is null || !watcher.IsRegistered || string.IsNullOrWhiteSpace(watcher.Nick))
            {
                return;
            }

            var me = watcher.Nick!;

            foreach (var nick in GetList(watcherConnId))
            {
                if (state.TryGetConnectionIdByNick(nick, out var targetConnId) && targetConnId is not null && state.TryGetUser(targetConnId, out var target) && target is not null && target.IsRegistered)
                {
                    await _routing.SendToUserAsync(watcherConnId, $":{server} 603 {me} {target.Nick} {(target.UserName ?? "u")} {(target.Host ?? "localhost")} :is online", ct);
                }
                else
                {
                    await _routing.SendToUserAsync(watcherConnId, $":{server} 605 {me} {nick} * * :is offline", ct);
                }
            }

            await _routing.SendToUserAsync(watcherConnId, $":{server} 606 {me} :End of WATCH list", ct);
        }
    }
}
