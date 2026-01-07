namespace IRCd.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;

    using IRCd.Shared.Options;

    using Microsoft.Extensions.Options;

    public sealed class RuntimeKLineService
    {
        private readonly IOptions<IrcOptions> _options;
        private readonly object _lock = new();

        public RuntimeKLineService(IOptions<IrcOptions> options)
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
                var list = new List<KLineOptions>(o.KLines ?? Array.Empty<KLineOptions>());

                var existingIdx = list.FindIndex(k => k is not null && string.Equals(k.Mask?.Trim(), mask.Trim(), StringComparison.OrdinalIgnoreCase));
                var kl = new KLineOptions { Mask = mask.Trim(), Reason = string.IsNullOrWhiteSpace(reason) ? "Banned" : reason };

                if (existingIdx >= 0)
                {
                    list[existingIdx] = kl;
                }
                else
                {
                    list.Add(kl);
                }

                o.KLines = list.ToArray();
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
                var list = new List<KLineOptions>(o.KLines ?? Array.Empty<KLineOptions>());
                var before = list.Count;

                list.RemoveAll(k => k is not null && string.Equals(k.Mask?.Trim(), mask.Trim(), StringComparison.OrdinalIgnoreCase));

                o.KLines = list.ToArray();
                return before != list.Count;
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                _options.Value.KLines = Array.Empty<KLineOptions>();
            }
        }

        public bool TryMatch(string nick, string userName, string host, out string reason)
        {
            reason = "Banned";

            var klines = _options.Value.KLines;
            if (klines is null || klines.Length == 0)
            {
                return false;
            }

            var full = $"{nick}!{userName}@{host}";

            foreach (var k in klines)
            {
                if (k is null || string.IsNullOrWhiteSpace(k.Mask))
                {
                    continue;
                }

                var mask = k.Mask.Trim();
                var value = (mask.Contains('!') || mask.Contains('@')) ? full : host;

                if (MaskMatcher.IsMatch(mask, value))
                {
                    reason = string.IsNullOrWhiteSpace(k.Reason) ? "Banned" : k.Reason;
                    return true;
                }
            }

            return false;
        }
    }
}
