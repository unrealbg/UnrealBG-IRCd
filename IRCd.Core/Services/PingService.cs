namespace IRCd.Core.Services
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Core.Abstractions;
    using IRCd.Shared.Options;

    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;

    public sealed class PingService
    {
        private readonly ISessionRegistry _sessions;
        private readonly IOptions<IrcOptions> _options;
        private readonly ILogger<PingService> _logger;

        public PingService(ISessionRegistry sessions, IOptions<IrcOptions> options, ILogger<PingService> logger)
        {
            _sessions = sessions;
            _options = options;
            _logger = logger;
        }

        public async Task RunAsync(CancellationToken ct)
        {
            _logger.LogInformation("PingService starting. Enabled={Enabled}, IdleSeconds={Idle}, TimeoutSeconds={Timeout}",
                _options.Value.Ping.Enabled, 
                _options.Value.Ping.IdleSecondsBeforePing, 
                _options.Value.Ping.DisconnectSecondsAfterPing);

            while (!ct.IsCancellationRequested)
            {
                await TickAsync(ct);

                try { await Task.Delay(TimeSpan.FromSeconds(1), ct); }
                catch (OperationCanceledException) { }
            }

            _logger.LogInformation("PingService stopped");
        }

        private async Task TickAsync(CancellationToken ct)
        {
            var cfg = _options.Value.Ping;
            if (!cfg.Enabled)
            {
                return;
            }

            var now = DateTime.UtcNow;
            var sessions = _sessions.All().ToList();
            var registeredCount = sessions.Count(s => s.IsRegistered);

            if (DateTime.UtcNow.Second == 0)
            {
                _logger.LogDebug("PingService tick: {Total} total sessions, {Registered} registered", 
                    sessions.Count, registeredCount);
            }

            foreach (var session in sessions)
            {
                if (!session.IsRegistered)
                {
                    continue;
                }

                var idleSeconds = (now - session.LastActivityUtc).TotalSeconds;

                if (session.AwaitingPong &&
                    (now - session.LastPingUtc).TotalSeconds >= cfg.DisconnectSecondsAfterPing)
                {
                    var elapsed = (now - session.LastPingUtc).TotalSeconds;
                    _logger.LogWarning("PING timeout for {Nick} ({ConnectionId}): no PONG received after {Elapsed:F1}s (limit: {Limit}s), disconnecting",
                        session.Nick ?? "*", session.ConnectionId, elapsed, cfg.DisconnectSecondsAfterPing);

                    try { await session.SendAsync("ERROR :Ping timeout", ct); } catch { /* ignore */ }

                    try { await session.CloseAsync(cfg.QuitMessage, ct); } catch { /* ignore */ }

                    continue;
                }

                if (!session.AwaitingPong &&
                    idleSeconds >= cfg.IdleSecondsBeforePing)
                {
                    var token = Guid.NewGuid().ToString("N");

                    _logger.LogInformation("Sending PING to {Nick} ({ConnectionId}) after {Idle:F1}s idle, token: {Token}",
                        session.Nick ?? "*", session.ConnectionId, idleSeconds, token);

                    session.OnPingSent(token);

                    try
                    {
                        await session.SendAsync($"PING :{token}", ct);
                    }
                    catch
                    {
                        // Transport/session may be closing; ignore and let the transport clean up.
                    }
                }
            }
        }
    }
}
