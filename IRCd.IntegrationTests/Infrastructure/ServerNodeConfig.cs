namespace IRCd.IntegrationTests.Infrastructure
{
    using System;
    using System.Globalization;
    using System.Text;

    public sealed record ServerNodeConfig(
        string Name,
        string Sid,
        int ClientPort,
        int ServerPort,
        int ObservabilityPort,
        string LinkPassword,
        string OperName,
        string OperPassword)
    {
        public string BuildConf(params LinkPeer[] peers)
        {
            var sb = new StringBuilder();

            sb.AppendLine("serverinfo {");
            sb.AppendLine($"    name = \"{Escape(Name)}\";");
            sb.AppendLine($"    sid = \"{Escape(Sid)}\";");
            sb.AppendLine($"    description = \"{Escape(Name)}\";");
            sb.AppendLine("    network = \"Integration\";");
            sb.AppendLine("    version = \"Integration\";");
            sb.AppendLine("};");
            sb.AppendLine();

            sb.AppendLine("listen {");
            sb.AppendLine("    bind = \"127.0.0.1\";");
            sb.AppendLine($"    clientport = {ClientPort.ToString(CultureInfo.InvariantCulture)};");
            sb.AppendLine("    enabletls = false;");
            sb.AppendLine($"    serverport = {ServerPort.ToString(CultureInfo.InvariantCulture)};");
            sb.AppendLine("};");
            sb.AppendLine();

            sb.AppendLine("observability {");
            sb.AppendLine("    enabled = true;");
            sb.AppendLine("    bind = \"127.0.0.1\";");
            sb.AppendLine($"    port = {ObservabilityPort.ToString(CultureInfo.InvariantCulture)};");
            sb.AppendLine("};");
            sb.AppendLine();

            sb.AppendLine("opersecurity {");
            sb.AppendLine("    require_hashed_passwords = false;");
            sb.AppendLine("};");
            sb.AppendLine();

            sb.AppendLine("class {");
            sb.AppendLine("    name = \"itest\";");
            sb.AppendLine("    description = \"Integration tests\";");
            sb.AppendLine("    maxconnectionsperip = 50;");
            sb.AppendLine("    capabilities = \"squit stats trace\";");
            sb.AppendLine("};");
            sb.AppendLine();

            sb.AppendLine("oper {");
            sb.AppendLine($"    name = \"{Escape(OperName)}\";");
            sb.AppendLine($"    password = \"{Escape(OperPassword)}\";");
            sb.AppendLine("    class = \"itest\";");
            sb.AppendLine("};");
            sb.AppendLine();

            // Keep integration tests deterministic: the harness polls (e.g. NAMES) while waiting for convergence.
            // Production configs should keep flood protection enabled.
            sb.AppendLine("flood {");
            sb.AppendLine("    commands {");
            sb.AppendLine("        enabled = false;");
            sb.AppendLine("    };");
            sb.AppendLine("};");
            sb.AppendLine();

            sb.AppendLine("ratelimit {");
            sb.AppendLine("    enabled = false;");
            sb.AppendLine("};");
            sb.AppendLine();

            // Speed up outbound link convergence.
            sb.AppendLine("transport {");
            sb.AppendLine("    s2s {");
            sb.AppendLine("        outbound_scan_interval = 1;");
            sb.AppendLine("        inbound_handshake_timeout = 10;");
            sb.AppendLine("        outbound_backoff_max = 2;");
            sb.AppendLine("        outbound_backoff_max_exponent = 1;");
            sb.AppendLine("    };");
            sb.AppendLine("};");
            sb.AppendLine();

            foreach (var peer in peers)
            {
                sb.AppendLine("link {");
                sb.AppendLine($"    name = \"{Escape(peer.Name)}\";");
                sb.AppendLine($"    sid = \"{Escape(peer.Sid)}\";");
                sb.AppendLine($"    host = \"127.0.0.1\";");
                sb.AppendLine($"    port = {peer.ServerPort.ToString(CultureInfo.InvariantCulture)};");
                sb.AppendLine($"    password = \"{Escape(LinkPassword)}\";");
                sb.AppendLine($"    outbound = {(peer.Outbound ? "true" : "false")};");
                sb.AppendLine("    usersync = true;");
                sb.AppendLine("};");
                sb.AppendLine();
            }

            return sb.ToString();
        }

        private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    public sealed record LinkPeer(string Name, string Sid, int ServerPort, bool Outbound);
}
