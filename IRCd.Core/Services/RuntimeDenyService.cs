namespace IRCd.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;

    using IRCd.Shared.Options;

    using Microsoft.Extensions.Options;

    public sealed class RuntimeDenyService
    {
        private readonly IOptions<IrcOptions> _options;
        private readonly object _lock = new();

        public RuntimeDenyService(IOptions<IrcOptions> options)
        {
            _options = options;
        }

        public void AddOrReplace(string mask, string? reason)
        {
            if (string.IsNullOrWhiteSpace(mask))
            {
                return;
            }

            lock (_lock)
            {
                var o = _options.Value;
                var list = new List<DenyOptions>(o.Denies ?? Array.Empty<DenyOptions>());

                var key = mask.Trim();
                var idx = list.FindIndex(d => d is not null && string.Equals(d.Mask?.Trim(), key, StringComparison.OrdinalIgnoreCase));
                var item = new DenyOptions { Mask = key, Reason = string.IsNullOrWhiteSpace(reason) ? "Denied" : reason };

                if (idx >= 0)
                {
                    list[idx] = item;
                }
                else
                {
                    list.Add(item);
                }

                o.Denies = list.ToArray();
            }
        }

        public bool Remove(string mask)
        {
            if (string.IsNullOrWhiteSpace(mask))
            {
                return false;
            }

            lock (_lock)
            {
                var o = _options.Value;
                var list = new List<DenyOptions>(o.Denies ?? Array.Empty<DenyOptions>());
                var before = list.Count;

                var key = mask.Trim();
                list.RemoveAll(d => d is not null && string.Equals(d.Mask?.Trim(), key, StringComparison.OrdinalIgnoreCase));

                o.Denies = list.ToArray();
                return before != list.Count;
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                _options.Value.Denies = Array.Empty<DenyOptions>();
            }
        }

        public bool TryMatch(string nick, string userName, string host, out string reason)
        {
            reason = "Denied";

            var denies = _options.Value.Denies;
            if (denies is null || denies.Length == 0)
            {
                return false;
            }

            var full = $"{nick}!{userName}@{host}";

            foreach (var d in denies)
            {
                if (d is null || string.IsNullOrWhiteSpace(d.Mask))
                {
                    continue;
                }

                var mask = d.Mask.Trim();
                var value = (mask.Contains('!') || mask.Contains('@')) ? full : nick;

                if (MaskMatcher.IsMatch(mask, value))
                {
                    reason = string.IsNullOrWhiteSpace(d.Reason) ? "Denied" : d.Reason;
                    return true;
                }
            }

            return false;
        }
    }
}
