namespace IRCd.Core.Services
{
    using System;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Core.Abstractions;
    using IRCd.Core.Protocol;
    using IRCd.Core.State;
    using IRCd.Shared.Options;

    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;

    public sealed class ServerLinkService
    {
        private readonly ILogger<ServerLinkService> _logger;
        private readonly IOptionsMonitor<IrcOptions> _options;
        private readonly ServerState _state;
        private readonly RoutingService _routing;
        private readonly ISessionRegistry _sessions;
        private readonly SilenceService _silence;
        private readonly WatchService _watch;

        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, IServerLinkSession> _linksByConn = new(StringComparer.OrdinalIgnoreCase);

        private readonly ExpiringMessageIdCache _seen;

        private readonly LinkFloodGate _floodGate;

        public ServerLinkService(ILogger<ServerLinkService> logger, IOptionsMonitor<IrcOptions> options, ServerState state, RoutingService routing, ISessionRegistry sessions, SilenceService silence, WatchService watch)
        {
            _logger = logger;
            _options = options;
            _state = state;
            _routing = routing;
            _sessions = sessions;
            _silence = silence;
            _watch = watch;

            var flood = options.CurrentValue.Flood?.ServerLink;
            var maxLines = flood?.MaxLines > 0 ? flood.MaxLines : 200;
            var windowSeconds = flood?.WindowSeconds > 0 ? flood.WindowSeconds : 10;
            _floodGate = new LinkFloodGate(maxLines: maxLines, window: TimeSpan.FromSeconds(windowSeconds));

            var s2s = options.CurrentValue.Transport?.S2S;
            var ttlSeconds = s2s?.MsgIdCacheTtlSeconds ?? 120;
            var maxEntries = s2s?.MsgIdCacheMaxEntries ?? 50_000;
            _seen = new ExpiringMessageIdCache(ttl: TimeSpan.FromSeconds(Math.Max(1, ttlSeconds)), maxEntries: Math.Max(1, maxEntries));
        }

