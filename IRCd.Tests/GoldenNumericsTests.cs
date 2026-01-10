namespace IRCd.Tests
{
    using IRCd.Core.Protocol;
    using IRCd.Tests.TestUtils;
    using IRCd.Shared.Options;

    using Xunit;

    public sealed class GoldenNumericsTests
    {
        public static IEnumerable<object[]> Cases => new List<object[]>
        {
            // 451 Not registered (critical client path)
            Case("MOTD requires registration", h => h.CreateSession("c1"), new[] { "MOTD" }, new[] { ":server 451 * :You have not registered" }),
            Case("LUSERS requires registration", h => h.CreateSession("c1"), new[] { "LUSERS" }, new[] { ":server 451 * :You have not registered" }),
            Case("NAMES requires registration", h => h.CreateSession("c1"), new[] { "NAMES" }, new[] { ":server 451 * :You have not registered" }),
            Case("WHOIS requires registration", h => h.CreateSession("c1"), new[] { "WHOIS bob" }, new[] { ":server 451 * :You have not registered" }),
            Case("PRIVMSG requires registration", h => h.CreateSession("c1"), new[] { "PRIVMSG bob :hi" }, new[] { ":server 451 * :You have not registered" }),
            Case("JOIN requires registration", h => h.CreateSession("c1"), new[] { "JOIN #chan" }, new[] { ":server 451 * :You have not registered" }),

            // 461 Need more params
            Case("WHOIS missing params -> 461", h => h.CreateSession("c1", registered: true, nick: "alice"), new[] { "WHOIS" }, new[] { ":server 461 alice WHOIS :Not enough parameters" }),
            Case("PRIVMSG missing params -> 461", h => h.CreateSession("c1", registered: true, nick: "alice"), new[] { "PRIVMSG" }, new[] { ":server 461 alice PRIVMSG :Not enough parameters" }),
            Case("JOIN missing params -> 461", h => h.CreateSession("c1", registered: true, nick: "alice"), new[] { "JOIN" }, new[] { ":server 461 alice JOIN :Not enough parameters" }),
            Case("MODE missing params -> 461", h => h.CreateSession("c1", registered: true, nick: "alice"), new[] { "MODE" }, new[] { ":server 461 alice MODE :Not enough parameters" }),
            Case("NICK missing params -> 431", h => h.CreateSession("c1", registered: true, nick: "alice"), new[] { "NICK" }, new[] { ":server 431 * :No nickname given" }),

            // 401 / 403
            Case("PRIVMSG to missing nick -> 401", h => h.CreateSession("c1", registered: true, nick: "alice"), new[] { "PRIVMSG bob :hi" }, new[] { ":server 401 alice bob :No such nick" }),
            Case("WHOIS missing nick -> 401/318", h => h.CreateSession("c1", registered: true, nick: "alice"), new[] { "WHOIS bob" }, new[]
            {
                ":server 401 alice bob :No such nick",
                ":server 318 alice bob :End of /WHOIS list.",
            }),
            Case("PRIVMSG to missing channel -> 403", h => h.CreateSession("c1", registered: true, nick: "alice"), new[] { "PRIVMSG #missing :hi" }, new[] { ":server 403 alice #missing :No such channel" }),
            Case("MODE on missing channel -> 403", h => h.CreateSession("c1", registered: true, nick: "alice"), new[] { "MODE #missing +m" }, new[] { ":server 403 alice #missing :No such channel" }),

            // NICK collisions
            Case("NICK in use -> 433", h =>
            {
                _ = h.CreateSession("c1", registered: true, nick: "alice");
                var bob = h.CreateSession("c2", registered: true, nick: "bob");
                return bob;
            }, new[] { "NICK alice" }, new[] { ":server 433 * alice :Nickname is already in use" }),

            // MOTD formats
            Case("MOTD success -> 375/372/376", h => h.CreateSession("c1", registered: true, nick: "alice"), new[] { "MOTD" }, new[]
            {
                ":server 375 alice :- server Message of the Day -",
                ":server 372 alice :- test motd",
                ":server 376 alice :End of /MOTD command.",
            }),

            // LUSERS formats (stable ordering)
            Case("LUSERS success -> 251/252/254/255", h => h.CreateSession("c1", registered: true, nick: "alice"), new[] { "LUSERS" }, new[]
            {
                ":server 251 alice :There are 1 users and 0 unknown connections",
                ":server 252 alice 0 :operator(s) online",
                ":server 254 alice 0 :channels formed",
                ":server 255 alice :I have 1 clients and 0 servers",
            }),

            // NAMES formats
            Case("NAMES empty -> 366 only", h => h.CreateSession("c1", registered: true, nick: "alice"), new[] { "NAMES #chan" }, new[]
            {
                ":server 366 alice #chan :End of /NAMES list.",
            }),

            // WHOIS formats (basic 311/312/318)
            Case("WHOIS basic -> 311/312/318", h =>
            {
                var session = h.CreateSession("c1", registered: true, nick: "alice");
                _ = h.CreateSession("c2", registered: true, nick: "bob");
                return session;
            }, new[] { "WHOIS bob" }, new[]
            {
                ":server 311 alice bob ident host * :Real Name",
                ":server 312 alice bob server :test",
                ":server 378 alice bob :is connecting from *@host",
                ":server 317 alice bob 10 1699999900 :seconds idle, signon time",
                ":server 318 alice bob :End of /WHOIS list.",
            }),

            // WHOIS already asserts deterministic 317 above.

            // Channel membership numerics
            Case("PRIVMSG to +n channel when not on channel -> 404", h =>
            {
                var alice = h.CreateSession("c1", registered: true, nick: "alice");
                var bob = h.CreateSession("c2", registered: true, nick: "bob");
                // Bob creates channel, Alice does not join
                _ = h.SendRawBatchAsync(bob, "JOIN #chan").GetAwaiter().GetResult();
                return alice;
            }, new[] { "PRIVMSG #chan :hi" }, new[] { ":server 404 alice #chan :Cannot send to channel" }),

            Case("PRIVMSG to non-+n channel when not on channel -> 442", h =>
            {
                var alice = h.CreateSession("c1", registered: true, nick: "alice");
                var bob = h.CreateSession("c2", registered: true, nick: "bob");
                _ = h.SendRawBatchAsync(bob, "JOIN #chan").GetAwaiter().GetResult();

                // Remove +n so it falls through to 442.
                if (h.State.TryGetChannel("#chan", out var ch) && ch is not null)
                {
                    ch.ApplyModeChange(IRCd.Core.State.ChannelModes.NoExternalMessages, enable: false);
                }

                return alice;
            }, new[] { "PRIVMSG #chan :hi" }, new[] { ":server 442 alice #chan :You're not on that channel" }),

            Case("PRIVMSG to +m channel without voice -> 404", h =>
            {
                var bob = h.CreateSession("c2", registered: true, nick: "bob");
                var alice = h.CreateSession("c1", registered: true, nick: "alice");
                _ = h.SendRawBatchAsync(bob, "JOIN #chan").GetAwaiter().GetResult();
                _ = h.SendRawBatchAsync(alice, "JOIN #chan").GetAwaiter().GetResult();

                if (h.State.TryGetChannel("#chan", out var ch) && ch is not null)
                {
                    ch.ApplyModeChange(IRCd.Core.State.ChannelModes.Moderated, enable: true);
                }

                return alice;
            }, new[] { "PRIVMSG #chan :hi" }, new[] { ":server 404 alice #chan :Cannot send to channel (+m)" }),

            Case("MODE on channel when not on channel -> 442", h =>
            {
                var alice = h.CreateSession("c1", registered: true, nick: "alice");
                var bob = h.CreateSession("c2", registered: true, nick: "bob");
                _ = h.SendRawBatchAsync(bob, "JOIN #chan").GetAwaiter().GetResult();
                return alice;
            }, new[] { "MODE #chan +m" }, new[] { ":server 442 alice #chan :You're not on that channel" }),

            Case("MODE requires chanop -> 482", h =>
            {
                var bob = h.CreateSession("c2", registered: true, nick: "bob");
                var alice = h.CreateSession("c1", registered: true, nick: "alice");
                // Bob creates channel first (likely gets op), Alice joins after.
                _ = h.SendRawBatchAsync(bob, "JOIN #chan").GetAwaiter().GetResult();
                _ = h.SendRawBatchAsync(alice, "JOIN #chan").GetAwaiter().GetResult();
                return alice;
            }, new[] { "MODE #chan +m" }, new[] { ":server 482 alice #chan :You're not channel operator" }),

            // Unknown command numeric from dispatcher
            Case("Unknown command -> 421", h => h.CreateSession("c1", registered: true, nick: "alice"), new[] { "WAT" }, new[] { ":server 421 alice WAT :Unknown command" }),

            // Additional critical: JOIN creates + end-of-names (basic client flow)
            Case("JOIN emits topic+created+names", h =>
            {
                // Pre-create channel and pin CreatedTs so the 329 numeric is deterministic.
                var ch = h.State.GetOrCreateChannel("#chan");
                ch.CreatedTs = 1_700_000_000;

                return h.CreateSession("c1", registered: true, nick: "alice");
            }, new[] { "JOIN #chan" }, new[]
            {
                ":alice!ident@host JOIN :#chan",
                ":server 331 alice #chan :No topic is set",
                ":server 329 alice #chan 1700000000",
                ":server 353 alice = #chan :@alice",
                ":server 366 alice #chan :End of /NAMES list.",
            }),

            // More 30+ coverage by duplicating core failures across commands
            Case("PRIVMSG missing trailing -> 461", h => h.CreateSession("c1", registered: true, nick: "alice"), new[] { "PRIVMSG bob hi" }, new[] { ":server 461 alice PRIVMSG :Not enough parameters" }),
            Case("WHOIS unknown command casing -> works/401/318", h => h.CreateSession("c1", registered: true, nick: "alice"), new[] { "whois bob" }, new[]
            {
                ":server 401 alice bob :No such nick",
                ":server 318 alice bob :End of /WHOIS list.",
            }),
            Case("NICK invalid -> 432", h => h.CreateSession("c1", registered: true, nick: "alice"), new[] { "NICK a,b" }, new[] { ":server 432 * a,b :Erroneous nickname" }),
            Case("NICK too long -> 432", h => h.CreateSession("c1", registered: true, nick: "alice"), new[] { "NICK aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" }, new[] { ":server 432 * aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa :Erroneous nickname" }),
            Case("JOIN bad channel name -> 479", h => h.CreateSession("c1", registered: true, nick: "alice"), new[] { "JOIN chan" }, new[] { ":server 479 alice chan :Illegal channel name" }),

            // MODE user-target errors
            Case("MODE other user -> 502", h =>
            {
                var alice = h.CreateSession("c1", registered: true, nick: "alice");
                _ = h.CreateSession("c2", registered: true, nick: "bob");
                return alice;
            }, new[] { "MODE bob +i" }, new[] { ":server 502 alice :Can't change mode for other users" }),

            Case("MODE unknown flags -> 501", h => h.CreateSession("c1", registered: true, nick: "alice"), new[] { "MODE alice ?" }, new[] { ":server 501 alice :Unknown MODE flags" }),

            // Additional PRIVMSG target validation
            Case("PRIVMSG multi-target invalid nicks -> 401x2", h => h.CreateSession("c1", registered: true, nick: "alice"), new[] { "PRIVMSG a,b :hi" }, new[]
            {
                ":server 401 alice a :No such nick",
                ":server 401 alice b :No such nick",
            }),
            Case("PRIVMSG multi-target mixed -> 403 then 401", h => h.CreateSession("c1", registered: true, nick: "alice"), new[] { "PRIVMSG #bad,chan :hi" }, new[]
            {
                ":server 403 alice #bad :No such channel",
                ":server 401 alice chan :No such nick",
            }),

            // Additional NAMES validation
            Case("NAMES invalid channel name -> 366", h => h.CreateSession("c1", registered: true, nick: "alice"), new[] { "NAMES chan" }, new[] { ":server 366 alice chan :End of /NAMES list." }),

            // NAMES on existing channel includes member list
            Case("NAMES returns 353/366 for existing channel", h =>
            {
                var alice = h.CreateSession("c1", registered: true, nick: "alice");
                _ = h.SendRawBatchAsync(alice, "JOIN #chan").GetAwaiter().GetResult();
                return alice;
            }, new[] { "NAMES #chan" }, new[]
            {
                ":server 353 alice = #chan :@alice",
                ":server 366 alice #chan :End of /NAMES list.",
            }),

            // MOTD disabled -> 422
            CaseWithOptions(
                "MOTD missing/disabled -> 422",
                o => { o.Motd.Lines = Array.Empty<string>(); o.Motd.FilePath = null; },
                h => h.CreateSession("c1", registered: true, nick: "alice"),
                new[] { "MOTD" },
                new[] { ":server 422 alice :MOTD File is missing" }
            ),
        };

        [Theory]
        [MemberData(nameof(Cases))]
        public async Task Golden_numerics_are_exact(
            string name,
            Action<IrcOptions> configureOptions,
            Func<GoldenIrcHarness, GoldenIrcHarness.GoldenTestSession> createSession,
            string[] inputLines,
            string[] expected)
        {
            Assert.False(string.IsNullOrWhiteSpace(name));

            var harness = new GoldenIrcHarness(configureOptions: configureOptions);
            var session = createSession(harness);

            var output = await harness.SendRawBatchAsync(session, inputLines);

            Assert.Equal(expected, output);
        }

        private static object[] Case(string name, Func<GoldenIrcHarness, GoldenIrcHarness.GoldenTestSession> createSession, string[] inputLines, string[] expected)
            => new object[] { name, (Action<IrcOptions>)NoOptions, createSession, inputLines, expected };

        private static object[] CaseWithOptions(
            string name,
            Action<IrcOptions> configureOptions,
            Func<GoldenIrcHarness, GoldenIrcHarness.GoldenTestSession> createSession,
            string[] inputLines,
            string[] expected)
            => new object[] { name, configureOptions, createSession, inputLines, expected };

        private static void NoOptions(IrcOptions _) { }
    }
}
