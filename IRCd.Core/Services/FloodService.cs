namespace IRCd.Core.Services
{
    using System;
    using System.Collections.Concurrent;
    using System.Linq;
    using System.Net;
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Core.Abstractions;
    using IRCd.Core.State;

    using IRCd.Shared.Options;

    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;

    /// <summary>
    /// Command-level flood control (token-bucket style) with optional escalation.
    /// </summary>
    public sealed class FloodService
    {
        private readonly IOptions<IrcOptions> _options;
        private readonly IServerClock _clock;
        private readonly BanService? _banService;
        private readonly ILogger<FloodService>? _logger;

        private readonly ConcurrentDictionary<string, TokenBucket> _buckets = new();
        private readonly ConcurrentDictionary<string, ViolationState> _violations = new();

        public FloodService(
            IOptions<IrcOptions> options,
            IServerClock clock,
            BanService? banService = null,
            ILogger<FloodService>? logger = null)
        {
            _options = options;
            _clock = clock;
            _banService = banService;
            _logger = logger;
        }

        public async ValueTask<FloodCheckResult> CheckCommandAsync(
            IClientSession session,
            IRCd.Core.Protocol.IrcMessage msg,
            ServerState state,
            CancellationToken ct)
        {
            var cfg = _options.Value.Flood?.Commands;
            if (cfg is null || !cfg.Enabled)
            {
                return default;
            }

            state.TryGetUser(session.ConnectionId, out var user);
            var isOperOrService = user is not null && (!string.IsNullOrWhiteSpace(user.OperName) || user.IsService);

            if (cfg.ExemptOpers && isOperOrService)
            {
                return default;
            }

            var operMultiplier = (!cfg.ExemptOpers && isOperOrService && cfg.OperMultiplier > 1) ? cfg.OperMultiplier : 1;
            var now = _clock.UtcNow;

            var command = msg.Command;
            if (command.Equals("PRIVMSG", StringComparison.OrdinalIgnoreCase)
                || command.Equals("NOTICE", StringComparison.OrdinalIgnoreCase))
            {
                return await CheckMessageAsync(session, msg, cfg, operMultiplier, now, ct);
            }

            if (command.Equals("JOIN", StringComparison.OrdinalIgnoreCase)
                || command.Equals("PART", StringComparison.OrdinalIgnoreCase))
            {
                return await CheckSimpleAsync(session, bucketName: "joinpart", cfg.JoinPart, cfg, operMultiplier, now, ct);
            }

            if (command.Equals("WHO", StringComparison.OrdinalIgnoreCase)
                || command.Equals("WHOIS", StringComparison.OrdinalIgnoreCase))
            {
                return await CheckSimpleAsync(session, bucketName: "who", cfg.WhoWhois, cfg, operMultiplier, now, ct);
            }

            if (command.Equals("MODE", StringComparison.OrdinalIgnoreCase))
            {
                return await CheckSimpleAsync(session, bucketName: "mode", cfg.Mode, cfg, operMultiplier, now, ct);
            }

            if (command.Equals("NICK", StringComparison.OrdinalIgnoreCase))
            {
                return await CheckSimpleAsync(session, bucketName: "nick", cfg.Nick, cfg, operMultiplier, now, ct);
            }

            return default;
        }

        /// <summary>
        /// Clear all flood tracking for a connection
        /// </summary>
        public void ClearConnection(string connectionId)
        {
            var keys = _buckets.Keys.Where(k => k.StartsWith(connectionId + ":", StringComparison.Ordinal)).ToList();
            foreach (var k in keys)
            {
                _buckets.TryRemove(k, out _);
            }

            _violations.TryRemove(connectionId, out _);
        }

        private async ValueTask<FloodCheckResult> CheckMessageAsync(
            IClientSession session,
            IRCd.Core.Protocol.IrcMessage msg,
            CommandFloodOptions cfg,
            int operMultiplier,
            DateTimeOffset now,
            CancellationToken ct)
        {
            var bucketCfg = cfg.Messages;
            if (!bucketCfg.Enabled)
            {
                return default;
            }

            var rawTargets = msg.Params is not null && msg.Params.Count > 0 ? msg.Params[0] : null;
            if (string.IsNullOrWhiteSpace(rawTargets))
            {
                return default;
            }

            FloodCheckResult worst = default;
            foreach (var target in rawTargets.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var key = bucketCfg.PerTarget ? $"{session.ConnectionId}:msg:{target}" : $"{session.ConnectionId}:msg";
                var r = await ConsumeAsync(session, key, bucketName: "message", bucketCfg, cfg, operMultiplier, now, ct);
                if (r.IsFlooding)
                {
                    return r;
                }

                if (r.ShouldWarn)
                {
                    worst = r;
                }
            }

            return worst;
        }

        private ValueTask<FloodCheckResult> CheckSimpleAsync(
            IClientSession session,
            string bucketName,
            CommandFloodBucketOptions bucketCfg,
            CommandFloodOptions cfg,
            int operMultiplier,
            DateTimeOffset now,
            CancellationToken ct)
        {
            if (!bucketCfg.Enabled)
            {
                return ValueTask.FromResult<FloodCheckResult>(default);
            }

            var key = $"{session.ConnectionId}:{bucketName}";
            return ConsumeAsync(session, key, bucketName, bucketCfg, cfg, operMultiplier, now, ct);
        }

        private async ValueTask<FloodCheckResult> ConsumeAsync(
            IClientSession session,
            string key,
            string bucketName,
            CommandFloodBucketOptions bucketCfg,
            CommandFloodOptions cfg,
            int operMultiplier,
            DateTimeOffset now,
            CancellationToken ct)
        {
            var maxEvents = bucketCfg.MaxEvents > 0 ? bucketCfg.MaxEvents : 1;
            var windowSeconds = bucketCfg.WindowSeconds > 0 ? bucketCfg.WindowSeconds : 1;

            var capacity = maxEvents * operMultiplier;
            var refillPerSecond = (double)maxEvents / windowSeconds * operMultiplier;

            var bucket = _buckets.GetOrAdd(key, _ => new TokenBucket(capacity, refillPerSecond, now));
            var allowed = bucket.TryConsume(cost: 1, now, out var retryAfterSeconds, out var shouldWarn);
            if (allowed)
            {
                return shouldWarn ? new FloodCheckResult(false, 0, true) : default;
            }

            var v = _violations.GetOrAdd(session.ConnectionId, _ => new ViolationState());
            var resetSeconds = cfg.ViolationResetSeconds > 0 ? cfg.ViolationResetSeconds : 60;
            var violationCount = v.RegisterViolation(now, resetSeconds);

            var shouldDisconnect = cfg.ViolationsBeforeDisconnect > 0 && violationCount >= cfg.ViolationsBeforeDisconnect;
            var canWarn = v.ShouldSendWarning(now, cfg.WarningCooldownSeconds);

            if (shouldDisconnect)
            {
                await MaybeTempDlineAsync(session, cfg, now, ct);
                _logger?.LogWarning("Flood disconnect: {ConnId} bucket={Bucket}", session.ConnectionId, bucketName);
                return new FloodCheckResult(true, Math.Max(1, retryAfterSeconds), ShouldWarn: canWarn, ShouldDisconnect: true);
            }

            return new FloodCheckResult(true, Math.Max(1, retryAfterSeconds), ShouldWarn: canWarn);
        }

        private async ValueTask MaybeTempDlineAsync(IClientSession session, CommandFloodOptions cfg, DateTimeOffset now, CancellationToken ct)
        {
            if (!cfg.TempDlineEnabled || cfg.TempDlineMinutes <= 0)
            {
                return;
            }

            if (_banService is null)
            {
                return;
            }

            if (session.RemoteEndPoint is not IPEndPoint ipEp)
            {
                return;
            }

            var ip = ipEp.Address;
            if (ip is null)
            {
                return;
            }

            try
            {
                await _banService.AddAsync(new BanEntry
                {
                    Id = Guid.NewGuid(),
                    Type = BanType.DLINE,
                    Mask = ip.ToString(),
                    Reason = "Excess Flood",
                    SetBy = "server",
                    CreatedAt = now,
                    ExpiresAt = now.AddMinutes(cfg.TempDlineMinutes),
                }, ct);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to add temp DLINE for {ConnId}", session.ConnectionId);
            }
        }

        private sealed class TokenBucket
        {
            private readonly object _lock = new();
            private readonly int _capacity;
            private readonly double _refillPerSecond;

            private double _tokens;
            private DateTimeOffset _last;

            public TokenBucket(int capacity, double refillPerSecond, DateTimeOffset now)
            {
                _capacity = Math.Max(1, capacity);
                _refillPerSecond = Math.Max(0.000001, refillPerSecond);
                _tokens = _capacity;
                _last = now;
            }

            public bool TryConsume(int cost, DateTimeOffset now, out int retryAfterSeconds, out bool shouldWarn)
            {
                cost = Math.Max(1, cost);
                lock (_lock)
                {
                    Refill(now);

                    shouldWarn = _tokens <= Math.Max(1.0, _capacity * 0.2);

                    if (_tokens >= cost)
                    {
                        _tokens -= cost;
                        retryAfterSeconds = 0;
                        return true;
                    }

                    var deficit = cost - _tokens;
                    retryAfterSeconds = (int)Math.Ceiling(deficit / _refillPerSecond);
                    if (retryAfterSeconds < 1)
                    {
                        retryAfterSeconds = 1;
                    }

                    return false;
                }
            }

            private void Refill(DateTimeOffset now)
            {
                var dt = (now - _last).TotalSeconds;
                if (dt <= 0)
                {
                    return;
                }

                _tokens = Math.Min(_capacity, _tokens + dt * _refillPerSecond);
                _last = now;
            }
        }

        private sealed class ViolationState
        {
            private readonly object _lock = new();
            private int _count;
            private DateTimeOffset _lastViolation;
            private DateTimeOffset _lastWarning;

            public int RegisterViolation(DateTimeOffset now, int resetSeconds)
            {
                lock (_lock)
                {
                    var rs = resetSeconds > 0 ? TimeSpan.FromSeconds(resetSeconds) : TimeSpan.FromSeconds(60);
                    if (_lastViolation != default && (now - _lastViolation) > rs)
                    {
                        _count = 0;
                    }

                    _lastViolation = now;
                    _count++;
                    return _count;
                }
            }

            public bool ShouldSendWarning(DateTimeOffset now, int cooldownSeconds)
            {
                lock (_lock)
                {
                    var cd = cooldownSeconds > 0 ? TimeSpan.FromSeconds(cooldownSeconds) : TimeSpan.Zero;
                    if (cd == TimeSpan.Zero)
                    {
                        _lastWarning = now;
                        return true;
                    }

                    if (_lastWarning == default || (now - _lastWarning) >= cd)
                    {
                        _lastWarning = now;
                        return true;
                    }

                    return false;
                }
            }
        }
    }

    /// <summary>
    /// Result of a flood check
    /// </summary>
    public readonly record struct FloodCheckResult(bool IsFlooding, int CooldownSeconds, bool ShouldWarn = false, bool ShouldDisconnect = false);
}
