namespace IRCd.Core.State
{
    using System.Collections.Concurrent;

    public sealed class ServerState
    {
        private readonly ConcurrentDictionary<string, string> _nickToConn = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, User> _usersByConn = new();

        private readonly ConcurrentDictionary<string, Channel> _channels = new(StringComparer.OrdinalIgnoreCase);

        private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _userChannels = new();

        public IReadOnlyList<Channel> UpdateNickInUserChannels(string connectionId, string newNick)
        {
            if (!_userChannels.TryGetValue(connectionId, out var set))
            {
                return Array.Empty<Channel>();
            }

            var result = new List<Channel>(capacity: set.Count);

            foreach (var chName in set.Keys)
            {
                if (_channels.TryGetValue(chName, out var ch))
                {
                    ch.TryUpdateMemberNick(connectionId, newNick);
                    result.Add(ch);
                }
            }

            return result;
        }

        public bool TryAddUser(User user)
        {
            var ok = _usersByConn.TryAdd(user.ConnectionId, user);
            _userChannels.TryAdd(user.ConnectionId, new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase));
            return ok;
        }

        public bool TryGetUser(string connectionId, out User? user) => _usersByConn.TryGetValue(connectionId, out user);

        public bool TryGetConnectionIdByNick(string nick, out string? connectionId)
        {
            if (_nickToConn.TryGetValue(nick, out var conn))
            {
                connectionId = conn;
                return true;
            }

            connectionId = null;
            return false;
        }

        public bool TrySetNick(string connectionId, string newNick)
        {
            if (_nickToConn.TryGetValue(newNick, out var existingConn) && existingConn != connectionId)
            {
                return false;
            }

            if (_usersByConn.TryGetValue(connectionId, out var user) && user.Nick is { Length: > 0 } oldNick)
            {
                _nickToConn.TryRemove(oldNick, out _);
            }

            _nickToConn[newNick] = connectionId;

            if (_usersByConn.TryGetValue(connectionId, out user))
            {
                user.Nick = newNick;
            }

            return true;
        }

        public Channel GetOrCreateChannel(string channelName)
            => _channels.GetOrAdd(channelName, name => new Channel(name));

        public bool TryGetChannel(string channelName, out Channel? channel)
            => _channels.TryGetValue(channelName, out channel);

        public IReadOnlyCollection<string> GetUserChannels(string connectionId)
        {
            if (_userChannels.TryGetValue(connectionId, out var set))
            {
                return set.Keys.ToArray();
            }

            return Array.Empty<string>();
        }

        public bool TryJoinChannel(string connectionId, string nick, string channelName)
        {
            var channel = GetOrCreateChannel(channelName);
            if (channel.Contains(connectionId)) return false;

            var priv = !channel.Members.Any()
                ? ChannelPrivilege.Owner
                : ChannelPrivilege.Normal;

            if (!channel.TryAddMember(connectionId, nick, priv))
                return false;

            var set = _userChannels.GetOrAdd(connectionId, _ => new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase));
            set[channelName] = 0;

