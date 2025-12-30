namespace IRCd.Core.State
{
    using System.Collections.Concurrent;

    public sealed class Channel
    {
        public Channel(string name)
        {
            Name = name;
        }

        public string Name { get; }
        public string? Topic { get; set; }

        // connectionId -> membership
        private readonly ConcurrentDictionary<string, ChannelMember> _members = new();

        public IReadOnlyCollection<ChannelMember> Members => (IReadOnlyCollection<ChannelMember>)_members.Values;

        public bool TryAddMember(ChannelMember member) => _members.TryAdd(member.ConnectionId, member);

        public bool TryRemoveMember(string connectionId, out ChannelMember? removed)
        {
            if (_members.TryRemove(connectionId, out var m))
            {
                removed = m;
                return true;
            }

            removed = null;
            return false;
        }

        public bool TryUpdateMemberNick(string connectionId, string newNick)
        {
            if (!_members.TryGetValue(connectionId, out var existing))
                return false;

            var updated = existing with { Nick = newNick };
            return _members.TryUpdate(connectionId, updated, existing);
        }

        public bool Contains(string connectionId) => _members.ContainsKey(connectionId);
    }

    public sealed record ChannelMember(string ConnectionId, string Nick);
}
