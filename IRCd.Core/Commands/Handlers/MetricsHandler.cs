namespace IRCd.Core.Commands.Handlers
{
    using System.Globalization;
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Core.Abstractions;
    using IRCd.Core.Commands.Contracts;
    using IRCd.Core.Services;
    using IRCd.Core.State;
    using IRCd.Shared.Options;

    using Microsoft.Extensions.Options;

    public sealed class MetricsHandler : IIrcCommandHandler
    {
        public string Command => "METRICS";

        private readonly IMetrics _metrics;
        private readonly IOptions<IrcOptions> _options;

        public MetricsHandler(IMetrics metrics, IOptions<IrcOptions> options)
        {
            _metrics = metrics;
            _options = options;
        }

        public async ValueTask HandleAsync(IClientSession session, Protocol.IrcMessage msg, ServerState state, CancellationToken ct)
        {
            if (!session.IsRegistered)
            {
                await session.SendAsync($":server 451 {(session.Nick ?? "*")} :You have not registered", ct);
                return;
            }

            var me = session.Nick!;
            var serverName = _options.Value.ServerInfo?.Name ?? "server";

            if (!state.TryGetUser(session.ConnectionId, out var user) || user is null || !OperCapabilityService.HasCapability(_options.Value, user, "metrics"))
            {
                await session.SendAsync($":{serverName} 481 {me} :Permission Denied- You're not an IRC operator", ct);
                return;
            }

            var s = _metrics.GetSnapshot();

            await session.SendAsync($":{serverName} NOTICE {me} :METRICS active={s.ActiveConnections} accepted={s.ConnectionsAccepted} closed={s.ConnectionsClosed}", ct);
            await session.SendAsync($":{serverName} NOTICE {me} :METRICS registered_total={s.RegisteredUsersTotal} channels_total={s.ChannelsCreatedTotal}", ct);
            await session.SendAsync($":{serverName} NOTICE {me} :METRICS commands_total={s.CommandsTotal} commands_per_sec={s.CommandsPerSecond.ToString("0.00", CultureInfo.InvariantCulture)} flood_kicks_total={s.FloodKicksTotal}", ct);
            await session.SendAsync($":{serverName} NOTICE {me} :End of /METRICS.", ct);
        }
    }
}
