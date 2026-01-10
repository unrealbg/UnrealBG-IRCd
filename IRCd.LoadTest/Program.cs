using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;

var options = LoadTestOptions.Parse(args);

if (options.ShowHelp)
{
    Console.WriteLine(LoadTestOptions.HelpText);
    return;
}

Console.WriteLine($"IRCd.LoadTest -> {options.Host}:{options.Port}");
Console.WriteLine($"Scenario={options.Scenario} Clients={options.Clients} Duration={options.Duration} Seed={options.Seed}");

var cts = new CancellationTokenSource(options.Duration);
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var runId = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);

var outDir = options.ResolveOutDir(runId);
if (outDir is not null)
{
    Directory.CreateDirectory(outDir);
}

var csvPath = options.ResolveCsvPath(runId, outDir);

using var csv = csvPath is null
    ? null
    : new StreamWriter(csvPath, append: false, new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = true };

if (csv is not null)
{
    csv.WriteLine(
        "run_id,ts_utc," +
        "clients_total,clients_connected,clients_registered,errors_total," +
        "latency_p50_ms,latency_p95_ms,latency_p99_ms," +
        "msg_sent_total,msg_recv_total,joins_total,parts_total," +
        "obs_health_ok,obs_http_status,obs_errors_total," +
        "m_connections_active,m_users_registered_active,m_outbound_queue_depth,m_outbound_queue_max_depth," +
        "m_outbound_queue_overflow_disconnects_total,m_outbound_queue_dropped_total," +
        "m_flood_kicks_total,m_autodline_total," +
        "server_working_set_mb,server_cpu_pct");
}

if (options.Scenario == Scenario.SplitHeal && !options.SpawnNetwork3)
{
    Console.Error.WriteLine("splitheal requires --spawn-network3 (local 3-node network mode)");
    Environment.ExitCode = 1;
    return;
}

await using var network = options.SpawnNetwork3
    ? await Network3Cluster.StartAsync(options, runId, outDir, cts.Token)
    : null;

Task? splitHealTask = null;
if (network is not null)
{
    options = options.WithTargets(
        host: IPAddress.Loopback.ToString(),
        port: network.NodeAClientPort,
        observabilityUrl: network.NodeAObservabilityUrl,
        serverPid: network.NodeAProcessId);

    if (options.Scenario == Scenario.SplitHeal)
    {
        splitHealTask = Task.Run(() => network.RunSplitHealAsync(options.SplitHealInterval, options.SplitHealDowntime, cts.Token), cts.Token);
    }
}

var stats = new GlobalStats();
var latency = new LatencyTracker(maxSamples: options.LatencySamples);

using var obs = options.ObservabilityUrl is null
    ? null
    : new ObservabilitySampler(options.ObservabilityUrl, options.ServerPid);

var tasks = new List<Task>(options.Clients);
var startGate = new ManualResetEventSlim(false);

