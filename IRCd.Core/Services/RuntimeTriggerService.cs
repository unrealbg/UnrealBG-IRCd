namespace IRCd.Core.Services
{
    using System;
    using System.Collections.Generic;

    using IRCd.Shared.Options;

    using Microsoft.Extensions.Options;

    public sealed class RuntimeTriggerService
    {
        private readonly IOptions<IrcOptions> _options;
        private readonly object _lock = new();

        public RuntimeTriggerService(IOptions<IrcOptions> options)
        {
            _options = options;
        }

        public void AddOrReplace(string pattern, string? response)
        {
            if (string.IsNullOrWhiteSpace(pattern))
            {
                return;
            }

            lock (_lock)
            {
                var o = _options.Value;
                var list = new List<TriggerOptions>(o.Triggers ?? Array.Empty<TriggerOptions>());

                var key = pattern.Trim();
                var idx = list.FindIndex(t => t is not null && string.Equals(t.Pattern?.Trim(), key, StringComparison.OrdinalIgnoreCase));
                var item = new TriggerOptions { Pattern = key, Response = string.IsNullOrWhiteSpace(response) ? "" : response };

                if (idx >= 0)
                {
                    list[idx] = item;
                }
                else
                {
                    list.Add(item);
                }

                o.Triggers = list.ToArray();
            }
        }

        public bool Remove(string pattern)
        {
            if (string.IsNullOrWhiteSpace(pattern))
            {
                return false;
            }

            lock (_lock)
            {
                var o = _options.Value;
                var list = new List<TriggerOptions>(o.Triggers ?? Array.Empty<TriggerOptions>());
                var before = list.Count;

                var key = pattern.Trim();
                list.RemoveAll(t => t is not null && string.Equals(t.Pattern?.Trim(), key, StringComparison.OrdinalIgnoreCase));

                o.Triggers = list.ToArray();
                return before != list.Count;
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                _options.Value.Triggers = Array.Empty<TriggerOptions>();
            }
        }
    }
}
