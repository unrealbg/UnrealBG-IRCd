namespace IRCd.Tests
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Core.Abstractions;
    using IRCd.Core.State;
    using IRCd.Shared.Options;
    using IRCd.Tests.TestUtils;
    using Microsoft.Extensions.DependencyInjection;
    using Xunit;

    public sealed class CompatibilityGoldenTests
    {
        public static IEnumerable<object[]> Cases()
        {
            foreach (var c in BuildCases())
            {
                yield return new object[] { c };
            }
        }

        [Theory]
        [MemberData(nameof(Cases))]
        public async Task Golden(Case c)
        {
            var harness = new GoldenIrcHarness(
                configureServices: c.ConfigureServices,
                configureOptions: c.ConfigureOptions);

            c.Seed?.Invoke(harness);

            var steps = c.Steps(harness);
            var outputs = new List<string>();

            foreach (var step in steps)
            {
                step.Before?.Invoke(harness);

                var session = GetSession(harness, step.SessionId);

                if (step.Kind == StepKind.Capture)
                {
                    outputs.AddRange(Format(step.Label, session.Sent.ToArray()));
                    session.Clear();
                }
                else if (step.Kind == StepKind.Single)
                {
                    var lines = await harness.SendRawAsync(session, step.Lines[0]);
                    outputs.AddRange(Format(step.Label, lines));
                }
                else
                {
                    var lines = await harness.SendRawBatchAsync(session, step.Lines);
                    outputs.AddRange(Format(step.Label, lines));
                }

                step.After?.Invoke(harness);
            }

            var snapshotPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Goldens", "compat", c.SnapshotFile);
            snapshotPath = Path.GetFullPath(snapshotPath);

            GoldenSnapshot.AssertLinesMatch(snapshotPath, outputs.ToArray());
        }

        private static GoldenIrcHarness.GoldenTestSession GetSession(GoldenIrcHarness harness, string connectionId)
        {
            if (!harness.Sessions.TryGet(connectionId, out var session) || session is not GoldenIrcHarness.GoldenTestSession golden)
            {
                throw new InvalidOperationException($"Session not found or wrong type: {connectionId}");
            }

            return golden;
        }

        private static IEnumerable<string> Format(string label, IReadOnlyCollection<string> lines)
        {
            yield return $"# {label}";
            foreach (var l in lines)
            {
                yield return l;
            }
        }

        private static IReadOnlyList<Case> BuildCases()
        {
            var list = new List<Case>();

            static void SeedChannelWithOp(GoldenIrcHarness h, string chan, string opSessionId, string opNick, params (string SessionId, string Nick)[] members)
            {
                var op = h.CreateSession(opSessionId, registered: true, nick: opNick);
                var ch = h.State.GetOrCreateChannel(chan);
                ch.CreatedTs = 100;
                h.State.TryJoinChannel(op.ConnectionId, op.Nick!, chan);
                ch.TryUpdateMemberPrivilege(op.ConnectionId, ChannelPrivilege.Op);

                foreach (var (sid, nick) in members)
                {
                    var s = h.CreateSession(sid, registered: true, nick: nick);
                    h.State.TryJoinChannel(s.ConnectionId, s.Nick!, chan);
                }
            }

            static Action<IServiceCollection> OverrideSaslPlain(string expectedUser, string expectedPass, string account)
                => services =>
                {
                    services.AddSingleton<IRCd.Core.Abstractions.ISaslPlainAuthenticator>(
                        new FixedSaslPlainAuthenticator(expectedUser, expectedPass, account));
                };

            // ---- Basic numeric correctness (errors) ----
            list.Add(Case.UnregisteredScript("who_unregistered_451", "WHO #c"));
            list.Add(Case.UnregisteredScript("whois_unregistered_451", "WHOIS Nick"));
            list.Add(Case.UnregisteredScript("mode_unregistered_451", "MODE #c"));
            list.Add(Case.UnregisteredScript("invite_unregistered_451", "INVITE a #c"));
            list.Add(Case.UnregisteredScript("join_unregistered_451", "JOIN #c"));
            list.Add(Case.UnregisteredScript("privmsg_unregistered_451", "PRIVMSG a :hi"));
            list.Add(Case.UnregisteredScript("names_unregistered_451", "NAMES #c"));
            list.Add(Case.UnregisteredScript("motd_unregistered_451", "MOTD"));
            list.Add(Case.UnregisteredScript("lusers_unregistered_451", "LUSERS"));

            list.Add(Case.RegisteredScript("who_missing_params_461", "WHO"));
            list.Add(Case.RegisteredScript("whois_missing_params_461", "WHOIS"));
            list.Add(Case.RegisteredScript("mode_missing_params_461", "MODE"));
            list.Add(Case.RegisteredScript("invite_missing_params_461", "INVITE"));
            list.Add(Case.RegisteredScript("join_missing_params_461", "JOIN"));
            list.Add(Case.RegisteredScript("privmsg_missing_params_461", "PRIVMSG a"));

            // Invalid nick/channel
            list.Add(Case.RegisteredScript("who_invalid_target_315", "WHO !!!"));
            list.Add(Case.RegisteredScript("whois_invalid_target_401_318", "WHOIS !!!"));
            list.Add(Case.RegisteredScript("invite_invalid_nick_401", "INVITE !!! #c"));
            list.Add(Case.RegisteredScript("invite_invalid_channel_403", "INVITE a !!!"));
            list.Add(Case.RegisteredScript("join_illegal_channel_479", "JOIN !!!"));

            // MODE target errors
            list.Add(Case.RegisteredScript("mode_nosuchchannel_403", "MODE #nope"));
            list.Add(Case.RegisteredScript("mode_unknownflags_501", "MODE #c blah"));

            // PRIVMSG target errors
            list.Add(Case.RegisteredScript("privmsg_nosuchnick_401", "PRIVMSG NoSuchNick :hi"));
            list.Add(Case.RegisteredScript("privmsg_invalid_channel_403", "PRIVMSG !!! :hi"));
            list.Add(Case.RegisteredScript("privmsg_nosuchchannel_403", "PRIVMSG #nope :hi"));

            // TOPIC basic errors
            list.Add(Case.UnregisteredScript("topic_unregistered_451", "TOPIC #c"));
            list.Add(Case.RegisteredScript("topic_missing_params_461", "TOPIC"));
            list.Add(Case.RegisteredScript("topic_invalid_channel_403", "TOPIC !!!"));
            list.Add(Case.RegisteredScript("topic_nosuchchannel_403", "TOPIC #nope"));

            // ---- WHO/WHOIS edge cases ----
            list.Add(Case.RegisteredScript(
                "who_too_long_mask_315",
                "WHO " + new string('x', 300),
                configureOptions: o => o.Limits = new CommandLimitsOptions { MaxWhoMaskLength = 10 }));

            list.Add(Case.Custom(
                "who_channel_sorted_flags",
                snapshot: "who_channel_sorted_flags.txt",
                seed: h =>
                {
                    var a = h.CreateSession("a", registered: true, nick: "Alice");
                    var b = h.CreateSession("b", registered: true, nick: "bob");
                    var c = h.CreateSession("c", registered: true, nick: "Carol");

                    var ch = h.State.GetOrCreateChannel("#c");
                    ch.CreatedTs = 100;
                    h.State.TryJoinChannel(a.ConnectionId, a.Nick!, "#c");
                    h.State.TryJoinChannel(b.ConnectionId, b.Nick!, "#c");
                    h.State.TryJoinChannel(c.ConnectionId, c.Nick!, "#c");
                    ch.TryUpdateMemberPrivilege(a.ConnectionId, ChannelPrivilege.Op);

                    // mark Bob away to get "G" in WHO
                    h.State.TryGetUser(b.ConnectionId, out var bu);
                    if (bu is not null) bu.AwayMessage = "away";
                },
                steps: _ => new[] { Step.Single("WHO #c", "a", "WHO #c") }));

            list.Add(Case.Custom(
                "who_secret_channel_not_member_403_315",
                snapshot: "who_secret_channel_not_member_403_315.txt",
                seed: h =>
                {
                    var a = h.CreateSession("a", registered: true, nick: "Alice");
                    var b = h.CreateSession("b", registered: true, nick: "Bob");
                    var ch = h.State.GetOrCreateChannel("#s");
                    ch.CreatedTs = 100;
                    ch.ApplyModeChange(ChannelModes.Secret, enable: true);
                    h.State.TryJoinChannel(b.ConnectionId, b.Nick!, "#s");
                },
                steps: _ => new[] { Step.Single("WHO #s", "a", "WHO #s") }));

            list.Add(Case.Custom(
                "whois_comma_separated_two",
                snapshot: "whois_comma_separated_two.txt",
                seed: h =>
                {
                    h.CreateSession("req", registered: true, nick: "Req");
                    h.CreateSession("a", registered: true, nick: "Alice");
                    h.CreateSession("b", registered: true, nick: "Bob");
                },
                steps: _ => new[] { Step.Single("WHOIS Alice,Bob", "req", "WHOIS Alice,Bob") }));

            list.Add(Case.Custom(
                "whois_too_many_targets_407",
                snapshot: "whois_too_many_targets_407.txt",
                configureOptions: o => o.Limits = new CommandLimitsOptions { MaxWhoisTargets = 1 },
                seed: h => h.CreateSession("req", registered: true, nick: "Req"),
                steps: _ => new[] { Step.Single("WHOIS a,b", "req", "WHOIS a,b") }));

            list.Add(Case.Custom(
                "whois_service_user",
                snapshot: "whois_service_user.txt",
                seed: h =>
                {
                    h.CreateSession("req", registered: true, nick: "Req");

                    h.State.TryAddUser(new User
                    {
                        ConnectionId = "svc",
                        Nick = "NickServ",
                        UserName = "services",
                        Host = "services.host",
                        RealName = "Services",
                        IsRegistered = true,
                        IsService = true,
                        ConnectedAtUtc = h.Clock.UtcNow - TimeSpan.FromSeconds(10),
                        LastActivityUtc = h.Clock.UtcNow,
                    });

                    var svcSess = new GoldenIrcHarness.GoldenTestSession("svc")
                    {
                        Nick = "NickServ",
                        UserName = "services",
                        IsRegistered = true,
                    };
                    h.Sessions.Add(svcSess);
                },
                steps: _ => new[] { Step.Single("WHOIS NickServ", "req", "WHOIS NickServ") }));

            list.Add(Case.Custom(
                "whois_operator_and_netadmin",
                snapshot: "whois_operator_and_netadmin.txt",
                configureOptions: o =>
                {
                    o.Classes = new[]
                    {
                        new OperClassOptions { Name = "admin", Capabilities = new[] { "netadmin" } }
                    };
                },
                seed: h =>
                {
                    h.CreateSession("req", registered: true, nick: "Req");
                    var oper = h.CreateSession("op", registered: true, nick: "Oper");

                    h.State.TryGetUser(oper.ConnectionId, out var u);
                    if (u is not null)
                    {
                        u.Modes |= UserModes.Operator;
                        u.OperClass = "admin";
                    }
                },
                steps: _ => new[] { Step.Single("WHOIS Oper", "req", "WHOIS Oper") }));

            list.Add(Case.Custom(
                "whois_away_line_301",
                snapshot: "whois_away_line_301.txt",
                seed: h =>
                {
                    h.CreateSession("req", registered: true, nick: "Req");
                    var away = h.CreateSession("a", registered: true, nick: "AwayNick");
                    h.State.TryGetUser(away.ConnectionId, out var u);
                    if (u is not null) u.AwayMessage = "gone";
                },
                steps: _ => new[] { Step.Single("WHOIS AwayNick", "req", "WHOIS AwayNick") }));

            list.Add(Case.Custom(
                "whois_shows_channel_prefixes_sorted",
                snapshot: "whois_shows_channel_prefixes_sorted.txt",
                seed: h =>
                {
                    var req = h.CreateSession("req", registered: true, nick: "Req");
                    var t = h.CreateSession("t", registered: true, nick: "Target");

                    var ch1 = h.State.GetOrCreateChannel("#a");
                    ch1.CreatedTs = 100;
                    h.State.TryJoinChannel(t.ConnectionId, t.Nick!, "#a");

                    var ch2 = h.State.GetOrCreateChannel("#b");
                    ch2.CreatedTs = 100;
                    h.State.TryJoinChannel(t.ConnectionId, t.Nick!, "#b");
                    ch2.TryUpdateMemberPrivilege(t.ConnectionId, ChannelPrivilege.Op);

                    var ch3 = h.State.GetOrCreateChannel("#s");
                    ch3.CreatedTs = 100;
                    ch3.ApplyModeChange(ChannelModes.Secret, enable: true);
                    h.State.TryJoinChannel(t.ConnectionId, t.Nick!, "#s");

                    // requester is only in #a, so secret #s should be hidden
                    h.State.TryJoinChannel(req.ConnectionId, req.Nick!, "#a");
                },
                steps: _ => new[] { Step.Single("WHOIS Target", "req", "WHOIS Target") }));

            list.Add(Case.Custom(
                "whois_identified_line_307",
                snapshot: "whois_identified_line_307.txt",
                configureServices: services => services.AddSingleton<IServiceSessionEvents>(new TestServiceEvents(identified: true)),
                seed: h =>
                {
                    h.CreateSession("req", registered: true, nick: "Req");
                    h.CreateSession("t", registered: true, nick: "Target");
                },
                steps: _ => new[] { Step.Single("WHOIS Target", "req", "WHOIS Target") }));

            // ---- Channel MODE list outputs ----
            list.Add(Case.Custom(
                "mode_query_324",
                snapshot: "mode_query_324.txt",
                seed: h =>
                {
                    var a = h.CreateSession("a", registered: true, nick: "Alice");
                    var ch = h.State.GetOrCreateChannel("#c");
                    ch.CreatedTs = 100;
                    h.State.TryJoinChannel(a.ConnectionId, a.Nick!, "#c");
                    ch.ApplyModeChange(ChannelModes.Moderated, enable: true);
                },
                steps: _ => new[] { Step.Single("MODE #c", "a", "MODE #c") }));

            list.Add(Case.Custom(
                "mode_ban_list_367_368",
                snapshot: "mode_ban_list_367_368.txt",
                seed: h =>
                {
                    var a = h.CreateSession("a", registered: true, nick: "Alice");
                    var ch = h.State.GetOrCreateChannel("#c");
                    ch.CreatedTs = 100;
                    h.State.TryJoinChannel(a.ConnectionId, a.Nick!, "#c");
                    ch.TryUpdateMemberPrivilege(a.ConnectionId, ChannelPrivilege.Op);

                    var fixedAt = DateTimeOffset.FromUnixTimeSeconds(1_700_000_123);
                    ch.AddBan("*!*@bad.host", "Alice", fixedAt);
                    ch.AddBan("bad!*@*", "Alice", fixedAt.AddSeconds(1));
                },
                steps: _ => new[] { Step.Single("MODE #c +b", "a", "MODE #c +b") }));

            list.Add(Case.Custom(
                "mode_ban_list_full_478",
                snapshot: "mode_ban_list_full_478.txt",
                configureOptions: o => o.Limits = new CommandLimitsOptions { MaxListModes = 1 },
                seed: h =>
                {
                    var a = h.CreateSession("a", registered: true, nick: "Alice");
                    var ch = h.State.GetOrCreateChannel("#c");
                    ch.CreatedTs = 100;
                    h.State.TryJoinChannel(a.ConnectionId, a.Nick!, "#c");
                    ch.TryUpdateMemberPrivilege(a.ConnectionId, ChannelPrivilege.Op);
                    ch.AddBan("*!*@bad.host", "Alice", DateTimeOffset.FromUnixTimeSeconds(1_700_000_123));
                },
                steps: _ => new[] { Step.Single("MODE #c +b *!*@worse.host", "a", "MODE #c +b *!*@worse.host") }));

            list.Add(Case.Custom(
                "mode_except_list_348_349",
                snapshot: "mode_except_list_348_349.txt",
                seed: h =>
                {
                    var a = h.CreateSession("a", registered: true, nick: "Alice");
                    var ch = h.State.GetOrCreateChannel("#c");
                    ch.CreatedTs = 100;
                    h.State.TryJoinChannel(a.ConnectionId, a.Nick!, "#c");
                    ch.TryUpdateMemberPrivilege(a.ConnectionId, ChannelPrivilege.Op);

                    var fixedAt = DateTimeOffset.FromUnixTimeSeconds(1_700_000_123);
                    ch.AddExceptBan("*!*@good.host", "Alice", fixedAt);
                },
                steps: _ => new[] { Step.Single("MODE #c +e", "a", "MODE #c +e") }));

            list.Add(Case.Custom(
                "mode_invite_except_list_346_347",
                snapshot: "mode_invite_except_list_346_347.txt",
                seed: h =>
                {
                    var a = h.CreateSession("a", registered: true, nick: "Alice");
                    var ch = h.State.GetOrCreateChannel("#c");
                    ch.CreatedTs = 100;
                    h.State.TryJoinChannel(a.ConnectionId, a.Nick!, "#c");
                    ch.TryUpdateMemberPrivilege(a.ConnectionId, ChannelPrivilege.Op);

                    var fixedAt = DateTimeOffset.FromUnixTimeSeconds(1_700_000_123);
                    ch.AddInviteException("Alice!*@host", "Alice", fixedAt);
                },
                steps: _ => new[] { Step.Single("MODE #c +I", "a", "MODE #c +I") }));

            list.Add(Case.Custom(
                "mode_set_and_query",
                snapshot: "mode_set_and_query.txt",
                seed: h =>
                {
                    var a = h.CreateSession("a", registered: true, nick: "Alice");
                    var b = h.CreateSession("b", registered: true, nick: "Bob");
                    var ch = h.State.GetOrCreateChannel("#c");
                    ch.CreatedTs = 100;
                    h.State.TryJoinChannel(a.ConnectionId, a.Nick!, "#c");
                    h.State.TryJoinChannel(b.ConnectionId, b.Nick!, "#c");
                    ch.TryUpdateMemberPrivilege(a.ConnectionId, ChannelPrivilege.Op);
                },
                steps: _ =>
                    new[]
                    {
                        Step.Single("Alice sets +im", "a", "MODE #c +im"),
                        Step.Single("Bob queries MODE", "b", "MODE #c"),
                    }));

            // MODE permissions and list-mode visibility
            list.Add(Case.Custom(
                "mode_list_b_not_on_channel_442",
                snapshot: "mode_list_b_not_on_channel_442.txt",
                seed: h =>
                {
                    h.CreateSession("a", registered: true, nick: "Alice");
                    var ch = h.State.GetOrCreateChannel("#c");
                    ch.CreatedTs = 100;
                },
                steps: _ => new[] { Step.Single("MODE #c +b", "a", "MODE #c +b") }));

            list.Add(Case.Custom(
                "mode_list_b_not_op_482",
                snapshot: "mode_list_b_not_op_482.txt",
                seed: h =>
                {
                    var a = h.CreateSession("a", registered: true, nick: "Alice");
                    var ch = h.State.GetOrCreateChannel("#c");
                    ch.CreatedTs = 100;
                    h.State.TryJoinChannel(a.ConnectionId, a.Nick!, "#c");
                },
                steps: _ => new[] { Step.Single("MODE #c +b", "a", "MODE #c +b") }));

            list.Add(Case.Custom(
                "mode_list_e_not_op_482",
                snapshot: "mode_list_e_not_op_482.txt",
                seed: h =>
                {
                    var a = h.CreateSession("a", registered: true, nick: "Alice");
                    var ch = h.State.GetOrCreateChannel("#c");
                    ch.CreatedTs = 100;
                    h.State.TryJoinChannel(a.ConnectionId, a.Nick!, "#c");
                },
                steps: _ => new[] { Step.Single("MODE #c +e", "a", "MODE #c +e") }));

            list.Add(Case.Custom(
                "mode_list_I_not_op_482",
                snapshot: "mode_list_I_not_op_482.txt",
                seed: h =>
                {
                    var a = h.CreateSession("a", registered: true, nick: "Alice");
                    var ch = h.State.GetOrCreateChannel("#c");
                    ch.CreatedTs = 100;
                    h.State.TryJoinChannel(a.ConnectionId, a.Nick!, "#c");
                },
                steps: _ => new[] { Step.Single("MODE #c +I", "a", "MODE #c +I") }));

            list.Add(Case.Custom(
                "mode_ban_add_and_remove_roundtrip",
                snapshot: "mode_ban_add_and_remove_roundtrip.txt",
                seed: h => SeedChannelWithOp(h, "#c", "a", "Alice"),
                steps: _ =>
                    new[]
                    {
                        Step.Single("Add ban", "a", "MODE #c +b bad!*@*"),
                        Step.Single("Remove ban", "a", "MODE #c -b bad!*@*"),
                        Step.Single("List bans", "a", "MODE #c +b"),
                    }));

            list.Add(Case.Custom(
                "mode_except_add_and_remove_roundtrip",
                snapshot: "mode_except_add_and_remove_roundtrip.txt",
                seed: h => SeedChannelWithOp(h, "#c", "a", "Alice"),
                steps: _ =>
                    new[]
                    {
                        Step.Single("Add except", "a", "MODE #c +e good!*@*"),
                        Step.Single("Remove except", "a", "MODE #c -e good!*@*"),
                        Step.Single("List except", "a", "MODE #c +e"),
                    }));

            list.Add(Case.Custom(
                "mode_invite_except_add_and_remove_roundtrip",
                snapshot: "mode_invite_except_add_and_remove_roundtrip.txt",
                seed: h => SeedChannelWithOp(h, "#c", "a", "Alice"),
                steps: _ =>
                    new[]
                    {
                        Step.Single("Add invite exception", "a", "MODE #c +I Alice!*@host"),
                        Step.Single("Remove invite exception", "a", "MODE #c -I Alice!*@host"),
                        Step.Single("List invite exceptions", "a", "MODE #c +I"),
                    }));

            list.Add(Case.Custom(
                "mode_set_requires_op_482",
                snapshot: "mode_set_requires_op_482.txt",
                seed: h =>
                {
                    var a = h.CreateSession("a", registered: true, nick: "Alice");
                    var ch = h.State.GetOrCreateChannel("#c");
                    ch.CreatedTs = 100;
                    h.State.TryJoinChannel(a.ConnectionId, a.Nick!, "#c");
                },
                steps: _ => new[] { Step.Single("MODE #c +m", "a", "MODE #c +m") }));

            // ---- INVITE + join/ban semantics ----
            list.Add(Case.Custom(
                "invite_success_and_delivery",
                snapshot: "invite_success_and_delivery.txt",
                seed: h =>
                {
                    var alice = h.CreateSession("a", registered: true, nick: "Alice");
                    h.CreateSession("b", registered: true, nick: "Bob");

                    var ch = h.State.GetOrCreateChannel("#c");
                    ch.CreatedTs = 100;
                    h.State.TryJoinChannel(alice.ConnectionId, alice.Nick!, "#c");
                    ch.TryUpdateMemberPrivilege(alice.ConnectionId, ChannelPrivilege.Op);
                },
                steps: _ =>
                    new[]
                    {
                        Step.Single("Alice INVITE Bob #c", "a", "INVITE Bob #c"),
                        Step.Capture("Bob receives", "b"),
                    }));

            list.Add(Case.Custom(
                "invite_no_such_nick_401",
                snapshot: "invite_no_such_nick_401.txt",
                seed: h =>
                {
                    var alice = h.CreateSession("a", registered: true, nick: "Alice");
                    var ch = h.State.GetOrCreateChannel("#c");
                    ch.CreatedTs = 100;
                    h.State.TryJoinChannel(alice.ConnectionId, alice.Nick!, "#c");
                    ch.TryUpdateMemberPrivilege(alice.ConnectionId, ChannelPrivilege.Op);
                },
                steps: _ => new[] { Step.Single("INVITE Missing #c", "a", "INVITE Missing #c") }));

            list.Add(Case.Custom(
                "invite_not_on_channel_442",
                snapshot: "invite_not_on_channel_442.txt",
                seed: h =>
                {
                    h.CreateSession("a", registered: true, nick: "Alice");
                    h.CreateSession("b", registered: true, nick: "Bob");
                    var ch = h.State.GetOrCreateChannel("#c");
                    ch.CreatedTs = 100;
                },
                steps: _ => new[] { Step.Single("INVITE Bob #c", "a", "INVITE Bob #c") }));

            list.Add(Case.Custom(
                "invite_inviteonly_requires_op_482",
                snapshot: "invite_inviteonly_requires_op_482.txt",
                seed: h =>
                {
                    var alice = h.CreateSession("a", registered: true, nick: "Alice");
                    h.CreateSession("b", registered: true, nick: "Bob");
                    var ch = h.State.GetOrCreateChannel("#c");
                    ch.CreatedTs = 100;
                    h.State.TryJoinChannel(alice.ConnectionId, alice.Nick!, "#c");
                    ch.ApplyModeChange(ChannelModes.InviteOnly, enable: true);
                },
                steps: _ => new[] { Step.Single("INVITE Bob #c", "a", "INVITE Bob #c") }));

            list.Add(Case.Custom(
                "join_denied_by_ban_474",
                snapshot: "join_denied_by_ban_474.txt",
                seed: h =>
                {
                    var alice = h.CreateSession("a", registered: true, nick: "Alice");
                    h.CreateSession("b", registered: true, nick: "Bob");

                    var ch = h.State.GetOrCreateChannel("#c");
                    ch.CreatedTs = 100;
                    h.State.TryJoinChannel(alice.ConnectionId, alice.Nick!, "#c");
                    ch.TryUpdateMemberPrivilege(alice.ConnectionId, ChannelPrivilege.Op);

                    ch.AddBan("Bob!*@*", "Alice", DateTimeOffset.FromUnixTimeSeconds(1_700_000_123));
                },
                steps: _ => new[] { Step.Single("Bob JOIN #c", "b", "JOIN #c") }));

            list.Add(Case.Custom(
                "join_denied_limit_471",
                snapshot: "join_denied_limit_471.txt",
                seed: h =>
                {
                    var alice = h.CreateSession("a", registered: true, nick: "Alice");
                    var bob = h.CreateSession("b", registered: true, nick: "Bob");
                    var carol = h.CreateSession("c", registered: true, nick: "Carol");

                    var ch = h.State.GetOrCreateChannel("#c");
                    ch.CreatedTs = 100;
                    h.State.TryJoinChannel(alice.ConnectionId, alice.Nick!, "#c");
                    h.State.TryJoinChannel(bob.ConnectionId, bob.Nick!, "#c");
                    ch.SetLimit(2);
                },
                steps: _ => new[] { Step.Single("Carol JOIN #c", "c", "JOIN #c") }));

            list.Add(Case.Custom(
                "join_denied_key_475",
                snapshot: "join_denied_key_475.txt",
                seed: h =>
                {
                    var alice = h.CreateSession("a", registered: true, nick: "Alice");
                    var bob = h.CreateSession("b", registered: true, nick: "Bob");

                    var ch = h.State.GetOrCreateChannel("#c");
                    ch.CreatedTs = 100;
                    h.State.TryJoinChannel(alice.ConnectionId, alice.Nick!, "#c");
                    ch.TryUpdateMemberPrivilege(alice.ConnectionId, ChannelPrivilege.Op);
                    ch.SetKey("secret");
                },
                steps: _ => new[] { Step.Single("Bob JOIN #c wrong", "b", "JOIN #c wrong") }));

            list.Add(Case.Custom(
                "join_allowed_key_joins",
                snapshot: "join_allowed_key_joins.txt",
                seed: h =>
                {
                    var alice = h.CreateSession("a", registered: true, nick: "Alice");
                    h.CreateSession("b", registered: true, nick: "Bob");

                    var ch = h.State.GetOrCreateChannel("#c");
                    ch.CreatedTs = 100;
                    h.State.TryJoinChannel(alice.ConnectionId, alice.Nick!, "#c");
                    ch.TryUpdateMemberPrivilege(alice.ConnectionId, ChannelPrivilege.Op);
                    ch.SetKey("secret");
                },
                steps: _ => new[] { Step.Single("Bob JOIN #c secret", "b", "JOIN #c secret") }));

            list.Add(Case.Custom(
                "join_allowed_by_except_joins",
                snapshot: "join_allowed_by_except_joins.txt",
                seed: h =>
                {
                    var alice = h.CreateSession("a", registered: true, nick: "Alice");
                    h.CreateSession("b", registered: true, nick: "Bob");

                    var ch = h.State.GetOrCreateChannel("#c");
                    ch.CreatedTs = 100;
                    h.State.TryJoinChannel(alice.ConnectionId, alice.Nick!, "#c");
                    ch.TryUpdateMemberPrivilege(alice.ConnectionId, ChannelPrivilege.Op);

                    ch.AddBan("Bob!*@*", "Alice", DateTimeOffset.FromUnixTimeSeconds(1_700_000_123));
                    ch.AddExceptBan("Bob!*@host", "Alice", DateTimeOffset.FromUnixTimeSeconds(1_700_000_124));
                },
                steps: _ => new[] { Step.Single("Bob JOIN #c", "b", "JOIN #c") }));

            list.Add(Case.Custom(
                "join_denied_inviteonly_473",
                snapshot: "join_denied_inviteonly_473.txt",
                seed: h =>
                {
                    var alice = h.CreateSession("a", registered: true, nick: "Alice");
                    h.CreateSession("b", registered: true, nick: "Bob");

                    var ch = h.State.GetOrCreateChannel("#c");
                    ch.CreatedTs = 100;
                    h.State.TryJoinChannel(alice.ConnectionId, alice.Nick!, "#c");
                    ch.TryUpdateMemberPrivilege(alice.ConnectionId, ChannelPrivilege.Op);
                    ch.ApplyModeChange(ChannelModes.InviteOnly, enable: true);
                },
                steps: _ => new[] { Step.Single("Bob JOIN #c", "b", "JOIN #c") }));

            list.Add(Case.Custom(
                "join_allowed_by_invite_joins",
                snapshot: "join_allowed_by_invite_joins.txt",
                seed: h =>
                {
                    var alice = h.CreateSession("a", registered: true, nick: "Alice");
                    h.CreateSession("b", registered: true, nick: "Bob");

                    var ch = h.State.GetOrCreateChannel("#c");
                    ch.CreatedTs = 100;
                    h.State.TryJoinChannel(alice.ConnectionId, alice.Nick!, "#c");
                    ch.TryUpdateMemberPrivilege(alice.ConnectionId, ChannelPrivilege.Op);
                    ch.ApplyModeChange(ChannelModes.InviteOnly, enable: true);

                    // pre-invite Bob
                    ch.AddInvite("Bob");
                },
                steps: _ => new[] { Step.Single("Bob JOIN #c", "b", "JOIN #c") }));

            list.Add(Case.Custom(
                "join_allowed_by_invite_exception_mask",
                snapshot: "join_allowed_by_invite_exception_mask.txt",
                seed: h =>
                {
                    var alice = h.CreateSession("a", registered: true, nick: "Alice");
                    h.CreateSession("b", registered: true, nick: "Bob");

                    var ch = h.State.GetOrCreateChannel("#c");
                    ch.CreatedTs = 100;
                    h.State.TryJoinChannel(alice.ConnectionId, alice.Nick!, "#c");
                    ch.TryUpdateMemberPrivilege(alice.ConnectionId, ChannelPrivilege.Op);
                    ch.ApplyModeChange(ChannelModes.InviteOnly, enable: true);

                    ch.AddInviteException("Bob!*@host", "Alice", DateTimeOffset.FromUnixTimeSeconds(1_700_000_125));
                },
                steps: _ => new[] { Step.Single("Bob JOIN #c", "b", "JOIN #c") }));

            list.Add(Case.Custom(
                "invite_services_denied_notice",
                snapshot: "invite_services_denied_notice.txt",
                seed: h =>
                {
                    SeedChannelWithOp(h, "#c", "a", "Alice");

                    h.State.TryAddUser(new User
                    {
                        ConnectionId = "svc",
                        Nick = "NickServ",
                        UserName = "services",
                        Host = "services.host",
                        RealName = "Services",
                        IsRegistered = true,
                        IsService = true,
                        ConnectedAtUtc = h.Clock.UtcNow - TimeSpan.FromSeconds(10),
                        LastActivityUtc = h.Clock.UtcNow,
                    });

                    var svcSess = new GoldenIrcHarness.GoldenTestSession("svc")
                    {
                        Nick = "NickServ",
                        UserName = "services",
                        IsRegistered = true,
                    };
                    h.Sessions.Add(svcSess);
                },
                steps: _ => new[] { Step.Single("INVITE NickServ #c", "a", "INVITE NickServ #c") }));

            // ---- Channel messaging semantics (PRIVMSG) ----
            list.Add(Case.Custom(
                "privmsg_channel_requires_membership_404",
                snapshot: "privmsg_channel_requires_membership_404.txt",
                seed: h =>
                {
                    h.CreateSession("a", registered: true, nick: "Alice");
                    var ch = h.State.GetOrCreateChannel("#c");
                    ch.CreatedTs = 100;
                },
                steps: _ => new[] { Step.Single("Alice PRIVMSG #c", "a", "PRIVMSG #c :hi") }));

            list.Add(Case.Custom(
                "privmsg_channel_banned_404_b",
                snapshot: "privmsg_channel_banned_404_b.txt",
                seed: h =>
                {
                    var alice = h.CreateSession("a", registered: true, nick: "Alice");
                    var ch = h.State.GetOrCreateChannel("#c");
                    ch.CreatedTs = 100;
                    h.State.TryJoinChannel(alice.ConnectionId, alice.Nick!, "#c");
                    ch.AddBan("Alice!*@*", "Alice", DateTimeOffset.FromUnixTimeSeconds(1_700_000_123));
                },
                steps: _ => new[] { Step.Single("Alice PRIVMSG #c", "a", "PRIVMSG #c :hi") }));

            list.Add(Case.Custom(
                "privmsg_channel_banned_but_excepted_sends",
                snapshot: "privmsg_channel_banned_but_excepted_sends.txt",
                seed: h =>
                {
                    var alice = h.CreateSession("a", registered: true, nick: "Alice");
                    var bob = h.CreateSession("b", registered: true, nick: "Bob");

                    var ch = h.State.GetOrCreateChannel("#c");
                    ch.CreatedTs = 100;
                    h.State.TryJoinChannel(alice.ConnectionId, alice.Nick!, "#c");
                    h.State.TryJoinChannel(bob.ConnectionId, bob.Nick!, "#c");

                    ch.AddBan("Alice!*@*", "Bob", DateTimeOffset.FromUnixTimeSeconds(1_700_000_123));
                    ch.AddExceptBan("Alice!*@host", "Bob", DateTimeOffset.FromUnixTimeSeconds(1_700_000_124));
                },
                steps: _ =>
                    new[]
                    {
                        Step.Single("Alice PRIVMSG #c", "a", "PRIVMSG #c :hi"),
                        Step.Capture("Bob receives", "b"),
                    }));

            list.Add(Case.Custom(
                "privmsg_channel_moderated_requires_voice_404_m",
                snapshot: "privmsg_channel_moderated_requires_voice_404_m.txt",
                seed: h =>
                {
                    var alice = h.CreateSession("a", registered: true, nick: "Alice");
                    var bob = h.CreateSession("b", registered: true, nick: "Bob");

                    var ch = h.State.GetOrCreateChannel("#c");
                    ch.CreatedTs = 100;
                    h.State.TryJoinChannel(alice.ConnectionId, alice.Nick!, "#c");
                    h.State.TryJoinChannel(bob.ConnectionId, bob.Nick!, "#c");
                    ch.ApplyModeChange(ChannelModes.Moderated, enable: true);
                },
                steps: _ => new[] { Step.Single("Alice PRIVMSG #c", "a", "PRIVMSG #c :hi"), }));

            // ---- TOPIC semantics ----
            list.Add(Case.Custom(
                "topic_query_no_topic_331",
                snapshot: "topic_query_no_topic_331.txt",
                seed: h =>
                {
                    SeedChannelWithOp(h, "#c", "a", "Alice");
                },
                steps: _ => new[] { Step.Single("TOPIC #c", "a", "TOPIC #c") }));

            list.Add(Case.Custom(
                "topic_query_with_topic_332_333",
                snapshot: "topic_query_with_topic_332_333.txt",
                seed: h =>
                {
                    SeedChannelWithOp(h, "#c", "a", "Alice");
                    h.State.TryGetChannel("#c", out var ch);
                    ch!.Topic = "Hello";
                    ch.TrySetTopicWithTs("Hello", "Alice!ident@localhost", 123);
                },
                steps: _ => new[] { Step.Single("TOPIC #c", "a", "TOPIC #c") }));

            list.Add(Case.Custom(
                "topic_secret_channel_hidden_403",
                snapshot: "topic_secret_channel_hidden_403.txt",
                seed: h =>
                {
                    var a = h.CreateSession("a", registered: true, nick: "Alice");
                    var b = h.CreateSession("b", registered: true, nick: "Bob");
                    var ch = h.State.GetOrCreateChannel("#s");
                    ch.CreatedTs = 100;
                    ch.ApplyModeChange(ChannelModes.Secret, enable: true);
                    h.State.TryJoinChannel(b.ConnectionId, b.Nick!, "#s");
                },
                steps: _ => new[] { Step.Single("TOPIC #s", "a", "TOPIC #s") }));

            list.Add(Case.Custom(
                "topic_set_not_on_channel_442",
                snapshot: "topic_set_not_on_channel_442.txt",
                seed: h =>
                {
                    h.CreateSession("a", registered: true, nick: "Alice");
                    var ch = h.State.GetOrCreateChannel("#c");
                    ch.CreatedTs = 100;
                },
                steps: _ => new[] { Step.Single("TOPIC #c :hi", "a", "TOPIC #c :hi") }));

            list.Add(Case.Custom(
                "topic_set_topicops_requires_op_482",
                snapshot: "topic_set_topicops_requires_op_482.txt",
                seed: h =>
                {
                    var a = h.CreateSession("a", registered: true, nick: "Alice");
                    var ch = h.State.GetOrCreateChannel("#c");
                    ch.CreatedTs = 100;
                    h.State.TryJoinChannel(a.ConnectionId, a.Nick!, "#c");
                    ch.ApplyModeChange(ChannelModes.TopicOpsOnly, enable: true);
                },
                steps: _ => new[] { Step.Single("TOPIC #c :hi", "a", "TOPIC #c :hi") }));

            list.Add(Case.Custom(
                "topic_set_success_broadcast",
                snapshot: "topic_set_success_broadcast.txt",
                seed: h =>
                {
                    SeedChannelWithOp(h, "#c", "a", "Alice", ("b", "Bob"));
                },
                steps: _ =>
                    new[]
                    {
                        Step.Single("Alice sets TOPIC", "a", "TOPIC #c :New topic"),
                        Step.Capture("Bob receives", "b"),
                    }));

            // ---- CAP / SASL flows ----
            list.Add(Case.Custom(
                "cap_ls_includes_sasl",
                snapshot: "cap_ls_includes_sasl.txt",
                seed: h =>
                {
                    var s = h.CreateSession("s", registered: false);
                    s.Nick = "Nick";
                },
                steps: _ => new[] { Step.Single("CAP LS", "s", "CAP LS") }));

            list.Add(Case.Custom(
                "cap_req_unknown_nak",
                snapshot: "cap_req_unknown_nak.txt",
                seed: h =>
                {
                    var s = h.CreateSession("s", registered: false);
                    s.Nick = "Nick";
                },
                steps: _ => new[] { Step.Single("CAP REQ :nope", "s", "CAP REQ :nope") }));

            list.Add(Case.Custom(
                "cap_req_sasl_ack_and_list",
                snapshot: "cap_req_sasl_ack_and_list.txt",
                seed: h =>
                {
                    var s = h.CreateSession("s", registered: false);
                    s.Nick = "Nick";
                },
                steps: _ =>
                    new[]
                    {
                        Step.Single("CAP REQ :sasl", "s", "CAP REQ :sasl"),
                        Step.Single("CAP LIST", "s", "CAP LIST"),
                    }));

            list.Add(Case.Custom(
                "sasl_without_cap_req_fails_904",
                snapshot: "sasl_without_cap_req_fails_904.txt",
                seed: h =>
                {
                    var s = h.CreateSession("s", registered: false);
                    s.Nick = "Nick";
                },
                steps: _ => new[] { Step.Single("AUTHENTICATE PLAIN", "s", "AUTHENTICATE PLAIN") }));

            list.Add(Case.Custom(
                "sasl_unsupported_mech_905",
                snapshot: "sasl_unsupported_mech_905.txt",
                seed: h =>
                {
                    var s = h.CreateSession("s", registered: false);
                    s.Nick = "Nick";
                },
                steps: _ =>
                    new[]
                    {
                        Step.Single("CAP REQ :sasl", "s", "CAP REQ :sasl"),
                        Step.Single("AUTHENTICATE SCRAM", "s", "AUTHENTICATE SCRAM-SHA-256"),
                    }));

            list.Add(Case.Custom(
                "sasl_abort_906",
                snapshot: "sasl_abort_906.txt",
                seed: h =>
                {
                    var s = h.CreateSession("s", registered: false);
                    s.Nick = "Nick";
                },
                steps: _ =>
                    new[]
                    {
                        Step.Single("CAP REQ :sasl", "s", "CAP REQ :sasl"),
                        Step.Single("AUTHENTICATE *", "s", "AUTHENTICATE *"),
                    }));

            list.Add(Case.Custom(
                "sasl_plain_success_900_903",
                snapshot: "sasl_plain_success_900_903.txt",
                configureServices: OverrideSaslPlain("alice", "password", "alice"),
                seed: h =>
                {
                    var s = h.CreateSession("s", registered: false);
                    s.Nick = "Nick";
                },
                steps: _ =>
                    new[]
                    {
                        Step.Single("CAP REQ :sasl", "s", "CAP REQ :sasl"),
                        Step.Single("AUTHENTICATE PLAIN", "s", "AUTHENTICATE PLAIN"),
                        Step.Single("AUTHENTICATE <b64>", "s", "AUTHENTICATE AGFsaWNlAHBhc3N3b3Jk"),
                    }));

            list.Add(Case.Custom(
                "sasl_plain_failure_904",
                snapshot: "sasl_plain_failure_904.txt",
                configureServices: OverrideSaslPlain("alice", "password", "alice"),
                seed: h =>
                {
                    var s = h.CreateSession("s", registered: false);
                    s.Nick = "Nick";
                },
                steps: _ =>
                    new[]
                    {
                        Step.Single("CAP REQ :sasl", "s", "CAP REQ :sasl"),
                        Step.Single("AUTHENTICATE PLAIN", "s", "AUTHENTICATE PLAIN"),
                        Step.Single("AUTHENTICATE <b64 wrong>", "s", "AUTHENTICATE AGFsaWNlAGJhZA=="),
                    }));

            // ---- MOTD/LUSERS/NAMES basics ----
            list.Add(Case.Custom(
                "motd_basic",
                snapshot: "motd_basic.txt",
                seed: h => h.CreateSession("s", registered: true, nick: "Nick"),
                steps: _ => new[] { Step.Single("MOTD", "s", "MOTD") }));

            list.Add(Case.Custom(
                "lusers_basic",
                snapshot: "lusers_basic.txt",
                seed: h => h.CreateSession("s", registered: true, nick: "Nick"),
                steps: _ => new[] { Step.Single("LUSERS", "s", "LUSERS") }));

            list.Add(Case.Custom(
                "names_basic",
                snapshot: "names_basic.txt",
                seed: h =>
                {
                    var a = h.CreateSession("a", registered: true, nick: "Alice");
                    var ch = h.State.GetOrCreateChannel("#c");
                    ch.CreatedTs = 100;
                    h.State.TryJoinChannel(a.ConnectionId, a.Nick!, "#c");
                },
                steps: _ => new[] { Step.Single("NAMES #c", "a", "NAMES #c") }));

            // Sanity check: ensure we keep a substantial golden matrix (70+).
            if (list.Count < 80)
            {
                throw new InvalidOperationException($"Expected >= 80 golden cases, got {list.Count}");
            }

            return list;
        }

        public sealed record Case(
            string Name,
            string SnapshotFile,
            Action<IrcOptions>? ConfigureOptions,
            Action<IServiceCollection>? ConfigureServices,
            Action<GoldenIrcHarness>? Seed,
            Func<GoldenIrcHarness, IReadOnlyList<Step>> Steps)
        {
            public static Case UnregisteredScript(string name, params string[] lines)
                => Script(name, registered: false, lines);

            public static Case RegisteredScript(string name, params string[] lines)
                => Script(name, registered: true, lines);

            public static Case RegisteredScript(string name, string line, Action<IrcOptions>? configureOptions)
                => Script(name, registered: true, new[] { line }, configureOptions);

            private static Case Script(string name, bool registered, string[] lines, Action<IrcOptions>? configureOptions = null)
            {
                return new Case(
                    name,
                    SnapshotFile: name + ".txt",
                    ConfigureOptions: configureOptions,
                    ConfigureServices: null,
                    Seed: h => h.CreateSession("s", registered: registered, nick: registered ? "Nick" : null),
                    Steps: _ => new[] { Step.Batch("script", "s", lines) });
            }

            public static Case Custom(
                string name,
                string snapshot,
                Action<GoldenIrcHarness>? seed,
                Func<GoldenIrcHarness, IReadOnlyList<Step>> steps,
                Action<IrcOptions>? configureOptions = null,
                Action<IServiceCollection>? configureServices = null)
            {
                return new Case(name, snapshot, configureOptions, configureServices, seed, steps);
            }
        }

        public enum StepKind { Single, Batch, Capture }

        public sealed record Step(
            StepKind Kind,
            string Label,
            string SessionId,
            string[] Lines,
            Action<GoldenIrcHarness>? Before = null,
            Action<GoldenIrcHarness>? After = null)
        {
            public static Step Single(string label, string sessionId, string line)
                => new(StepKind.Single, label, sessionId, new[] { line });

            public static Step Batch(string label, string sessionId, params string[] lines)
                => new(StepKind.Batch, label, sessionId, lines);

            public static Step Capture(string label, string sessionId)
                => new(StepKind.Capture, label, sessionId, Array.Empty<string>());
        }

        private sealed class FixedSaslPlainAuthenticator : IRCd.Core.Abstractions.ISaslPlainAuthenticator
        {
            private readonly string _user;
            private readonly string _pass;
            private readonly string _account;

            public FixedSaslPlainAuthenticator(string user, string pass, string account)
            {
                _user = user;
                _pass = pass;
                _account = account;
            }

            public ValueTask<IRCd.Core.Abstractions.SaslAuthenticateResult> AuthenticatePlainAsync(string authcid, string password, CancellationToken ct)
            {
                if (string.Equals(authcid, _user, StringComparison.Ordinal) && string.Equals(password, _pass, StringComparison.Ordinal))
                {
                    return ValueTask.FromResult(new IRCd.Core.Abstractions.SaslAuthenticateResult(true, _account, null));
                }

                return ValueTask.FromResult(new IRCd.Core.Abstractions.SaslAuthenticateResult(false, null, "invalid"));
            }
        }

        private sealed class TestServiceEvents : IServiceSessionEvents
        {
            private readonly bool _identified;
            private readonly bool _registered;

            public TestServiceEvents(bool identified, bool registered = false)
            {
                _identified = identified;
                _registered = registered;
            }

            public ValueTask OnNickChangedAsync(IClientSession session, string? oldNick, string newNick, ServerState state, CancellationToken ct)
                => ValueTask.CompletedTask;

            public ValueTask OnQuitAsync(IClientSession session, string reason, ServerState state, CancellationToken ct)
                => ValueTask.CompletedTask;

            public ValueTask<bool> IsNickRegisteredAsync(string nick, CancellationToken ct)
                => ValueTask.FromResult(_registered);

            public ValueTask<bool> IsIdentifiedForNickAsync(string connectionId, string nick, CancellationToken ct)
                => ValueTask.FromResult(_identified);
        }
    }
}