        private async Task SendToSidAsync(string sid, string excludeConnId, string line, CancellationToken ct)
        {
            if (_state.TryGetNextHopBySid(sid, out var nextHop) && nextHop is not null)
            {
                if (string.Equals(nextHop, excludeConnId, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                if (_linksByConn.TryGetValue(nextHop, out var sess) && sess.IsAuthenticated && sess.UserSyncEnabled)
                {
                    await sess.SendAsync(line, ct);
                }
            }
        }

        private static string NewMsgId() => Guid.NewGuid().ToString("N");

        private async Task PropagateRawAsync(string? excludeConnId, string line, CancellationToken ct)
        {
            foreach (var kv in _linksByConn)
            {
                if (string.Equals(kv.Key, excludeConnId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var sess = kv.Value;
                if (!sess.IsAuthenticated || !sess.UserSyncEnabled)
                {
                    continue;
                }

                await sess.SendAsync(line, ct);
            }
        }

        private void MarkSeen(string msgId) => _seen.MarkSeen(msgId);

        private static bool IsValidSid(string? sid)
        {
            if (string.IsNullOrWhiteSpace(sid) || sid.Length != 3)
            {
                return false;
            }

            foreach (var c in sid)
            {
                if (!char.IsLetterOrDigit(c))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsValidProto(string? trailing)
        {
            if (string.IsNullOrWhiteSpace(trailing))
            {
                return false;
            }

            var parts = trailing.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 0)
            {
                return false;
            }

            if (!string.Equals(parts[0], "TS", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (parts.Length == 1)
            {
                return true;
            }

            return int.TryParse(parts[1], out var v) && v == 1;
        }

        private static bool ApplyIncomingChannelTs(Channel ch, long incomingTs)
        {
            if (incomingTs <= 0)
            {
                incomingTs = ChannelTimestamps.NowTs();
            }

            if (incomingTs > ch.CreatedTs)
            {
                return false;
            }

            if (incomingTs < ch.CreatedTs)
            {
                ch.ResetForTsCollision(incomingTs);
            }

            return true;
        }

        private string LocalOriginSid => _options.CurrentValue.ServerInfo?.Sid ?? "001";

        private sealed class LinkFloodGate
        {
            private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SlidingWindow> _windows = new(StringComparer.Ordinal);
            private readonly int _maxLines;
            private readonly TimeSpan _window;

            public LinkFloodGate(int maxLines, TimeSpan window)
            {
                _maxLines = maxLines;
                _window = window;
            }

            public bool Allow(string connectionId)
            {
                var w = _windows.GetOrAdd(connectionId, _ => new SlidingWindow(_window));
                return w.Hit(_maxLines);
            }

            public void Remove(string connectionId) => _windows.TryRemove(connectionId, out _);

            private sealed class SlidingWindow
            {
                private readonly TimeSpan _window;
                private readonly System.Collections.Generic.Queue<DateTimeOffset> _hits = new();
                private readonly object _lock = new();

                public SlidingWindow(TimeSpan window) => _window = window;

                public bool Hit(int max)
                {
                    lock (_lock)
                    {
                        var now = DateTimeOffset.UtcNow;
                        while (_hits.Count > 0 && (now - _hits.Peek()) > _window)
                        {
                            _hits.Dequeue();
                        }

                        if (_hits.Count >= max)
                        {
                            return false;
                        }

                        _hits.Enqueue(now);
                        return true;
                    }
                }
            }
        }

        private sealed class ExpiringMessageIdCache
        {
            private readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _expiresAt = new(StringComparer.Ordinal);
            private readonly TimeSpan _ttl;
            private readonly int _maxEntries;
            private int _tick;

            public ExpiringMessageIdCache(TimeSpan ttl, int maxEntries)
            {
                _ttl = ttl;
                _maxEntries = maxEntries;
            }

            public void MarkSeen(string msgId) => _ = TryMarkSeen(msgId);

            public bool TryMarkSeen(string msgId)
            {
                if (string.IsNullOrWhiteSpace(msgId))
                {
                    return true;
                }

                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                if (_expiresAt.TryGetValue(msgId, out var existingExp) && existingExp > now)
                {
                    return false;
                }

                var exp = now + (long)Math.Max(1, _ttl.TotalSeconds);
                _expiresAt[msgId] = exp;

                PruneMaybe(now);
                return true;
            }

            private void PruneMaybe(long now)
            {
                if (System.Threading.Interlocked.Increment(ref _tick) % 1024 != 0 && _expiresAt.Count < _maxEntries)
                {
                    return;
                }

                foreach (var kv in _expiresAt)
                {
                    if (kv.Value <= now)
                    {
                        _expiresAt.TryRemove(kv.Key, out _);
                    }
                }

                if (_expiresAt.Count <= _maxEntries)
                {
                    return;
                }

                var toDrop = _expiresAt.Count - _maxEntries;
                foreach (var k in _expiresAt.Keys.Take(toDrop))
                {
                    _expiresAt.TryRemove(k, out _);
                }
            }
        }

        public async Task PropagateJoinAsync(string uid, string channel, CancellationToken ct)
        {
            var msgId = NewMsgId();
            MarkSeen(msgId);
            var originSid = LocalOriginSid;

            foreach (var s in _linksByConn.Values)
            {
                if (!s.IsAuthenticated || !s.UserSyncEnabled)
                {
                    continue;
                }

                await s.SendAsync($"JOIN {msgId} {originSid} {uid} {channel}", ct);
            }
        }

        public async Task PropagatePartAsync(string uid, string channel, string reason, CancellationToken ct)
        {
            var msgId = NewMsgId();
            MarkSeen(msgId);
            var originSid = LocalOriginSid;

            foreach (var s in _linksByConn.Values)
            {
                if (!s.IsAuthenticated || !s.UserSyncEnabled)
                {
                    continue;
                }

                await s.SendAsync($"PART {msgId} {originSid} {uid} {channel} :{reason}", ct);
            }
        }

        public async Task PropagateSvsPartAsync(string uid, string channel, string reason, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(uid) || string.IsNullOrWhiteSpace(channel))
            {
                return;
            }

            var msgId = NewMsgId();
            MarkSeen(msgId);
            var originSid = LocalOriginSid;

            foreach (var s in _linksByConn.Values)
            {
                if (s.IsAuthenticated && s.UserSyncEnabled)
                {
                    await s.SendAsync($"SVSPART {msgId} {originSid} {uid} {channel} :{reason}", ct);
                }
            }
        }

        public async Task<bool> SendSvsPartAsync(string sid, string uid, string channel, string reason, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(sid) || string.IsNullOrWhiteSpace(uid) || string.IsNullOrWhiteSpace(channel))
            {
                return false;
            }

            if (!_state.TryGetNextHopBySid(sid, out var nextHop) || nextHop is null)
            {
                return false;
            }

            if (!_linksByConn.TryGetValue(nextHop, out var sess) || !sess.IsAuthenticated || !sess.UserSyncEnabled)
            {
                return false;
            }

            var msgId = NewMsgId();
            MarkSeen(msgId);
            var originSid = LocalOriginSid;
            await sess.SendAsync($"SVSPART {msgId} {originSid} {uid} {channel} :{reason}", ct);
            return true;
        }

        public async Task PropagatePrivMsgAsync(string fromUid, string target, string text, CancellationToken ct)
        {
            var msgId = NewMsgId();
            MarkSeen(msgId);
            var originSid = LocalOriginSid;

            foreach (var s in _linksByConn.Values)
            {
                if (!s.IsAuthenticated || !s.UserSyncEnabled)
                {
                    continue;
                }

                await s.SendAsync($"PRIVMSG {msgId} {originSid} {fromUid} {target} :{text}", ct);
            }
        }

        public async Task PropagateNoticeAsync(string fromUid, string target, string text, CancellationToken ct)
        {
            var msgId = NewMsgId();
            MarkSeen(msgId);
            var originSid = LocalOriginSid;

            foreach (var s in _linksByConn.Values)
            {
                if (!s.IsAuthenticated || !s.UserSyncEnabled)
                {
                    continue;
                }

                await s.SendAsync($"NOTICE {msgId} {originSid} {fromUid} {target} :{text}", ct);
            }
        }

        public async Task PropagateUserModeAsync(string uid, string modeToken, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(uid) || string.IsNullOrWhiteSpace(modeToken))
            {
                return;
            }

            var msgId = NewMsgId();
            MarkSeen(msgId);
            var originSid = LocalOriginSid;

            foreach (var s in _linksByConn.Values)
            {
                if (s.IsAuthenticated && s.UserSyncEnabled)
                {
                    await s.SendAsync($"UMODE {msgId} {originSid} {uid} {modeToken}", ct);
                }
            }
        }

        public async Task PropagateSvsJoinAsync(string uid, string channel, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(uid) || string.IsNullOrWhiteSpace(channel))
            {
                return;
            }

            var msgId = NewMsgId();
            MarkSeen(msgId);
            var originSid = LocalOriginSid;

            foreach (var s in _linksByConn.Values)
            {
                if (s.IsAuthenticated && s.UserSyncEnabled)
                {
                    await s.SendAsync($"SVSJOIN {msgId} {originSid} {uid} {channel}", ct);
                }
            }
        }

        public async Task<bool> SendSvsJoinAsync(string sid, string uid, string channel, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(sid) || string.IsNullOrWhiteSpace(uid) || string.IsNullOrWhiteSpace(channel))
            {
                return false;
            }

            if (!_state.TryGetNextHopBySid(sid, out var nextHop) || nextHop is null)
            {
                return false;
            }

            if (!_linksByConn.TryGetValue(nextHop, out var sess) || !sess.IsAuthenticated || !sess.UserSyncEnabled)
            {
                return false;
            }

            var msgId = NewMsgId();
            MarkSeen(msgId);
            var originSid = LocalOriginSid;
            await sess.SendAsync($"SVSJOIN {msgId} {originSid} {uid} {channel}", ct);
            return true;
        }

        public async Task PropagateTopicAsync(string fromUid, string channel, string? topic, CancellationToken ct)
        {
            var msgId = NewMsgId();
            MarkSeen(msgId);
            var originSid = LocalOriginSid;

            long channelTs;
            long topicTs;
            if (_state.TryGetChannel(channel, out var ch) && ch is not null)
            {
                channelTs = ch.CreatedTs;
                topicTs = ch.TopicTs > 0 ? ch.TopicTs : ChannelTimestamps.NowTs();
            }
            else
            {
                channelTs = ChannelTimestamps.NowTs();
                topicTs = channelTs;
            }

            foreach (var s in _linksByConn.Values)
            {
                if (!s.IsAuthenticated || !s.UserSyncEnabled)
                {
                    continue;
                }

                await s.SendAsync($"TOPIC {msgId} {originSid} {fromUid} {channel} {channelTs} {topicTs} :{topic ?? string.Empty}", ct);
            }
        }

        public async Task PropagateChannelModesAsync(string channel, long ts, string modes, CancellationToken ct)
        {
            var msgId = NewMsgId();
            MarkSeen(msgId);
            var originSid = LocalOriginSid;

            foreach (var s in _linksByConn.Values)
            {
                if (!s.IsAuthenticated || !s.UserSyncEnabled)
                {
                    continue;
                }

                await s.SendAsync($"MODECH {msgId} {originSid} {channel} {ts} {modes}", ct);
            }
        }

        public async Task PropagateMemberPrivilegeAsync(string channel, string uid, ChannelPrivilege privilege, CancellationToken ct)
        {
            var msgId = NewMsgId();
            MarkSeen(msgId);
            var originSid = LocalOriginSid;

            foreach (var s in _linksByConn.Values)
            {
                if (!s.IsAuthenticated || !s.UserSyncEnabled)
                {
                    continue;
                }

                await s.SendAsync($"MEMBER {msgId} {originSid} {channel} {uid} {(int)privilege}", ct);
            }
        }

        public async Task PropagateChannelMetaAsync(string channel, long ts, string key, string limit, CancellationToken ct)
        {
            var msgId = NewMsgId();
            MarkSeen(msgId);
            var originSid = LocalOriginSid;

            foreach (var s in _linksByConn.Values)
            {
                if (!s.IsAuthenticated || !s.UserSyncEnabled)
                {
                    continue;
                }

                await s.SendAsync($"CHANMETA {msgId} {originSid} {channel} {ts} {key} {limit}", ct);
            }
        }

        public async Task PropagateBanAsync(string channel, long ts, string mask, string setBy, long setAt, CancellationToken ct)
        {
            var msgId = NewMsgId();
            MarkSeen(msgId);
            var originSid = LocalOriginSid;

            foreach (var s in _linksByConn.Values)
            {
                if (!s.IsAuthenticated || !s.UserSyncEnabled)
                {
                    continue;
                }

                await s.SendAsync($"BAN {msgId} {originSid} {channel} {ts} {mask} {setBy} {setAt}", ct);
            }
        }

        public async Task PropagateBanDelAsync(string channel, long ts, string mask, CancellationToken ct)
        {
            var msgId = NewMsgId();
            MarkSeen(msgId);
            var originSid = LocalOriginSid;

            foreach (var s in _linksByConn.Values)
            {
                if (!s.IsAuthenticated || !s.UserSyncEnabled)
                {
                    continue;
                }

                await s.SendAsync($"BANDEL {msgId} {originSid} {channel} {ts} {mask}", ct);
            }
        }

        private static void ApplyChannelModeString(Channel ch, string modeString)
        {
            if (string.IsNullOrWhiteSpace(modeString))
            {
                return;
            }

            var adding = true;

            foreach (var c in modeString)
            {
                if (c == '+')
                {
                    adding = true;
                    continue;
                }
                if (c == '-')
                {
                    adding = false;
                    continue;
                }

                var mode = c switch
                {
                    'n' => ChannelModes.NoExternalMessages,
                    't' => ChannelModes.TopicOpsOnly,
                    'm' => ChannelModes.Moderated,
                    'i' => ChannelModes.InviteOnly,
                    's' => ChannelModes.Secret,
                    _ => (ChannelModes?)null
                };

                if (mode is null)
                {
                    continue;
                }

                ch.ApplyModeChange(mode.Value, adding);
            }
        }

        private static string MakeCollisionNick(string uid)
        {
            var suffix = uid.Length <= 6 ? uid : uid[^6..];
            return $"uid{suffix}";
        }

        private static bool IsSidBetter(string? sidA, string? sidB)
        {
            if (string.IsNullOrWhiteSpace(sidA))
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(sidB))
            {
                return true;
            }

            return string.Compare(sidA, sidB, StringComparison.OrdinalIgnoreCase) < 0;
        }

        private async Task ApplyNickWithCollisionHandlingAsync(User user, long ts, string newNick, CancellationToken ct)
        {
            var oldNick = user.Nick ?? string.Empty;

            if (_state.TrySetNick(user.ConnectionId, newNick))
            {
                user.NickTs = ts;
                user.Nick = newNick;

                if (!string.Equals(oldNick, newNick, StringComparison.OrdinalIgnoreCase))
                {
                    await _watch.NotifyNickChangeAsync(_state, user, oldNick, ct);
                }

                await BroadcastNickChangeAsync(user.ConnectionId, oldNick, newNick, ct);

                return;
            }

            if (!_state.TryGetConnectionIdByNick(newNick, out var otherConn) || otherConn is null)
            {
                user.NickTs = ts;
                user.Nick = newNick;
                return;
            }

            if (!_state.TryGetUser(otherConn, out var otherUser) || otherUser is null)
            {
                user.NickTs = ts;
                user.Nick = newNick;
                return;
            }

            var iWin = ts > otherUser.NickTs || (ts == otherUser.NickTs && IsSidBetter(user.RemoteSid, otherUser.RemoteSid));

            if (iWin)
            {
                var forcedNick = MakeCollisionNick(otherUser.Uid ?? otherUser.ConnectionId);
                var otherOldNick = otherUser.Nick ?? string.Empty;

                otherUser.NickTs = Math.Max(otherUser.NickTs, ChannelTimestamps.NowTs());
                _state.TrySetNick(otherUser.ConnectionId, forcedNick);
                otherUser.Nick = forcedNick;

                if (!string.Equals(otherOldNick, forcedNick, StringComparison.OrdinalIgnoreCase))
                {
                    await _watch.NotifyNickChangeAsync(_state, otherUser, otherOldNick, ct);
                }

                await BroadcastNickChangeAsync(otherUser.ConnectionId, otherOldNick, forcedNick, ct);

                if (!string.IsNullOrWhiteSpace(otherUser.Uid))
                {
                    await PropagateNickAsync(otherUser.Uid!, forcedNick, ct);
                }

                user.NickTs = ts;
                _state.TrySetNick(user.ConnectionId, newNick);
                user.Nick = newNick;

                if (!string.Equals(oldNick, newNick, StringComparison.OrdinalIgnoreCase))
                {
                    await _watch.NotifyNickChangeAsync(_state, user, oldNick, ct);
                }

                await BroadcastNickChangeAsync(user.ConnectionId, oldNick, newNick, ct);

                return;
            }

            var myForced = MakeCollisionNick(user.Uid ?? user.ConnectionId);
            user.NickTs = ts;
            _state.TrySetNick(user.ConnectionId, myForced);
            user.Nick = myForced;

            if (!string.Equals(oldNick, myForced, StringComparison.OrdinalIgnoreCase))
            {
                await _watch.NotifyNickChangeAsync(_state, user, oldNick, ct);
            }

            await BroadcastNickChangeAsync(user.ConnectionId, oldNick, myForced, ct);

            if (!string.IsNullOrWhiteSpace(user.Uid))
            {
                await PropagateNickAsync(user.Uid!, myForced, ct);
            }
        }

        private async Task BroadcastNickChangeAsync(string connectionId, string oldNick, string newNick, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(oldNick) || string.IsNullOrWhiteSpace(newNick) || string.Equals(oldNick, newNick, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var channels = _state.UpdateNickInUserChannels(connectionId, newNick);

            var userName = "u";
            var host = _state.GetHostFor(connectionId);

            if (_state.TryGetUser(connectionId, out var u) && u is not null)
            {
                if (!string.IsNullOrWhiteSpace(u.UserName))
                {
                    userName = u.UserName!;
                }

                if (!string.IsNullOrWhiteSpace(u.Host))
                {
                    host = u.Host!;
                }
            }

            var nickLine = $":{oldNick}!{userName}@{host} NICK :{newNick}";

            foreach (var ch in channels)
            {
                await _routing.BroadcastToChannelAsync(ch, nickLine, excludeConnectionId: connectionId, ct);
            }

            if (_sessions.TryGet(connectionId, out var sess) && sess is not null)
            {
                sess.Nick = newNick;
                await sess.SendAsync(nickLine, ct);
            }
        }

        private async Task ApplyPartByUidAsync(string uid, string channelName, string reason, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(uid) || string.IsNullOrWhiteSpace(channelName))
            {
                return;
            }

            if (!_state.TryGetUserByUid(uid, out var u) || u is null)
            {
                return;
            }

            if (!_state.TryPartChannel(u.ConnectionId, channelName, out var ch) || ch is null)
            {
                return;
            }

            var nick = u.Nick ?? "*";
            var userName = u.UserName ?? "u";
            var host = u.Host ?? _state.GetHostFor(u.ConnectionId);

            var partLine = $":{nick}!{userName}@{host} PART {channelName}";
            if (!string.IsNullOrWhiteSpace(reason))
            {
                partLine += $" :{reason}";
            }

            await _routing.BroadcastToChannelAsync(ch, partLine, excludeConnectionId: null, ct);

            if (!u.IsRemote && _sessions.TryGet(u.ConnectionId, out var sess) && sess is not null)
            {
                await sess.SendAsync(partLine, ct);
            }
        }

        private async Task ResolveNickCollisionOnAddAsync(IServerLinkSession session, User incoming, CancellationToken ct)
        {
            incoming.NickTs = ChannelTimestamps.NowTs();
            incoming.Nick = MakeCollisionNick(incoming.Uid ?? incoming.ConnectionId);

            if (!_state.TryAddRemoteUser(incoming))
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(incoming.Uid))
            {
                await PropagateNickAsync(incoming.Uid!, incoming.Nick!, ct);
            }
        }

