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

    public sealed class S2SThreeServerSplitHealConvergenceTests
    {
        [Fact]
        public async Task SplitHeal_ThreeServers_ChannelStateConverges_ByLowerChannelTs()
        {
            // Server A
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

            // Server B (hub)
            var stateB = new ServerState();
            var sessionsB = new FakeSessionRegistry();
            var routingB = new RoutingService(sessionsB, new IrcFormatter());
            var silenceB = new SilenceService();

            var linkBToC = new LinkOptions { Name = "c", Sid = "003", Password = "pw", Outbound = true, UserSync = true };

            var optionsB = new IrcOptions
            {
                ServerInfo = new ServerInfoOptions { Name = "b", Sid = "002", Description = "B" },
                Links = new[]
                {
                    new LinkOptions { Name = "a", Sid = "001", Password = "pw", Outbound = false, UserSync = true },
                    linkBToC
                }
            };

            var watchB = new WatchService(Options.Create(optionsB), routingB);
            var svcB = new ServerLinkService(NullLogger<ServerLinkService>.Instance, new OptionsMonitorStub<IrcOptions>(optionsB), stateB, routingB, sessionsB, silenceB, watchB);

            // Server C
            var stateC = new ServerState();
            var sessionsC = new FakeSessionRegistry();
            var routingC = new RoutingService(sessionsC, new IrcFormatter());
            var silenceC = new SilenceService();

            var optionsC = new IrcOptions
            {
                ServerInfo = new ServerInfoOptions { Name = "c", Sid = "003", Description = "C" },
                Links = new[]
                {
                    new LinkOptions { Name = "b", Sid = "002", Password = "pw", Outbound = false, UserSync = true }
                }
            };

            var watchC = new WatchService(Options.Create(optionsC), routingC);
            var svcC = new ServerLinkService(NullLogger<ServerLinkService>.Instance, new OptionsMonitorStub<IrcOptions>(optionsC), stateC, routingC, sessionsC, silenceC, watchC);

            // Seed local users (so they are present before link establishment).
            stateA.TryAddUser(new User { ConnectionId = "uA", Uid = "001UA", Nick = "Alice", UserName = "a", Host = "hA", IsRegistered = true, IsRemote = false, RemoteSid = "001", NickTs = 10 });
            stateC.TryAddUser(new User { ConnectionId = "uC", Uid = "003UC", Nick = "Carol", UserName = "c", Host = "hC", IsRegistered = true, IsRemote = false, RemoteSid = "003", NickTs = 20 });

            // Establish A<->B and B<->C.
            var (sessA_AB, sessB_AB) = PairedServerLinkSession.CreatePair(connectionIdA: "linkAB_A", connectionIdB: "linkAB_B");
            var (sessB_BC, sessC_BC) = PairedServerLinkSession.CreatePair(connectionIdA: "linkBC_B", connectionIdB: "linkBC_C");

            using var ctsAB = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            using var ctsBC = new CancellationTokenSource(TimeSpan.FromSeconds(6));

            var inboundBFromA = svcB.HandleIncomingLinkAsync(sessB_AB, ctsAB.Token);
            var outboundAToB = svcA.HandleOutboundLinkAsync(sessA_AB, optionsA.Links[0], ctsAB.Token);

            var inboundCFromB = svcC.HandleIncomingLinkAsync(sessC_BC, ctsBC.Token);
            var outboundBToC = svcB.HandleOutboundLinkAsync(sessB_BC, linkBToC, ctsBC.Token);

            await WaitForAuthAsync(sessA_AB, sessB_AB, ctsAB.Token);
            await WaitForAuthAsync(sessB_BC, sessC_BC, ctsBC.Token);

            // Split B<->C.
            ctsBC.Cancel();
            sessB_BC.Complete();
            sessC_BC.Complete();
            try { await outboundBToC; } catch (OperationCanceledException) { }
            try { await inboundCFromB; } catch (OperationCanceledException) { }

            // Divergent channel state while split.
            var chA = stateA.GetOrCreateChannel("#c");
            chA.CreatedTs = 100;
            stateA.TryJoinChannel("uA", "Alice", "#c");
            chA.TryUpdateMemberPrivilege("uA", ChannelPrivilege.Op);
            chA.ApplyModeChange(ChannelModes.Moderated, enable: true); // +m
            chA.SetTopic("topic-from-a", setBy: "Alice");

            // Propagate A-side changes to B over the still-alive A<->B link.
            await svcA.PropagateJoinAsync("001UA", "#c", ctsAB.Token);
            await svcA.PropagateChannelModesAsync("#c", 100, chA.FormatModeString(), ctsAB.Token);
            await svcA.PropagateTopicAsync("001UA", "#c", chA.Topic, ctsAB.Token);
            await svcA.PropagateMemberPrivilegeAsync("#c", "001UA", ChannelPrivilege.Op, ctsAB.Token);

            var chC = stateC.GetOrCreateChannel("#c");
            chC.CreatedTs = 200;
            stateC.TryJoinChannel("uC", "Carol", "#c");
            chC.TryUpdateMemberPrivilege("uC", ChannelPrivilege.Op);
            chC.ApplyModeChange(ChannelModes.Secret, enable: true); // +s (should lose)
            chC.SetTopic("topic-from-c", setBy: "Carol");

            // Heal B<->C with a fresh link.
            using var ctsBC2 = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            var (sessB_BC2, sessC_BC2) = PairedServerLinkSession.CreatePair(connectionIdA: "linkBC2_B", connectionIdB: "linkBC2_C");

            var inboundCFromB2 = svcC.HandleIncomingLinkAsync(sessC_BC2, ctsBC2.Token);
            var outboundBToC2 = svcB.HandleOutboundLinkAsync(sessB_BC2, linkBToC, ctsBC2.Token);

            await WaitForAuthAsync(sessB_BC2, sessC_BC2, ctsBC2.Token);

            // Wait for convergence across all three servers.
            var start = DateTime.UtcNow;
            while ((DateTime.UtcNow - start) < TimeSpan.FromSeconds(3))
            {
                if (stateA.TryGetChannel("#c", out var a) && a is not null &&
                    stateB.TryGetChannel("#c", out var b) && b is not null &&
                    stateC.TryGetChannel("#c", out var c) && c is not null &&
                    a.CreatedTs == 100 && b.CreatedTs == 100 && c.CreatedTs == 100 &&
                    string.Equals(a.Topic, "topic-from-a", StringComparison.Ordinal) &&
                    string.Equals(b.Topic, "topic-from-a", StringComparison.Ordinal) &&
                    string.Equals(c.Topic, "topic-from-a", StringComparison.Ordinal) &&
                    a.Modes.HasFlag(ChannelModes.Moderated) &&
                    b.Modes.HasFlag(ChannelModes.Moderated) &&
                    c.Modes.HasFlag(ChannelModes.Moderated) &&
                    !a.Modes.HasFlag(ChannelModes.Secret) &&
                    !b.Modes.HasFlag(ChannelModes.Secret) &&
                    !c.Modes.HasFlag(ChannelModes.Secret) &&
                    a.Members.Count >= 2 && b.Members.Count >= 2 && c.Members.Count >= 2)
                {
                    break;
                }

                await Task.Delay(10, ctsAB.Token);
            }

            Assert.True(stateA.TryGetChannel("#c", out var finalA) && finalA is not null);
            Assert.True(stateB.TryGetChannel("#c", out var finalB) && finalB is not null);
            Assert.True(stateC.TryGetChannel("#c", out var finalC) && finalC is not null);

            Assert.Equal(100, finalA!.CreatedTs);
            Assert.Equal(100, finalB!.CreatedTs);
            Assert.Equal(100, finalC!.CreatedTs);

            Assert.Equal("topic-from-a", finalA.Topic);
            Assert.Equal("topic-from-a", finalB.Topic);
            Assert.Equal("topic-from-a", finalC.Topic);

            Assert.True(finalA.Modes.HasFlag(ChannelModes.Moderated));
            Assert.True(finalB.Modes.HasFlag(ChannelModes.Moderated));
            Assert.True(finalC.Modes.HasFlag(ChannelModes.Moderated));

            Assert.False(finalA.Modes.HasFlag(ChannelModes.Secret));
            Assert.False(finalB.Modes.HasFlag(ChannelModes.Secret));
            Assert.False(finalC.Modes.HasFlag(ChannelModes.Secret));

            Assert.Contains(finalA.Members, m => string.Equals(m.Nick, "Carol", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(finalB.Members, m => string.Equals(m.Nick, "Alice", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(finalC.Members, m => string.Equals(m.Nick, "Alice", StringComparison.OrdinalIgnoreCase));

            // Privileges: TS collision should strip younger-side ops; Alice remains op, Carol becomes normal.
            Assert.Equal(ChannelPrivilege.Op, finalA.GetPrivilege("uA"));
            Assert.Equal(ChannelPrivilege.Op, finalB.GetPrivilege("uid:001UA"));
            Assert.Equal(ChannelPrivilege.Op, finalC.GetPrivilege("uid:001UA"));

            Assert.Equal(ChannelPrivilege.Normal, finalA.GetPrivilege("uid:003UC"));
            Assert.Equal(ChannelPrivilege.Normal, finalB.GetPrivilege("uid:003UC"));
            Assert.Equal(ChannelPrivilege.Normal, finalC.GetPrivilege("uC"));

            ctsBC2.Cancel();
            sessB_BC2.Complete();
            sessC_BC2.Complete();
            try { await outboundBToC2; } catch (OperationCanceledException) { }
            try { await inboundCFromB2; } catch (OperationCanceledException) { }

            ctsAB.Cancel();
            sessA_AB.Complete();
            sessB_AB.Complete();
            try { await outboundAToB; } catch (OperationCanceledException) { }
            try { await inboundBFromA; } catch (OperationCanceledException) { }
        }

        [Fact]
        public async Task SplitHeal_ThreeServers_NickCollisionConverges_ByLowerNickTs()
        {
            // Server A
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

            // Server B (hub)
            var stateB = new ServerState();
            var sessionsB = new FakeSessionRegistry();
            var routingB = new RoutingService(sessionsB, new IrcFormatter());
            var silenceB = new SilenceService();

            var linkBToC = new LinkOptions { Name = "c", Sid = "003", Password = "pw", Outbound = true, UserSync = true };

            var optionsB = new IrcOptions
            {
                ServerInfo = new ServerInfoOptions { Name = "b", Sid = "002", Description = "B" },
                Links = new[]
                {
                    new LinkOptions { Name = "a", Sid = "001", Password = "pw", Outbound = false, UserSync = true },
                    linkBToC
                }
            };

            var watchB = new WatchService(Options.Create(optionsB), routingB);
            var svcB = new ServerLinkService(NullLogger<ServerLinkService>.Instance, new OptionsMonitorStub<IrcOptions>(optionsB), stateB, routingB, sessionsB, silenceB, watchB);

            // Server C
            var stateC = new ServerState();
            var sessionsC = new FakeSessionRegistry();
            var routingC = new RoutingService(sessionsC, new IrcFormatter());
            var silenceC = new SilenceService();

            var optionsC = new IrcOptions
            {
                ServerInfo = new ServerInfoOptions { Name = "c", Sid = "003", Description = "C" },
                Links = new[]
                {
                    new LinkOptions { Name = "b", Sid = "002", Password = "pw", Outbound = false, UserSync = true }
                }
            };

            var watchC = new WatchService(Options.Create(optionsC), routingC);
            var svcC = new ServerLinkService(NullLogger<ServerLinkService>.Instance, new OptionsMonitorStub<IrcOptions>(optionsC), stateC, routingC, sessionsC, silenceC, watchC);

            // Step 1: Establish B<->C, then split it BEFORE A is linked, so C doesn't learn about A's user.
            using var ctsBC = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            var (sessB_BC, sessC_BC) = PairedServerLinkSession.CreatePair(connectionIdA: "linkBC_B", connectionIdB: "linkBC_C");
            var inboundTaskC = svcC.HandleIncomingLinkAsync(sessC_BC, ctsBC.Token);
            var outboundTaskB = svcB.HandleOutboundLinkAsync(sessB_BC, linkBToC, ctsBC.Token);
            await WaitForAuthAsync(sessB_BC, sessC_BC, ctsBC.Token);

            ctsBC.Cancel();
            sessB_BC.Complete();
            sessC_BC.Complete();
            try { await outboundTaskB; } catch (OperationCanceledException) { }
            try { await inboundTaskC; } catch (OperationCanceledException) { }

            // Step 2: Now link A<->B and have A introduce a local user "Dup" with older NickTs.
            stateA.TryAddUser(new User
            {
                ConnectionId = "uA",
                Uid = "001UA",
                Nick = "Dup",
                UserName = "a",
                Host = "hA",
                IsRegistered = true,
                IsRemote = false,
                RemoteSid = "001",
                NickTs = 10
            });

            using var ctsAB = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            var (sessA_AB, sessB_AB) = PairedServerLinkSession.CreatePair(connectionIdA: "linkAB_A", connectionIdB: "linkAB_B");
            var inboundTaskB = svcB.HandleIncomingLinkAsync(sessB_AB, ctsAB.Token);
            var outboundTaskA = svcA.HandleOutboundLinkAsync(sessA_AB, optionsA.Links[0], ctsAB.Token);
            await WaitForAuthAsync(sessA_AB, sessB_AB, ctsAB.Token);

            // Step 3: While B<->C is split, create a conflicting local user on C with newer NickTs.
            stateC.TryAddUser(new User
            {
                ConnectionId = "uC",
                Uid = "003UC",
                Nick = "Dup",
                UserName = "c",
                Host = "hC",
                IsRegistered = true,
                IsRemote = false,
                RemoteSid = "003",
                NickTs = 20
            });

            // Step 4: Heal B<->C. B should burst A's Dup to C, forcing C's local Dup to a collision nick.
            using var ctsBC2 = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            var (sessB_BC2, sessC_BC2) = PairedServerLinkSession.CreatePair(connectionIdA: "linkBC2_B", connectionIdB: "linkBC2_C");
            var inboundTaskC2 = svcC.HandleIncomingLinkAsync(sessC_BC2, ctsBC2.Token);
            var outboundTaskB2 = svcB.HandleOutboundLinkAsync(sessB_BC2, linkBToC, ctsBC2.Token);
            await WaitForAuthAsync(sessB_BC2, sessC_BC2, ctsBC2.Token);

            // Wait for convergence of nick collision outcome.
            var start = DateTime.UtcNow;
            while ((DateTime.UtcNow - start) < TimeSpan.FromSeconds(3))
            {
                stateA.TryGetUserByUid("001UA", out var aLocal);
                stateB.TryGetUserByUid("001UA", out var bRemoteA);
                stateC.TryGetUserByUid("001UA", out var cRemoteA);

                stateA.TryGetUserByUid("003UC", out var aRemoteC);
                stateB.TryGetUserByUid("003UC", out var bRemoteC);
                stateC.TryGetUserByUid("003UC", out var cLocal);

                var ok =
                    aLocal is not null && string.Equals(aLocal.Nick, "Dup", StringComparison.OrdinalIgnoreCase) &&
                    bRemoteA is not null && string.Equals(bRemoteA.Nick, "Dup", StringComparison.OrdinalIgnoreCase) &&
                    cRemoteA is not null && string.Equals(cRemoteA.Nick, "Dup", StringComparison.OrdinalIgnoreCase) &&
                    cLocal is not null && string.Equals(cLocal.Nick, "uid003UC", StringComparison.OrdinalIgnoreCase) &&
                    bRemoteC is not null && string.Equals(bRemoteC.Nick, "uid003UC", StringComparison.OrdinalIgnoreCase) &&
                    aRemoteC is not null && string.Equals(aRemoteC.Nick, "uid003UC", StringComparison.OrdinalIgnoreCase);

                if (ok)
                {
                    break;
                }

                await Task.Delay(10, ctsAB.Token);
            }

            Assert.True(stateA.TryGetUserByUid("001UA", out var finalA) && finalA is not null);
            Assert.Equal("Dup", finalA!.Nick);

            Assert.True(stateC.TryGetUserByUid("001UA", out var finalCRemoteA) && finalCRemoteA is not null);
            Assert.Equal("Dup", finalCRemoteA!.Nick);

            Assert.True(stateC.TryGetUserByUid("003UC", out var finalCLocal) && finalCLocal is not null);
            Assert.Equal("uid003UC", finalCLocal!.Nick);

            Assert.True(stateB.TryGetUserByUid("003UC", out var finalBRemoteC) && finalBRemoteC is not null);
            Assert.Equal("uid003UC", finalBRemoteC!.Nick);

            Assert.True(stateA.TryGetUserByUid("003UC", out var finalARemoteC) && finalARemoteC is not null);
            Assert.Equal("uid003UC", finalARemoteC!.Nick);

            // Cleanup.
            ctsBC2.Cancel();
            sessB_BC2.Complete();
            sessC_BC2.Complete();
            try { await outboundTaskB2; } catch (OperationCanceledException) { }
            try { await inboundTaskC2; } catch (OperationCanceledException) { }

            ctsAB.Cancel();
            sessA_AB.Complete();
            sessB_AB.Complete();
            try { await outboundTaskA; } catch (OperationCanceledException) { }
            try { await inboundTaskB; } catch (OperationCanceledException) { }
        }

        private static async Task WaitForAuthAsync(PairedServerLinkSession x, PairedServerLinkSession y, CancellationToken ct)
        {
            var start = DateTime.UtcNow;
            while (!(x.IsAuthenticated && y.IsAuthenticated) && (DateTime.UtcNow - start) < TimeSpan.FromSeconds(2))
            {
                await Task.Delay(5, ct);
            }

            Assert.True(x.IsAuthenticated, string.Join("\n", x.Outgoing));
            Assert.True(y.IsAuthenticated, string.Join("\n", y.Outgoing));
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
