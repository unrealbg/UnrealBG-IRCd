namespace IRCd.Core.Services
{
    using System;
    using System.Collections.Generic;

    using IRCd.Shared.Options;

    using Microsoft.Extensions.Options;

    public sealed class RuntimeDLineService
    {
        private readonly IOptions<IrcOptions> _options;
        private readonly object _lock = new();

        public RuntimeDLineService(IOptions<IrcOptions> options)
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
                var list = new List<DLineOptions>(o.DLines ?? Array.Empty<DLineOptions>());

                var trimmed = mask.Trim();
                var existingIdx = list.FindIndex(d => d is not null && string.Equals(d.Mask?.Trim(), trimmed, StringComparison.OrdinalIgnoreCase));
                var dl = new DLineOptions { Mask = trimmed, Reason = string.IsNullOrWhiteSpace(reason) ? "Banned" : reason };

                if (existingIdx >= 0)
                {
                    list[existingIdx] = dl;
                }
                else
                {
                    list.Add(dl);
                }

                o.DLines = list.ToArray();
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
                var list = new List<DLineOptions>(o.DLines ?? Array.Empty<DLineOptions>());
                var before = list.Count;

                list.RemoveAll(d => d is not null && string.Equals(d.Mask?.Trim(), mask.Trim(), StringComparison.OrdinalIgnoreCase));

                o.DLines = list.ToArray();
                return before != list.Count;
            }
        }

        public bool TryMatch(string remoteIp, out string reason)
        {
            reason = "Banned";

            if (string.IsNullOrWhiteSpace(remoteIp))
            {
                return false;
            }

            var dlines = _options.Value.DLines;
            if (dlines is null || dlines.Length == 0)
            {
                return false;
            }

            var value = remoteIp.Trim();

            foreach (var d in dlines)
            {
                if (d is null || string.IsNullOrWhiteSpace(d.Mask))
                {
                    continue;
                }

                var mask = d.Mask.Trim();
                if (MaskMatcher.IsMatch(mask, value))
                {
                    reason = string.IsNullOrWhiteSpace(d.Reason) ? "Banned" : d.Reason;
                    return true;
                }
            }

            return false;
        }
    }
}
