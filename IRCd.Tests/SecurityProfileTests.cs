namespace IRCd.Tests
{
    using System;
    using System.Globalization;
    using System.IO;

    using IRCd.Core.Config;
    using IRCd.Shared.Options;

    using Xunit;

    public sealed class SecurityProfileTests
    {
        [Fact]
        public void PublicProfile_DoesNotOverrideExplicitConfValues()
        {
            var dir = Path.Combine(Path.GetTempPath(), "ircd-profile-conf-test-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(dir);

            try
            {
                var conf = Path.Combine(dir, "root.conf");

                File.WriteAllText(conf,
                    "security {\n" +
                    "  profile = \"public\"\n" +
                    "}\n" +
                    "opersecurity {\n" +
                    "  require_hashed_passwords = false\n" +
                    "}\n" +
                    "connectionguard {\n" +
                    "  registrationTimeoutSeconds = 99\n" +
                    "}\n" +
                    "transport {\n" +
                    "  client_max_line_chars = 500\n" +
                    "}\n");

                var selectedProfile = IrcdConfLoader.TryGetSecurityProfile(conf) ?? "default";
                var o = new IrcOptions();
                o.Security.Profile = selectedProfile;
                SecurityProfileApplier.Apply(o);
                IrcdConfLoader.ApplyConfFile(o, conf);

                Assert.Equal("public", o.Security.Profile);
                Assert.False(o.OperSecurity.RequireHashedPasswords);
                Assert.Equal(99, o.ConnectionGuard.RegistrationTimeoutSeconds);
                Assert.Equal(500, o.Transport.ClientMaxLineChars);
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        }

        [Fact]
        public void ApplyPublic_AppliesDefaultsWhenUnchangedFromBaseline()
        {
            var o = new IrcOptions
            {
                Security = new SecurityOptions { Profile = "public" }
            };

            SecurityProfileApplier.Apply(o);

            Assert.True(o.ConnectionGuard.Enabled);
            Assert.Equal(20, o.ConnectionGuard.RegistrationTimeoutSeconds);
            Assert.True(o.OperSecurity.RequireHashedPasswords);
            Assert.Equal(400, o.Transport.ClientMaxLineChars);
        }

        [Fact]
        public void TryGetSecurityProfile_ResolvesIncludesAndReturnsLastSeenValue()
        {
            var dir = Path.Combine(Path.GetTempPath(), "ircd-profile-test-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(dir);

            try
            {
                var a = Path.Combine(dir, "a.conf");
                var b = Path.Combine(dir, "b.conf");
                var root = Path.Combine(dir, "root.conf");

                File.WriteAllText(a, "security { profile = \"public\"; };\n");
                File.WriteAllText(b, "security { profile = \"trusted-lan\"; };\n");
                File.WriteAllText(root, "include \"a.conf\";\ninclude \"b.conf\";\n");

                var p = IrcdConfLoader.TryGetSecurityProfile(root);
                Assert.Equal("trusted-lan", p);
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        }
    }
}
