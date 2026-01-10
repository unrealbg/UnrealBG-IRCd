namespace IRCd.Tests
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Core.Protocol;
    using IRCd.Core.Services;
    using IRCd.Core.State;
    using IRCd.Shared.Options;
    using IRCd.Tests.TestDoubles;

    using Microsoft.Extensions.Logging.Abstractions;
    using Microsoft.Extensions.Options;

    using Xunit;

    public sealed class S2SSplitHealConvergenceTests
    {
        [Fact]
        public async Task SplitHeal_ChannelStateConverges_ByLowerChannelTs()
        {
            // Server A (older TS wins)
            var stateA = new ServerState();
            var sessionsA = new FakeSessionRegistry();
            var routingA = new RoutingService(sessionsA, new IrcFormatter());
            var silenceA = new SilenceService();

            var optionsA = new IrcOptions
            {
                ServerInfo = new ServerInfoOptions { Name = "a", Sid = "001", Description = "A" },
                Links = new[]
                {
                    new LinkOptions { Name = "b", Sid = "002", Password = "pw", Outbound = true, UserSync = true }
                }
            };

            var watchA = new WatchService(Options.Create(optionsA), routingA);
            var svcA = new ServerLinkService(NullLogger<ServerLinkService>.Instance, new OptionsMonitorStub<IrcOptions>(optionsA), stateA, routingA, sessionsA, silenceA, watchA);

            // Server B (newer TS loses)
            var stateB = new ServerState();
            var sessionsB = new FakeSessionRegistry();
            var routingB = new RoutingService(sessionsB, new IrcFormatter());
            var silenceB = new SilenceService();

            var optionsB = new IrcOptions
            {
                ServerInfo = new ServerInfoOptions { Name = "b", Sid = "002", Description = "B" },
                Links = new[]
                {
                    new LinkOptions { Name = "a", Sid = "001", Password = "pw", Outbound = false, UserSync = true }
                }
            };

            var watchB = new WatchService(Options.Create(optionsB), routingB);
            var svcB = new ServerLinkService(NullLogger<ServerLinkService>.Instance, new OptionsMonitorStub<IrcOptions>(optionsB), stateB, routingB, sessionsB, silenceB, watchB);

            // Seed users/channels independently
            stateA.TryAddUser(new User { ConnectionId = "uA", Uid = "001UA", Nick = "Alice", UserName = "a", Host = "hA", IsRegistered = true, IsRemote = false, RemoteSid = "001", NickTs = 10 });
            stateB.TryAddUser(new User { ConnectionId = "uB", Uid = "002UB", Nick = "Bob", UserName = "b", Host = "hB", IsRegistered = true, IsRemote = false, RemoteSid = "002", NickTs = 20 });

            var chA = stateA.GetOrCreateChannel("#c");
            chA.CreatedTs = 100;
            stateA.TryJoinChannel("uA", "Alice", "#c");
            chA.TryUpdateMemberPrivilege("uA", ChannelPrivilege.Op);
            chA.ApplyModeChange(ChannelModes.Moderated, enable: true); // +m
            chA.SetTopic("topic-from-a", setBy: "Alice");

            var chB = stateB.GetOrCreateChannel("#c");
            chB.CreatedTs = 200;
            stateB.TryJoinChannel("uB", "Bob", "#c");
            chB.TryUpdateMemberPrivilege("uB", ChannelPrivilege.Op);
            chB.ApplyModeChange(ChannelModes.Secret, enable: true); // +s (should lose)
            chB.SetTopic("topic-from-b", setBy: "Bob");

            // Connect the two servers via paired sessions.
            var (sessA, sessB) = PairedServerLinkSession.CreatePair(connectionIdA: "linkA", connectionIdB: "linkB");

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));

            var inboundTask = svcB.HandleIncomingLinkAsync(sessB, cts.Token);
            var outboundTask = svcA.HandleOutboundLinkAsync(sessA, optionsA.Links[0], cts.Token);

            // Wait until both sides consider the link authenticated.
            var start = DateTime.UtcNow;
            while (!(sessA.IsAuthenticated && sessB.IsAuthenticated) && (DateTime.UtcNow - start) < TimeSpan.FromSeconds(2))
            {
                await Task.Delay(5, cts.Token);
            }

            Assert.True(sessA.IsAuthenticated, string.Join("\n", sessA.Outgoing));
            Assert.True(sessB.IsAuthenticated, string.Join("\n", sessB.Outgoing));

            // Wait for convergence (bursts are asynchronous and sessions only record what they sent).
            start = DateTime.UtcNow;
            while ((DateTime.UtcNow - start) < TimeSpan.FromSeconds(2))
            {
                if (stateA.TryGetChannel("#c", out var maybeA) && maybeA is not null &&
                    stateB.TryGetChannel("#c", out var maybeB) && maybeB is not null &&
                    maybeA.CreatedTs == 100 &&
                    maybeB.CreatedTs == 100 &&
                    string.Equals(maybeA.Topic, "topic-from-a", StringComparison.Ordinal) &&
                    string.Equals(maybeB.Topic, "topic-from-a", StringComparison.Ordinal) &&
                    maybeA.Modes.HasFlag(ChannelModes.Moderated) &&
                    maybeB.Modes.HasFlag(ChannelModes.Moderated) &&
                    !maybeA.Modes.HasFlag(ChannelModes.Secret) &&
                    !maybeB.Modes.HasFlag(ChannelModes.Secret) &&
                    maybeA.Members.Count >= 2 &&
                    maybeB.Members.Count >= 2)
                {
                    break;
                }

                await Task.Delay(10, cts.Token);
            }

            // Convergence: lower channel TS should win on both sides.
            Assert.True(stateA.TryGetChannel("#c", out var finalA) && finalA is not null);
            Assert.True(stateB.TryGetChannel("#c", out var finalB) && finalB is not null);

            Assert.Equal(100, finalA!.CreatedTs);
            Assert.True(
                finalB!.CreatedTs == 100,
                $"Expected B channel TS to converge to 100, actual={finalB.CreatedTs}.\n" +
                "--- A Outgoing ---\n" + string.Join("\n", sessA.Outgoing) + "\n" +
                "--- B Outgoing ---\n" + string.Join("\n", sessB.Outgoing));

            // Topic should converge to A's topic (B's TOPICSET has higher channel TS so it is ignored by A;
            // B gets reset then applies A's TOPICSET).
            Assert.Equal("topic-from-a", finalA.Topic);
            Assert.Equal("topic-from-a", finalB.Topic);

            // Modes should converge to A's (A has +m, B had +s but loses).
            Assert.True(finalA.Modes.HasFlag(ChannelModes.Moderated));
            Assert.True(finalB.Modes.HasFlag(ChannelModes.Moderated));
            Assert.False(finalA.Modes.HasFlag(ChannelModes.Secret));
            Assert.False(finalB.Modes.HasFlag(ChannelModes.Secret));

            // Membership should converge with no dupes; Bob should be present on A and Alice present on B.
            Assert.Contains(finalA.Members, m => string.Equals(m.Nick, "Bob", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(finalB.Members, m => string.Equals(m.Nick, "Alice", StringComparison.OrdinalIgnoreCase));

            // Privileges: TS collision should strip younger-side ops; Alice remains op, Bob becomes normal.
            // Note: remote users are tracked under connectionId "uid:<UID>".
            Assert.Equal(ChannelPrivilege.Op, finalA.GetPrivilege("uA"));
            Assert.Equal(ChannelPrivilege.Op, finalB.GetPrivilege("uid:001UA"));

            Assert.Equal(ChannelPrivilege.Normal, finalA.GetPrivilege("uid:002UB"));
            Assert.Equal(ChannelPrivilege.Normal, finalB.GetPrivilege("uB"));

            cts.Cancel();
            sessA.Complete();
            sessB.Complete();
            try { await outboundTask; } catch (OperationCanceledException) { }
            try { await inboundTask; } catch (OperationCanceledException) { }
        }

        private sealed class OptionsMonitorStub<T> : IOptionsMonitor<T> where T : class
        {
            private readonly T _value;

            public OptionsMonitorStub(T value) => _value = value;

            public T CurrentValue => _value;

            public T Get(string? name) => _value;

            public IDisposable? OnChange(Action<T, string?> listener) => null;
        }
    }
}
