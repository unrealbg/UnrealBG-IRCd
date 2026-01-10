using System;
using System.IO;
using IRCd.Core.Config;
using IRCd.Shared.Options;

public sealed class ObservabilityConfigTests
{
    [Fact]
    public void ObservabilityBlock_ParsesEnabledBindAndPort()
    {
        var tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".conf");
        try
        {
            File.WriteAllText(tmp,
                "observability {\n" +
                "  enabled = true;\n" +
                "  bind = \"127.0.0.1\";\n" +
                "  port = 12345;\n" +
                "};\n");

            var o = new IrcOptions();
            IrcdConfLoader.ApplyConfFile(o, tmp);

            Assert.True(o.Observability.Enabled);
            Assert.Equal("127.0.0.1", o.Observability.BindIp);
            Assert.Equal(12345, o.Observability.Port);
        }
        finally
        {
            try { File.Delete(tmp); } catch { }
        }
    }
}
