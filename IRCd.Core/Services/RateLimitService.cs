namespace IRCd.Core.Services
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;

    using IRCd.Shared.Options;

    using Microsoft.Extensions.Options;

    public sealed class RateLimitService
    {
        private readonly IOptionsMonitor<IrcOptions> _options;

        private readonly ConcurrentDictionary<string, TokenBucket> _buckets = new();
        private readonly ConcurrentDictionary<string, ViolationWindow> _violations = new();

        private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _connToBucketKeys = new();

        public RateLimitService(IOptionsMonitor<IrcOptions> options)
        {
            _options = options;
        }

        public bool Enabled => _options.CurrentValue.RateLimit.Enabled;

        public bool TryConsume(string connectionId, string commandKey, out int retryAfterSeconds)
        {
            retryAfterSeconds = 0;

            var rule = GetRule(commandKey);
            var bucketKey = connectionId + "|" + commandKey;

            var bucket = _buckets.GetOrAdd(bucketKey, _ =>
            {
                IndexBucketKey(connectionId, bucketKey);
                return new TokenBucket(rule);
            });

            return bucket.TryConsume(out retryAfterSeconds);
        }

        public bool RegisterViolationAndShouldDisconnect(string connectionId)
        {
            var cfg = _options.CurrentValue.RateLimit.Disconnect;
            if (!cfg.Enabled)
            {
                return false;
            }

            var window = _violations.GetOrAdd(connectionId, _ => new ViolationWindow(cfg.WindowSeconds));
            var count = window.AddViolationAndGetCount(cfg.WindowSeconds);

            return count >= cfg.MaxViolations;
        }

        public string GetDisconnectQuitMessage()
            => _options.CurrentValue.RateLimit.Disconnect.QuitMessage;

        public void ClearConnection(string connectionId)
        {
            if (_connToBucketKeys.TryRemove(connectionId, out var keys))
            {
                foreach (var kvp in keys.Keys)
                {
                    _buckets.TryRemove(kvp, out _);
                }
            }

            _violations.TryRemove(connectionId, out _);
        }

        private void IndexBucketKey(string connectionId, string bucketKey)
        {
            var dict = _connToBucketKeys.GetOrAdd(connectionId, _ => new ConcurrentDictionary<string, byte>());
            dict.TryAdd(bucketKey, 0);
        }

        private RateLimitRule GetRule(string key)
        {
            var rl = _options.CurrentValue.RateLimit;

            TokenBucketOptions opt = key switch
            {
                "PRIVMSG" => rl.PrivMsg,
                "NOTICE" => rl.Notice,

                "JOIN" => rl.Join,

                "NICK" => new TokenBucketOptions { Capacity = 4, RefillTokens = 1, RefillPeriodSeconds = 2 },
                "USER" => new TokenBucketOptions { Capacity = 2, RefillTokens = 1, RefillPeriodSeconds = 5 },

                "WHO" => new TokenBucketOptions { Capacity = 3, RefillTokens = 1, RefillPeriodSeconds = 5 },
                "WHOIS" => new TokenBucketOptions { Capacity = 5, RefillTokens = 1, RefillPeriodSeconds = 2 },
                "NAMES" => new TokenBucketOptions { Capacity = 4, RefillTokens = 1, RefillPeriodSeconds = 3 },
                "LIST" => new TokenBucketOptions { Capacity = 2, RefillTokens = 1, RefillPeriodSeconds = 10 },

                _ => new TokenBucketOptions { Capacity = 10, RefillTokens = 1, RefillPeriodSeconds = 1 }
            };

            var period = TimeSpan.FromSeconds(Math.Max(1, opt.RefillPeriodSeconds));
            var capacity = Math.Max(1, opt.Capacity);
            var refill = Math.Max(1, opt.RefillTokens);

            return new RateLimitRule(capacity, refill, period);
        }

        private sealed class TokenBucket
        {
            private readonly int _capacity;
            private readonly int _refillTokens;
            private readonly TimeSpan _refillPeriod;

            private double _tokens;
            private DateTimeOffset _lastRefillUtc;
            private readonly object _lock = new();

            public TokenBucket(RateLimitRule rule)
            {
                _capacity = rule.Capacity;
                _refillTokens = rule.RefillTokens;
                _refillPeriod = rule.RefillPeriod;

                _tokens = _capacity;
                _lastRefillUtc = DateTimeOffset.UtcNow;
            }

            public bool TryConsume(out int retryAfterSeconds)
            {
                retryAfterSeconds = 0;

                lock (_lock)
                {
                    Refill();

                    if (_tokens >= 1)
                    {
                        _tokens -= 1;
                        return true;
                    }

                    var now = DateTimeOffset.UtcNow;
                    var elapsed = now - _lastRefillUtc;

                    var periodSec = _refillPeriod.TotalSeconds;
                    var elapsedSec = elapsed.TotalSeconds;

                    var remain = periodSec - (elapsedSec % periodSec);
                    retryAfterSeconds = (int)Math.Ceiling(remain);
                    if (retryAfterSeconds < 1)
                    {
                        retryAfterSeconds = 1;
                    }

                    return false;
                }
            }

            private void Refill()
            {
                var now = DateTimeOffset.UtcNow;
                var elapsed = now - _lastRefillUtc;

                if (elapsed < _refillPeriod)
                {
                    return;
                }

                var periods = (int)(elapsed.TotalSeconds / _refillPeriod.TotalSeconds);
                if (periods <= 0)
                {
                    return;
                }

                var add = periods * _refillTokens;
                _tokens = Math.Min(_capacity, _tokens + add);
                _lastRefillUtc = _lastRefillUtc.AddSeconds(periods * _refillPeriod.TotalSeconds);
            }
        }

        private sealed class ViolationWindow
        {
            private readonly object _lock = new();
            private DateTimeOffset _windowStartUtc;
            private int _count;

            public ViolationWindow(int windowSeconds)
            {
                _windowStartUtc = DateTimeOffset.UtcNow;
                _count = 0;
            }

            public int AddViolationAndGetCount(int windowSeconds)
            {
                lock (_lock)
                {
                    var now = DateTimeOffset.UtcNow;
                    var window = TimeSpan.FromSeconds(Math.Max(1, windowSeconds));

                    if (now - _windowStartUtc > window)
                    {
                        _windowStartUtc = now;
                        _count = 0;
                    }

                    _count++;
                    return _count;
                }
            }
        }

        private readonly record struct RateLimitRule(int Capacity, int RefillTokens, TimeSpan RefillPeriod);
    }
}