        public async Task HandleIncomingLinkAsync(IServerLinkSession session, CancellationToken ct)
        {
            var writerTask = Task.Run(() => session.RunWriterLoopAsync(ct), ct);

            try
            {
                var passHasValidProto = false;

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var handshakeSeconds = _options.CurrentValue.Transport?.S2S?.InboundHandshakeTimeoutSeconds ?? 15;
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, handshakeSeconds)));

                while (!timeoutCts.IsCancellationRequested)
                {
                    var line = await session.ReadLineAsync(timeoutCts.Token);
                    if (line is null)
                    {
                        break;
                    }

                    if (!_floodGate.Allow(session.ConnectionId))
                    {
                        try { await session.SendAsync("ERROR :Excess Flood", ct); } catch { }
                        break;
                    }

                    if (!_floodGate.Allow(session.ConnectionId))
                    {
                        try { await session.SendAsync("ERROR :Excess Flood", ct); } catch { }
                        break;
                    }

                    if (!ServerLinkParser.TryParse(line, out var command, out var args, out var trailing))
                    {
                        continue;
                    }

                    if (string.Equals(command, "CAPAB", StringComparison.OrdinalIgnoreCase))
                    {
                        session.CapabReceived = true;
                        continue;
                    }

                    if (string.Equals(command, "PASS", StringComparison.OrdinalIgnoreCase))
                    {
                        if (args.Length >= 1)
                        {
                            session.Pass = args[0];
                        }

                        passHasValidProto = IsValidProto(trailing);
                        if (!passHasValidProto)
                        {
                            _logger.LogWarning("S2S link rejected from {Remote}: missing/invalid proto", session.RemoteEndPoint);
                            await session.SendAsync("ERROR :Bad protocol", ct);
                            break;
                        }

                        continue;
                    }

                    if (string.Equals(command, "SERVER", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!passHasValidProto || string.IsNullOrWhiteSpace(session.Pass))
                        {
                            await session.SendAsync("ERROR :Missing PASS", ct);
                            break;
                        }

                        if (args.Length >= 1)
                        {
                            session.RemoteServerName = args[0];
                        }

                        if (args.Length >= 2)
                        {
                            session.RemoteSid = args[1];
                        }

                        if (!IsValidSid(session.RemoteSid))
                        {
                            await session.SendAsync("ERROR :Bad SID", ct);
                            break;
                        }

                        var localSid = _options.CurrentValue.ServerInfo?.Sid ?? "001";
                        if (string.Equals(session.RemoteSid, localSid, StringComparison.OrdinalIgnoreCase))
                        {
                            await session.SendAsync("ERROR :SID collision", ct);
                            break;
                        }

                        var links = _options.CurrentValue.Links;
                        var match = links.FirstOrDefault(l => !l.Outbound && string.Equals(l.Name, session.RemoteServerName, StringComparison.OrdinalIgnoreCase));

                        if (match is null)
                        {
                            _logger.LogWarning("S2S link rejected from {Remote}: server '{ServerName}' not approved for inbound link", session.RemoteEndPoint, session.RemoteServerName);
                            await session.SendAsync($"ERROR :Unknown server", ct);
                            break;
                        }

                        if (!string.IsNullOrWhiteSpace(match.Sid) && !string.Equals(match.Sid, session.RemoteSid, StringComparison.OrdinalIgnoreCase))
                        {
                            _logger.LogWarning("S2S link rejected from {Remote}: unexpected SID '{Sid}' for '{ServerName}'", session.RemoteEndPoint, session.RemoteSid, session.RemoteServerName);
                            await session.SendAsync("ERROR :Unexpected SID", ct);
                            break;
                        }

                        if (!string.IsNullOrWhiteSpace(match.Host) && System.Net.IPAddress.TryParse(match.Host, out var allowedIp))
                        {
                            var remoteIp = (session.RemoteEndPoint as System.Net.IPEndPoint)?.Address;
                            if (remoteIp is null || !remoteIp.Equals(allowedIp))
                            {
                                _logger.LogWarning("S2S link rejected from {Remote}: peer IP not allowed for '{ServerName}'", session.RemoteEndPoint, session.RemoteServerName);
                                await session.SendAsync($"ERROR :Not authorized", ct);
                                break;
                            }
                        }

                        if (string.IsNullOrWhiteSpace(match.Password) || !string.Equals(match.Password, session.Pass, StringComparison.Ordinal))
                        {
                            _logger.LogWarning("S2S link rejected from {Remote}: bad password for '{ServerName}'", session.RemoteEndPoint, session.RemoteServerName);
                            await session.SendAsync($"ERROR :Bad password", ct);
                            break;
                        }

                        session.IsAuthenticated = true;
                        session.UserSyncEnabled = match.UserSync;

                        _linksByConn[session.ConnectionId] = session;

                        var local = _options.CurrentValue.ServerInfo;
                        var localName = local?.Name ?? "server";
                        var localSid2 = local?.Sid ?? "001";
                        var localDesc = local?.Description ?? "IRCd";

                        await session.SendAsync($"PASS {match.Password} :TS 1", ct);
                        await session.SendAsync($"SERVER {localName} {localSid2} :{localDesc}", ct);

                        var ok = _state.TryRegisterRemoteServer(new RemoteServer
                        {
                            ConnectionId = session.ConnectionId,
                            Name = session.RemoteServerName ?? match.Name,
                            Sid = session.RemoteSid ?? string.Empty,
                            Description = trailing ?? string.Empty,
                            ParentSid = localSid2
                        });

                        if (!ok)
                        {
                            _logger.LogWarning("S2S link rejected from {Remote}: failed to register remote server {Name}/{Sid}", session.RemoteEndPoint, session.RemoteServerName, session.RemoteSid);
                            await session.SendAsync($"ERROR :Registration failed", ct);
                            break;
                        }

                        _logger.LogInformation("S2S link established: {RemoteServer} ({Sid}) from {Remote}", session.RemoteServerName, session.RemoteSid, session.RemoteEndPoint);

                        await SendBurstAsync(session, ct);

                        await ReceiveLoopAsync(session, timeoutCts.Token);
                        return;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // shutdown/timeout
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "S2S link loop error");
            }
            finally
            {
                try { _floodGate.Remove(session.ConnectionId); } catch { }
                _linksByConn.TryRemove(session.ConnectionId, out _);

                var (removedServers, removedUserUids) = _state.RemoveRemoteServerTreeByConnectionDetailed(session.ConnectionId);

                foreach (var uid in removedUserUids)
                {
                    var msgId = NewMsgId();
                    MarkSeen(msgId);
                    var originSid = LocalOriginSid;
                    await PropagateRawAsync(session.ConnectionId, $"QUIT {msgId} {originSid} {uid} :*.net *.split", CancellationToken.None);
                }

                foreach (var srv in removedServers)
                {
                    var msgId = NewMsgId();
                    MarkSeen(msgId);
                    var originSid = LocalOriginSid;
                    if (!string.IsNullOrWhiteSpace(srv.Sid))
                    {
                        await PropagateRawAsync(session.ConnectionId, $"SQUIT {msgId} {originSid} {srv.Sid} :Connection lost", CancellationToken.None);
                    }
                }

                try { await session.CloseAsync("S2S closed", CancellationToken.None); } catch { }
                try { await writerTask; } catch { }
            }
        }

        public async Task HandleOutboundLinkAsync(IServerLinkSession session, LinkOptions link, CancellationToken ct)
        {
            var writerTask = Task.Run(() => session.RunWriterLoopAsync(ct), ct);

            try
            {
                var local = _options.CurrentValue.ServerInfo;
                var localName = local?.Name ?? "server";
                var localSid2 = local?.Sid ?? "001";
                var localDesc = local?.Description ?? "IRCd";

                await session.SendAsync($"PASS {link.Password} :TS 1", ct);
                await session.SendAsync($"SERVER {localName} {localSid2} :{localDesc}", ct);

                var passHasValidProto = false;

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(15));

                while (!timeoutCts.IsCancellationRequested)
                {
                    var line = await session.ReadLineAsync(timeoutCts.Token);
                    if (line is null)
                    {
                        break;
                    }

                    if (!ServerLinkParser.TryParse(line, out var command, out var args, out var trailing))
                    {
                        continue;
                    }

                    if (string.Equals(command, "CAPAB", StringComparison.OrdinalIgnoreCase))
                    {
                        session.CapabReceived = true;
                        continue;
                    }

                    if (string.Equals(command, "PASS", StringComparison.OrdinalIgnoreCase))
                    {
                        if (args.Length >= 1)
                        {
                            session.Pass = args[0];
                        }

                        passHasValidProto = IsValidProto(trailing);
                        if (!passHasValidProto)
                        {
                            await session.SendAsync("ERROR :Bad protocol", ct);
                            break;
                        }

                        continue;
                    }

                    if (string.Equals(command, "SERVER", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!passHasValidProto || string.IsNullOrWhiteSpace(session.Pass))
                        {
                            await session.SendAsync("ERROR :Missing PASS", ct);
                            break;
                        }

                        if (args.Length >= 1)
                        {
                            session.RemoteServerName = args[0];
                        }

                        if (args.Length >= 2)
                        {
                            session.RemoteSid = args[1];
                        }

                        if (!IsValidSid(session.RemoteSid))
                        {
                            await session.SendAsync("ERROR :Bad SID", ct);
                            break;
                        }

                        if (string.Equals(session.RemoteSid, localSid2, StringComparison.OrdinalIgnoreCase))
                        {
                            await session.SendAsync("ERROR :SID collision", ct);
                            break;
                        }

                        if (!string.Equals(session.RemoteServerName, link.Name, StringComparison.OrdinalIgnoreCase))
                        {
                            _logger.LogWarning("S2S outbound got unexpected SERVER name '{RemoteName}' expected '{Expected}'", session.RemoteServerName, link.Name);
                            await session.SendAsync("ERROR :Unexpected server name", ct);
                            break;
                        }

                        if (!string.IsNullOrWhiteSpace(link.Sid) && !string.Equals(session.RemoteSid, link.Sid, StringComparison.OrdinalIgnoreCase))
                        {
                            _logger.LogWarning("S2S outbound got unexpected SERVER SID '{RemoteSid}' expected '{ExpectedSid}'", session.RemoteSid, link.Sid);
                            await session.SendAsync("ERROR :Unexpected SID", ct);
                            break;
                        }

                        session.IsAuthenticated = true;
                        session.UserSyncEnabled = link.UserSync;

                        _linksByConn[session.ConnectionId] = session;

                        var ok = _state.TryRegisterRemoteServer(new RemoteServer
                        {
                            ConnectionId = session.ConnectionId,
                            Name = session.RemoteServerName ?? link.Name,
                            Sid = session.RemoteSid ?? string.Empty,
                            Description = trailing ?? string.Empty,
                            ParentSid = localSid2
                        });

                        if (!ok)
                        {
                            _logger.LogWarning("S2S outbound failed to register remote server {Name}/{Sid}", session.RemoteServerName, session.RemoteSid);
                            await session.SendAsync("ERROR :Registration failed", ct);
                            break;
                        }

                        _logger.LogInformation("S2S outbound link established: {RemoteServer} ({Sid})", session.RemoteServerName, session.RemoteSid);

                        await SendBurstAsync(session, ct);

                        await ReceiveLoopAsync(session, timeoutCts.Token);
                        return;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // shutdown/timeout
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "S2S outbound link loop error");
            }
            finally
            {
                try { _floodGate.Remove(session.ConnectionId); } catch { }
                _linksByConn.TryRemove(session.ConnectionId, out _);

                var (removedServers, removedUserUids) = _state.RemoveRemoteServerTreeByConnectionDetailed(session.ConnectionId);

                foreach (var uid in removedUserUids)
                {
                    var msgId = NewMsgId();
                    MarkSeen(msgId);
                    var originSid = LocalOriginSid;
                    await PropagateRawAsync(session.ConnectionId, $"QUIT {msgId} {originSid} {uid} :*.net *.split", CancellationToken.None);
                }

                foreach (var srv in removedServers)
                {
                    var msgId = NewMsgId();
                    MarkSeen(msgId);
                    var originSid = LocalOriginSid;
                    if (!string.IsNullOrWhiteSpace(srv.Sid))
                    {
                        await PropagateRawAsync(session.ConnectionId, $"SQUIT {msgId} {originSid} {srv.Sid} :Connection lost", CancellationToken.None);
                    }
                }

                try { await session.CloseAsync("S2S outbound closed", CancellationToken.None); } catch { }
                try { await writerTask; } catch { }
            }
        }

        private async Task SendBurstAsync(IServerLinkSession session, CancellationToken ct)
        {
            var local = _options.CurrentValue.ServerInfo;
            var localName = local?.Name ?? "server";
            var localSid2 = local?.Sid ?? "001";
            var localDesc = local?.Description ?? "IRCd";

            await session.SendAsync($"SERVERLIST {localName} {localSid2} {localSid2} :{localDesc}", ct);

            foreach (var s in _state.GetRemoteServers())
            {
                if (!string.IsNullOrWhiteSpace(s.Sid) && !string.Equals(s.Sid, session.RemoteSid, StringComparison.OrdinalIgnoreCase))
                {
                    await session.SendAsync($"SERVERLIST {s.Name} {s.Sid} {s.ParentSid ?? localSid2} :{s.Description}", ct);
                }
            }

            if (session.UserSyncEnabled)
            {
                foreach (var u in _state.GetUsersSnapshot())
                {
                    if (!u.IsRegistered)
                    {
                        continue;
                    }

                    if (u.IsRemote)
                    {
                        continue;
                    }

                    var uid = !string.IsNullOrWhiteSpace(u.Uid)
                        ? u.Uid!
                        : $"{localSid2}{u.ConnectionId[..Math.Min(6, u.ConnectionId.Length)].ToUpperInvariant()}";

                    var nick = u.Nick ?? "*";
                    var user = u.UserName ?? "u";
                    var host = u.Host ?? "localhost";
                    var gecos = u.RealName ?? "";

                    var secure = u.IsSecureConnection ? "1" : "0";
                    await session.SendAsync($"USER {uid} {nick} {user} {host} {secure} :{gecos}", ct);
                }

                foreach (var chName in _state.GetAllChannelNames())
                {
                    if (!_state.TryGetChannel(chName, out var ch) || ch is null)
                    {
                        continue;
                    }

                    var uids = new List<string>();
                    foreach (var m in ch.Members)
                    {
                        if (_state.TryGetUser(m.ConnectionId, out var memberUser) && memberUser is not null && !string.IsNullOrWhiteSpace(memberUser.Uid))
                        {
                            uids.Add(memberUser.Uid!);
                        }
                    }

                    if (uids.Count > 0)
                    {
                        await session.SendAsync($"CHAN {ch.Name} {ch.CreatedTs} {string.Join(',', uids)}", ct);
                    }

                    await session.SendAsync($"MODECH {ch.Name} {ch.CreatedTs} {ch.FormatModeString()}", ct);

                    var key = ch.Key ?? string.Empty;
                    var limit = ch.UserLimit?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
                    await session.SendAsync($"CHANMETA {ch.Name} {ch.CreatedTs} {key} {limit}", ct);

                    foreach (var b in ch.Bans)
                    {
                        await session.SendAsync($"BAN {ch.Name} {ch.CreatedTs} {b.Mask} {b.SetBy} {b.SetAtUtc.ToUnixTimeSeconds()}", ct);
                    }

                    foreach (var m in ch.Members)
                    {
                        if (_state.TryGetUser(m.ConnectionId, out var memberUser) && memberUser is not null && !string.IsNullOrWhiteSpace(memberUser.Uid))
                        {
                            await session.SendAsync($"MEMBER {ch.Name} {memberUser.Uid} {(int)m.Privilege}", ct);
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(ch.Topic))
                    {
                        var ts = ch.TopicTs > 0 ? ch.TopicTs : ChannelTimestamps.NowTs(); // Use TS-based format
                        await session.SendAsync($"TOPICSET {ch.Name} {ch.CreatedTs} {ts} :{ch.Topic}", ct);
                    }
                }
            }

            await session.SendAsync("ENDBURST", ct);
        }

        private async Task ReceiveLoopAsync(IServerLinkSession session, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await session.ReadLineAsync(ct);
                if (line is null)
                {
                    return;
                }

                if (!_floodGate.Allow(session.ConnectionId))
                {
                    try { await session.SendAsync("ERROR :Excess Flood", ct); } catch { }
                    return;
                }

                if (!ServerLinkParser.TryParse(line, out var command, out var args, out var trailing))
                {
                    continue;
                }

                string? msgId = null;
                string? originSid = null;
                int argOffset = 0;

                if (args.Length >= 2 && args[0].Length >= 6 && args[1].Length >= 3)
                {
                    msgId = args[0];
                    originSid = args[1];
                    argOffset = 2;

                    if (!_seen.TryMarkSeen(msgId))
                    {
                        continue;
                    }
                }

                if (string.Equals(command, "SERVERLIST", StringComparison.OrdinalIgnoreCase))
                {
                    if (args.Length >= 2)
                    {
                        var name = args[0];
                        var sid = args[1];
                        var parentSid = args.Length >= 3 ? args[2] : session.RemoteSid;

                        if (string.IsNullOrWhiteSpace(sid) || string.Equals(sid, parentSid, StringComparison.OrdinalIgnoreCase))
                        {
                            await session.SendAsync("ERROR :Bad SERVERLIST", ct);
                            return;
                        }

                        if (!IsValidSid(sid))
                        {
                            await session.SendAsync("ERROR :Bad SID", ct);
                            return;
                        }

                        if (_state.TryGetNextHopBySid(sid, out var existingHop) && existingHop is not null &&
                            !string.Equals(existingHop, session.ConnectionId, StringComparison.OrdinalIgnoreCase))
                        {
                            await session.SendAsync("ERROR :SID collision", ct);
                            return;
                        }

                        _state.TryRegisterRemoteServer(new RemoteServer
                        {
                            ConnectionId = session.ConnectionId,
                            Name = name,
                            Sid = sid,
                            Description = trailing ?? string.Empty,
                            ParentSid = parentSid
                        });

                        _state.TrySetNextHopBySid(sid, session.ConnectionId);
                    }

                    continue;
                }

                if (string.Equals(command, "SQUIT", StringComparison.OrdinalIgnoreCase))
                {
                    if (args.Length >= argOffset + 1)
                    {
                        var sid = args[argOffset + 0];
                        var (_, removedUserUids) = _state.RemoveRemoteServerTreeBySidDetailed(sid);

                        foreach (var uid in removedUserUids)
                        {
                            var qid = NewMsgId();
                            MarkSeen(qid);
                            var os = LocalOriginSid;
                            await PropagateRawAsync(session.ConnectionId, $"QUIT {qid} {os} {uid} :*.net *.split", ct);
                        }

                        if (!string.IsNullOrWhiteSpace(msgId) && !string.IsNullOrWhiteSpace(originSid))
                        {
                            await PropagateRawAsync(session.ConnectionId, $"SQUIT {msgId} {originSid} {sid} :{trailing ?? string.Empty}", ct);
                        }
                    }

                    continue;
                }

                if (string.Equals(command, "NICK", StringComparison.OrdinalIgnoreCase))
                {
                    if (!session.UserSyncEnabled)
                    {
                        continue;
                    }

                    if (args.Length >= argOffset + 3)
                    {
                        var uid = args[argOffset + 0];
                        var tsStr = args[argOffset + 1];
                        var newNick = args[argOffset + 2];

                        if (!long.TryParse(tsStr, out var ts))
                        {
                            ts = ChannelTimestamps.NowTs();
                        }

                        if (_state.TryGetUserByUid(uid, out var u) && u is not null)
                        {
                            if (ts >= u.NickTs)
                            {
                                await ApplyNickWithCollisionHandlingAsync(u, ts, newNick, ct);
                            }
                        }

                        if (!string.IsNullOrWhiteSpace(msgId) && !string.IsNullOrWhiteSpace(originSid))
                        {
                            await PropagateRawAsync(session.ConnectionId, $"NICK {msgId} {originSid} {uid} {ts} {newNick}", ct);
                        }
                    }

                    continue;
                }

                if (string.Equals(command, "SVSNICK", StringComparison.OrdinalIgnoreCase))
                {
                    if (!session.UserSyncEnabled)
                    {
                        continue;
                    }

                    var localMsgId = msgId;
                    var localOriginSid = originSid;
                    var localOffset = argOffset;

                    if (localOffset == 0 && args.Length >= 5 && args[0].Length >= 6 && IsValidSid(args[1]))
                    {
                        localMsgId = args[0];
                        localOriginSid = args[1];
                        localOffset = 2;

                        if (!_seen.TryMarkSeen(localMsgId))
                        {
                            continue;
                        }
                    }

                    if (args.Length >= localOffset + 3)
                    {
                        var uid = args[localOffset + 0];
                        var tsStr = args[localOffset + 1];
                        var newNick = args[localOffset + 2];

                        if (!long.TryParse(tsStr, out var ts))
                            ts = ChannelTimestamps.NowTs();

                        if (_state.TryGetUserByUid(uid, out var u) && u is not null)
                        {
                            if (ts >= u.NickTs)
                            {
                                await ApplyNickWithCollisionHandlingAsync(u, ts, newNick, ct);
                            }
                        }

                        if (!string.IsNullOrWhiteSpace(localMsgId) && !string.IsNullOrWhiteSpace(localOriginSid))
                        {
                            await PropagateRawAsync(session.ConnectionId, $"SVSNICK {localMsgId} {localOriginSid} {uid} {ts} {newNick}", ct);
                        }
                    }

                    continue;
                }

                if (string.Equals(command, "UMODE", StringComparison.OrdinalIgnoreCase))
                {
                    if (!session.UserSyncEnabled)
                    {
                        continue;
                    }

                    if (args.Length >= argOffset + 2)
                    {
                        var uid = args[argOffset + 0];
                        var modeToken = args[argOffset + 1];

                        if (_state.TryGetUserByUid(uid, out var u) && u is not null)
                        {
                            var sign = modeToken.Length > 0 ? modeToken[0] : '+';

                            for (int i = 1; i < modeToken.Length; i++)
                            {
                                var c = modeToken[i];
                                if (c == '+' || c == '-')
                                {
                                    sign = c;
                                    continue;
                                }

                                if (c != 'i')
                                    continue;

                                var enable = sign == '+';
                                _state.TrySetUserMode(u.ConnectionId, UserModes.Invisible, enable);
                            }
                        }

                        if (!string.IsNullOrWhiteSpace(msgId) && !string.IsNullOrWhiteSpace(originSid))
                        {
                            await PropagateRawAsync(session.ConnectionId, $"UMODE {msgId} {originSid} {uid} {modeToken}", ct);
                        }
                    }

                    continue;
                }

                if (string.Equals(command, "SVSJOIN", StringComparison.OrdinalIgnoreCase))
                {
                    if (!session.UserSyncEnabled)
                    {
                        continue;
                    }

                    if (args.Length >= argOffset + 2)
                    {
                        var uid = args[argOffset + 0];
                        var channelName = args[argOffset + 1];

                        if (_state.TryGetUserByUid(uid, out var u) && u is not null && !string.IsNullOrWhiteSpace(u.Nick))
                        {
                            if (_state.TryJoinChannel(u.ConnectionId, u.Nick!, channelName) && _state.TryGetChannel(channelName, out var ch) && ch is not null)
                            {
                                var userName = u.UserName ?? "u";
                                var host = u.Host ?? "localhost";
                                var joinLine = $":{u.Nick}!{userName}@{host} JOIN :{channelName}";
                                await _routing.BroadcastToChannelAsync(ch, joinLine, excludeConnectionId: null, ct);
                            }
                        }

                        if (!string.IsNullOrWhiteSpace(msgId) && !string.IsNullOrWhiteSpace(originSid))
                        {
                            await PropagateRawAsync(session.ConnectionId, $"SVSJOIN {msgId} {originSid} {uid} {channelName}", ct);
                        }
                    }

                    continue;
                }

                if (string.Equals(command, "QUIT", StringComparison.OrdinalIgnoreCase))
                {
                    if (!session.UserSyncEnabled)
                    {
                        continue;
                    }

                    if (args.Length >= argOffset + 1)
                    {
                        var uid = args[argOffset + 0];
                        if (_state.TryGetUserByUid(uid, out var u) && u is not null)
                        {
                            if (!string.IsNullOrWhiteSpace(u.Nick))
                            {
                                await _watch.NotifyLogoffAsync(_state, u.Nick!, u.UserName, u.Host, ct);
                            }

                            _state.RemoveUser(u.ConnectionId);
                        }

                        if (!string.IsNullOrWhiteSpace(msgId) && !string.IsNullOrWhiteSpace(originSid))
                        {
                            await PropagateRawAsync(session.ConnectionId, $"QUIT {msgId} {originSid} {uid} :{trailing ?? string.Empty}", ct);
                        }
                    }

                    continue;
                }

                if (string.Equals(command, "KILL", StringComparison.OrdinalIgnoreCase))
                {
                    if (!session.UserSyncEnabled)
                    {
                        continue;
                    }

                    if (args.Length >= argOffset + 1)
                    {
                        var uid = args[argOffset + 0];
                        var killQuit = string.IsNullOrWhiteSpace(trailing) ? "Killed" : trailing;

                        if (_state.TryGetUserByUid(uid, out var u) && u is not null)
                        {
                            if (u.IsRemote)
                            {
                                if (!string.IsNullOrWhiteSpace(u.RemoteSid))
                                {
                                    await SendToSidAsync(u.RemoteSid!, session.ConnectionId, line, ct);
                                }
                            }
                            else
                            {
                                var quitLine = $":{u.Nick}!{(u.UserName ?? "u")}@{(u.Host ?? "localhost")} QUIT :{killQuit}";

                                var recipients = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
                                foreach (var chName in _state.GetUserChannels(u.ConnectionId))
                                {
                                    if (!_state.TryGetChannel(chName, out var ch) || ch is null)
                                        continue;

                                    foreach (var member in ch.Members)
                                    {
                                        if (member.ConnectionId == u.ConnectionId)
                                            continue;

                                        recipients.Add(member.ConnectionId);
                                    }
                                }

                                foreach (var connId in recipients)
                                {
                                    await _routing.SendToUserAsync(connId, quitLine, ct);
                                }

                                if (_sessions.TryGet(u.ConnectionId, out var targetSession) && targetSession is not null)
                                {
                                    try { await targetSession.SendAsync($":server NOTICE {u.Nick} :*** You were killed ({killQuit})", ct); } catch { }
                                    try { await targetSession.CloseAsync(killQuit, ct); } catch { }
                                }

                                _silence.RemoveAll(u.ConnectionId);

                                if (!string.IsNullOrWhiteSpace(u.Nick))
                                {
                                    await _watch.NotifyLogoffAsync(_state, u.Nick!, u.UserName, u.Host, ct);
                                }

                                _watch.RemoveAll(u.ConnectionId);

                                _state.RemoveUser(u.ConnectionId);

                                var qid = NewMsgId();
                                MarkSeen(qid);
                                var os = LocalOriginSid;
                                await PropagateRawAsync(excludeConnId: null, $"QUIT {qid} {os} {uid} :{killQuit}", ct);
                            }
                        }
                    }

                    continue;
                }

                if (string.Equals(command, "USER", StringComparison.OrdinalIgnoreCase))
                {
                    if (!session.UserSyncEnabled)
                    {
                        continue;
                    }

                    if (args.Length >= 4)
                    {
                        var uid = args[0];
                        var nick = args[1];
                        var userName = args[2];
                        var host = args[3];

                        var secureFlag = args.Length >= 5 ? args[4] : "0";
                        var isSecure = secureFlag == "1";

                        var u = new User
                        {
                            ConnectionId = $"uid:{uid}",
                            Uid = uid,
                            Nick = nick,
                            UserName = userName,
                            Host = host,
                            IsSecureConnection = isSecure,
                            RealName = trailing,
                            IsRegistered = true,
                            IsRemote = true,
                            RemoteSid = session.RemoteSid
                        };

                        if (!_state.TryAddRemoteUser(u))
                        {
                            if (!string.IsNullOrWhiteSpace(u.Uid) && _state.TryGetUserByUid(u.Uid!, out _))
                            {
                                await session.SendAsync("ERROR :UID collision", ct);
                                return;
                            }

                            await ResolveNickCollisionOnAddAsync(session, u, ct);
                        }

                        await _watch.NotifyLogonAsync(_state, u, ct);
                    }

                    continue;
                }

                if (string.Equals(command, "ENDBURST", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation("S2S burst completed for {RemoteServer}", session.RemoteServerName);
                    continue;
                }

                if (string.Equals(command, "CHAN", StringComparison.OrdinalIgnoreCase))
                {
                    if (!session.UserSyncEnabled)
                    {
                        continue;
                    }

                    if (args.Length >= 3)
                    {
                        var channelName = args[0];
                        var tsStr = args[1];
                        var list = args[2];

                        if (!long.TryParse(tsStr, out var remoteTs))
                        {
                            remoteTs = ChannelTimestamps.NowTs();
                        }

                        var ch = _state.GetOrCreateChannel(channelName);

                        if (remoteTs < ch.CreatedTs)
                        {
                            ch.ResetForTsCollision(remoteTs);
                        }

                        foreach (var uid in list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        {
                            if (_state.TryGetUserByUid(uid, out var u) && u is not null && !string.IsNullOrWhiteSpace(u.Nick))
                            {
                                _state.TryJoinChannel(u.ConnectionId, u.Nick!, channelName);
                            }
                        }
                    }

                    continue;
                }

                if (string.Equals(command, "MODECH", StringComparison.OrdinalIgnoreCase))
                {
                    if (!session.UserSyncEnabled)
                    {
                        continue;
                    }

                    if (args.Length >= 3)
                    {
                        var channelName = args[0];
                        if (!long.TryParse(args[1], out var ts))
                        {
                            ts = ChannelTimestamps.NowTs();
                        }

                        var modes = args[2];
                        var ch = _state.GetOrCreateChannel(channelName);

                        if (!ApplyIncomingChannelTs(ch, ts))
                        {
                            continue;
                        }

                        var sign = '+';
                        foreach (var mc in modes)
                        {
                            if (mc is '+' or '-')
                            {
                                sign = mc;
                                continue;
                            }

                            var enable = sign == '+';
                            switch (mc)
                            {
                                case 'n': ch.ApplyModeChange(ChannelModes.NoExternalMessages, enable); break;
                                case 't': ch.ApplyModeChange(ChannelModes.TopicOpsOnly, enable); break;
                                case 'i': ch.ApplyModeChange(ChannelModes.InviteOnly, enable); break;
                                case 'm': ch.ApplyModeChange(ChannelModes.Moderated, enable); break;
                                case 's': ch.ApplyModeChange(ChannelModes.Secret, enable); break;
                                default: break;
                            }
                        }
                    }

                    continue;
                }

                if (string.Equals(command, "CHANMETA", StringComparison.OrdinalIgnoreCase))
                {
                    if (!session.UserSyncEnabled)
                    {
                        continue;
                    }

                    if (args.Length >= 4)
                    {
                        var channelName = args[0];
                        if (!long.TryParse(args[1], out var ts))
                        {
                            ts = ChannelTimestamps.NowTs();
                        }

                        var key = args[2];
                        var limit = args[3];

                        var ch = _state.GetOrCreateChannel(channelName);

                        if (!ApplyIncomingChannelTs(ch, ts))
                        {
                            continue;
                        }

                        ch.SetKey(string.IsNullOrWhiteSpace(key) ? null : key);

                        if (int.TryParse(limit, out var lim) && lim > 0)
                        {
                            ch.SetLimit(lim);
                        }
                        else
                        {
                            ch.ClearLimit();
                        }
                    }

                    continue;
                }

                if (string.Equals(command, "TOPICSET", StringComparison.OrdinalIgnoreCase))
                {
                    if (!session.UserSyncEnabled)
                    {
                        continue;
                    }

                    if (args.Length >= 3)
                    {
                        var channelName = args[0];
                        if (!long.TryParse(args[1], out var channelTs))
                        {
                            channelTs = ChannelTimestamps.NowTs();
                        }

                        if (!long.TryParse(args[2], out var topicTs))
                        {
                            topicTs = ChannelTimestamps.NowTs();
                        }

                        var topic = trailing ?? string.Empty;
                        var ch = _state.GetOrCreateChannel(channelName);

                        if (!ApplyIncomingChannelTs(ch, channelTs))
                        {
                            continue;
                        }

                        ch.TrySetTopicWithTs(topic, setBy: session.RemoteServerName ?? "remote", topicTs);
                    }
                    else if (args.Length >= 2)
                    {
                        var channelName = args[0];
                        if (!long.TryParse(args[1], out var topicTs))
                        {
                            topicTs = ChannelTimestamps.NowTs();
                        }

                        var topic = trailing ?? string.Empty;
                        var ch = _state.GetOrCreateChannel(channelName);
                        ch.TrySetTopicWithTs(topic, setBy: session.RemoteServerName ?? "remote", topicTs);
                    }

                    continue;
                }

                if (string.Equals(command, "TOPIC", StringComparison.OrdinalIgnoreCase))
                {
                    if (!session.UserSyncEnabled)
                    {
                        continue;
                    }

                    if (args.Length >= argOffset + 4)
                    {
                        var fromUid = args[argOffset + 0];
                        var channelName = args[argOffset + 1];
                        var channelTsStr = args.Length >= argOffset + 3 ? args[argOffset + 2] : "0";
                        var topicTsStr = args.Length >= argOffset + 4 ? args[argOffset + 3] : "0";

                        if (!long.TryParse(channelTsStr, out var channelTs))
                            channelTs = ChannelTimestamps.NowTs();
                        if (!long.TryParse(topicTsStr, out var topicTs))
                            topicTs = ChannelTimestamps.NowTs();

                        var topic = trailing ?? string.Empty;

                        if (_state.TryGetUserByUid(fromUid, out _))
                        {
                            var ch = _state.GetOrCreateChannel(channelName);

                            if (!ApplyIncomingChannelTs(ch, channelTs))
                            {
                                continue;
                            }

                            ch.TrySetTopicWithTs(topic, setBy: session.RemoteServerName ?? "remote", topicTs);
                        }

                        if (!string.IsNullOrWhiteSpace(msgId) && !string.IsNullOrWhiteSpace(originSid))
                        {
                            await PropagateRawAsync(session.ConnectionId, $"TOPIC {msgId} {originSid} {fromUid} {channelName} {channelTs} {topicTs} :{topic}", ct);
                        }
                    }

                    continue;
                }

                if (string.Equals(command, "BAN", StringComparison.OrdinalIgnoreCase))
                {
                    if (!session.UserSyncEnabled)
                    {
                        continue;
                    }

                    if (args.Length >= 5)
                    {
                        var channelName = args[0];
                        if (!long.TryParse(args[1], out var ts))
                        {
                            ts = ChannelTimestamps.NowTs();
                        }

                        var mask = args[2];
                        var setBy = args[3];
                        var setAt = long.TryParse(args[4], out var tmp) ? tmp : DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                        var ch = _state.GetOrCreateChannel(channelName);

                        if (!ApplyIncomingChannelTs(ch, ts))
                        {
                            continue;
                        }

                        ch.AddBan(mask, setBy, DateTimeOffset.FromUnixTimeSeconds(setAt));
                    }

                    continue;
                }

                if (string.Equals(command, "BANDEL", StringComparison.OrdinalIgnoreCase))
                {
                    if (!session.UserSyncEnabled)
                    {
                        continue;
                    }

                    if (args.Length >= 3)
                    {
                        var channelName = args[0];
                        if (!long.TryParse(args[1], out var ts))
                        {
                            ts = ChannelTimestamps.NowTs();
                        }

                        var mask = args[2];

                        var ch = _state.GetOrCreateChannel(channelName);

                        if (!ApplyIncomingChannelTs(ch, ts))
                        {
                            continue;
                        }

                        ch.RemoveBan(mask);
                    }

                    continue;
                }

                if (string.Equals(command, "MEMBER", StringComparison.OrdinalIgnoreCase))
                {
                    if (!session.UserSyncEnabled)
                    {
                        continue;
                    }

                    if (args.Length >= 3)
                    {
                        var channelName = args[0];
                        var uid = args[1];
                        var priv = int.TryParse(args[2], out var p) ? (ChannelPrivilege)p : ChannelPrivilege.Normal;

                        if (_state.TryGetChannel(channelName, out var ch) && ch is not null &&
                            _state.TryGetUserByUid(uid, out var u) && u is not null)
                        {
                            ch.TryUpdateMemberPrivilege(u.ConnectionId, priv);
                        }
                    }

                    continue;
                }

                if (string.Equals(command, "JOIN", StringComparison.OrdinalIgnoreCase))
                {
                    if (!session.UserSyncEnabled)
                    {
                        continue;
                    }

                    if (args.Length >= argOffset + 2)
                    {
                        var uid = args[argOffset + 0];
                        var channel = args[argOffset + 1];

                        if (_state.TryGetUserByUid(uid, out var u) && u is not null && !string.IsNullOrWhiteSpace(u.Nick))
                        {
                            _state.TryJoinChannel(u.ConnectionId, u.Nick!, channel);
                        }

                        if (!string.IsNullOrWhiteSpace(msgId) && !string.IsNullOrWhiteSpace(originSid))
                        {
                            await PropagateRawAsync(session.ConnectionId, $"JOIN {msgId} {originSid} {uid} {channel}", ct);
                        }
                    }

                    continue;
                }

                if (string.Equals(command, "PART", StringComparison.OrdinalIgnoreCase))
                {
                    if (!session.UserSyncEnabled)
                    {
                        continue;
                    }

                    if (args.Length >= argOffset + 2)
                    {
                        var uid = args[argOffset + 0];
                        var channel = args[argOffset + 1];

                        await ApplyPartByUidAsync(uid, channel, trailing ?? string.Empty, ct);

                        if (!string.IsNullOrWhiteSpace(msgId) && !string.IsNullOrWhiteSpace(originSid))
                        {
                            await PropagateRawAsync(session.ConnectionId, $"PART {msgId} {originSid} {uid} {channel} :{trailing ?? string.Empty}", ct);
                        }
                    }

                    continue;
                }

                if (string.Equals(command, "SVSPART", StringComparison.OrdinalIgnoreCase))
                {
                    if (!session.UserSyncEnabled)
                    {
                        continue;
                    }

                    if (args.Length >= argOffset + 2)
                    {
                        var uid = args[argOffset + 0];
                        var channel = args[argOffset + 1];

                        await ApplyPartByUidAsync(uid, channel, trailing ?? string.Empty, ct);

                        if (!string.IsNullOrWhiteSpace(msgId) && !string.IsNullOrWhiteSpace(originSid))
                        {
                            await PropagateRawAsync(session.ConnectionId, $"SVSPART {msgId} {originSid} {uid} {channel} :{trailing ?? string.Empty}", ct);
                        }
                    }

                    continue;
                }

                if (string.Equals(command, "PRIVMSG", StringComparison.OrdinalIgnoreCase) || string.Equals(command, "NOTICE", StringComparison.OrdinalIgnoreCase))
                {
                    if (!session.UserSyncEnabled)
                    {
                        continue;
                    }

                    if (args.Length >= argOffset + 2)
                    {
                        var fromUid = args[argOffset + 0];
                        var target = args[argOffset + 1];
                        var text = trailing ?? string.Empty;

                        if (_state.TryGetUserByUid(fromUid, out var fromU) && fromU is not null)
                        {
                            var fromNick = fromU.Nick ?? "*";
                            var fromUser = fromU.UserName ?? "u";
                            var fromHost = fromU.Host ?? "localhost";
                            var prefix = $":{fromNick}!{fromUser}@{fromHost}";
                            var lineOut = $"{prefix} {command.ToUpperInvariant()} {target} :{text}";

                            if (target.StartsWith('#'))
                            {
                                if (_state.TryGetChannel(target, out var ch) && ch is not null)
                                {
                                    await _routing.BroadcastToChannelAsync(ch, lineOut, excludeConnectionId: null, ct);
                                }
                            }
                            else
                            {
                                if (_state.TryGetConnectionIdByNick(target, out var toConn) && toConn is not null)
                                {
                                    if (_state.TryGetUser(toConn, out var toU) && toU is not null && !toU.IsRemote)
                                    {
                                        var fromMask = $"{fromNick}!{fromUser}@{fromHost}";
                                        if (_silence.IsSilenced(toConn, fromMask))
                                        {
                                            goto AfterLocalDeliver;
                                        }
                                    }

                                    await _routing.SendToUserAsync(toConn, lineOut, ct);
                                }
                            }
                        }

                    AfterLocalDeliver:

                        if (!string.IsNullOrWhiteSpace(msgId) && !string.IsNullOrWhiteSpace(originSid))
                        {
                            await PropagateRawAsync(session.ConnectionId, $"{command.ToUpperInvariant()} {msgId} {originSid} {fromUid} {target} :{text}", ct);
                        }
                    }

                    continue;
                }
            }
        }

        public async Task PropagateNickAsync(string uid, string newNick, CancellationToken ct)
        {
            var msgId = NewMsgId();
            var originSid = _options.CurrentValue.ServerInfo?.Sid ?? "001";
            foreach (var s in _linksByConn.Values)
            {
                if (!s.IsAuthenticated || !s.UserSyncEnabled)
                {
                    continue;
                }

                if (_state.TryGetUserByUid(uid, out var u) && u is not null)
                {
                    await s.SendAsync($"NICK {msgId} {originSid} {uid} {u.NickTs} {newNick}", ct);
                }
            }
        }

        public async Task PropagateSvsNickAsync(string uid, string newNick, CancellationToken ct)
        {
            var msgId = NewMsgId();
            MarkSeen(msgId);
            var originSid = LocalOriginSid;

            foreach (var s in _linksByConn.Values)
            {
                if (!s.IsAuthenticated || !s.UserSyncEnabled)
                {
                    continue;
                }

                if (_state.TryGetUserByUid(uid, out var u) && u is not null)
                {
                    await s.SendAsync($"SVSNICK {msgId} {originSid} {uid} {u.NickTs} {newNick}", ct);
                }
            }
        }

        public async Task<bool> SendSvsNickAsync(string targetSid, string uid, string newNick, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(targetSid) || string.IsNullOrWhiteSpace(uid) || string.IsNullOrWhiteSpace(newNick))
                return false;

            if (!_state.TryGetNextHopBySid(targetSid, out var nextHop) || nextHop is null)
                return false;

            if (!_linksByConn.TryGetValue(nextHop, out var sess) || !sess.IsAuthenticated || !sess.UserSyncEnabled)
                return false;

            var msgId = NewMsgId();
            MarkSeen(msgId);
            var originSid = LocalOriginSid;

            var ts = ChannelTimestamps.NowTs();
            if (_state.TryGetUserByUid(uid, out var u) && u is not null)
            {
                ts = Math.Max(ts, u.NickTs);
            }

            await sess.SendAsync($"SVSNICK {msgId} {originSid} {uid} {ts} {newNick}", ct);
            return true;
        }

        public async Task PropagateQuitAsync(string uid, string reason, CancellationToken ct)
        {
            var msgId = NewMsgId();
            var originSid = _options.CurrentValue.ServerInfo?.Sid ?? "001";
            foreach (var s in _linksByConn.Values)
            {
                if (!s.IsAuthenticated || !s.UserSyncEnabled)
                {
                    continue;
                }

                await s.SendAsync($"QUIT {msgId} {originSid} {uid} :{reason}", ct);
            }
        }

        public async Task<bool> SendKillAsync(string targetSid, string uid, string reason, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(targetSid) || string.IsNullOrWhiteSpace(uid))
                return false;

            if (!_state.TryGetNextHopBySid(targetSid, out var nextHop) || nextHop is null)
                return false;

            if (!_linksByConn.TryGetValue(nextHop, out var sess) || !sess.IsAuthenticated || !sess.UserSyncEnabled)
                return false;

            var msgId = NewMsgId();
            MarkSeen(msgId);
            var originSid = LocalOriginSid;

            await sess.SendAsync($"KILL {msgId} {originSid} {uid} :{reason}", ct);
            return true;
        }

        public async Task<bool> LocalSquitAsync(string sid, string reason, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(sid))
                return false;

            var (_, removedUserUids) = _state.RemoveRemoteServerTreeBySidDetailed(sid);
            if (removedUserUids.Count == 0 && !_state.GetRemoteServers().Any(s => string.Equals(s.Sid, sid, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            var msgId = NewMsgId();
            MarkSeen(msgId);
            var originSid = LocalOriginSid;

            foreach (var uid in removedUserUids)
            {
                var qid = NewMsgId();
                MarkSeen(qid);
                await PropagateRawAsync(excludeConnId: null, $"QUIT {qid} {originSid} {uid} :*.net *.split", ct);
            }

            await PropagateRawAsync(excludeConnId: null, $"SQUIT {msgId} {originSid} {sid} :{reason}", ct);
            return true;
        }
    }
}
