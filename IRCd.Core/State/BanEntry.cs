namespace IRCd.Core.State
{
    using System;

    /// <summary>
    /// Type of network ban
    /// </summary>
    public enum BanType
    {
        /// <summary>
        /// K-Line: Ban user@host mask
        /// </summary>
        KLINE,

        /// <summary>
        /// D-Line: Ban IP address or CIDR
        /// </summary>
        DLINE,

        /// <summary>
        /// Q-Line: Forbid/reserve nickname pattern
        /// </summary>
        QLINE,

        /// <summary>
        /// Z-Line: IP ban (alternative to D-Line)
        /// </summary>
        ZLINE,

        /// <summary>
        /// AKill/G-Line: Global network ban
        /// </summary>
        AKILL
    }

    /// <summary>
    /// Single ban entry with expiration support
    /// </summary>
    public sealed record BanEntry
    {
        /// <summary>
        /// Unique identifier
        /// </summary>
        public Guid Id { get; init; } = Guid.NewGuid();

        /// <summary>
        /// Type of ban
        /// </summary>
        public BanType Type { get; init; }

        /// <summary>
        /// Ban mask (user@host, IP/CIDR, nick pattern)
        /// </summary>
        public string Mask { get; init; } = string.Empty;

        /// <summary>
        /// Reason for the ban
        /// </summary>
        public string Reason { get; init; } = "Banned";

        /// <summary>
        /// Who set the ban
        /// </summary>
        public string SetBy { get; init; } = "server";

        /// <summary>
        /// When the ban was created
        /// </summary>
        public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

        /// <summary>
        /// When the ban expires (null = permanent)
        /// </summary>
        public DateTimeOffset? ExpiresAt { get; init; }

        /// <summary>
        /// Whether this ban is currently active (not expired)
        /// </summary>
        public bool IsActive => ExpiresAt is null || ExpiresAt.Value > DateTimeOffset.UtcNow;

        /// <summary>
        /// Parse duration string (e.g., "1h", "2d", "30m", "perm") and return ExpiresAt
        /// </summary>
        public static DateTimeOffset? ParseDuration(string? duration)
        {
            if (string.IsNullOrWhiteSpace(duration) || duration.Equals("perm", StringComparison.OrdinalIgnoreCase) || duration.Equals("0", StringComparison.Ordinal))
            {
                return null; // permanent
            }

            duration = duration.Trim();
            if (duration.Length < 2)
            {
                return null;
            }

            var numPart = duration[..^1];
            var unit = duration[^1];

            if (!int.TryParse(numPart, out var value) || value <= 0)
            {
                return null;
            }

            var span = unit switch
            {
                's' => TimeSpan.FromSeconds(value),
                'm' => TimeSpan.FromMinutes(value),
                'h' => TimeSpan.FromHours(value),
                'd' => TimeSpan.FromDays(value),
                'w' => TimeSpan.FromDays(value * 7),
                _ => TimeSpan.Zero
            };

            return span > TimeSpan.Zero ? DateTimeOffset.UtcNow.Add(span) : null;
        }
    }
}
