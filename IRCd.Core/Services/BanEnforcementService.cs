namespace IRCd.Core.Services
{
    using System;
    using System.Linq;
    using System.Net;
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Core.Abstractions;
    using IRCd.Core.Protocol;
    using IRCd.Core.State;

    using Microsoft.Extensions.Hosting;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;

    /// <summary>
    /// Background service for ban enforcement and cleanup
    /// </summary>
    public sealed class BanEnforcementService : BackgroundService, IBanEnforcer
    {
        private readonly BanService _banService;
        private readonly ServerState _state;
        private readonly ISessionRegistry _sessions;
        private readonly RoutingService _routing;
        private readonly ILogger<BanEnforcementService> _logger;

        private readonly TimeSpan _checkInterval;

        public BanEnforcementService(
            IOptions<IRCd.Shared.Options.IrcOptions> options,
            BanService banService,
            ServerState state,
            ISessionRegistry sessions,
            RoutingService routing,
            ILogger<BanEnforcementService> logger)
        {
            _banService = banService;
            _state = state;
            _sessions = sessions;
            _routing = routing;
            _logger = logger;

            var seconds = options?.Value?.Bans?.EnforcementCheckIntervalSeconds ?? 300;
            seconds = Math.Max(1, seconds);
            _checkInterval = TimeSpan.FromSeconds(seconds);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Ban enforcement service started");

            try
            {
                await _banService.ReloadAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load bans on startup");
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(_checkInterval, stoppingToken);

                    var cleanedCount = await _banService.CleanupExpiredAsync(stoppingToken);
                    if (cleanedCount > 0)
                    {
                        _logger.LogInformation("Cleaned up {Count} expired bans", cleanedCount);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in ban enforcement loop");
                }
            }

            _logger.LogInformation("Ban enforcement service stopped");
        }

        /// <summary>
        /// Apply a new ban immediately to online users
        /// </summary>
        public async Task EnforceBanImmediatelyAsync(BanEntry ban, CancellationToken ct = default)
        {
            _logger.LogInformation("Enforcing {Type} ban immediately: {Mask}", ban.Type, ban.Mask);

            var affectedUsers = _state.GetAllUsers()
                .Where(u => u.IsRegistered && !u.IsRemote && !u.IsService)
                .ToList();

            foreach (var user in affectedUsers)
            {
                if (!_sessions.TryGet(user.ConnectionId, out var session) || session is null)
                {
                    continue;
                }

                var shouldKill = ban.Type switch
                {
                    BanType.KLINE => await CheckKLineBan(user, ban, ct),
                    BanType.DLINE or BanType.ZLINE => await CheckIpBan(session, ban, ct),
                    BanType.QLINE => await CheckQLineBan(user, ban, ct),
                    _ => false
                };

                if (shouldKill)
                {
                    await KillUserForBan(session, user, ban, ct);
                }
            }
        }

        private async Task<bool> CheckKLineBan(User user, BanEntry ban, CancellationToken ct)
        {
            var match = await _banService.TryMatchUserAsync(
                user.Nick ?? "*",
                user.UserName ?? "user",
                user.Host ?? "localhost",
                ct);

            return match is not null && match.Id == ban.Id;
        }

        private async Task<bool> CheckIpBan(IClientSession session, BanEntry ban, CancellationToken ct)
        {
            var remoteIp = session.RemoteEndPoint is IPEndPoint ipEndPoint ? ipEndPoint.Address : null;
            if (remoteIp is null)
            {
                return false;
            }

            var match = await _banService.TryMatchIpAsync(remoteIp, ct);
            return match is not null && match.Id == ban.Id;
        }

        private async Task<bool> CheckQLineBan(User user, BanEntry ban, CancellationToken ct)
        {
            var match = await _banService.TryMatchNickAsync(user.Nick ?? "*", ct);
            return match is not null && match.Id == ban.Id;
        }

        private async Task KillUserForBan(IClientSession session, User user, BanEntry ban, CancellationToken ct)
        {
            var nick = user.Nick ?? "*";
            var banTypeText = ban.Type switch
            {
                BanType.KLINE => "K-Lined",
                BanType.DLINE => "D-Lined",
                BanType.ZLINE => "Z-Lined",
                BanType.QLINE => "Q-Lined",
                BanType.AKILL => "AKilled",
                _ => "Banned"
            };

            _logger.LogInformation("Killing user {Nick} due to {Type} ban: {Mask}", nick, ban.Type, ban.Mask);

            await session.SendAsync($":server KILL {nick} :{banTypeText} ({ban.Reason})", ct);

            var channels = _state.GetAllChannels().Where(ch => ch.Contains(user.ConnectionId)).ToList();
            foreach (var channel in channels)
            {
                var members = channel.Members;
                foreach (var member in members)
                {
                    if (member.ConnectionId == user.ConnectionId)
                    {
                        continue;
                    }

                    if (_sessions.TryGet(member.ConnectionId, out var memberSession) && memberSession is not null)
                    {
                        var quitMsg = $":{nick}!{user.UserName ?? "user"}@{user.Host ?? "localhost"} QUIT :{banTypeText}: {ban.Reason}";
                        await memberSession.SendAsync(quitMsg, ct);
                    }
                }
            }

            await session.CloseAsync(banTypeText, ct);

            _state.RemoveUser(user.ConnectionId);
        }
    }
}