for (var i = 0; i < options.Clients; i++)
{
    var clientIndex = i;
    tasks.Add(Task.Run(async () =>
    {
        var rnd = new Random(HashSeed(options.Seed, clientIndex));
        var nick = $"lt{clientIndex:D5}";

        try
        {
            await RunClientForeverAsync(options, nick, rnd, stats, latency, startGate, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // normal
        }
        catch (Exception)
        {
            stats.ErrorsTotal.Increment();
        }
    }, cts.Token));
}

startGate.Set();

var sw = Stopwatch.StartNew();
var nextTick = TimeSpan.FromSeconds(1);

var nextSampleAt = TimeSpan.Zero;
var obsErrorsTotal = 0L;
ObservabilitySnapshot? firstObs = null;
ObservabilitySnapshot? lastObs = null;
var maxQueueDepth = 0L;
var maxQueueMaxDepth = 0L;
long? firstWorkingSetBytes = null;
long? maxWorkingSetBytes = null;

try
{
    while (!cts.IsCancellationRequested)
    {
        await Task.Delay(250, cts.Token);

        if (sw.Elapsed < nextTick)
            continue;

        nextTick += TimeSpan.FromSeconds(1);

        var snapshot = stats.Snapshot();
        var lat = latency.SnapshotAndRotate();

        ObservabilitySnapshot? obsSnap = null;
        if (obs is not null && sw.Elapsed >= nextSampleAt)
        {
            nextSampleAt = sw.Elapsed + options.SampleInterval;

            obsSnap = await obs.SampleAsync(cts.Token);
            lastObs = obsSnap;
            firstObs ??= obsSnap;
            obsErrorsTotal = obs.ErrorsTotal;

            if (obsSnap.Metrics.TryGetValue("ircd_outbound_queue_depth", out var qd))
                maxQueueDepth = Math.Max(maxQueueDepth, qd);
            if (obsSnap.Metrics.TryGetValue("ircd_outbound_queue_max_depth", out var qmd))
                maxQueueMaxDepth = Math.Max(maxQueueMaxDepth, qmd);

            if (obsSnap.ServerWorkingSetBytes is long ws)
            {
                firstWorkingSetBytes ??= ws;
                maxWorkingSetBytes = Math.Max(maxWorkingSetBytes ?? 0, ws);
            }
        }

        var obsLine = obs is null
            ? ""
            : $" obs={(lastObs?.HealthOk == true ? "ok" : "bad")} q={(lastObs?.Metrics.TryGetValue("ircd_outbound_queue_depth", out var q) == true ? q.ToString(CultureInfo.InvariantCulture) : "?")}";

        Console.WriteLine(
            $"t={sw.Elapsed.TotalSeconds,6:0}s " +
            $"conn={snapshot.Connected}/{options.Clients} reg={snapshot.Registered}/{options.Clients} " +
            $"err={snapshot.Errors} " +
            $"ping(ms) p50={lat.P50Ms:0.0} p95={lat.P95Ms:0.0} p99={lat.P99Ms:0.0} " +
            $"msg s/r={snapshot.MessagesSent}/{snapshot.MessagesReceived} jp={snapshot.Joins}/{snapshot.Parts}" +
            obsLine);

        if (csv is not null)
        {
            var toWrite = obsSnap ?? lastObs;
            var healthOk = toWrite?.HealthOk == true;
            var httpStatus = toWrite?.HttpStatusCode;

            var mConnectionsActive = GetMetric(toWrite, "ircd_connections_active");
            var mUsersRegActive = GetMetric(toWrite, "ircd_users_registered_active");
            var mQueueDepth = GetMetric(toWrite, "ircd_outbound_queue_depth");
            var mQueueMaxDepth = GetMetric(toWrite, "ircd_outbound_queue_max_depth");
            var mOverflowDisc = GetMetric(toWrite, "ircd_outbound_queue_overflow_disconnects_total");
            var mDropped = GetMetric(toWrite, "ircd_outbound_queue_dropped_total");
            var mFloodKicks = GetMetric(toWrite, "ircd_flood_kicks_total");
            var mAutoDline = GetMetric(toWrite, "ircd_autodline_total");

            var wsMb = toWrite?.ServerWorkingSetBytes is long ws
                ? (ws / (1024.0 * 1024.0)).ToString(CultureInfo.InvariantCulture)
                : "";
            var cpuPct = toWrite?.ServerCpuPercent is double cpu
                ? cpu.ToString(CultureInfo.InvariantCulture)
                : "";

            csv.WriteLine(string.Join(",",
                runId,
                DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                options.Clients.ToString(CultureInfo.InvariantCulture),
                snapshot.Connected.ToString(CultureInfo.InvariantCulture),
                snapshot.Registered.ToString(CultureInfo.InvariantCulture),
                snapshot.Errors.ToString(CultureInfo.InvariantCulture),
                lat.P50Ms.ToString(CultureInfo.InvariantCulture),
                lat.P95Ms.ToString(CultureInfo.InvariantCulture),
                lat.P99Ms.ToString(CultureInfo.InvariantCulture),
                snapshot.MessagesSent.ToString(CultureInfo.InvariantCulture),
                snapshot.MessagesReceived.ToString(CultureInfo.InvariantCulture),
                snapshot.Joins.ToString(CultureInfo.InvariantCulture),
                snapshot.Parts.ToString(CultureInfo.InvariantCulture),
                healthOk ? "1" : "0",
                httpStatus?.ToString(CultureInfo.InvariantCulture) ?? "",
                obsErrorsTotal.ToString(CultureInfo.InvariantCulture),
                mConnectionsActive,
                mUsersRegActive,
                mQueueDepth,
                mQueueMaxDepth,
                mOverflowDisc,
                mDropped,
                mFloodKicks,
                mAutoDline,
                wsMb,
                cpuPct));
        }
    }
}
catch (OperationCanceledException)
{
    // normal
}

try { await Task.WhenAll(tasks); } catch { }

if (splitHealTask is not null)
{
    try { await splitHealTask; } catch { }
}

var finalSnap = stats.Snapshot();
var finalLat = latency.FinalSnapshot();

Console.WriteLine("--- summary ---");
Console.WriteLine($"clients_total          : {options.Clients}");
Console.WriteLine($"clients_connected_max  : {finalSnap.ConnectedMax}");
Console.WriteLine($"clients_registered_max : {finalSnap.RegisteredMax}");
Console.WriteLine($"errors_total           : {finalSnap.Errors}");
Console.WriteLine($"messages_sent_total    : {finalSnap.MessagesSent}");
Console.WriteLine($"messages_recv_total    : {finalSnap.MessagesReceived}");
Console.WriteLine($"joins_total            : {finalSnap.Joins}");
Console.WriteLine($"parts_total            : {finalSnap.Parts}");
Console.WriteLine($"ping_latency_ms p50/p95/p99 : {finalLat.P50Ms:0.0} / {finalLat.P95Ms:0.0} / {finalLat.P99Ms:0.0}");
Console.WriteLine($"managed_memory_mb      : {GC.GetTotalMemory(forceFullCollection: false) / (1024.0 * 1024.0):0.0}");

var verdict = options.EvaluateVerdict(
    clientFinal: finalSnap,
    firstObs: firstObs,
    lastObs: lastObs,
    maxQueueDepthObserved: maxQueueDepth,
    maxQueueMaxDepthObserved: maxQueueMaxDepth,
    firstWorkingSetBytesObserved: firstWorkingSetBytes,
    maxWorkingSetBytesObserved: maxWorkingSetBytes,
    obsErrorsTotal: obsErrorsTotal);

if (csvPath is not null)
{
    Console.WriteLine($"csv                    : {csvPath}");
}

Console.WriteLine($"verdict                : {(verdict.Passed ? "PASS" : "FAIL")}");
if (verdict.Reasons.Count > 0)
{
    foreach (var r in verdict.Reasons)
        Console.WriteLine($"reason                 : {r}");
}

if (!verdict.Passed)
{
    Environment.ExitCode = 2;
}

static string GetMetric(ObservabilitySnapshot? snap, string name)
{
    if (snap is null)
        return "";
    return snap.Metrics.TryGetValue(name, out var v)
        ? v.ToString(CultureInfo.InvariantCulture)
        : "";
}

static int HashSeed(int baseSeed, int clientIndex)
{
    unchecked
    {
        var x = (uint)baseSeed;
        x ^= (uint)clientIndex * 0x9E3779B9u;
        x ^= x >> 16;
        x *= 0x7FEB352Du;
        x ^= x >> 15;
        x *= 0x846CA68Bu;
        x ^= x >> 16;
        return (int)x;
    }
}

static async Task RunClientForeverAsync(
    LoadTestOptions options,
    string nick,
    Random rnd,
    GlobalStats stats,
    LatencyTracker latency,
    ManualResetEventSlim startGate,
    CancellationToken ct)
{
    startGate.Wait(ct);

    var attempt = 0;
    while (!ct.IsCancellationRequested)
    {
        try
        {
            await RunClientOnceAsync(options, nick, rnd, stats, latency, ct);
            attempt = 0;
        }
        catch (OperationCanceledException)
        {
            break;
        }
        catch
        {
            stats.ErrorsTotal.Increment();
        }

        if (ct.IsCancellationRequested)
            break;

        attempt = Math.Min(attempt + 1, 6);
        var delay = options.ReconnectBaseDelay + TimeSpan.FromMilliseconds(rnd.Next(0, Math.Max(1, options.ReconnectJitterMs + 1)));
        var backoffMs = Math.Min(options.ReconnectMaxDelay.TotalMilliseconds, delay.TotalMilliseconds * Math.Pow(2, attempt));
        await Task.Delay(TimeSpan.FromMilliseconds(backoffMs), ct);
    }
}

static async Task RunClientOnceAsync(
    LoadTestOptions options,
    string nick,
    Random rnd,
    GlobalStats stats,
    LatencyTracker latency,
    CancellationToken ct)
{

    if (options.RampUpSeconds > 0)
    {
        var idx = ParseNickIndex(nick);
        if (idx >= 0)
        {
            var rampMs = options.RampUpSeconds * 1000.0;
            var baseDelayMs = rampMs * idx / Math.Max(1, options.Clients);

            var jitterMs = options.RampUpJitterMs > 0
                ? rnd.Next(0, options.RampUpJitterMs + 1)
                : 0;

            var totalDelay = TimeSpan.FromMilliseconds(baseDelayMs + jitterMs);
            if (totalDelay > TimeSpan.Zero)
                await Task.Delay(totalDelay, ct);
        }
    }

    using var sessionCts = options.Scenario == Scenario.Churn && options.ChurnSessionMin > TimeSpan.Zero
        ? CancellationTokenSource.CreateLinkedTokenSource(ct)
        : null;

    if (sessionCts is not null)
    {
        var maxMs = Math.Max(options.ChurnSessionMin.TotalMilliseconds, options.ChurnSessionMax.TotalMilliseconds);
        var minMs = Math.Min(options.ChurnSessionMin.TotalMilliseconds, options.ChurnSessionMax.TotalMilliseconds);
        var dur = TimeSpan.FromMilliseconds(minMs + rnd.NextDouble() * (maxMs - minMs));
        sessionCts.CancelAfter(dur);
    }

    var sessionToken = sessionCts?.Token ?? ct;

    using var client = new TcpClient();
    client.NoDelay = true;

    await client.ConnectAsync(options.Host, options.Port, sessionToken);
    stats.Connected.Increment();

    using var net = client.GetStream();
    using var reader = new StreamReader(net, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 8 * 1024, leaveOpen: true);
    using var writer = new StreamWriter(net, new UTF8Encoding(false), bufferSize: 8 * 1024, leaveOpen: true)
    {
        NewLine = "\r\n",
        AutoFlush = true
    };

    var registered = false;
    var joined = false;
    var channel = options.ChannelPrefix + (rnd.Next(options.ChannelCount) + 1).ToString(CultureInfo.InvariantCulture);

    await SendAsync(writer, $"NICK {nick}", sessionToken);
    await SendAsync(writer, $"USER {nick} 0 * :{nick}", sessionToken);

    var recvTask = Task.Run(async () =>
    {
        while (!sessionToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync().WaitAsync(sessionToken);
            if (line is null)
                break;

            stats.MessagesReceived.Increment();

            if (line.StartsWith("PING ", StringComparison.Ordinal))
            {
                var payload = line.Length > 5 ? line.Substring(5) : string.Empty;
                await SendAsync(writer, "PONG " + payload, sessionToken);
                continue;
            }

            if (!registered && line.Contains(" 001 ", StringComparison.Ordinal))
            {
                registered = true;
                stats.Registered.Increment();

                if (options.Scenario != Scenario.Idle)
                {
                    await SendAsync(writer, $"JOIN {channel}", sessionToken);
                }
                continue;
            }

            if (!joined && registered && line.Contains(" JOIN ", StringComparison.Ordinal) && line.Contains(channel, StringComparison.OrdinalIgnoreCase))
            {
                joined = true;
                stats.Joins.Increment();
                continue;
            }

            if (line.Contains(" PONG ", StringComparison.Ordinal))
            {
                var token = ExtractTrailingToken(line);
                if (token is not null && TryParsePingToken(token, out var sentTicks))
                {
                    var elapsed = Stopwatch.GetTimestamp() - sentTicks;
                    var ms = elapsed * 1000.0 / Stopwatch.Frequency;
                    latency.Add(ms);
                }
            }

            if (line.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase))
            {
                stats.ErrorsTotal.Increment();
                break;
            }
        }
    }, sessionToken);

    var start = Stopwatch.GetTimestamp();
    while (!registered && !sessionToken.IsCancellationRequested)
    {
        await Task.Delay(10, sessionToken);
        if ((Stopwatch.GetTimestamp() - start) > Stopwatch.Frequency * 10)
        {
            stats.ErrorsTotal.Increment();
            break;
        }
    }

    try
    {
        switch (options.Scenario)
        {
            case Scenario.Idle:
                await RunIdleAsync(writer, latency, options, sessionToken);
                break;
            case Scenario.Churn:
                await RunJoinPartBurstAsync(writer, channel, rnd, stats, options, sessionToken);
                break;
            case Scenario.Chat:
                await RunMessageFloodAsync(writer, channel, rnd, stats, options, sessionToken);
                break;
            case Scenario.SplitHeal:
                await RunMessageFloodAsync(writer, channel, rnd, stats, options, sessionToken);
                break;
        }
    }
    finally
    {
        try { client.Close(); } catch { }
        stats.Connected.Decrement();
        if (registered) stats.Registered.Decrement();
    }

    try { await recvTask; } catch { }
}

