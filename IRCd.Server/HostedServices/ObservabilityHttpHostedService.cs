namespace IRCd.Server.HostedServices
{
    using System.Globalization;
    using System.Net;
    using System.Text;
    using IRCd.Core.Abstractions;
    using IRCd.Core.State;
    using IRCd.Shared.Options;
    using Microsoft.AspNetCore.Builder;
    using Microsoft.AspNetCore.Hosting;
    using Microsoft.AspNetCore.Http;
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.Hosting;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;

    public sealed class ObservabilityHttpHostedService : IHostedService
    {
        private readonly ILogger<ObservabilityHttpHostedService> _logger;
        private readonly IOptionsMonitor<IrcOptions> _options;
        private readonly IMetrics _metrics;
        private readonly ServerState _state;
        private readonly IBanRepository _bans;
        private readonly IAcceptLoopStatus _acceptLoops;
        private readonly IRCd.Core.Services.IAutoDlineMetrics? _autoDlineMetrics;

        private WebApplication? _app;

        public ObservabilityHttpHostedService(
            ILogger<ObservabilityHttpHostedService> logger,
            IOptionsMonitor<IrcOptions> options,
            IMetrics metrics,
            ServerState state,
            IBanRepository bans,
            IAcceptLoopStatus acceptLoops,
            IRCd.Core.Services.IAutoDlineMetrics? autoDlineMetrics = null)
        {
            _logger = logger;
            _options = options;
            _metrics = metrics;
            _state = state;
            _bans = bans;
            _acceptLoops = acceptLoops;
            _autoDlineMetrics = autoDlineMetrics;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            var cfg = _options.CurrentValue.Observability;
            if (cfg is null || !cfg.Enabled)
                return;

            var ip = IPAddress.Loopback;
            if (!string.IsNullOrWhiteSpace(cfg.BindIp) && IPAddress.TryParse(cfg.BindIp, out var parsed))
                ip = parsed;

            var port = cfg.Port > 0 ? cfg.Port : 6060;

            try
            {
                var builder = WebApplication.CreateBuilder(new WebApplicationOptions
                {
                    Args = Array.Empty<string>(),
                });

                builder.Logging.ClearProviders();

                builder.WebHost
                    .UseKestrel(k => k.Listen(ip, port))
                    .UseShutdownTimeout(TimeSpan.FromSeconds(2));

                builder.Services.AddSingleton(_metrics);
                builder.Services.AddSingleton(_state);
                builder.Services.AddSingleton(_bans);
                builder.Services.AddSingleton(_acceptLoops);

                var app = builder.Build();

                app.MapGet("/healthz", (IAcceptLoopStatus loops) =>
                {
                    return loops.ActiveAcceptLoops > 0
                        ? Results.Text("ok\n", "text/plain")
                        : Results.Text("not_ready\n", "text/plain", statusCode: StatusCodes.Status503ServiceUnavailable);
                });

                app.MapGet("/metrics", async (HttpContext ctx, IMetrics metrics, ServerState state, IBanRepository bansRepo) =>
                {
                    var snap = metrics.GetSnapshot();

                    var users = state.GetUsersSnapshot();
                    var activeUsers = users.Count(u => !u.IsService);
                    var activeRegisteredUsers = users.Count(u => u.IsRegistered && !u.IsService);

                    var channels = state.GetAllChannelNames().Count;

                    var activeBans = 0;
                    try
                    {
                        activeBans = (await bansRepo.GetAllActiveAsync(ctx.RequestAborted)).Count;
                    }
                    catch
                    {
                        // Metrics endpoint must never throw.
                    }

                    var sb = new StringBuilder(1024);

                    AppendCounter(sb, "ircd_connections_accepted_total", snap.ConnectionsAccepted, "Total connections accepted.");
                    AppendCounter(sb, "ircd_connections_closed_total", snap.ConnectionsClosed, "Total connections closed.");
                    AppendGauge(sb, "ircd_connections_active", snap.ActiveConnections, "Current active connections.");

                    AppendGauge(sb, "ircd_users_active", activeUsers, "Current active users (excluding services)." );
                    AppendGauge(sb, "ircd_users_registered_active", activeRegisteredUsers, "Current active registered users (excluding services)." );
                    AppendCounter(sb, "ircd_users_registered_total", snap.RegisteredUsersTotal, "Total users registered since process start.");

                    AppendGauge(sb, "ircd_channels_active", channels, "Current active channels.");
                    AppendCounter(sb, "ircd_channels_created_total", snap.ChannelsCreatedTotal, "Total channels created since process start.");

                    AppendCounter(sb, "ircd_commands_total", snap.CommandsTotal, "Total IRC commands processed.");
                    AppendGauge(sb, "ircd_commands_per_second", snap.CommandsPerSecond, "Approximate command rate (commands/sec).");

                    AppendCounter(sb, "ircd_flood_kicks_total", snap.FloodKicksTotal, "Total flood kicks.");

                    if (_autoDlineMetrics is not null)
                    {
                        AppendCounter(sb, "ircd_autodline_total", _autoDlineMetrics.AutoDlinesTotal, "Total automatic temporary DLINE bans applied.");

                        foreach (var (prefix, count) in _autoDlineMetrics.GetTopOffenders(5))
                        {
                            AppendGaugeWithLabel(sb, "ircd_autodline_top_offenders", "prefix", prefix, count, "Top offending IP prefixes (aggregate only)." );
                        }
                    }

                    AppendGauge(sb, "ircd_outbound_queue_depth", snap.OutboundQueueDepth, "Current outbound queue depth.");
                    AppendGauge(sb, "ircd_outbound_queue_max_depth", snap.OutboundQueueMaxDepth, "Max outbound queue depth observed.");
                    AppendCounter(sb, "ircd_outbound_queue_dropped_total", snap.OutboundQueueDroppedTotal, "Total outbound messages dropped.");
                    AppendCounter(sb, "ircd_outbound_queue_overflow_disconnects_total", snap.OutboundQueueOverflowDisconnectsTotal, "Total disconnects due to outbound queue overflow.");

                    AppendGauge(sb, "ircd_bans_active", activeBans, "Current active bans (all types)." );

                    ctx.Response.ContentType = "text/plain; version=0.0.4; charset=utf-8";
                    await ctx.Response.WriteAsync(sb.ToString(), ctx.RequestAborted);
                });

                _app = app;

                await app.StartAsync(cancellationToken);

                _logger.LogInformation("Observability HTTP listening on {IP}:{Port}", ip, port);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start observability HTTP listener on {IP}:{Port}", ip, port);
                _app = null;
            }
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            var app = _app;
            _app = null;

            if (app is null)
                return;

            try
            {
                await app.StopAsync(cancellationToken);
            }
            catch
            {
                // ignore
            }

            try
            {
                await app.DisposeAsync();
            }
            catch
            {
                // ignore
            }
        }

        private static void AppendCounter(StringBuilder sb, string name, long value, string help)
        {
            sb.Append("# HELP ").Append(name).Append(' ').Append(help).Append('\n');
            sb.Append("# TYPE ").Append(name).Append(" counter\n");
            sb.Append(name).Append(' ').Append(value.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }

        private static void AppendGauge(StringBuilder sb, string name, long value, string help)
        {
            sb.Append("# HELP ").Append(name).Append(' ').Append(help).Append('\n');
            sb.Append("# TYPE ").Append(name).Append(" gauge\n");
            sb.Append(name).Append(' ').Append(value.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }

        private static void AppendGauge(StringBuilder sb, string name, double value, string help)
        {
            sb.Append("# HELP ").Append(name).Append(' ').Append(help).Append('\n');
            sb.Append("# TYPE ").Append(name).Append(" gauge\n");
            sb.Append(name).Append(' ').Append(value.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }

        private static void AppendGaugeWithLabel(StringBuilder sb, string name, string labelKey, string labelValue, long value, string help)
        {
            sb.Append("# HELP ").Append(name).Append(' ').Append(help).Append('\n');
            sb.Append("# TYPE ").Append(name).Append(" gauge\n");
            sb.Append(name)
                .Append('{')
                .Append(labelKey)
                .Append("=\"")
                .Append(labelValue.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal))
                .Append("\"}")
                .Append(' ')
                .Append(value.ToString(CultureInfo.InvariantCulture))
                .Append('\n');
        }
    }
}
