namespace IRCd.IntegrationTests
{
    using System;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.IntegrationTests.Infrastructure;

    using Xunit;
    using Xunit.Abstractions;

    public sealed class S2SIntegrationTests
    {
        private readonly ITestOutputHelper _output;

        public S2SIntegrationTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public async Task TwoNode_Netburst_UserSync_PropagatesNames()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

            var repoRoot = RepoRoot.Find();
            var keep = !string.Equals(Environment.GetEnvironmentVariable("IRCD_ITEST_CLEANUP"), "1", StringComparison.Ordinal);
            var rootDir = Path.Combine(repoRoot, ".artifacts", "itest", "ircd-itest-2node-" + Guid.NewGuid().ToString("N"));

            await using var cluster = new IntegrationCluster(repoRoot, rootDir, keepLogs: keep);
            _output.WriteLine($"Cluster dir: {cluster.RootDir}");

            var pw = "itest";
            var operName = "oper";
            var operPw = "itest";

            var (aClientPort, aServerPort, aObsPort) = TestPorts.AllocateNodePorts();
            var (bClientPort, bServerPort, bObsPort) = TestPorts.AllocateNodePorts();

            var nodeA = new ServerNodeConfig(Name: "A", Sid: "001", ClientPort: aClientPort, ServerPort: aServerPort, ObservabilityPort: aObsPort, LinkPassword: pw, OperName: operName, OperPassword: operPw);
            var nodeB = new ServerNodeConfig(Name: "B", Sid: "002", ClientPort: bClientPort, ServerPort: bServerPort, ObservabilityPort: bObsPort, LinkPassword: pw, OperName: operName, OperPassword: operPw);

            // A connects to B; B accepts inbound from A.
            await cluster.AddAndStartNodeAsync(nodeB, peers: new[] { new LinkPeer(nodeA.Name, nodeA.Sid, nodeA.ServerPort, Outbound: false) }, cts.Token);
            await cluster.AddAndStartNodeAsync(nodeA, peers: new[] { new LinkPeer(nodeB.Name, nodeB.Sid, nodeB.ServerPort, Outbound: true) }, cts.Token);

            await using var alice = new IrcTestClient("127.0.0.1", nodeA.ClientPort);
            await using var bob = new IrcTestClient("127.0.0.1", nodeB.ClientPort);

            await alice.RegisterAsync("Alice", "alice", cts.Token);
            await bob.RegisterAsync("Bob", "bob", cts.Token);

            await alice.JoinAsync("#test", cts.Token);
            await bob.JoinAsync("#test", cts.Token);

            await EventuallyAsync(async () =>
            {
                var aNames = await alice.GetNamesAsync("#test", cts.Token);
                var bNames = await bob.GetNamesAsync("#test", cts.Token);

                Assert.Contains("Alice", aNames);
                Assert.Contains("Bob", aNames);
                Assert.Contains("Alice", bNames);
                Assert.Contains("Bob", bNames);
            }, timeout: TimeSpan.FromSeconds(20), cts.Token);
        }