            return true;
        }

        public bool TryPartChannel(string connectionId, string channelName, out Channel? channel)
        {
            channel = null;

            if (!_channels.TryGetValue(channelName, out var ch))
            {
                return false;
            }

            if (!ch.TryRemoveMember(connectionId, out _))
            {
                return false;
            }

            channel = ch;

            if (_userChannels.TryGetValue(connectionId, out var set))
            {
                set.TryRemove(channelName, out _);
            }

            if (!ch.Members.Any())
            {
                _channels.TryRemove(channelName, out _);
            }

            return true;
        }

        public void RemoveUser(string connectionId)
        {
            if (_userChannels.TryRemove(connectionId, out var set))
            {
                foreach (var chName in set.Keys)
                {
                    if (_channels.TryGetValue(chName, out var ch))
                    {
                        ch.TryRemoveMember(connectionId, out _);
                        if (!ch.Members.Any())
                        {
                            _channels.TryRemove(chName, out _);
                        }
                    }
                }
            }

            if (_usersByConn.TryRemove(connectionId, out var user) && user.Nick is { Length: > 0 } nick)
            {
                _nickToConn.TryRemove(nick, out _);
            }
        }

        public bool TryApplyChannelModes(
                string channelName,
                string actorConnectionId,
                IReadOnlyList<(ChannelModes Mode, bool Enable)> changes,
                out Channel? channel,
                out string? error)
        {
            error = null;
            channel = null;

            if (!_channels.TryGetValue(channelName, out var ch))
            {
                error = "No such channel";
                return false;
            }

            channel = ch;

            if (!ch.Contains(actorConnectionId))
            {
                error = "You're not on that channel";
                return false;
            }

            if (!ch.HasPrivilege(actorConnectionId, ChannelPrivilege.Op))
            {
                error = "You're not channel operator";
                return false;
            }

            var changedAnything = false;
            foreach (var (mode, enable) in changes)
                changedAnything |= ch.ApplyModeChange(mode, enable);

            return changedAnything;
        }

        public ChannelPrivilege GetUserPrivilegeInChannel(string connectionId, string channelName)
        {
            if (_channels.TryGetValue(channelName, out var ch))
                return ch.GetPrivilege(connectionId);

            return ChannelPrivilege.Normal;
        }

        public bool TrySetChannelPrivilege(
    string channelName,
    string actorConnectionId,
    char modeChar,
    bool enable,
    string targetNick,
    out Channel? channel,
    out string? error)
        {
            error = null;
            channel = null;

            if (!_channels.TryGetValue(channelName, out var ch))
            {
                error = "No such channel";
                return false;
            }

            channel = ch;

            if (!ch.Contains(actorConnectionId))
            {
                error = "You're not on that channel";
                return false;
            }

            if (!TryGetConnectionIdByNick(targetNick, out var targetConn) || targetConn is null)
            {
                error = "No such nick";
                return false;
            }

            if (!ch.Contains(targetConn))
            {
                error = "They aren't on that channel";
                return false;
            }

            var actorPriv = ch.GetPrivilege(actorConnectionId);
            var targetPriv = ch.GetPrivilege(targetConn);

            var privToSet = modeChar switch
            {
                'v' => ChannelPrivilege.Voice,
                'h' => ChannelPrivilege.HalfOp,
                'o' => ChannelPrivilege.Op,
                'a' => ChannelPrivilege.Admin,
                'q' => ChannelPrivilege.Owner,
                _ => ChannelPrivilege.Normal
            };

            if (privToSet == ChannelPrivilege.Normal)
            {
                error = "Unknown mode";
                return false;
            }

            var required = modeChar switch
            {
                'v' or 'h' or 'o' => ChannelPrivilege.Op,
                'a' => ChannelPrivilege.Admin,
                'q' => ChannelPrivilege.Owner,
                _ => ChannelPrivilege.Op
            };

            if (!actorPriv.IsAtLeast(required))
            {
                error = "You're not channel operator";
                return false;
            }

            if (enable && privToSet > actorPriv)
            {
                error = "Insufficient privilege";
                return false;
            }

            ChannelPrivilege newPriv;
            if (enable)
            {
                newPriv = targetPriv < privToSet ? privToSet : targetPriv;
            }
            else
            {
                newPriv = targetPriv == privToSet ? ChannelPrivilege.Normal : targetPriv;
            }

            if (!enable && targetPriv > actorPriv)
            {
                error = "Insufficient privilege";
                return false;
            }

            return ch.TryUpdateMemberPrivilege(targetConn, newPriv);
        }

        public IEnumerable<Channel> GetAllChannels()
        {
            return _channels.Values;
        }

        public IReadOnlyCollection<User> GetAllUsers()
                 => _usersByConn.Values.ToArray();

        public int UserCount => _usersByConn.Count;

        public DateTimeOffset CreatedUtc { get; } = DateTimeOffset.UtcNow;
    }
}
