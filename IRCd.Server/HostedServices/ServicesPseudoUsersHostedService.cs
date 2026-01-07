namespace IRCd.Server.HostedServices
{
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Core.State;
    using IRCd.Services;
    using IRCd.Shared.Options;

    using Microsoft.Extensions.Hosting;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;

    public sealed class ServicesPseudoUsersHostedService : IHostedService
    {
        private readonly ServerState _state;
        private readonly IOptions<IrcOptions> _options;
        private readonly ILogger<ServicesPseudoUsersHostedService> _logger;

        public ServicesPseudoUsersHostedService(ServerState state, IOptions<IrcOptions> options, ILogger<ServicesPseudoUsersHostedService> logger)
        {
            _state = state;
            _options = options;
            _logger = logger;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            ServiceUserSeeder.EnsureServiceUsers(_state, _options.Value, _logger);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