        [Fact]
        public async Task ThreeNode_SplitHeal_HubRestart_ConvergesNames()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));

            var repoRoot = RepoRoot.Find();
            var keep = !string.Equals(Environment.GetEnvironmentVariable("IRCD_ITEST_CLEANUP"), "1", StringComparison.Ordinal);
            var rootDir = Path.Combine(repoRoot, ".artifacts", "itest", "ircd-itest-3node-" + Guid.NewGuid().ToString("N"));

            await using var cluster = new IntegrationCluster(repoRoot, rootDir, keepLogs: keep);
            _output.WriteLine($"Cluster dir: {cluster.RootDir}");

            var pw = "itest";
            var operName = "oper";
            var operPw = "itest";

            var (aClientPort, aServerPort, aObsPort) = TestPorts.AllocateNodePorts();
            var (bClientPort, bServerPort, bObsPort) = TestPorts.AllocateNodePorts();
            var (cClientPort, cServerPort, cObsPort) = TestPorts.AllocateNodePorts();

            var nodeA = new ServerNodeConfig(Name: "A", Sid: "001", ClientPort: aClientPort, ServerPort: aServerPort, ObservabilityPort: aObsPort, LinkPassword: pw, OperName: operName, OperPassword: operPw);
            var nodeB = new ServerNodeConfig(Name: "B", Sid: "002", ClientPort: bClientPort, ServerPort: bServerPort, ObservabilityPort: bObsPort, LinkPassword: pw, OperName: operName, OperPassword: operPw);
            var nodeC = new ServerNodeConfig(Name: "C", Sid: "003", ClientPort: cClientPort, ServerPort: cServerPort, ObservabilityPort: cObsPort, LinkPassword: pw, OperName: operName, OperPassword: operPw);

            // Hub B dials out to A and C; A/C accept inbound from B.
            var (cfgA, procA) = await cluster.AddAndStartNodeAsync(nodeA, peers: new[] { new LinkPeer(nodeB.Name, nodeB.Sid, nodeB.ServerPort, Outbound: false) }, cts.Token);
            var (cfgC, procC) = await cluster.AddAndStartNodeAsync(nodeC, peers: new[] { new LinkPeer(nodeB.Name, nodeB.Sid, nodeB.ServerPort, Outbound: false) }, cts.Token);
            var (cfgB, procB) = await cluster.AddAndStartNodeAsync(nodeB, peers: new[]
            {
                new LinkPeer(nodeA.Name, nodeA.Sid, nodeA.ServerPort, Outbound: true),
                new LinkPeer(nodeC.Name, nodeC.Sid, nodeC.ServerPort, Outbound: true),
            }, cts.Token);

            await using var alice = new IrcTestClient("127.0.0.1", cfgA.ClientPort);
            await using var charlie = new IrcTestClient("127.0.0.1", cfgC.ClientPort);

            await alice.RegisterAsync("Alice", "alice", cts.Token);
            await charlie.RegisterAsync("Charlie", "charlie", cts.Token);

            await alice.JoinAsync("#test", cts.Token);
            await charlie.JoinAsync("#test", cts.Token);

            // Pre-split convergence
            await EventuallyAsync(async () =>
            {
                var aNames = await alice.GetNamesAsync("#test", cts.Token);
                Assert.Contains("Alice", aNames);
                Assert.Contains("Charlie", aNames);
            }, timeout: TimeSpan.FromSeconds(25), cts.Token);

            // Split: kill hub
            await procB.StopAsync(cts.Token);

            // During split, A should no longer see Charlie.
            await EventuallyAsync(async () =>
            {
                var aNames = await alice.GetNamesAsync("#test", cts.Token);
                Assert.Contains("Alice", aNames);
                Assert.DoesNotContain("Charlie", aNames);
            }, timeout: TimeSpan.FromSeconds(20), cts.Token);

            // Heal: restart hub with same config.
            var bNodeDir = Path.Combine(cluster.RootDir, nodeB.Name);
            var bConfPath = Path.Combine(bNodeDir, "ircd.conf");
            var restartedHub = new ServerProcess(repoRoot, bNodeDir, nodeB.Name, nodeB.ObservabilityPort);
            await restartedHub.StartAsync(bConfPath, cts.Token);

            // Post-heal convergence
            await EventuallyAsync(async () =>
            {
                var aNames = await alice.GetNamesAsync("#test", cts.Token);
                Assert.Contains("Alice", aNames);
                Assert.Contains("Charlie", aNames);

                var cNames = await charlie.GetNamesAsync("#test", cts.Token);
                Assert.Contains("Alice", cNames);
                Assert.Contains("Charlie", cNames);
            }, timeout: TimeSpan.FromSeconds(30), cts.Token);

            // Clean up restarted hub explicitly (cluster still holds the old procB)
            await restartedHub.DisposeAsync();
        }

        private static async Task EventuallyAsync(Func<Task> assertion, TimeSpan timeout, CancellationToken ct)
        {
            var deadline = DateTimeOffset.UtcNow + timeout;
            Exception? last = null;

            while (DateTimeOffset.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    await assertion();
                    return;
                }
                catch (Exception ex)
                {
                    last = ex;
                }

                await Task.Delay(1000, ct);
            }

            throw new TimeoutException($"Condition not met within {timeout}.", last);
        }
    }
}
