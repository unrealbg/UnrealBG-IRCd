namespace IRCd.Server.HostedServices
{
    using IRCd.Core.Services;

    using Microsoft.Extensions.Hosting;

    public sealed class PingHostedService : BackgroundService
    {
        private readonly PingService _ping;

        public PingHostedService(PingService ping)
        {
            _ping = ping;
        }

        protected override Task ExecuteAsync(CancellationToken stoppingToken)
            => _ping.RunAsync(stoppingToken);
    }
}