static int ParseNickIndex(string nick)
{
    if (nick.Length < 3)
        return -1;

    if (!nick.StartsWith("lt", StringComparison.Ordinal))
        return -1;

    var digits = nick[2..];
    return int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : -1;
}

static async Task RunIdleAsync(StreamWriter writer, LatencyTracker latency, LoadTestOptions options, CancellationToken ct)
{
    var seq = 0;
    while (!ct.IsCancellationRequested)
    {
        var token = MakePingToken(seq++);
        await SendAsync(writer, $"PING :{token}", ct);
        await Task.Delay(options.PingInterval, ct);
    }

    static string MakePingToken(int seq)
        => $"lt{seq}:{Stopwatch.GetTimestamp().ToString(CultureInfo.InvariantCulture)}";
}

static async Task RunJoinPartBurstAsync(StreamWriter writer, string baseChannel, Random rnd, GlobalStats stats, LoadTestOptions options, CancellationToken ct)
{
    while (!ct.IsCancellationRequested)
    {
        var ch = options.ChannelPrefix + (rnd.Next(options.ChannelCount) + 1).ToString(CultureInfo.InvariantCulture);

        await SendAsync(writer, $"JOIN {ch}", ct);
        stats.Joins.Increment();

        await Task.Delay(options.JoinPartHold, ct);

        await SendAsync(writer, $"PART {ch} :bye", ct);
        stats.Parts.Increment();

        await Task.Delay(options.JoinPartPause, ct);
    }
}

static async Task RunMessageFloodAsync(StreamWriter writer, string channel, Random rnd, GlobalStats stats, LoadTestOptions options, CancellationToken ct)
{
    var interval = options.MessagesPerSecondPerClient <= 0
        ? TimeSpan.FromMilliseconds(50)
        : TimeSpan.FromSeconds(1.0 / options.MessagesPerSecondPerClient);

    var seq = 0;
    while (!ct.IsCancellationRequested)
    {
        var payload = $"m{seq++:D6}";
        await SendAsync(writer, $"PRIVMSG {channel} :{payload}", ct);
        stats.MessagesSent.Increment();
        await Task.Delay(interval, ct);
    }
}

static async Task SendAsync(StreamWriter writer, string line, CancellationToken ct)
{
    await writer.WriteLineAsync(line.AsMemory(), ct);
}

static string? ExtractTrailingToken(string line)
{
    var idx = line.LastIndexOf(':');
    if (idx < 0 || idx + 1 >= line.Length)
        return null;
    return line[(idx + 1)..].Trim();
}

static bool TryParsePingToken(string token, out long sentTicks)
{
    sentTicks = 0;

    var idx = token.LastIndexOf(':');
    if (idx < 0 || idx + 1 >= token.Length)
        return false;

    var ticksPart = token[(idx + 1)..];
    return long.TryParse(ticksPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out sentTicks);
}

enum Scenario
{
    Idle,
    Churn,
    Chat,
    SplitHeal,
}

sealed class LoadTestOptions
{
    public bool SpawnNetwork3 { get; init; }
    public string? RepoRoot { get; init; }
    public bool KeepLogs { get; init; }

    public string Host { get; init; } = "127.0.0.1";
    public int Port { get; init; } = 6667;

    public string? ObservabilityUrl { get; init; }
    public TimeSpan SampleInterval { get; init; } = TimeSpan.FromSeconds(5);
    public int? ServerPid { get; init; }

    public int Clients { get; init; } = 1000;
    public TimeSpan Duration { get; init; } = TimeSpan.FromSeconds(30);
    public int Seed { get; init; } = 12345;

    public int RampUpSeconds { get; init; } = 0;

    public int RampUpJitterMs { get; init; } = 0;

    public Scenario Scenario { get; init; } = Scenario.Idle;

    public string ChannelPrefix { get; init; } = "#lt";
    public int ChannelCount { get; init; } = 10;

