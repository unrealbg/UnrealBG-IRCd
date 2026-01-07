namespace IRCd.Core.Services
{
    using System;
    using System.Collections.Generic;

    using IRCd.Shared.Options;

    using Microsoft.Extensions.Options;

    public sealed class RuntimeWarnService
    {
        private readonly IOptions<IrcOptions> _options;
        private readonly object _lock = new();

        public RuntimeWarnService(IOptions<IrcOptions> options)
        {
            _options = options;
        }

        public void AddOrReplace(string mask, string? message)
        {
            if (string.IsNullOrWhiteSpace(mask))
            {
                return;
            }

            lock (_lock)
            {
                var o = _options.Value;
                var list = new List<WarnOptions>(o.Warns ?? Array.Empty<WarnOptions>());

                var key = mask.Trim();
                var idx = list.FindIndex(w => w is not null && string.Equals(w.Mask?.Trim(), key, StringComparison.OrdinalIgnoreCase));
                var item = new WarnOptions { Mask = key, Message = string.IsNullOrWhiteSpace(message) ? "Warning" : message };

                if (idx >= 0)
                {
                    list[idx] = item;
                }
                else
                {
                    list.Add(item);
                }

                o.Warns = list.ToArray();
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
                var list = new List<WarnOptions>(o.Warns ?? Array.Empty<WarnOptions>());
                var before = list.Count;

                var key = mask.Trim();
                list.RemoveAll(w => w is not null && string.Equals(w.Mask?.Trim(), key, StringComparison.OrdinalIgnoreCase));

                o.Warns = list.ToArray();
                return before != list.Count;
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                _options.Value.Warns = Array.Empty<WarnOptions>();
            }
        }
    }
}
