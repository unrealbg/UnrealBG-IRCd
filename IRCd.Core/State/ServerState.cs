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
            if (channel.Contains(connectionId))
            {
                return false;
            }

            if (!channel.TryAddMember(new ChannelMember(connectionId, nick)))
            {
                return false;
            }

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
    }
}
