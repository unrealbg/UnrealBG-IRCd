namespace IRCd.Core.State
{
    using System.Collections.Concurrent;

    public sealed class Channel
    {
        private readonly ConcurrentDictionary<string, ChannelMember> _members =
            new(StringComparer.OrdinalIgnoreCase);

        private readonly List<ChannelBan> _bans = new();
        private readonly List<ChannelBan> _exceptBans = new();
        private readonly List<ChannelBan> _inviteExceptions = new();

        public string? Key { get; private set; }
        public int? UserLimit { get; private set; }

        private readonly HashSet<string> _invitedNicks = new(StringComparer.OrdinalIgnoreCase);

        public Channel(string name)
        {
            Name = name;
            Modes = ChannelModes.NoExternalMessages | ChannelModes.TopicOpsOnly; // +nt
            CreatedTs = ChannelTimestamps.NowTs();
        }

        public string Name { get; }

        public long CreatedTs { get; set; }

        public string? Topic { get; set; }

        public string? TopicSetBy { get; private set; }

        public DateTimeOffset? TopicSetAtUtc { get; private set; }

        public long TopicTs { get; private set; }

        public ChannelModes Modes { get; private set; }

        public IReadOnlyCollection<ChannelMember> Members => _members.Values.ToArray();

        public bool Contains(string connectionId) => _members.ContainsKey(connectionId);

        public ChannelPrivilege GetPrivilege(string connectionId)
            => _members.TryGetValue(connectionId, out var m) ? m.Privilege : ChannelPrivilege.Normal;

        public bool HasPrivilege(string connectionId, ChannelPrivilege minimum)
            => _members.TryGetValue(connectionId, out var m) && m.Privilege.IsAtLeast(minimum);

        public bool TryAddMember(string connectionId, string nick, ChannelPrivilege privilege)
            => _members.TryAdd(connectionId, new ChannelMember(connectionId, nick, privilege));

        public bool TryRemoveMember(string connectionId, out ChannelMember? removed)
            => _members.TryRemove(connectionId, out removed);

        public bool TryUpdateMemberNick(string connectionId, string newNick)
        {
            if (!_members.TryGetValue(connectionId, out var existing))
                return false;

            var updated = existing with { Nick = newNick };
            return _members.TryUpdate(connectionId, updated, existing);
        }

        public bool TryUpdateMemberPrivilege(string connectionId, ChannelPrivilege newPrivilege)
        {
            if (!_members.TryGetValue(connectionId, out var existing))
                return false;

            var updated = existing with { Privilege = newPrivilege };
            return _members.TryUpdate(connectionId, updated, existing);
        }

        public bool ApplyModeChange(ChannelModes mode, bool enable)
        {
            var before = Modes;
            Modes = enable ? (Modes | mode) : (Modes & ~mode);
            return before != Modes;
        }

        public string FormatModeString()
        {
            var flags = new List<char>();

            if (Modes.HasFlag(ChannelModes.NoExternalMessages))
            {
                flags.Add('n');
            }

            if (Modes.HasFlag(ChannelModes.TopicOpsOnly))
            {
                flags.Add('t');
            }

            if (Modes.HasFlag(ChannelModes.InviteOnly))
            {
                flags.Add('i');
            }

            if (Modes.HasFlag(ChannelModes.Key))
            {
                flags.Add('k');
            }

            if (Modes.HasFlag(ChannelModes.Limit))
            {
                flags.Add('l');
            }

            if (Modes.HasFlag(ChannelModes.Moderated))
            {
                flags.Add('m');
            }

            if (Modes.HasFlag(ChannelModes.Private))
            {
                flags.Add('p');
            }

            if (Modes.HasFlag(ChannelModes.Secret))
            {
                flags.Add('s');
            }

            return flags.Count == 0 ? "+" : "+" + new string(flags.ToArray());
        }

        public IReadOnlyList<ChannelBan> Bans
        {
            get { lock (_bans) return _bans.ToList(); }
        }

        public IReadOnlyList<ChannelBan> ExceptBans
        {
            get { lock (_exceptBans) return _exceptBans.ToList(); }
        }

        public IReadOnlyList<ChannelBan> InviteExceptions
        {
            get { lock (_inviteExceptions) return _inviteExceptions.ToList(); }
        }

        public bool AddBan(string mask, string setBy)
        {
            lock (_bans)
            {
                if (_bans.Any(b => string.Equals(b.Mask, mask, StringComparison.OrdinalIgnoreCase)))
                    return false;

                _bans.Add(new ChannelBan(mask, setBy, DateTimeOffset.UtcNow));
                return true;
            }
        }

        public bool AddBan(string mask, string setBy, DateTimeOffset setAtUtc)
        {
            lock (_bans)
            {
                if (_bans.Any(b => string.Equals(b.Mask, mask, StringComparison.OrdinalIgnoreCase)))
                    return false;

                _bans.Add(new ChannelBan(mask, setBy, setAtUtc));
                return true;
            }
        }

        public bool RemoveBan(string mask)
        {
            lock (_bans)
            {
                var idx = _bans.FindIndex(b => string.Equals(b.Mask, mask, StringComparison.OrdinalIgnoreCase));
                if (idx < 0) return false;
                _bans.RemoveAt(idx);
                return true;
            }
        }

        public bool AddExceptBan(string mask, string setBy)
        {
            lock (_exceptBans)
            {
                if (_exceptBans.Any(b => string.Equals(b.Mask, mask, StringComparison.OrdinalIgnoreCase)))
                    return false;

                _exceptBans.Add(new ChannelBan(mask, setBy, DateTimeOffset.UtcNow));
                return true;
            }
        }

        public bool AddExceptBan(string mask, string setBy, DateTimeOffset setAtUtc)
        {
            lock (_exceptBans)
            {
                if (_exceptBans.Any(b => string.Equals(b.Mask, mask, StringComparison.OrdinalIgnoreCase)))
                    return false;

                _exceptBans.Add(new ChannelBan(mask, setBy, setAtUtc));
                return true;
            }
        }

        public bool RemoveExceptBan(string mask)
        {
            lock (_exceptBans)
            {
                var idx = _exceptBans.FindIndex(b => string.Equals(b.Mask, mask, StringComparison.OrdinalIgnoreCase));
                if (idx < 0) return false;
                _exceptBans.RemoveAt(idx);
                return true;
            }
        }

        public bool AddInviteException(string mask, string setBy)
        {
            lock (_inviteExceptions)
            {
                if (_inviteExceptions.Any(b => string.Equals(b.Mask, mask, StringComparison.OrdinalIgnoreCase)))
                    return false;

                _inviteExceptions.Add(new ChannelBan(mask, setBy, DateTimeOffset.UtcNow));
                return true;
            }
        }

        public bool AddInviteException(string mask, string setBy, DateTimeOffset setAtUtc)
        {
            lock (_inviteExceptions)
            {
                if (_inviteExceptions.Any(b => string.Equals(b.Mask, mask, StringComparison.OrdinalIgnoreCase)))
                    return false;

                _inviteExceptions.Add(new ChannelBan(mask, setBy, setAtUtc));
                return true;
            }
        }

        public bool RemoveInviteException(string mask)
        {
            lock (_inviteExceptions)
            {
                var idx = _inviteExceptions.FindIndex(b => string.Equals(b.Mask, mask, StringComparison.OrdinalIgnoreCase));
                if (idx < 0) return false;
                _inviteExceptions.RemoveAt(idx);
                return true;
            }
        }

        public bool IsInvited(string nick)
        {
            lock (_invitedNicks) return _invitedNicks.Contains(nick);
        }

        public void AddInvite(string nick)
        {
            lock (_invitedNicks) _invitedNicks.Add(nick);
        }

        public void RemoveInvite(string nick)
        {
            lock (_invitedNicks) _invitedNicks.Remove(nick);
        }

        public void SetKey(string? key)
        {
            Key = string.IsNullOrWhiteSpace(key) ? null : key;
            ApplyModeChange(ChannelModes.Key, Key is not null);
        }

        public void ClearKey() => SetKey(null);

        public void SetLimit(int? limit)
        {
            UserLimit = (limit.HasValue && limit.Value > 0) ? limit.Value : null;
            ApplyModeChange(ChannelModes.Limit, UserLimit is not null);
        }

        public void ClearLimit() => SetLimit(null);

        public void SetTopic(string? topic, string setBy)
        {
            Topic = string.IsNullOrWhiteSpace(topic) ? null : topic;
            TopicSetBy = string.IsNullOrWhiteSpace(setBy) ? null : setBy;
            TopicSetAtUtc = DateTimeOffset.UtcNow;
            TopicTs = ChannelTimestamps.NowTs();
        }

        public bool TrySetTopicWithTs(string? topic, string setBy, long ts)
        {
            if (ts <= 0)
                ts = ChannelTimestamps.NowTs();

            if (ts < TopicTs)
                return false;

            Topic = string.IsNullOrWhiteSpace(topic) ? null : topic;
            TopicSetBy = string.IsNullOrWhiteSpace(setBy) ? null : setBy;
            TopicSetAtUtc = DateTimeOffset.FromUnixTimeSeconds(ts);
            TopicTs = ts;
            return true;
        }

        public void ResetForTsCollision(long newCreatedTs)
        {
            if (newCreatedTs <= 0)
            {
                newCreatedTs = ChannelTimestamps.NowTs();
            }

            CreatedTs = newCreatedTs;

            Modes = ChannelModes.NoExternalMessages | ChannelModes.TopicOpsOnly; // +nt baseline
            Key = null;
            UserLimit = null;

            Topic = null;
            TopicSetBy = null;
            TopicSetAtUtc = null;
            TopicTs = 0;

            lock (_bans)
            {
                _bans.Clear();
            }

            lock (_exceptBans)
            {
                _exceptBans.Clear();
            }

            lock (_inviteExceptions)
            {
                _inviteExceptions.Clear();
            }

            lock (_invitedNicks)
            {
                _invitedNicks.Clear();
            }

            foreach (var kv in _members.ToArray())
            {
                var existing = kv.Value;
                if (existing.Privilege != ChannelPrivilege.Normal)
                {
                    var updated = existing with { Privilege = ChannelPrivilege.Normal };
                    _members.TryUpdate(kv.Key, updated, existing);
                }
            }
        }
    }

    public sealed record ChannelMember(string ConnectionId, string Nick, ChannelPrivilege Privilege);
}
