namespace IRCd.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Net.Sockets;
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Core.Abstractions;
    using IRCd.Core.State;

    using Microsoft.Extensions.Logging;

    /// <summary>
    /// Centralized ban service with matching engine
    /// </summary>
    public sealed class BanService
    {
        private readonly IBanRepository _repository;
        private readonly ILogger<BanService> _logger;

        public BanService(IBanRepository repository, ILogger<BanService> logger)
        {
            _repository = repository;
            _logger = logger;
        }

        /// <summary>
        /// Add a new ban entry
        /// </summary>
        public async Task<BanEntry> AddAsync(BanEntry entry, CancellationToken ct = default)
        {
            var result = await _repository.AddAsync(entry, ct);
            _logger.LogInformation("Added {Type} ban: {Mask} by {SetBy}, expires: {ExpiresAt}", 
                entry.Type, entry.Mask, entry.SetBy, entry.ExpiresAt?.ToString() ?? "never");
            return result;
        }

        /// <summary>
        /// Remove ban by ID
        /// </summary>
        public async Task<bool> RemoveByIdAsync(Guid id, CancellationToken ct = default)
        {
            var result = await _repository.RemoveByIdAsync(id, ct);
            if (result)
            {
                _logger.LogInformation("Removed ban by ID: {Id}", id);
            }
            return result;
        }

        /// <summary>
        /// Remove ban by type and mask
        /// </summary>
        public async Task<bool> RemoveAsync(BanType type, string mask, CancellationToken ct = default)
        {
            var result = await _repository.RemoveByMaskAsync(type, mask, ct);
            if (result)
            {
                _logger.LogInformation("Removed {Type} ban: {Mask}", type, mask);
            }
            return result;
        }

        /// <summary>
        /// Get all active bans of a specific type
        /// </summary>
        public Task<IReadOnlyList<BanEntry>> GetActiveByTypeAsync(BanType type, CancellationToken ct = default)
        {
            return _repository.GetActiveByTypeAsync(type, ct);
        }

        /// <summary>
        /// Get all active bans
        /// </summary>
        public Task<IReadOnlyList<BanEntry>> GetAllActiveAsync(CancellationToken ct = default)
        {
            return _repository.GetAllActiveAsync(ct);
        }

        /// <summary>
        /// Try to match a user against KLINE bans
        /// </summary>
        public async Task<BanEntry?> TryMatchUserAsync(string nick, string userName, string host, CancellationToken ct = default)
        {
            var klines = await _repository.GetActiveByTypeAsync(BanType.KLINE, ct);
            var userAtHost = $"{userName}@{host}";
            var fullMask = $"{nick}!{userName}@{host}";

            foreach (var ban in klines)
            {
                var pattern = ban.Mask;

                var target = pattern.Contains('!', StringComparison.Ordinal)
                    ? fullMask
                    : userAtHost;

                if (!pattern.Contains('@', StringComparison.Ordinal))
                {
                    pattern = "*@" + pattern;
                }

                if (MaskMatcher.IsMatch(pattern, target))
                {
                    _logger.LogDebug("KLINE match: {Target} matches {Mask}", target, pattern);
                    return ban;
                }
            }

            return null;
        }

        /// <summary>
        /// Try to match a nick against QLINE bans
        /// </summary>
        public async Task<BanEntry?> TryMatchNickAsync(string nick, CancellationToken ct = default)
        {
            var qlines = await _repository.GetActiveByTypeAsync(BanType.QLINE, ct);

            foreach (var ban in qlines)
            {
                if (MaskMatcher.IsMatch(ban.Mask, nick))
                {
                    _logger.LogDebug("QLINE match: {Nick} matches {Mask}", nick, ban.Mask);
                    return ban;
                }
            }

            return null;
        }

        /// <summary>
        /// Try to match an IP address against DLINE/ZLINE bans
        /// </summary>
        public async Task<BanEntry?> TryMatchIpAsync(IPAddress ip, CancellationToken ct = default)
        {
            var dlines = await _repository.GetActiveByTypeAsync(BanType.DLINE, ct);
            var zlines = await _repository.GetActiveByTypeAsync(BanType.ZLINE, ct);
            var allIpBans = dlines.Concat(zlines).ToList();

            foreach (var ban in allIpBans)
            {
                if (MatchesIpOrCidr(ip, ban.Mask))
                {
                    _logger.LogDebug("{Type} match: {Ip} matches {Mask}", ban.Type, ip, ban.Mask);
                    return ban;
                }
            }

            return null;
        }

        /// <summary>
        /// Try to match a user session against all applicable bans (KLINE, DLINE, QLINE)
        /// </summary>
        public async Task<List<BanEntry>> TryMatchSessionAsync(string nick, string userName, string host, IPAddress? ip, CancellationToken ct = default)
        {
            var matches = new List<BanEntry>();

            var klineMatch = await TryMatchUserAsync(nick, userName, host, ct);
            if (klineMatch is not null)
            {
                matches.Add(klineMatch);
            }

            var qlineMatch = await TryMatchNickAsync(nick, ct);
            if (qlineMatch is not null)
            {
                matches.Add(qlineMatch);
            }

            if (ip is not null)
            {
                var ipMatch = await TryMatchIpAsync(ip, ct);
                if (ipMatch is not null)
                {
                    matches.Add(ipMatch);
                }
            }

            return matches;
        }

        /// <summary>
        /// Cleanup expired bans
        /// </summary>
        public async Task<int> CleanupExpiredAsync(CancellationToken ct = default)
        {
            var count = await _repository.CleanupExpiredAsync(ct);
            if (count > 0)
            {
                _logger.LogInformation("Cleaned up {Count} expired bans", count);
            }
            return count;
        }

        /// <summary>
        /// Reload bans from storage
        /// </summary>
        public Task ReloadAsync(CancellationToken ct = default)
        {
            _logger.LogInformation("Reloading bans from repository");
            return _repository.ReloadAsync(ct);
        }

        /// <summary>
        /// Check if an IP address matches a CIDR or single IP mask
        /// </summary>
        private static bool MatchesIpOrCidr(IPAddress ip, string mask)
        {
            if (string.IsNullOrWhiteSpace(mask))
            {
                return false;
            }

            mask = mask.Trim();

            if (mask.Contains('/', StringComparison.Ordinal))
            {
                var parts = mask.Split('/');
                if (parts.Length != 2)
                {
                    return false;
                }

                if (!IPAddress.TryParse(parts[0], out var networkIp))
                {
                    return false;
                }

                if (!int.TryParse(parts[1], out var prefixLength))
                {
                    return false;
                }

                return IsInCidr(ip, networkIp, prefixLength);
            }

            if (mask.Contains('*', StringComparison.Ordinal) || mask.Contains('?', StringComparison.Ordinal))
            {
                var ipString = ip.ToString();
                return WildcardMatch(ipString, mask);
            }

            if (IPAddress.TryParse(mask, out var banIp))
            {
                return ip.Equals(banIp);
            }

            return false;
        }

        /// <summary>
        /// Check if IP is within CIDR range
        /// </summary>
        private static bool IsInCidr(IPAddress ip, IPAddress network, int prefixLength)
        {
            if (ip.AddressFamily != network.AddressFamily)
            {
                return false;
            }

            var ipBytes = ip.GetAddressBytes();
            var networkBytes = network.GetAddressBytes();

            if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            {
                if (prefixLength < 0 || prefixLength > 128)
                {
                    return false;
                }
            }
            else if (ip.AddressFamily == AddressFamily.InterNetwork)
            {
                if (prefixLength < 0 || prefixLength > 32)
                {
                    return false;
                }
            }
            else
            {
                return false;
            }

            var bytesToCheck = prefixLength / 8;
            var bitsToCheck = prefixLength % 8;

            for (var i = 0; i < bytesToCheck; i++)
            {
                if (ipBytes[i] != networkBytes[i])
                {
                    return false;
                }
            }

            if (bitsToCheck > 0)
            {
                var mask = (byte)(0xFF << (8 - bitsToCheck));
                if ((ipBytes[bytesToCheck] & mask) != (networkBytes[bytesToCheck] & mask))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Simple wildcard matching (* and ?)
        /// </summary>
        private static bool WildcardMatch(string text, string pattern)
        {
            var textIndex = 0;
            var patternIndex = 0;
            var textBacktrack = -1;
            var patternBacktrack = -1;

            while (textIndex < text.Length)
            {
                if (patternIndex < pattern.Length && (pattern[patternIndex] == text[textIndex] || pattern[patternIndex] == '?'))
                {
                    textIndex++;
                    patternIndex++;
                }
                else if (patternIndex < pattern.Length && pattern[patternIndex] == '*')
                {
                    patternBacktrack = patternIndex;
                    textBacktrack = textIndex;
                    patternIndex++;
                }
                else if (patternBacktrack != -1)
                {
                    patternIndex = patternBacktrack + 1;
                    textBacktrack++;
                    textIndex = textBacktrack;
                }
                else
                {
                    return false;
                }
            }

            while (patternIndex < pattern.Length && pattern[patternIndex] == '*')
            {
                patternIndex++;
            }

            return patternIndex == pattern.Length;
        }
    }
}
