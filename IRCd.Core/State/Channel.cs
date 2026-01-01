namespace IRCd.Core.State
{
    using System.Collections.Concurrent;

    public sealed class Channel
    {
        private readonly ConcurrentDictionary<string, ChannelMember> _members =
            new(StringComparer.OrdinalIgnoreCase);

        private readonly List<ChannelBan> _bans = new();

        public Channel(string name)
        {
            Name = name;
            Modes = ChannelModes.NoExternalMessages | ChannelModes.TopicOpsOnly; // +nt
        }

        public string Name { get; }
        public string? Topic { get; set; }

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
            if (Modes.HasFlag(ChannelModes.NoExternalMessages)) flags.Add('n');
            if (Modes.HasFlag(ChannelModes.TopicOpsOnly)) flags.Add('t');
            return flags.Count == 0 ? "+" : "+" + new string(flags.ToArray());
        }

        public IReadOnlyList<ChannelBan> Bans
        {
            get { lock (_bans) return _bans.ToList(); }
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
    }

    public sealed record ChannelMember(string ConnectionId, string Nick, ChannelPrivilege Privilege);
}