    public TimeSpan PingInterval { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan JoinPartHold { get; init; } = TimeSpan.FromMilliseconds(200);
    public TimeSpan JoinPartPause { get; init; } = TimeSpan.FromMilliseconds(50);

    public TimeSpan ChurnSessionMin { get; init; } = TimeSpan.FromSeconds(20);
    public TimeSpan ChurnSessionMax { get; init; } = TimeSpan.FromSeconds(60);

    public double MessagesPerSecondPerClient { get; init; } = 5;

    public int LatencySamples { get; init; } = 20000;

    public string? CsvPath { get; init; }

        public string? OutDir { get; init; }

        public TimeSpan SplitHealInterval { get; init; } = TimeSpan.FromSeconds(120);
        public TimeSpan SplitHealDowntime { get; init; } = TimeSpan.FromSeconds(10);

        public TimeSpan ReconnectBaseDelay { get; init; } = TimeSpan.FromMilliseconds(250);
        public int ReconnectJitterMs { get; init; } = 250;
        public TimeSpan ReconnectMaxDelay { get; init; } = TimeSpan.FromSeconds(10);

        public long MaxOutboundQueueDepth { get; init; } = 5000;
        public long MaxOutboundQueueMaxDepth { get; init; } = 20000;
        public long MaxQueueOverflowDisconnectsDelta { get; init; } = 0;
        public long MaxOutboundDroppedDelta { get; init; } = 0;
        public long MaxFloodKicksDelta { get; init; } = 0;
        public long MaxAutoDlineDelta { get; init; } = 0;
        public long MaxObservabilityErrors { get; init; } = 0;
        public double MaxWorkingSetGrowthPercent { get; init; } = 20.0;

        public bool ShowHelp { get; init; }

        public static string HelpText =>
@"IRCd.LoadTest (soak runner)

Usage:
    dotnet run --project IRCd.LoadTest -c Release -- [options]

Core:
    --host <ip/host>                 IRC server host (default 127.0.0.1)
    --port <port>                    IRC server port (default 6667)
    --clients <N>                    Number of clients
    --duration <seconds>             Run duration in seconds
    --seed <int>                     Repeatability seed
    --scenario <idle|churn|chat|splitheal>

Network mode:
    --spawn-network3                 Spawn a local 3-node S2S network (A-B-C); clients connect to node A
    --repo-root <dir>                Repo root (defaults to current directory)
    --keep-logs                      Keep spawned node logs/configs under --out
    --splitheal-interval-s <s>        Split/heal cycle interval (default 120)
    --splitheal-downtime-s <s>        Node B downtime per cycle (default 10)

Traffic:
    --channels <N>                   Channel count (default 10)
    --channel-prefix <prefix>        Channel prefix (default #lt)
    --mps <double>                   Messages/sec per client (chat/splitheal)
    --joinpart-hold-ms <ms>
    --joinpart-pause-ms <ms>
    --churn-session-min-s <s>
    --churn-session-max-s <s>

Observability sampling:
    --obs-url <http://ip:port>       Base URL for /healthz and /metrics (e.g. http://127.0.0.1:6060)
    --sample-seconds <s>             Poll interval (default 5)
    --server-pid <pid>               Optional local PID for CPU/memory sampling

Output:
    --out <dir>                      Output directory; writes <dir>/run-<runId>/samples.csv
    --csv <path>                     CSV output path (overrides --out)

Thresholds (pass/fail):
    --max-queue-depth <N>
    --max-queue-max-depth <N>
    --max-overflow-disc-delta <N>
    --max-outbound-dropped-delta <N>
    --max-flood-kicks-delta <N>
    --max-autodline-delta <N>
    --max-obs-errors <N>
    --max-ws-growth-pct <double>

Notes:
    - For /metrics to work, enable: observability { enabled = true; bind = ""127.0.0.1""; port = 6060; }.
    - Exit code is 0 on PASS, 2 on FAIL.
";

    public static LoadTestOptions Parse(string[] args)
    {
        var spawnNetwork3 = false;
        string? repoRoot = null;
        var keepLogs = false;

        string? host = null;
        int? port = null;
        int? clients = null;
        TimeSpan? duration = null;
        int? seed = null;
        string? scenario = null;
        string? csv = null;
        string? outDir = null;
        int? channels = null;
        string? channelPrefix = null;
        double? mps = null;
        int? rampSeconds = null;
        int? rampJitterMs = null;

        string? obsUrl = null;
        TimeSpan? sampleInterval = null;
        int? serverPid = null;

        long? maxQueueDepth = null;
        long? maxQueueMaxDepth = null;
        long? maxOverflowDiscDelta = null;
        long? maxDroppedDelta = null;
        long? maxFloodKicksDelta = null;
        long? maxAutoDlineDelta = null;
        long? maxObsErrors = null;
        double? maxWsGrowthPct = null;

        int? joinPartHoldMs = null;
        int? joinPartPauseMs = null;
        int? churnMinS = null;
        int? churnMaxS = null;

        int? splitHealIntervalS = null;
        int? splitHealDowntimeS = null;

        var showHelp = false;

        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a is "--help" or "-?" or "-help") { showHelp = true; continue; }

            if (a is "--spawn-network3") { spawnNetwork3 = true; continue; }
            else if (a is "--repo-root") repoRoot = args[++i];
            else if (a is "--keep-logs") { keepLogs = true; continue; }
            else if (a is "--splitheal-interval-s") splitHealIntervalS = int.Parse(args[++i], CultureInfo.InvariantCulture);
            else if (a is "--splitheal-downtime-s") splitHealDowntimeS = int.Parse(args[++i], CultureInfo.InvariantCulture);

            if (a is "-h" or "--host") host = args[++i];
            else if (a is "-p" or "--port") port = int.Parse(args[++i], CultureInfo.InvariantCulture);
            else if (a is "-n" or "--clients") clients = int.Parse(args[++i], CultureInfo.InvariantCulture);
            else if (a is "-d" or "--duration") duration = TimeSpan.FromSeconds(double.Parse(args[++i], CultureInfo.InvariantCulture));
            else if (a is "--seed") seed = int.Parse(args[++i], CultureInfo.InvariantCulture);
            else if (a is "-s" or "--scenario") scenario = args[++i];
            else if (a is "--out") outDir = args[++i];
            else if (a is "--csv") csv = args[++i];
            else if (a is "--channels") channels = int.Parse(args[++i], CultureInfo.InvariantCulture);
            else if (a is "--channel-prefix") channelPrefix = args[++i];
            else if (a is "--mps") mps = double.Parse(args[++i], CultureInfo.InvariantCulture);
            else if (a is "--ramp-seconds") rampSeconds = int.Parse(args[++i], CultureInfo.InvariantCulture);
            else if (a is "--ramp-jitter-ms") rampJitterMs = int.Parse(args[++i], CultureInfo.InvariantCulture);

            else if (a is "--obs-url") obsUrl = args[++i];
            else if (a is "--sample-seconds") sampleInterval = TimeSpan.FromSeconds(double.Parse(args[++i], CultureInfo.InvariantCulture));
            else if (a is "--server-pid") serverPid = int.Parse(args[++i], CultureInfo.InvariantCulture);

            else if (a is "--max-queue-depth") maxQueueDepth = long.Parse(args[++i], CultureInfo.InvariantCulture);
            else if (a is "--max-queue-max-depth") maxQueueMaxDepth = long.Parse(args[++i], CultureInfo.InvariantCulture);
            else if (a is "--max-overflow-disc-delta") maxOverflowDiscDelta = long.Parse(args[++i], CultureInfo.InvariantCulture);
            else if (a is "--max-outbound-dropped-delta") maxDroppedDelta = long.Parse(args[++i], CultureInfo.InvariantCulture);
            else if (a is "--max-flood-kicks-delta") maxFloodKicksDelta = long.Parse(args[++i], CultureInfo.InvariantCulture);
            else if (a is "--max-autodline-delta") maxAutoDlineDelta = long.Parse(args[++i], CultureInfo.InvariantCulture);
            else if (a is "--max-obs-errors") maxObsErrors = long.Parse(args[++i], CultureInfo.InvariantCulture);
            else if (a is "--max-ws-growth-pct") maxWsGrowthPct = double.Parse(args[++i], CultureInfo.InvariantCulture);

            else if (a is "--joinpart-hold-ms") joinPartHoldMs = int.Parse(args[++i], CultureInfo.InvariantCulture);
            else if (a is "--joinpart-pause-ms") joinPartPauseMs = int.Parse(args[++i], CultureInfo.InvariantCulture);
            else if (a is "--churn-session-min-s") churnMinS = int.Parse(args[++i], CultureInfo.InvariantCulture);
            else if (a is "--churn-session-max-s") churnMaxS = int.Parse(args[++i], CultureInfo.InvariantCulture);
        }

        return new LoadTestOptions
        {
            SpawnNetwork3 = spawnNetwork3,
            RepoRoot = string.IsNullOrWhiteSpace(repoRoot) ? null : repoRoot,
            KeepLogs = keepLogs,

            Host = host ?? "127.0.0.1",
            Port = port ?? 6667,
            Clients = clients ?? 1000,
            Duration = duration ?? TimeSpan.FromSeconds(30),
            Seed = seed ?? 12345,
            Scenario = ParseScenario(scenario),
            CsvPath = string.IsNullOrWhiteSpace(csv) ? null : csv,
            OutDir = string.IsNullOrWhiteSpace(outDir) ? null : outDir,
            ChannelCount = channels ?? 10,
            ChannelPrefix = channelPrefix ?? "#lt",
            MessagesPerSecondPerClient = mps ?? 5,

            ObservabilityUrl = NormalizeObsUrl(obsUrl),
            SampleInterval = sampleInterval is { } si && si > TimeSpan.Zero ? si : TimeSpan.FromSeconds(5),
            ServerPid = serverPid,

            JoinPartHold = joinPartHoldMs is > 0 ? TimeSpan.FromMilliseconds(joinPartHoldMs.Value) : TimeSpan.FromMilliseconds(200),
            JoinPartPause = joinPartPauseMs is > 0 ? TimeSpan.FromMilliseconds(joinPartPauseMs.Value) : TimeSpan.FromMilliseconds(50),
            ChurnSessionMin = churnMinS is > 0 ? TimeSpan.FromSeconds(churnMinS.Value) : TimeSpan.FromSeconds(20),
            ChurnSessionMax = churnMaxS is > 0 ? TimeSpan.FromSeconds(churnMaxS.Value) : TimeSpan.FromSeconds(60),

            MaxOutboundQueueDepth = maxQueueDepth ?? 5000,
            MaxOutboundQueueMaxDepth = maxQueueMaxDepth ?? 20000,
            MaxQueueOverflowDisconnectsDelta = maxOverflowDiscDelta ?? 0,
            MaxOutboundDroppedDelta = maxDroppedDelta ?? 0,
            MaxFloodKicksDelta = maxFloodKicksDelta ?? 0,
            MaxAutoDlineDelta = maxAutoDlineDelta ?? 0,
            MaxObservabilityErrors = maxObsErrors ?? 0,
            MaxWorkingSetGrowthPercent = maxWsGrowthPct ?? 20.0,

            RampUpSeconds = rampSeconds is > 0 ? rampSeconds.Value : 0,
            RampUpJitterMs = rampJitterMs is > 0 ? rampJitterMs.Value : 0,

            SplitHealInterval = splitHealIntervalS is > 0 ? TimeSpan.FromSeconds(splitHealIntervalS.Value) : TimeSpan.FromSeconds(120),
            SplitHealDowntime = splitHealDowntimeS is > 0 ? TimeSpan.FromSeconds(splitHealDowntimeS.Value) : TimeSpan.FromSeconds(10),

            ShowHelp = showHelp,
        };
    }

