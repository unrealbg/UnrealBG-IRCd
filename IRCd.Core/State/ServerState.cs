namespace IRCd.Core.State
{
    using System.Collections.Concurrent;

    public sealed class ServerState
    {
        private readonly ConcurrentDictionary<string, string> _nickToConn = new(StringComparer.OrdinalIgnoreCase);

        private readonly ConcurrentDictionary<string, User> _usersByConn = new();

        public bool TryAddUser(User user) => _usersByConn.TryAdd(user.ConnectionId, user);

        public bool TryGetUser(string connectionId, out User? user) => _usersByConn.TryGetValue(connectionId, out user);

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
    }
}
