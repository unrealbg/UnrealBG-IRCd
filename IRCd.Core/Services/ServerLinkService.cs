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

        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, IServerLinkSession> _linksByConn = new(StringComparer.OrdinalIgnoreCase);

        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _seen = new(StringComparer.Ordinal);

        public ServerLinkService(ILogger<ServerLinkService> logger, IOptionsMonitor<IrcOptions> options, ServerState state, RoutingService routing)
        {
            _logger = logger;
            _options = options;
            _state = state;
            _routing = routing;
        }

        private async Task SendToSidAsync(string sid, string excludeConnId, string line, CancellationToken ct)
        {
            if (_state.TryGetNextHopBySid(sid, out var nextHop) && nextHop is not null)
            {
                if (string.Equals(nextHop, excludeConnId, StringComparison.OrdinalIgnoreCase))
                    return;

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
                    continue;

                var sess = kv.Value;
                if (!sess.IsAuthenticated || !sess.UserSyncEnabled)
                    continue;

                await sess.SendAsync(line, ct);
            }
        }

        private void MarkSeen(string msgId)
        {
            _seen.TryAdd(msgId, 0);
        }

        private string LocalOriginSid => _options.CurrentValue.ServerInfo?.Sid ?? "001";

        public async Task PropagateJoinAsync(string uid, string channel, CancellationToken ct)
        {
            var msgId = NewMsgId();
            MarkSeen(msgId);
            var originSid = LocalOriginSid;

            foreach (var s in _linksByConn.Values)
            {
                if (!s.IsAuthenticated || !s.UserSyncEnabled)
                    continue;
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
                    continue;
                await s.SendAsync($"PART {msgId} {originSid} {uid} {channel} :{reason}", ct);
            }
        }

        public async Task PropagatePrivMsgAsync(string fromUid, string target, string text, CancellationToken ct)
        {
            var msgId = NewMsgId();
            MarkSeen(msgId);
            var originSid = LocalOriginSid;

            foreach (var s in _linksByConn.Values)
            {
                if (!s.IsAuthenticated || !s.UserSyncEnabled)
                    continue;
                await s.SendAsync($"PRIVMSG {msgId} {originSid} {fromUid} {target} :{text}", ct);
            }
        }

        public async Task PropagateTopicAsync(string fromUid, string channel, string? topic, CancellationToken ct)
        {
            var msgId = NewMsgId();
            MarkSeen(msgId);
            var originSid = LocalOriginSid;

            long ts;
            if (_state.TryGetChannel(channel, out var ch) && ch is not null && ch.TopicTs > 0)
                ts = ch.TopicTs;
            else
                ts = ChannelTimestamps.NowTs();

            foreach (var s in _linksByConn.Values)
            {
                if (!s.IsAuthenticated || !s.UserSyncEnabled)
                    continue;
                await s.SendAsync($"TOPIC {msgId} {originSid} {fromUid} {ts} {channel} :{topic ?? string.Empty}", ct);
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
                    continue;
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
                    continue;
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
                    continue;
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
                    continue;
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
                    continue;
                await s.SendAsync($"BANDEL {msgId} {originSid} {channel} {ts} {mask}", ct);
            }
        }

        private static void ApplyChannelModeString(Channel ch, string modeString)
        {
            if (string.IsNullOrWhiteSpace(modeString))
                return;

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
            if (_state.TrySetNick(user.ConnectionId, newNick))
            {
                user.NickTs = ts;
                user.Nick = newNick;
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

                otherUser.NickTs = Math.Max(otherUser.NickTs, ChannelTimestamps.NowTs());
                _state.TrySetNick(otherUser.ConnectionId, forcedNick);
                otherUser.Nick = forcedNick;

                if (!string.IsNullOrWhiteSpace(otherUser.Uid))
                {
                    await PropagateNickAsync(otherUser.Uid!, forcedNick, ct);
                }

                user.NickTs = ts;
                _state.TrySetNick(user.ConnectionId, newNick);
                user.Nick = newNick;
                return;
            }

            var myForced = MakeCollisionNick(user.Uid ?? user.ConnectionId);
            user.NickTs = ts;
            _state.TrySetNick(user.ConnectionId, myForced);
            user.Nick = myForced;

            if (!string.IsNullOrWhiteSpace(user.Uid))
            {
                await PropagateNickAsync(user.Uid!, myForced, ct);
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

                        continue;
                    }

                    if (string.Equals(command, "SERVER", StringComparison.OrdinalIgnoreCase))
                    {
                        if (args.Length >= 1)
                        {
                            session.RemoteServerName = args[0];
                        }

                        if (args.Length >= 2)
                        {
                            session.RemoteSid = args[1];
                        }

                        var links = _options.CurrentValue.Links;
                        var match = links.FirstOrDefault(l => string.Equals(l.Name, session.RemoteServerName, StringComparison.OrdinalIgnoreCase));

                        if (match is null)
                        {
                            _logger.LogWarning("S2S link rejected from {Remote}: unknown server '{ServerName}'", session.RemoteEndPoint, session.RemoteServerName);
                            await session.SendAsync($"ERROR :Unknown server", ct);
                            break;
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
                        await session.SendAsync($"PASS {match.Password} :TS", ct);
                        await session.SendAsync($"SERVER {local.Name} {local.Sid} :{local.Description}", ct);

                        var ok = _state.TryRegisterRemoteServer(new RemoteServer
                        {
                            ConnectionId = session.ConnectionId,
                            Name = session.RemoteServerName ?? match.Name,
                            Sid = session.RemoteSid ?? string.Empty,
                            Description = trailing ?? string.Empty,
                            ParentSid = local.Sid
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
                _linksByConn.TryRemove(session.ConnectionId, out _);
                _state.RemoveRemoteServerByConnection(session.ConnectionId);
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

                await session.SendAsync($"PASS {link.Password} :TS", ct);
                await session.SendAsync($"SERVER {local.Name} {local.Sid} :{local.Description}", ct);

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

                        continue;
                    }

                    if (string.Equals(command, "SERVER", StringComparison.OrdinalIgnoreCase))
                    {
                        if (args.Length >= 1)
                        {
                            session.RemoteServerName = args[0];
                        }

                        if (args.Length >= 2)
                        {
                            session.RemoteSid = args[1];
                        }

                        if (!string.Equals(session.RemoteServerName, link.Name, StringComparison.OrdinalIgnoreCase))
                        {
                            _logger.LogWarning("S2S outbound got unexpected SERVER name '{RemoteName}' expected '{Expected}'", session.RemoteServerName, link.Name);
                            await session.SendAsync("ERROR :Unexpected server name", ct);
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
                            ParentSid = local.Sid
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
                _linksByConn.TryRemove(session.ConnectionId, out _);
                _state.RemoveRemoteServerByConnection(session.ConnectionId);
                try { await session.CloseAsync("S2S outbound closed", CancellationToken.None); } catch { }
                try { await writerTask; } catch { }
            }
        }

        private async Task SendBurstAsync(IServerLinkSession session, CancellationToken ct)
        {
            var local = _options.CurrentValue.ServerInfo;

            await session.SendAsync($"SERVERLIST {local.Name} {local.Sid} {local.Sid} :{local.Description}", ct);

            foreach (var s in _state.GetRemoteServers())
            {
                if (!string.IsNullOrWhiteSpace(s.Sid) && !string.Equals(s.Sid, session.RemoteSid, StringComparison.OrdinalIgnoreCase))
                {
                    await session.SendAsync($"SERVERLIST {s.Name} {s.Sid} {s.ParentSid ?? local.Sid} :{s.Description}", ct);
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
                        : $"{local.Sid}{u.ConnectionId[..Math.Min(6, u.ConnectionId.Length)].ToUpperInvariant()}";

                    var nick = u.Nick ?? "*";
                    var user = u.UserName ?? "u";
                    var host = u.Host ?? "localhost";
                    var gecos = u.RealName ?? "";

                    await session.SendAsync($"USER {uid} {nick} {user} {host} :{gecos}", ct);
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
                        await session.SendAsync($"TOPICSET {ch.Name} {ts} :{ch.Topic}", ct);
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

                    if (!_seen.TryAdd(msgId, 0))
                    {
                        continue;
                    }

                    if (_seen.Count > 50_000)
                    {
                        foreach (var k in _seen.Keys.Take(10_000))
                            _seen.TryRemove(k, out _);
                    }
                }

                if (string.Equals(command, "SERVERLIST", StringComparison.OrdinalIgnoreCase))
                {
                    if (args.Length >= 2)
                    {
                        var name = args[0];
                        var sid = args[1];
                        var parentSid = args.Length >= 3 ? args[2] : session.RemoteSid;

                        _state.TryRegisterRemoteServer(new RemoteServer
                        {
                            ConnectionId = session.ConnectionId,
                            Name = name,
                            Sid = sid,
                            Description = trailing ?? string.Empty,
                            ParentSid = parentSid
                        });
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
                            ts = ChannelTimestamps.NowTs();

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
                            _state.RemoveUser(u.ConnectionId);
                        }

                        if (!string.IsNullOrWhiteSpace(msgId) && !string.IsNullOrWhiteSpace(originSid))
                        {
                            await PropagateRawAsync(session.ConnectionId, $"QUIT {msgId} {originSid} {uid} :{trailing ?? string.Empty}", ct);
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

                        var remoteConnId = $"uid:{uid}";

                        var u = new User
                        {
                            ConnectionId = remoteConnId,
                            Uid = uid,
                            Nick = nick,
                            UserName = userName,
                            Host = host,
                            RealName = trailing,
                            IsRegistered = true,
                            IsRemote = true,
                            RemoteSid = session.RemoteSid
                        };

                        if (!_state.TryAddRemoteUser(u))
                        {
                            await ResolveNickCollisionOnAddAsync(session, u, ct);
                        }
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
                            ch.CreatedTs = remoteTs;
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
    }
}