    public LoadTestOptions WithTargets(string host, int port, string? observabilityUrl, int? serverPid)
    {
        return new LoadTestOptions
        {
            SpawnNetwork3 = SpawnNetwork3,
            RepoRoot = RepoRoot,
            KeepLogs = KeepLogs,

            Host = host,
            Port = port,

            ObservabilityUrl = observabilityUrl,
            SampleInterval = SampleInterval,
            ServerPid = serverPid,

            Clients = Clients,
            Duration = Duration,
            Seed = Seed,

            RampUpSeconds = RampUpSeconds,
            RampUpJitterMs = RampUpJitterMs,
            Scenario = Scenario,

            ChannelPrefix = ChannelPrefix,
            ChannelCount = ChannelCount,

            PingInterval = PingInterval,
            JoinPartHold = JoinPartHold,
            JoinPartPause = JoinPartPause,
            ChurnSessionMin = ChurnSessionMin,
            ChurnSessionMax = ChurnSessionMax,
            MessagesPerSecondPerClient = MessagesPerSecondPerClient,

            LatencySamples = LatencySamples,
            CsvPath = CsvPath,
            OutDir = OutDir,

            ReconnectBaseDelay = ReconnectBaseDelay,
            ReconnectJitterMs = ReconnectJitterMs,
            ReconnectMaxDelay = ReconnectMaxDelay,

            MaxOutboundQueueDepth = MaxOutboundQueueDepth,
            MaxOutboundQueueMaxDepth = MaxOutboundQueueMaxDepth,
            MaxQueueOverflowDisconnectsDelta = MaxQueueOverflowDisconnectsDelta,
            MaxOutboundDroppedDelta = MaxOutboundDroppedDelta,
            MaxFloodKicksDelta = MaxFloodKicksDelta,
            MaxAutoDlineDelta = MaxAutoDlineDelta,
            MaxObservabilityErrors = MaxObservabilityErrors,
            MaxWorkingSetGrowthPercent = MaxWorkingSetGrowthPercent,

            SplitHealInterval = SplitHealInterval,
            SplitHealDowntime = SplitHealDowntime,

            ShowHelp = ShowHelp,
        };
    }

    private static Scenario ParseScenario(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
            return Scenario.Idle;

        return s.Trim().ToLowerInvariant() switch
        {
            "idle" => Scenario.Idle,
            "churn" => Scenario.Churn,
            "joinpart" or "join_part" or "join-part" => Scenario.Churn,
            "chat" => Scenario.Chat,
            "flood" or "messageflood" or "message_flood" or "message-flood" => Scenario.Chat,
            "splitheal" or "split-heal" or "split_heal" => Scenario.SplitHeal,
            _ => Scenario.Idle,
        };
    }

