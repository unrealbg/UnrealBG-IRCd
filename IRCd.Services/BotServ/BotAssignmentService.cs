namespace IRCd.Services.BotServ
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;

    public sealed class BotAssignmentService
    {
        private readonly ConcurrentDictionary<string, string> _channelToBot = new(StringComparer.OrdinalIgnoreCase);

        public bool TryGetAssignedBot(string channelName, out string? botNick)
        {
            if (string.IsNullOrWhiteSpace(channelName))
            {
                botNick = null;
                return false;
            }

            return _channelToBot.TryGetValue(channelName.Trim(), out botNick);
        }

        public void Assign(string channelName, string botNick)
        {
            if (string.IsNullOrWhiteSpace(channelName) || string.IsNullOrWhiteSpace(botNick))
            {
                return;
            }

            _channelToBot[channelName.Trim()] = botNick.Trim();
        }

        public bool Unassign(string channelName)
        {
            if (string.IsNullOrWhiteSpace(channelName))
            {
                return false;
            }

            return _channelToBot.TryRemove(channelName.Trim(), out _);
        }

        public IReadOnlyDictionary<string, string> Snapshot()
            => new Dictionary<string, string>(_channelToBot, StringComparer.OrdinalIgnoreCase);
    }
}
