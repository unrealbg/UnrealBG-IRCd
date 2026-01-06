namespace IRCd.Core.Services
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Linq;

    public sealed class SilenceService
    {
        private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _byConn = new(StringComparer.Ordinal);

        public IReadOnlyCollection<string> GetList(string connectionId)
        {
            if (string.IsNullOrWhiteSpace(connectionId))
            {
                return Array.Empty<string>();
            }

            if (_byConn.TryGetValue(connectionId, out var set))
            {
                return set.Keys.ToArray();
            }

            return Array.Empty<string>();
        }

        public bool TryAdd(string connectionId, string mask, int maxEntries)
        {
            if (string.IsNullOrWhiteSpace(connectionId) || string.IsNullOrWhiteSpace(mask))
            {
                return false;
            }

            var set = _byConn.GetOrAdd(connectionId, _ => new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase));
            if (set.Count >= maxEntries && !set.ContainsKey(mask))
            {
                return false;
            }

            set[mask] = 0;
            return true;
        }

        public bool Remove(string connectionId, string mask)
        {
            if (string.IsNullOrWhiteSpace(connectionId) || string.IsNullOrWhiteSpace(mask))
            {
                return false;
            }

            if (_byConn.TryGetValue(connectionId, out var set))
            {
                return set.TryRemove(mask, out _);
            }

            return false;
        }

        public void RemoveAll(string connectionId)
        {
            if (string.IsNullOrWhiteSpace(connectionId))
            {
                return;
            }

            _byConn.TryRemove(connectionId, out _);
        }

        public bool IsSilenced(string connectionId, string senderHostmask)
        {
            if (string.IsNullOrWhiteSpace(connectionId) || string.IsNullOrWhiteSpace(senderHostmask))
            {
                return false;
            }

            if (!_byConn.TryGetValue(connectionId, out var set))
            {
                return false;
            }

            foreach (var mask in set.Keys)
            {
                if (MaskMatcher.IsMatch(mask, senderHostmask))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