    private static string? NormalizeObsUrl(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
            return null;

        var trimmed = s.Trim();
        if (!trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            trimmed = "http://" + trimmed;

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
            return uri.ToString().TrimEnd('/');

        return null;
    }

    public string? ResolveOutDir(string runId)
    {
        if (string.IsNullOrWhiteSpace(OutDir))
            return null;
        return Path.Combine(OutDir, "run-" + runId);
    }

    public string? ResolveCsvPath(string runId, string? resolvedOutDir)
    {
        if (!string.IsNullOrWhiteSpace(CsvPath))
            return CsvPath;
        if (!string.IsNullOrWhiteSpace(resolvedOutDir))
            return Path.Combine(resolvedOutDir, "samples.csv");
        return null;
    }

    public Verdict EvaluateVerdict(
        StatsSnapshot clientFinal,
        ObservabilitySnapshot? firstObs,
        ObservabilitySnapshot? lastObs,
        long maxQueueDepthObserved,
        long maxQueueMaxDepthObserved,
        long? firstWorkingSetBytesObserved,
        long? maxWorkingSetBytesObserved,
        long obsErrorsTotal)
    {
        var reasons = new List<string>();

        if (obsErrorsTotal > MaxObservabilityErrors)
            reasons.Add($"observability_errors_total={obsErrorsTotal} > max={MaxObservabilityErrors}");

        if (maxQueueDepthObserved > MaxOutboundQueueDepth)
            reasons.Add($"outbound_queue_depth_max={maxQueueDepthObserved} > max={MaxOutboundQueueDepth}");

        if (maxQueueMaxDepthObserved > MaxOutboundQueueMaxDepth)
            reasons.Add($"outbound_queue_max_depth_max={maxQueueMaxDepthObserved} > max={MaxOutboundQueueMaxDepth}");

        static long GetMetricDelta(ObservabilitySnapshot? a, ObservabilitySnapshot? b, string name)
        {
            var av = a is not null && a.Metrics.TryGetValue(name, out var va) ? va : 0;
            var bv = b is not null && b.Metrics.TryGetValue(name, out var vb) ? vb : 0;
            return Math.Max(0, bv - av);
        }

        var overflowDiscDelta = GetMetricDelta(firstObs, lastObs, "ircd_outbound_queue_overflow_disconnects_total");
        if (overflowDiscDelta > MaxQueueOverflowDisconnectsDelta)
            reasons.Add($"queue_overflow_disconnects_delta={overflowDiscDelta} > max={MaxQueueOverflowDisconnectsDelta}");

        var droppedDelta = GetMetricDelta(firstObs, lastObs, "ircd_outbound_queue_dropped_total");
        if (droppedDelta > MaxOutboundDroppedDelta)
            reasons.Add($"outbound_dropped_delta={droppedDelta} > max={MaxOutboundDroppedDelta}");

        var floodKicksDelta = GetMetricDelta(firstObs, lastObs, "ircd_flood_kicks_total");
        if (floodKicksDelta > MaxFloodKicksDelta)
            reasons.Add($"flood_kicks_delta={floodKicksDelta} > max={MaxFloodKicksDelta}");

        var autoDlineDelta = GetMetricDelta(firstObs, lastObs, "ircd_autodline_total");
        if (autoDlineDelta > MaxAutoDlineDelta)
            reasons.Add($"autodline_delta={autoDlineDelta} > max={MaxAutoDlineDelta}");

        if (firstWorkingSetBytesObserved is long ws0 && maxWorkingSetBytesObserved is long wsMax && ws0 > 0)
        {
            var growthPct = (wsMax - ws0) * 100.0 / ws0;
            if (growthPct > MaxWorkingSetGrowthPercent)
                reasons.Add($"working_set_growth_pct={growthPct:0.0} > max={MaxWorkingSetGrowthPercent:0.0}");
        }

        if (Clients > 0 && clientFinal.RegisteredMax <= 0)
            reasons.Add("no_registered_clients_observed");

        return new Verdict(Passed: reasons.Count == 0, Reasons: reasons);
    }
}

readonly record struct Verdict(bool Passed, List<string> Reasons);

sealed class ObservabilitySampler : IDisposable
{
    private readonly string _baseUrl;
    private readonly HttpClient _http;
    private readonly int? _serverPid;

    private long _errors;
    private DateTimeOffset? _lastCpuAt;
    private TimeSpan? _lastCpuTotal;

    public ObservabilitySampler(string baseUrl, int? serverPid)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _serverPid = serverPid;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
    }

    public long ErrorsTotal => Interlocked.Read(ref _errors);

    public async Task<ObservabilitySnapshot> SampleAsync(CancellationToken ct)
    {
        var healthOk = false;
        int? healthStatus = null;

        try
        {
            using var resp = await _http.GetAsync(_baseUrl + "/healthz", ct);
            healthStatus = (int)resp.StatusCode;
            healthOk = resp.StatusCode == HttpStatusCode.OK;
        }
        catch
        {
            Interlocked.Increment(ref _errors);
        }

        var metrics = new Dictionary<string, long>(StringComparer.Ordinal);
        int? metricsStatus = null;

        try
        {
            using var resp = await _http.GetAsync(_baseUrl + "/metrics", ct);
            metricsStatus = (int)resp.StatusCode;

            if (resp.StatusCode == HttpStatusCode.OK)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                ParsePrometheusText(body, metrics);
            }
            else
            {
                Interlocked.Increment(ref _errors);
            }
        }
        catch
        {
            Interlocked.Increment(ref _errors);
        }

        long? workingSet = null;
        double? cpuPct = null;

        if (_serverPid is int pid)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                workingSet = p.WorkingSet64;

                var now = DateTimeOffset.UtcNow;
                var cpu = p.TotalProcessorTime;
                if (_lastCpuAt is DateTimeOffset lastAt && _lastCpuTotal is TimeSpan lastCpu)
                {
                    var wall = (now - lastAt).TotalSeconds;
                    var cpuDelta = (cpu - lastCpu).TotalSeconds;
                    if (wall > 0)
                    {
                        cpuPct = cpuDelta / wall * 100.0 / Math.Max(1, Environment.ProcessorCount);
                        if (cpuPct < 0) cpuPct = 0;
                    }
                }

                _lastCpuAt = now;
                _lastCpuTotal = cpu;
            }
            catch
            {
                // ignore: PID might not exist or access denied
            }
        }

        var status = metricsStatus ?? healthStatus;

        return new ObservabilitySnapshot(
            HealthOk: healthOk,
            HttpStatusCode: status,
            Metrics: metrics,
            ServerWorkingSetBytes: workingSet,
            ServerCpuPercent: cpuPct);
    }

    public void Dispose() => _http.Dispose();

    private static void ParsePrometheusText(string body, Dictionary<string, long> dst)
    {
        var lines = body.Split('\n');
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0)
                continue;
            if (line[0] == '#')
                continue;

            var space = line.IndexOf(' ');
            if (space <= 0 || space + 1 >= line.Length)
                continue;

            var namePart = line[..space];
            var name = namePart;
            var brace = namePart.IndexOf('{');
            if (brace > 0)
                name = namePart[..brace];

            var valuePart = line[(space + 1)..].Trim();
            if (valuePart.Length == 0)
                continue;

            if (long.TryParse(valuePart, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
            {
                dst[name] = v;
            }
        }
    }
}

sealed record ObservabilitySnapshot(
    bool HealthOk,
    int? HttpStatusCode,
    Dictionary<string, long> Metrics,
    long? ServerWorkingSetBytes,
    double? ServerCpuPercent);

sealed class Network3Cluster : IAsyncDisposable
{
    private readonly string _repoRoot;
    private readonly string _rootDir;
    private readonly bool _keepLogs;

    private readonly ServerProcess _a;
    private readonly ServerProcess _b;
    private readonly ServerProcess _c;

    private readonly string _confA;
    private readonly string _confB;
    private readonly string _confC;

    public int NodeAClientPort { get; }
    public string NodeAObservabilityUrl { get; }
    public int? NodeAProcessId => _a.ProcessId;

