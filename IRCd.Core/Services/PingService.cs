namespace IRCd.Core.Services
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Core.Abstractions;
    using IRCd.Shared.Options;

    using Microsoft.Extensions.Options;

    public sealed class PingService
    {
        private readonly ISessionRegistry _sessions;
        private readonly IOptions<IrcOptions> _options;

        public PingService(ISessionRegistry sessions, IOptions<IrcOptions> options)
        {
            _sessions = sessions;
            _options = options;
        }

        public async Task RunAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                await TickAsync(ct);

                try { await Task.Delay(TimeSpan.FromSeconds(1), ct); }
                catch (OperationCanceledException) { }
            }
        }

        private async Task TickAsync(CancellationToken ct)
        {
            var cfg = _options.Value.Ping;
            if (!cfg.Enabled)
            {
                return;
            }

            var now = DateTime.UtcNow;

            foreach (var session in _sessions.All())
            {
                if (!session.IsRegistered)
                {
                    continue;
                }

                if (session.AwaitingPong &&
                    (now - session.LastPingUtc).TotalSeconds >= cfg.DisconnectSecondsAfterPing)
                {
                    try { await session.SendAsync("ERROR :Ping timeout", ct); } catch { /* ignore */ }

                    try { await session.CloseAsync(cfg.QuitMessage, ct); } catch { /* ignore */ }

                    continue;
                }

                if (!session.AwaitingPong &&
                    (now - session.LastActivityUtc).TotalSeconds >= cfg.IdleSecondsBeforePing)
                {
                    var token = Guid.NewGuid().ToString("N");

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
