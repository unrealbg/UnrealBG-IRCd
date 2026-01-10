namespace IRCd.Core.Services
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Core.Abstractions;
    using IRCd.Core.State;
    using IRCd.Shared.Options;

    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;

    public sealed class AutoDlineService : IAutoDlineMetrics
    {
        private readonly IOptionsMonitor<IrcOptions> _options;
        private readonly IServerClock _clock;
        private readonly BanService _bans;
        private readonly ILogger<AutoDlineService>? _logger;

        private readonly ConcurrentDictionary<string, OffenseState> _states = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, long> _offenseCountsByPrefix = new(StringComparer.Ordinal);

        private long _autoDlinesTotal;

        public AutoDlineService(
            IOptionsMonitor<IrcOptions> options,
            IServerClock clock,
            BanService bans,
            ILogger<AutoDlineService>? logger = null)
        {
            _options = options;
            _clock = clock;
            _bans = bans;
            _logger = logger;
        }

        public long AutoDlinesTotal => System.Threading.Interlocked.Read(ref _autoDlinesTotal);

        public IReadOnlyList<(string Prefix, long OffenseCount)> GetTopOffenders(int topN)
        {
            topN = Math.Max(0, topN);
            if (topN == 0)
            {
                return Array.Empty<(string, long)>();
            }

            return _offenseCountsByPrefix
                .OrderByDescending(kvp => kvp.Value)
                .ThenBy(kvp => kvp.Key, StringComparer.Ordinal)
                .Take(topN)
                .Select(kvp => (kvp.Key, kvp.Value))
                .ToArray();
        }

        public async ValueTask<bool> ObserveRateLimitAsync(IClientSession session, ServerState state, CancellationToken ct)
            => await ObserveAsync(session, state, reason: "RateLimit", ct);

        public async ValueTask<bool> ObserveFloodAsync(IClientSession session, ServerState state, CancellationToken ct)
            => await ObserveAsync(session, state, reason: "Flood", ct);

        private async ValueTask<bool> ObserveAsync(IClientSession session, ServerState state, string reason, CancellationToken ct)
        {
            var cfg = _options.CurrentValue.AutoDline;
            if (!cfg.Enabled)
            {
                return false;
            }

            if (IsExemptOper(session, state))
            {
                return false;
            }

            if (session.RemoteEndPoint is not IPEndPoint ipEp)
            {
                return false;
            }

            var ip = ipEp.Address;
            if (ip is null)
            {
                return false;
            }

            if (IsWhitelisted(ip, cfg.WhitelistCidrs))
            {
                return false;
            }

            var now = _clock.UtcNow;

            IncrementOffenseAggregate(ip);

            var key = ip.ToString();
            var s = _states.GetOrAdd(key, _ => new OffenseState(now));
            var applied = await s.RegisterOffenseAndMaybeDlineAsync(
                ip,
                cfg,
                reason,
                now,
                _bans,
                _logger,
                () => System.Threading.Interlocked.Increment(ref _autoDlinesTotal),
                ct);

            if (_states.Count > 10_000)
            {
                TryCleanupOldStates(now, cfg.WindowSeconds);
            }

            return applied;
        }

        private static bool IsExemptOper(IClientSession session, ServerState state)
        {
            if (!state.TryGetUser(session.ConnectionId, out var user) || user is null)
            {
                return false;
            }

            return user.IsService
                || !string.IsNullOrWhiteSpace(user.OperName)
                || user.Modes.HasFlag(UserModes.Operator);
        }

        private static bool IsWhitelisted(IPAddress ip, string[] whitelistCidrs)
        {
            if (whitelistCidrs is null || whitelistCidrs.Length == 0)
            {
                return false;
            }

            foreach (var entry in whitelistCidrs)
            {
                if (IpMaskMatcher.MatchesIpOrCidr(ip, entry))
                {
                    return true;
                }
            }

            return false;
        }

        private void IncrementOffenseAggregate(IPAddress ip)
        {
            var prefix = ToAggregatePrefix(ip);
            _offenseCountsByPrefix.AddOrUpdate(prefix, 1, (_, old) => old + 1);
        }

        private static string ToAggregatePrefix(IPAddress ip)
        {
            if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                var b = ip.GetAddressBytes();
                if (b.Length != 4)
                {
                    return ip + "/32";
                }

                return $"{b[0]}.{b[1]}.{b[2]}.0/24";
            }

            if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
            {
                var b = ip.GetAddressBytes();
                if (b.Length != 16)
                {
                    return ip + "/128";
                }

                for (var i = 8; i < 16; i++)
                {
                    b[i] = 0;
                }

                return new IPAddress(b) + "/64";
            }

            return ip.ToString();
        }

        private void TryCleanupOldStates(DateTimeOffset now, int windowSeconds)
        {
            var cutoff = now - TimeSpan.FromSeconds(Math.Max(1, windowSeconds) * 10);
            foreach (var kvp in _states)
            {
                if (kvp.Value.LastOffenseUtc < cutoff)
                {
                    _states.TryRemove(kvp.Key, out _);
                }
            }
        }

        private sealed class OffenseState
        {
            private readonly object _lock = new();

            private DateTimeOffset _windowStartUtc;
            private int _score;

            private int _strikeCount;
            private DateTimeOffset? _lastAppliedExpiresAt;

            public DateTimeOffset LastOffenseUtc { get; private set; }

            public OffenseState(DateTimeOffset now)
            {
                _windowStartUtc = now;
                LastOffenseUtc = now;
                _score = 0;
            }

            public async ValueTask<bool> RegisterOffenseAndMaybeDlineAsync(
                IPAddress ip,
                AutoDlineOptions cfg,
                string reason,
                DateTimeOffset now,
                BanService bans,
                ILogger<AutoDlineService>? logger,
                Action incrementTotal,
                CancellationToken ct)
            {
                int durationSeconds;
                bool shouldApply;

                lock (_lock)
                {
                    LastOffenseUtc = now;

                    var window = TimeSpan.FromSeconds(Math.Max(1, cfg.WindowSeconds));
                    if ((now - _windowStartUtc) > window)
                    {
                        _windowStartUtc = now;
                        _score = 0;
                    }

                    _score++;
                    shouldApply = cfg.Threshold > 0 && _score >= cfg.Threshold;

                    if (!shouldApply)
                    {
                        return false;
                    }

                    _score = 0;

                    _strikeCount++;

                    var baseSec = Math.Max(1, cfg.BaseDurationSeconds);
                    var factor = Math.Max(1, cfg.BackoffFactor);
                    var maxSec = Math.Max(baseSec, cfg.MaxDurationSeconds);

                    try
                    {
                        checked
                        {
                            var pow = 1L;
                            for (var i = 1; i < _strikeCount; i++)
                            {
                                pow *= factor;
                            }

                            var computed = (long)baseSec * pow;
                            durationSeconds = (int)Math.Min(maxSec, Math.Max(1, computed));
                        }
                    }
                    catch
                    {
                        durationSeconds = Math.Max(1, cfg.MaxDurationSeconds);
                    }

                    if (_lastAppliedExpiresAt is not null)
                    {
                        var remaining = _lastAppliedExpiresAt.Value - now;
                        if (remaining.TotalSeconds >= durationSeconds - 1)
                        {
                            return false;
                        }
                    }

                    _lastAppliedExpiresAt = now.AddSeconds(durationSeconds);
                }

                try
                {
                    var mask = ip.ToString();

                    await bans.RemoveAsync(BanType.DLINE, mask, ct);

                    await bans.AddAsync(new BanEntry
                    {
                        Id = Guid.NewGuid(),
                        Type = BanType.DLINE,
                        Mask = mask,
                        Reason = $"Auto DLINE ({reason})",
                        SetBy = "server",
                        CreatedAt = now,
                        ExpiresAt = now.AddSeconds(durationSeconds),
                    }, ct);

                    incrementTotal();
                    logger?.LogWarning("Auto DLINE applied for {Ip} duration={Seconds}s reason={Reason}", mask, durationSeconds, reason);
                    return true;
                }
                catch (Exception ex)
                {
                    logger?.LogWarning(ex, "Failed to apply Auto DLINE for {Ip}", ip);
                    return false;
                }
            }
        }
    }
}