    private Network3Cluster(
        string repoRoot,
        string rootDir,
        bool keepLogs,
        ServerProcess a,
        ServerProcess b,
        ServerProcess c,
        string confA,
        string confB,
        string confC,
        int nodeAClientPort,
        string nodeAObsUrl)
    {
        _repoRoot = repoRoot;
        _rootDir = rootDir;
        _keepLogs = keepLogs;
        _a = a;
        _b = b;
        _c = c;
        _confA = confA;
        _confB = confB;
        _confC = confC;
        NodeAClientPort = nodeAClientPort;
        NodeAObservabilityUrl = nodeAObsUrl;
    }

    public static async Task<Network3Cluster> StartAsync(LoadTestOptions options, string runId, string? outDir, CancellationToken ct)
    {
        var repoRoot = string.IsNullOrWhiteSpace(options.RepoRoot)
            ? Directory.GetCurrentDirectory()
            : options.RepoRoot;

        var serverCsproj = Path.Combine(repoRoot, "IRCd.Server", "IRCd.Server.csproj");
        if (!File.Exists(serverCsproj))
            throw new FileNotFoundException($"Could not find IRCd.Server.csproj at {serverCsproj}. Use --repo-root.");

        var baseDir = outDir ?? Path.Combine(Path.GetTempPath(), "IRCd.LoadTest", "run-" + runId);
        Directory.CreateDirectory(baseDir);

        var workDir = Path.Combine(baseDir, "net3");
        Directory.CreateDirectory(workDir);

        var nodeDirA = Path.Combine(workDir, "A");
        var nodeDirB = Path.Combine(workDir, "B");
        var nodeDirC = Path.Combine(workDir, "C");
        Directory.CreateDirectory(nodeDirA);
        Directory.CreateDirectory(nodeDirB);
        Directory.CreateDirectory(nodeDirC);

        var (aClient, aServer, aObs) = AllocateNodePorts();
        var (bClient, bServer, bObs) = AllocateNodePorts();
        var (cClient, cServer, cObs) = AllocateNodePorts();

        var pw = "itest";

        var baseConf = Path.Combine(repoRoot, "IRCd.Server", "confs", "ircd.conf");
        baseConf = Path.GetFullPath(baseConf);

        var confA = BuildNodeConf(baseConf, name: "A", sid: "001", clientPort: aClient, serverPort: aServer, obsPort: aObs,
            peers: [new LinkPeer("B", "002", bServer, Outbound: true)],
            linkPassword: pw);

        var confB = BuildNodeConf(baseConf, name: "B", sid: "002", clientPort: bClient, serverPort: bServer, obsPort: bObs,
            peers: [new LinkPeer("A", "001", aServer, Outbound: false), new LinkPeer("C", "003", cServer, Outbound: false)],
            linkPassword: pw);

        var confC = BuildNodeConf(baseConf, name: "C", sid: "003", clientPort: cClient, serverPort: cServer, obsPort: cObs,
            peers: [new LinkPeer("B", "002", bServer, Outbound: true)],
            linkPassword: pw);

        var confPathA = Path.Combine(nodeDirA, "ircd.conf");
        var confPathB = Path.Combine(nodeDirB, "ircd.conf");
        var confPathC = Path.Combine(nodeDirC, "ircd.conf");

        await File.WriteAllTextAsync(confPathA, confA, ct);
        await File.WriteAllTextAsync(confPathB, confB, ct);
        await File.WriteAllTextAsync(confPathC, confC, ct);

        var procA = new ServerProcess(repoRoot, nodeDirA, nodeName: "A", observabilityPort: aObs);
        var procB = new ServerProcess(repoRoot, nodeDirB, nodeName: "B", observabilityPort: bObs);
        var procC = new ServerProcess(repoRoot, nodeDirC, nodeName: "C", observabilityPort: cObs);

        await procA.StartAsync(confPathA, ct);
        await procB.StartAsync(confPathB, ct);
        await procC.StartAsync(confPathC, ct);

        return new Network3Cluster(
            repoRoot: repoRoot,
            rootDir: workDir,
            keepLogs: options.KeepLogs,
            a: procA,
            b: procB,
            c: procC,
            confA: confPathA,
            confB: confPathB,
            confC: confPathC,
            nodeAClientPort: aClient,
            nodeAObsUrl: $"http://127.0.0.1:{aObs.ToString(CultureInfo.InvariantCulture)}");
    }

    public async Task RunSplitHealAsync(TimeSpan interval, TimeSpan downtime, CancellationToken ct)
    {
        if (interval <= TimeSpan.Zero)
            interval = TimeSpan.FromSeconds(120);
        if (downtime < TimeSpan.Zero)
            downtime = TimeSpan.Zero;

        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(interval, ct);
            if (ct.IsCancellationRequested)
                break;

            try { await _b.StopAsync(ct); } catch { }
            await Task.Delay(downtime, ct);
            if (ct.IsCancellationRequested)
                break;
            try { await _b.StartAsync(_confB, ct); } catch { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await _a.StopAsync(cts.Token); } catch { }
        try { await _b.StopAsync(cts.Token); } catch { }
        try { await _c.StopAsync(cts.Token); } catch { }

        if (_keepLogs)
            return;

        try
        {
            if (Directory.Exists(_rootDir))
                Directory.Delete(_rootDir, recursive: true);
        }
        catch
        {
            // ignore
        }
    }

    private static (int ClientPort, int ServerPort, int ObservabilityPort) AllocateNodePorts()
    {
        return (GetFreeTcpPort(), GetFreeTcpPort(), GetFreeTcpPort());
    }

    private static int GetFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static string BuildNodeConf(string includeConfPath, string name, string sid, int clientPort, int serverPort, int obsPort, LinkPeer[] peers, string linkPassword)
    {
        static string Escape(string s) => s.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

        var sb = new StringBuilder();

        sb.AppendLine($"include \"{Escape(includeConfPath)}\";");
        sb.AppendLine();

        sb.AppendLine("serverinfo {");
        sb.AppendLine($"    name = \"{Escape(name)}\";");
        sb.AppendLine($"    sid = \"{Escape(sid)}\";");
        sb.AppendLine($"    description = \"{Escape(name)}\";");
        sb.AppendLine("    network = \"Soak\";");
        sb.AppendLine("    version = \"Soak\";");
        sb.AppendLine("};");
        sb.AppendLine();

        sb.AppendLine("listen {");
        sb.AppendLine("    bind = \"127.0.0.1\";");
        sb.AppendLine($"    clientport = {clientPort.ToString(CultureInfo.InvariantCulture)};");
        sb.AppendLine("    enabletls = false;");
        sb.AppendLine($"    serverport = {serverPort.ToString(CultureInfo.InvariantCulture)};");
        sb.AppendLine("};");
        sb.AppendLine();

        sb.AppendLine("observability {");
        sb.AppendLine("    enabled = true;");
        sb.AppendLine("    bind = \"127.0.0.1\";");
        sb.AppendLine($"    port = {obsPort.ToString(CultureInfo.InvariantCulture)};");
        sb.AppendLine("};");
        sb.AppendLine();

        foreach (var peer in peers)
        {
            sb.AppendLine("link {");
            sb.AppendLine($"    name = \"{Escape(peer.Name)}\";");
            sb.AppendLine($"    sid = \"{Escape(peer.Sid)}\";");
            sb.AppendLine("    host = \"127.0.0.1\";");
            sb.AppendLine($"    port = {peer.ServerPort.ToString(CultureInfo.InvariantCulture)};");
            sb.AppendLine($"    password = \"{Escape(linkPassword)}\";");
            sb.AppendLine($"    outbound = {(peer.Outbound ? "true" : "false")};");
            sb.AppendLine("    usersync = true;");
            sb.AppendLine("};");
            sb.AppendLine();
        }

        return sb.ToString();
    }
}

