using System;
using System.IO;
using IRCd.Core.Config;
using IRCd.Shared.Options;

public sealed class OperSecurityConfigTests
{
    [Fact]
    public void OperSecurityBlock_ParsesRequireHashedPasswords()
    {
        var tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".conf");
        try
        {
            File.WriteAllText(tmp,
                "opersecurity {\n" +
                "  require_hashed_passwords = true;\n" +
                "};\n");

            var o = new IrcOptions();
            IrcdConfLoader.ApplyConfFile(o, tmp);

            Assert.True(o.OperSecurity.RequireHashedPasswords);
        }
        finally
        {
            try { File.Delete(tmp); } catch { }
        }
    }
}