sealed record LinkPeer(string Name, string Sid, int ServerPort, bool Outbound);

sealed class ServerProcess
{
    private readonly string _repoRoot;
    private readonly string _workDir;
    private readonly string _nodeName;
    private readonly int _observabilityPort;

    private Process? _process;

    public ServerProcess(string repoRoot, string workDir, string nodeName, int observabilityPort)
    {
        _repoRoot = repoRoot;
        _workDir = workDir;
        _nodeName = nodeName;
        _observabilityPort = observabilityPort;
        Directory.CreateDirectory(_workDir);
    }

    public string StdoutPath => Path.Combine(_workDir, "stdout.log");
    public string StderrPath => Path.Combine(_workDir, "stderr.log");
    public int? ProcessId => _process is null ? null : _process.Id;

    public async Task StartAsync(string configPath, CancellationToken ct)
    {
        if (_process is { HasExited: false })
            throw new InvalidOperationException($"Server process already running: {_nodeName}");

        var stdout = new FileStream(StdoutPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        var stderr = new FileStream(StderrPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = _repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        psi.ArgumentList.Add("run");
        psi.ArgumentList.Add("--no-build");
        psi.ArgumentList.Add("--project");
        psi.ArgumentList.Add(Path.Combine(_repoRoot, "IRCd.Server", "IRCd.Server.csproj"));
        psi.ArgumentList.Add("--");
        psi.ArgumentList.Add("--Irc:ConfigFile");
        psi.ArgumentList.Add(configPath);

        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        if (!p.Start())
            throw new InvalidOperationException($"Failed to start server process for {_nodeName}");

        _process = p;

        _ = PumpStreamAsync(p.StandardOutput, stdout, ct);
        _ = PumpStreamAsync(p.StandardError, stderr, ct);

        await WaitForHealthyAsync(ct);
    }

    public async Task StopAsync(CancellationToken ct)
    {
        var p = _process;
        if (p is null)
            return;

        try
        {
            if (!p.HasExited)
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                try { await p.WaitForExitAsync(ct); } catch { }
            }
        }
        finally
        {
            try { p.Dispose(); } catch { }
            _process = null;
        }
    }

    private async Task WaitForHealthyAsync(CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var url = $"http://127.0.0.1:{_observabilityPort.ToString(CultureInfo.InvariantCulture)}/healthz";
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);

        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            if (_process is { HasExited: true })
                throw new InvalidOperationException($"Server process for {_nodeName} exited early. Logs: {StdoutPath} {StderrPath}");

            try
            {
                using var resp = await http.GetAsync(url, ct);
                if (resp.StatusCode == HttpStatusCode.OK)
                    return;
            }
            catch
            {
                // retry
            }

            await Task.Delay(200, ct);
        }

        throw new TimeoutException($"Timed out waiting for /healthz on {_nodeName} ({url}). Logs: {StdoutPath} {StderrPath}");
    }

    private static async Task PumpStreamAsync(StreamReader reader, FileStream output, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync();
                if (line is null)
                    break;

                var bytes = Encoding.UTF8.GetBytes(line + Environment.NewLine);
                await output.WriteAsync(bytes, ct);
                await output.FlushAsync(ct);
            }
        }
        catch
        {
            // best-effort
        }
        finally
        {
            try { output.Dispose(); } catch { }
        }
    }
}

sealed class AtomicLong
{
    private long _value;

    public AtomicLong(long initial = 0) => _value = initial;

    public long Value => Interlocked.Read(ref _value);

    public void Increment() => Interlocked.Increment(ref _value);

    public void Decrement()
    {
        var v = Interlocked.Decrement(ref _value);
        if (v < 0) Interlocked.Exchange(ref _value, 0);
    }
}

sealed class GlobalStats
{
    public AtomicLong Connected { get; } = new();
    public AtomicLong Registered { get; } = new();

    public AtomicLong ErrorsTotal { get; } = new();

    public AtomicLong MessagesSent { get; } = new();
    public AtomicLong MessagesReceived { get; } = new();

    public AtomicLong Joins { get; } = new();
    public AtomicLong Parts { get; } = new();

    private long _connectedMax;
    private long _registeredMax;

    public StatsSnapshot Snapshot()
    {
        var connected = Connected.Value;
        var registered = Registered.Value;

        TrackMax(ref _connectedMax, connected);
        TrackMax(ref _registeredMax, registered);

        return new StatsSnapshot(
            Connected: connected,
            Registered: registered,
            Errors: ErrorsTotal.Value,
            MessagesSent: MessagesSent.Value,
            MessagesReceived: MessagesReceived.Value,
            Joins: Joins.Value,
            Parts: Parts.Value,
            ConnectedMax: Interlocked.Read(ref _connectedMax),
            RegisteredMax: Interlocked.Read(ref _registeredMax));
    }

    private static void TrackMax(ref long max, long v)
    {
        long cur;
        while ((cur = Interlocked.Read(ref max)) < v)
        {
            if (Interlocked.CompareExchange(ref max, v, cur) == cur)
                break;
        }
    }
}

readonly record struct StatsSnapshot(
    long Connected,
    long Registered,
    long Errors,
    long MessagesSent,
    long MessagesReceived,
    long Joins,
    long Parts,
    long ConnectedMax,
    long RegisteredMax);

sealed class LatencyTracker
{
    private readonly object _lock = new();
    private readonly double[] _samples;
    private int _count;

    public LatencyTracker(int maxSamples)
    {
        _samples = new double[Math.Max(1000, maxSamples)];
    }

    public void Add(double ms)
    {
        lock (_lock)
        {
            if (_count >= _samples.Length)
                return;

            _samples[_count++] = ms;
        }
    }

    public LatencySnapshot SnapshotAndRotate()
    {
        double[] data;
        int n;

        lock (_lock)
        {
            n = _count;
            if (n == 0)
                return default;

            data = new double[n];
            Array.Copy(_samples, data, n);
            _count = 0;
        }

        Array.Sort(data);
        return LatencySnapshot.FromSorted(data);
    }

    public LatencySnapshot FinalSnapshot()
    {
        double[] data;
        int n;

        lock (_lock)
        {
            n = _count;
            if (n == 0)
                return default;

            data = new double[n];
            Array.Copy(_samples, data, n);
        }

        Array.Sort(data);
        return LatencySnapshot.FromSorted(data);
    }
}

readonly record struct LatencySnapshot(double P50Ms, double P95Ms, double P99Ms)
{
    public static LatencySnapshot FromSorted(double[] sorted)
    {
        if (sorted.Length == 0)
            return default;

        return new LatencySnapshot(
            P50Ms: Percentile(sorted, 0.50),
            P95Ms: Percentile(sorted, 0.95),
            P99Ms: Percentile(sorted, 0.99));
    }

    private static double Percentile(double[] sorted, double p)
    {
        if (sorted.Length == 0) return 0;
        if (p <= 0) return sorted[0];
        if (p >= 1) return sorted[^1];

        var idx = (int)Math.Round((sorted.Length - 1) * p, MidpointRounding.AwayFromZero);
        idx = Math.Clamp(idx, 0, sorted.Length - 1);
        return sorted[idx];
    }
}
