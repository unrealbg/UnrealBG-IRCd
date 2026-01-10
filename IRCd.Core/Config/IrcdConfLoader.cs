namespace IRCd.Core.Config
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Text;
    using IRCd.Shared.Options;

    public static class IrcdConfLoader
    {
        public static void ApplyConfFile(IrcOptions target, string filePath)
        {
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            ApplyInternal(target, filePath, visited);
        }

        /// <summary>
        /// Scans a conf file (and its includes) for security { profile = ... }.
        /// Returns the last profile value encountered in include-expansion order, or null if not specified.
        /// </summary>
        public static string? TryGetSecurityProfile(string filePath)
        {
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            return TryGetSecurityProfileInternal(filePath, visited);
        }

        private static string? TryGetSecurityProfileInternal(string filePath, HashSet<string> visited)
        {
            var fullPath = Path.GetFullPath(filePath);
            if (!visited.Add(fullPath))
            {
                throw new InvalidOperationException($"Config include cycle detected: {fullPath}");
            }

            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException($"Config file not found: {fullPath}", fullPath);
            }

            var baseDir = Path.GetDirectoryName(fullPath) ?? Directory.GetCurrentDirectory();

            var tokens = Tokenize(fullPath);
            string? found = null;

            for (var i = 0; i < tokens.Count; i++)
            {
                var t = tokens[i];
                if (t.Kind != TokenKind.Ident)
                {
                    continue;
                }

                if (string.Equals(t.Text, "include", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 < tokens.Count)
                    {
                        var includePath = tokens[i + 1].Text;
                        i++;

                        var p = Path.IsPathRooted(includePath)
                            ? includePath
                            : Path.Combine(baseDir, includePath);

                        var nested = TryGetSecurityProfileInternal(p, visited);
                        if (!string.IsNullOrWhiteSpace(nested))
                        {
                            found = nested;
                        }
                    }
                    continue;
                }

                if (!string.Equals(t.Text, "security", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (i + 1 >= tokens.Count || tokens[i + 1].Kind != TokenKind.Punct || tokens[i + 1].Text != "{")
                {
                    continue;
                }

                i += 2;
                while (i < tokens.Count)
                {
                    var inner = tokens[i];
                    if (inner.Kind == TokenKind.Punct && inner.Text == "}")
                    {
                        break;
                    }

                    if (inner.Kind == TokenKind.Ident && string.Equals(inner.Text, "profile", StringComparison.OrdinalIgnoreCase))
                    {
                        var j = i + 1;
                        if (j < tokens.Count && tokens[j].Kind == TokenKind.Punct && tokens[j].Text == "=")
                        {
                            j++;
                        }

                        if (j < tokens.Count && tokens[j].Kind is TokenKind.Ident or TokenKind.String)
                        {
                            var v = tokens[j].Text;
                            if (!string.IsNullOrWhiteSpace(v))
                            {
                                found = v;
                            }
                        }
                    }

                    i++;
                }
            }

            return found;
        }

        private static void ApplyInternal(IrcOptions target, string filePath, HashSet<string> visited)
        {
            var fullPath = Path.GetFullPath(filePath);
            if (!visited.Add(fullPath))
            {
                throw new InvalidOperationException($"Config include cycle detected: {fullPath}");
            }

            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException($"Config file not found: {fullPath}", fullPath);
            }

            var baseDir = Path.GetDirectoryName(fullPath) ?? Directory.GetCurrentDirectory();

            var tokens = Tokenize(fullPath);
            var p = new Parser(tokens, baseDir, target, visited);
            p.ParseDocument();
        }

        private static List<Token> Tokenize(string filePath)
        {
            var text = File.ReadAllText(filePath);
            var tokens = new List<Token>(text.Length / 3);

            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];

                if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
                {
                    while (i < text.Length && text[i] != '\n')
                        i++;
                    continue;
                }

                if (c == '#' || c == ';')
                {
                    while (i < text.Length && text[i] != '\n')
                    {
                        i++;
                    }

                    continue;
                }

                if (char.IsWhiteSpace(c))
                {
                    continue;
                }

                if (c is '{' or '}' or ';' or '=')
                {
                    tokens.Add(new Token(c.ToString(), TokenKind.Punct));
                    continue;
                }

                if (c == '"' || c == '\'')
                {
                    var quote = c;
                    var sb = new StringBuilder();
                    i++;
                    for (; i < text.Length; i++)
                    {
                        c = text[i];
                        if (c == quote)
                        {
                            break;
                        }

                        if (c == '\\' && i + 1 < text.Length)
                        {
                            var n = text[i + 1];
                            if (n == 'n')
                            {
                                sb.Append('\n');
                                i++;
                                continue;
                            }

                            sb.Append(n);
                            i++;
                            continue;
                        }

                        sb.Append(c);
                    }

                    tokens.Add(new Token(sb.ToString(), TokenKind.String));
                    continue;
                }

                {
                    var start = i;
                    while (i < text.Length)
                    {
                        c = text[i];
                        if (char.IsWhiteSpace(c) || c is '{' or '}' or ';' or '=')
                        {
                            break;
                        }

                        if (c == '#' || c == ';')
                        {
                            break;
                        }

                        i++;
                    }
                    var raw = text[start..i];
                    i--;
                    tokens.Add(new Token(raw, TokenKind.Ident));
                }
            }

            return tokens;
        }

        private enum TokenKind
        {
            Ident,
            String,
            Punct,
        }

        private readonly record struct Token(string Text, TokenKind Kind);

        private sealed class Parser
        {
            private readonly List<Token> _t;
            private int _i;

            private readonly string _baseDir;
            private readonly IrcOptions _o;
            private readonly HashSet<string> _visited;

            public Parser(List<Token> tokens, string baseDir, IrcOptions o, HashSet<string> visited)
            {
                _t = tokens;
                _baseDir = baseDir;
                _o = o;
                _visited = visited;
            }

            public void ParseDocument()
            {
                while (!Eof)
                {
                    if (TryConsumeIdent("include"))
                    {
                        var inc = ParseValue();
                        ConsumeOptionalPunct(";");
                        Include(inc);
                        continue;
                    }

                    if (TryConsumeIdent("serverinfo"))
                    {
                        ConsumePunct("{");
                        ParseServerInfoBlock();
                        ConsumePunct("}");
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("listen"))
                    {
                        ConsumePunct("{");
                        ParseListenBlock();
                        ConsumePunct("}");
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("class"))
                    {
                        ConsumePunct("{");
                        ParseClassBlock();
                        ConsumePunct("}");
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("oper"))
                    {
                        ConsumePunct("{");
                        ParseOperBlock();
                        ConsumePunct("}");
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("opersecurity"))
                    {
                        ConsumePunct("{");
                        ParseOperSecurityBlock();
                        ConsumePunct("}");
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("kline"))
                    {
                        ConsumePunct("{");
                        ParseKLineBlock();
                        ConsumePunct("}");
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("dline"))
                    {
                        ConsumePunct("{");
                        ParseDLineBlock();
                        ConsumePunct("}");
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("link"))
                    {
                        ConsumePunct("{");
                        ParseLinkBlock(defaultOutbound: false);
                        ConsumePunct("}");
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("connect"))
                    {
                        ConsumePunct("{");
                        ParseLinkBlock(defaultOutbound: true);
                        ConsumePunct("}");
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("motd"))
                    {
                        ConsumePunct("{");
                        ParseMotdBlock();
                        ConsumePunct("}");
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("limits"))
                    {
                        ConsumePunct("{");
                        ParseLimitsBlock();
                        ConsumePunct("}");
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("bans"))
                    {
                        ConsumePunct("{");
                        ParseBansBlock();
                        ConsumePunct("}");
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("connectionguard"))
                    {
                        ConsumePunct("{");
                        ParseConnectionGuardBlock();
                        ConsumePunct("}");
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("connectionprecheck"))
                    {
                        ConsumePunct("{");
                        ParseConnectionPrecheckBlock();
                        ConsumePunct("}");
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("ratelimit"))
                    {
                        ConsumePunct("{");
                        ParseRateLimitBlock();
                        ConsumePunct("}");
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("ping"))
                    {
                        ConsumePunct("{");
                        ParsePingBlock();
                        ConsumePunct("}");
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("observability"))
                    {
                        ConsumePunct("{");
                        ParseObservabilityBlock();
                        ConsumePunct("}");
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("audit"))
                    {
                        ConsumePunct("{");
                        ParseAuditBlock();
                        ConsumePunct("}");
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("services"))
                    {
                        ConsumePunct("{");
                        ParseServicesBlock();
                        ConsumePunct("}");
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("isupport"))
                    {
                        ConsumePunct("{");
                        ParseIsupportBlock();
                        ConsumePunct("}");
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("transport"))
                    {
                        ConsumePunct("{");
                        ParseTransportBlock();
                        ConsumePunct("}");
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("security"))
                    {
                        ConsumePunct("{");
                        ParseSecurityBlock();
                        ConsumePunct("}");
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("flood"))
                    {
                        ConsumePunct("{");
                        ParseFloodBlock();
                        ConsumePunct("}");
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("autodline"))
                    {
                        ConsumePunct("{");
                        ParseAutoDlineBlock();
                        ConsumePunct("}");
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("auth"))
                    {
                        ConsumePunct("{");
                        ParseAuthBlock();
                        ConsumePunct("}");
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("sasl"))
                    {
                        ConsumePunct("{");
                        ParseSaslBlock();
                        ConsumePunct("}");
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    var key = ParseIdent();
                    ConsumePunct("=");
                    var value = ParseValue();
                    ConsumeOptionalPunct(";");
                    ApplyKeyValue(_o, key, value);
                }
            }

            private void ParseServerInfoBlock()
            {
                while (!Eof && !IsPunct("}"))
                {
                    if (TryConsumeIdent("name"))
                    {
                        ConsumePunct("=");
                        _o.ServerInfo.Name = ParseValue();
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("sid"))
                    {
                        ConsumePunct("=");
                        _o.ServerInfo.Sid = ParseValue();
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("description"))
                    {
                        ConsumePunct("=");
                        _o.ServerInfo.Description = ParseValue();
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("network"))
                    {
                        ConsumePunct("=");
                        _o.ServerInfo.Network = ParseValue();
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("version"))
                    {
                        ConsumePunct("=");
                        _o.ServerInfo.Version = ParseValue();
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("adminlocation1"))
                    {
                        ConsumePunct("=");
                        _o.ServerInfo.AdminLocation1 = ParseValue();
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("adminlocation2"))
                    {
                        ConsumePunct("=");
                        _o.ServerInfo.AdminLocation2 = ParseValue();
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("adminemail"))
                    {
                        ConsumePunct("=");
                        _o.ServerInfo.AdminEmail = ParseValue();
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    ConsumeUnknownStatement();
                }
            }

            private void ParseAutoDlineBlock()
            {
                var whitelist = new List<string>();

                while (!Eof && !IsPunct("}"))
                {
                    if (TryConsumeIdent("enabled"))
                    {
                        ConsumePunct("=");
                        _o.AutoDline.Enabled = ParseBoolValue(ParseValue());
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("windowSeconds"))
                    {
                        ConsumePunct("=");
                        if (int.TryParse(ParseValue(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                        {
                            _o.AutoDline.WindowSeconds = v;
                        }

                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("threshold"))
                    {
                        ConsumePunct("=");
                        if (int.TryParse(ParseValue(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                        {
                            _o.AutoDline.Threshold = v;
                        }

                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("baseDurationSeconds"))
                    {
                        ConsumePunct("=");
                        if (int.TryParse(ParseValue(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                        {
                            _o.AutoDline.BaseDurationSeconds = v;
                        }

                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("backoffFactor"))
                    {
                        ConsumePunct("=");
                        if (int.TryParse(ParseValue(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                        {
                            _o.AutoDline.BackoffFactor = v;
                        }

                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("maxDurationSeconds"))
                    {
                        ConsumePunct("=");
                        if (int.TryParse(ParseValue(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                        {
                            _o.AutoDline.MaxDurationSeconds = v;
                        }

                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("whitelist"))
                    {
                        ConsumePunct("=");
                        var raw = ParseValue();
                        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        {
                            if (!string.IsNullOrWhiteSpace(part))
                            {
                                whitelist.Add(part);
                            }
                        }

                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    var _ = ParseIdent();
                    if (IsPunct("="))
                    {
                        ConsumePunct("=");
                        _ = ParseValue();
                    }

                    ConsumeOptionalPunct(";");
                }

                if (whitelist.Count > 0)
                {
                    _o.AutoDline.WhitelistCidrs = whitelist.ToArray();
                }
            }

            private static bool ParseBoolValue(string v)
            {
                if (string.IsNullOrWhiteSpace(v))
                {
                    return false;
                }

                return v.Equals("true", StringComparison.OrdinalIgnoreCase)
                    || v.Equals("yes", StringComparison.OrdinalIgnoreCase)
                    || v.Equals("on", StringComparison.OrdinalIgnoreCase)
                    || v == "1";
            }

            private void ParseListenBlock()
            {
                var bindIp = _o.Listen.BindIp;
                var clientPort = (int?)null;
                var tlsPort = (int?)null;

                while (!Eof && !IsPunct("}"))
                {
                    if (TryConsumeIdent("bind"))
                    {
                        ConsumePunct("=");
                        bindIp = ParseValue();
                        _o.Listen.BindIp = bindIp;
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("clientport"))
                    {
                        ConsumePunct("=");
                        if (int.TryParse(ParseValue(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var p))
                        {
                            _o.Listen.ClientPort = p;
                            clientPort = p;
                        }

                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("tlsclientport"))
                    {
                        ConsumePunct("=");
                        if (int.TryParse(ParseValue(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var p))
                        {
                            _o.Listen.TlsClientPort = p;
                            tlsPort = p;
                        }

                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("tlscertificatepath"))
                    {
                        ConsumePunct("=");
                        _o.Listen.TlsCertificatePath = ParseValue();
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("tlscertificatepassword"))
                    {
                        ConsumePunct("=");
                        _o.Listen.TlsCertificatePassword = ParseValue();
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("autogenerateselfsignedcertificate"))
                    {
                        ConsumePunct("=");
                        var v = ParseValue();
                        _o.Listen.AutoGenerateSelfSignedCertificate = string.Equals(v, "true", StringComparison.OrdinalIgnoreCase) || v == "1";
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("autogeneratedcertpath"))
                    {
                        ConsumePunct("=");
                        _o.Listen.AutoGeneratedCertPath = ParseValue();
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("autogeneratedcertpassword"))
                    {
                        ConsumePunct("=");
                        _o.Listen.AutoGeneratedCertPassword = ParseValue();
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("autogeneratedcertdaysvalid"))
                    {
                        ConsumePunct("=");
                        if (int.TryParse(ParseValue(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                        {
                            _o.Listen.AutoGeneratedCertDaysValid = v;
                        }

                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("autogeneratedcertcommonname"))
                    {
                        ConsumePunct("=");
                        _o.Listen.AutoGeneratedCertCommonName = ParseValue();
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("tlscertificate") || TryConsumeIdent("certificate"))
                    {
                        ConsumePunct("{");
                        ParseListenTlsCertificateBlock();
                        ConsumePunct("}");
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("enabletls"))
                    {
                        ConsumePunct("=");
                        var v = ParseValue();
                        _o.Listen.EnableTls = string.Equals(v, "true", StringComparison.OrdinalIgnoreCase) || v == "1";
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("requestclientcertificate"))
                    {
                        ConsumePunct("=");
                        var v = ParseValue();
                        _o.Listen.RequestClientCertificate = string.Equals(v, "true", StringComparison.OrdinalIgnoreCase) || v == "1";
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("serverport"))
                    {
                        ConsumePunct("=");
                        if (int.TryParse(ParseValue(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var p))
                        {
                            _o.Listen.ServerPort = p;
                        }

                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    ConsumeUnknownStatement();
                }

                var endpoints = new List<ListenEndpointOptions>(_o.ListenEndpoints);

                if (clientPort is int cp && cp > 0)
                {
                    endpoints.Add(new ListenEndpointOptions { BindIp = bindIp, Port = cp, Tls = false });
                }

                if ((_o.Listen.EnableTls || tlsPort is not null) && (tlsPort ?? _o.Listen.TlsClientPort) > 0)
                {
                    endpoints.Add(new ListenEndpointOptions { BindIp = bindIp, Port = tlsPort ?? _o.Listen.TlsClientPort, Tls = true });
                }

                if (endpoints.Count > 0)
                    _o.ListenEndpoints = endpoints.ToArray();
            }

            private void ParseListenTlsCertificateBlock()
            {
                string? name = null;
                var cert = new TlsCertificateOptions();

                while (!Eof && !IsPunct("}"))
                {
                    if (TryConsumeIdent("name"))
                    {
                        ConsumePunct("=");
                        name = ParseValue();
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("path"))
                    {
                        ConsumePunct("=");
                        cert.Path = ParseValue();
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("password"))
                    {
                        ConsumePunct("=");
                        cert.Password = ParseValue();
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    ConsumeUnknownStatement();
                }

                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(cert.Path))
                {
                    return;
                }

                _o.Listen.TlsCertificates ??= new Dictionary<string, TlsCertificateOptions>(StringComparer.OrdinalIgnoreCase);
                _o.Listen.TlsCertificates[name!] = cert;
            }

            private void ParseLimitsBlock()
            {
                while (!Eof && !IsPunct("}"))
                {
                    if (TryConsumeIdent("maxisonnames"))
                    {
                        ConsumePunct("=");
                        if (int.TryParse(ParseValue(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                        {
                            _o.Limits.MaxIsonNames = v;
                        }

                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("maxwhomasklength"))
                    {
                        ConsumePunct("=");
                        if (int.TryParse(ParseValue(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                        {
                            _o.Limits.MaxWhoMaskLength = v;
                        }

                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("maxwhoistargets"))
                    {
                        ConsumePunct("=");
                        if (int.TryParse(ParseValue(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                        {
                            _o.Limits.MaxWhoisTargets = v;
                        }

                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("maxuserhosttargets"))
                    {
                        ConsumePunct("=");
                        if (int.TryParse(ParseValue(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                        {
                            _o.Limits.MaxUserhostTargets = v;
                        }

                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("maxnameschannels"))
                    {
                        ConsumePunct("=");
                        if (int.TryParse(ParseValue(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                        {
                            _o.Limits.MaxNamesChannels = v;
                        }

                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("maxlisttargets"))
                    {
                        ConsumePunct("=");
                        if (int.TryParse(ParseValue(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                        {
                            _o.Limits.MaxListTargets = v;
                        }

                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("maxprivmsgtargets"))
                    {
                        ConsumePunct("=");
                        if (int.TryParse(ParseValue(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                        {
                            _o.Limits.MaxPrivmsgTargets = v;
                        }

                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("maxnoticetargets"))
                    {
                        ConsumePunct("=");
                        if (int.TryParse(ParseValue(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                        {
                            _o.Limits.MaxNoticeTargets = v;
                        }

                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("maxsilenceentries"))
                    {
                        ConsumePunct("=");
                        if (int.TryParse(ParseValue(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                        {
                            _o.Limits.MaxSilenceEntries = v;
                        }

                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("maxwatchentries"))
                    {
                        ConsumePunct("=");
                        if (int.TryParse(ParseValue(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                        {
                            _o.Limits.MaxWatchEntries = v;
                        }

                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    ConsumeUnknownStatement();
                }
            }

            private void ParseConnectionGuardBlock()
            {
                while (!Eof && !IsPunct("}"))
                {
                    if (TryConsumeIdent("enabled"))
                    {
                        ConsumePunct("=");
                        var v = ParseValue();
                        _o.ConnectionGuard.Enabled = string.Equals(v, "true", StringComparison.OrdinalIgnoreCase) || v == "1";
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("maxconnectionsperwindowperip"))
                    {
                        ConsumePunct("=");
                        if (int.TryParse(ParseValue(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                        {
                            _o.ConnectionGuard.MaxConnectionsPerWindowPerIp = v;
                        }

                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("maxconnectionsperwindowperiptls"))
                    {
                        ConsumePunct("=");
                        if (int.TryParse(ParseValue(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                        {
                            _o.ConnectionGuard.MaxConnectionsPerWindowPerIpTls = v;
                        }

                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("maxtlshandshakesperwindowperip"))
                    {
                        ConsumePunct("=");
                        if (int.TryParse(ParseValue(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                        {
                            _o.ConnectionGuard.MaxTlsHandshakesPerWindowPerIp = v;
                        }

                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("windowseconds"))
                    {
                        ConsumePunct("=");
                        if (int.TryParse(ParseValue(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                        {
                            _o.ConnectionGuard.WindowSeconds = v;
                        }

                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("maxactiveconnectionsperip"))
                    {
                        ConsumePunct("=");
                        if (int.TryParse(ParseValue(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                        {
                            _o.ConnectionGuard.MaxActiveConnectionsPerIp = v;
                        }

                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("globalmaxactiveconnections"))
                    {
                        ConsumePunct("=");
                        if (int.TryParse(ParseValue(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                        {
                            _o.ConnectionGuard.GlobalMaxActiveConnections = v;
                        }

                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("maxunregisteredperip"))
                    {
                        ConsumePunct("=");
                        if (int.TryParse(ParseValue(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                        {
                            _o.ConnectionGuard.MaxUnregisteredPerIp = v;
                        }

                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("registrationtimeoutseconds"))
                    {
                        ConsumePunct("=");
                        if (int.TryParse(ParseValue(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                        {
                            _o.ConnectionGuard.RegistrationTimeoutSeconds = v;
                        }

                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("tlshandshaketimeoutseconds"))
                    {
                        ConsumePunct("=");
                        if (int.TryParse(ParseValue(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                            _o.ConnectionGuard.TlsHandshakeTimeoutSeconds = v;
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("rejectmessage"))
                    {
                        ConsumePunct("=");
                        _o.ConnectionGuard.RejectMessage = ParseValue();
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    ConsumeUnknownStatement();
                }
            }

            private void ParseConnectionPrecheckBlock()
            {
                var dnsbl = new List<DnsblZoneOptions>();
                var tor = new List<DnsblZoneOptions>();
                var vpn = new List<DnsblZoneOptions>();

                while (!Eof && !IsPunct("}"))
                {
                    if (TryConsumeIdent("enabled"))
                    {
                        ConsumePunct("=");
                        var v = ParseValue();
                        _o.ConnectionPrecheck.Enabled = string.Equals(v, "true", StringComparison.OrdinalIgnoreCase) || v == "1";
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("fail_open") || TryConsumeIdent("failopen"))
                    {
                        ConsumePunct("=");
                        _o.ConnectionPrecheck.FailOpen = ParseBoolValue(ParseValue());
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("timeoutms") || TryConsumeIdent("global_timeout_ms") || TryConsumeIdent("globaltimeoutms"))
                    {
                        ConsumePunct("=");
                        if (int.TryParse(ParseValue(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                        {
                            _o.ConnectionPrecheck.GlobalTimeoutMs = v;
                        }

                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("dnsbl_timeout_ms") || TryConsumeIdent("dnsbltimeoutms"))
                    {
                        ConsumePunct("=");
                        if (int.TryParse(ParseValue(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                        {
                            _o.ConnectionPrecheck.DnsblTimeoutMs = v;
                        }

                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("tor_timeout_ms") || TryConsumeIdent("tortimeoutms"))
                    {
                        ConsumePunct("=");
                        if (int.TryParse(ParseValue(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                        {
                            _o.ConnectionPrecheck.TorTimeoutMs = v;
                        }

                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("vpn_timeout_ms") || TryConsumeIdent("vpntimeoutms"))
                    {
                        ConsumePunct("=");
                        if (int.TryParse(ParseValue(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                        {
                            _o.ConnectionPrecheck.VpnTimeoutMs = v;
                        }

                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("cacheallowttlseconds"))
                    {
                        ConsumePunct("=");
                        if (int.TryParse(ParseValue(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                        {
                            _o.ConnectionPrecheck.CacheAllowTtlSeconds = v;
                        }

                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("cacheblockttlseconds"))
                    {
                        ConsumePunct("=");
                        if (int.TryParse(ParseValue(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                        {
                            _o.ConnectionPrecheck.CacheBlockTtlSeconds = v;
                        }

                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("cachemaxentries"))
                    {
                        ConsumePunct("=");
                        if (int.TryParse(ParseValue(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                        {
                            _o.ConnectionPrecheck.CacheMaxEntries = v;
                        }

                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("skipnonpublicips"))
                    {
                        ConsumePunct("=");
                        var v = ParseValue();
                        _o.ConnectionPrecheck.SkipNonPublicIps = string.Equals(v, "true", StringComparison.OrdinalIgnoreCase) || v == "1";
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("dnsbl"))
                    {
                        ConsumePunct("{");
                        dnsbl.Add(ParseConnectionPrecheckZoneBlock());
                        ConsumePunct("}");
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("tor_dnsbl"))
                    {
                        ConsumePunct("{");
                        tor.Add(ParseConnectionPrecheckZoneBlock());
                        ConsumePunct("}");
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("vpn_dnsbl"))
                    {
                        ConsumePunct("{");
                        vpn.Add(ParseConnectionPrecheckZoneBlock());
                        ConsumePunct("}");
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    ConsumeUnknownStatement();
                }

                _o.ConnectionPrecheck.Dnsbl = dnsbl.ToArray();
                _o.ConnectionPrecheck.TorDnsbl = tor.ToArray();
                _o.ConnectionPrecheck.VpnDnsbl = vpn.ToArray();
            }

            private DnsblZoneOptions ParseConnectionPrecheckZoneBlock()
            {
                var z = new DnsblZoneOptions();

                while (!Eof && !IsPunct("}"))
                {
                    if (TryConsumeIdent("zone"))
                    {
                        ConsumePunct("=");
                        z.Zone = ParseValue();
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("reason"))
                    {
                        ConsumePunct("=");
                        z.Reason = ParseValue();
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("tempdlineseconds"))
                    {
                        ConsumePunct("=");
                        if (int.TryParse(ParseValue(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                            z.TempDlineSeconds = v;
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    ConsumeUnknownStatement();
                }

                return z;
            }

            private void ParseBansBlock()
            {
                while (!Eof && !IsPunct("}"))
                {
                    if (TryConsumeIdent("enforcement_check_interval"))
                    {
                        ConsumePunct("=");
                        if (int.TryParse(ParseValue(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                        {
                            _o.Bans.EnforcementCheckIntervalSeconds = v;
                        }

                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    ConsumeUnknownStatement();
                }
            }

            private void ParseRateLimitBlock()
            {
                while (!Eof && !IsPunct("}"))
                {
                    if (TryConsumeIdent("enabled"))
                    {
                        ConsumePunct("=");
                        var v = ParseValue();
                        _o.RateLimit.Enabled = string.Equals(v, "true", StringComparison.OrdinalIgnoreCase) || v == "1";
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("privmsg"))
                    {
                        ConsumePunct("{");
                        ParseTokenBucketBlock(_o.RateLimit.PrivMsg);
                        ConsumePunct("}");
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("notice"))
                    {
                        ConsumePunct("{");
                        ParseTokenBucketBlock(_o.RateLimit.Notice);
                        ConsumePunct("}");
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("join"))
                    {
                        ConsumePunct("{");
                        ParseTokenBucketBlock(_o.RateLimit.Join);
                        ConsumePunct("}");
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("disconnect"))
                    {
                        ConsumePunct("{");
                        ParseFloodDisconnectBlock(_o.RateLimit.Disconnect);
                        ConsumePunct("}");
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    ConsumeUnknownStatement();
                }
            }

            private void ParseTokenBucketBlock(TokenBucketOptions target)
            {
                while (!Eof && !IsPunct("}"))
                {
                    if (TryConsumeIdent("capacity"))
                    {
                        ConsumePunct("=");
                        if (int.TryParse(ParseValue(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                        {
                            target.Capacity = v;
                        }

                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("refilltokens"))
                    {
                        ConsumePunct("=");
                        if (int.TryParse(ParseValue(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                        {
                            target.RefillTokens = v;
                        }

                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("refillperiod"))
                    {
                        ConsumePunct("=");
                        if (int.TryParse(ParseValue(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                        {
                            target.RefillPeriodSeconds = v;
                        }

                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    ConsumeUnknownStatement();
                }
            }

            private void ParseFloodDisconnectBlock(FloodDisconnectOptions target)
            {
                while (!Eof && !IsPunct("}"))
                {
                    if (TryConsumeIdent("enabled")) { ConsumePunct("="); var v = ParseValue(); target.Enabled = string.Equals(v, "true", StringComparison.OrdinalIgnoreCase) || v == "1"; ConsumeOptionalPunct(";"); continue; }
                    if (TryConsumeIdent("maxviolations")) { ConsumePunct("="); if (int.TryParse(ParseValue(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) target.MaxViolations = v; ConsumeOptionalPunct(";"); continue; }
                    if (TryConsumeIdent("window")) { ConsumePunct("="); if (int.TryParse(ParseValue(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) target.WindowSeconds = v; ConsumeOptionalPunct(";"); continue; }
                    if (TryConsumeIdent("quit")) { ConsumePunct("="); target.QuitMessage = ParseValue(); ConsumeOptionalPunct(";"); continue; }
                    ConsumeUnknownStatement();
                }
            }

            private void ParseLinkBlock(bool defaultOutbound)
            {
                var link = new LinkOptions();
                link.Outbound = defaultOutbound;

                while (!Eof && !IsPunct("}"))
                {
                    if (TryConsumeIdent("name"))
                    {
                        ConsumePunct("=");
                        link.Name = ParseValue();
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("sid"))
                    {
                        ConsumePunct("=");
                        link.Sid = ParseValue();
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("host"))
                    {
                        ConsumePunct("=");
                        link.Host = ParseValue();
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("port"))
                    {
                        ConsumePunct("=");
                        if (int.TryParse(ParseValue(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var p))
                            link.Port = p;
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("password"))
                    {
                        ConsumePunct("=");
                        link.Password = ParseValue();
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("outbound"))
                    {
                        ConsumePunct("=");
                        var v = ParseValue();
                        link.Outbound = string.Equals(v, "true", StringComparison.OrdinalIgnoreCase) || v == "1";
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("usersync"))
                    {
                        ConsumePunct("=");
                        var v = ParseValue();
                        link.UserSync = string.Equals(v, "true", StringComparison.OrdinalIgnoreCase) || v == "1";
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    ConsumeUnknownStatement();
                }

                if (!string.IsNullOrWhiteSpace(link.Name))
                {
                    var list = new List<IRCd.Shared.Options.LinkOptions>(_o.Links);
                    list.Add(link);
                    _o.Links = list.ToArray();
                }
            }

            private void ParseOperBlock()
            {
                var oper = new OperOptions();

                while (!Eof && !IsPunct("}"))
                {
                    if (TryConsumeIdent("name"))
                    {
                        ConsumePunct("=");
                        oper.Name = ParseValue();
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("password"))
                    {
                        ConsumePunct("=");
                        oper.Password = ParseValue();
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("class"))
                    {
                        ConsumePunct("=");
                        oper.Class = ParseValue();
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    ConsumeUnknownStatement();
                }

                if (!string.IsNullOrWhiteSpace(oper.Name) && !string.IsNullOrWhiteSpace(oper.Password))
                {
                    var list = new List<OperOptions>(_o.Opers);
                    list.Add(oper);
                    _o.Opers = list.ToArray();
                }
            }

            private void ParseOperSecurityBlock()
            {
                while (!Eof && !IsPunct("}"))
                {
                    if (TryConsumeIdent("require_hashed_passwords"))
                    {
                        ConsumePunct("=");
                        _o.OperSecurity.RequireHashedPasswords = ParseBoolValue(ParseValue());
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    ConsumeUnknownStatement();
                }
            }

            private void ParseClassBlock()
            {
                var cls = new OperClassOptions();

                while (!Eof && !IsPunct("}"))
                {
                    if (TryConsumeIdent("name"))
                    {
                        ConsumePunct("=");
                        cls.Name = ParseValue();
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("maxconnectionsperip"))
                    {
                        ConsumePunct("=");
                        if (int.TryParse(ParseValue(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                            cls.MaxConnectionsPerIp = v;
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("capabilities"))
                    {
                        ConsumePunct("=");
                        var raw = ParseValue();
                        ConsumeOptionalPunct(";");

                        cls.Capabilities = raw
                            .Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                        continue;
                    }

                    if (TryConsumeIdent("description"))
                    {
                        ConsumePunct("=");
                        cls.Description = ParseValue();
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    ConsumeUnknownStatement();
                }

                if (!string.IsNullOrWhiteSpace(cls.Name))
                {
                    var list = new List<OperClassOptions>(_o.Classes);
                    list.Add(cls);
                    _o.Classes = list.ToArray();
                }
            }

            private void ParseKLineBlock()
            {
                var kl = new KLineOptions();

                while (!Eof && !IsPunct("}"))
                {
                    if (TryConsumeIdent("mask"))
                    {
                        ConsumePunct("=");
                        kl.Mask = ParseValue();
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("reason"))
                    {
                        ConsumePunct("=");
                        kl.Reason = ParseValue();
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    ConsumeUnknownStatement();
                }

                if (!string.IsNullOrWhiteSpace(kl.Mask))
                {
                    var list = new List<KLineOptions>(_o.KLines);
                    list.Add(kl);
                    _o.KLines = list.ToArray();
                }
            }

            private void ParseDLineBlock()
            {
                var dl = new DLineOptions();

                while (!Eof && !IsPunct("}"))
                {
                    if (TryConsumeIdent("mask"))
                    {
                        ConsumePunct("=");
                        dl.Mask = ParseValue();
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("reason"))
                    {
                        ConsumePunct("=");
                        dl.Reason = ParseValue();
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    ConsumeUnknownStatement();
                }

                if (!string.IsNullOrWhiteSpace(dl.Mask))
                {
                    var list = new List<DLineOptions>(_o.DLines);
                    list.Add(dl);
                    _o.DLines = list.ToArray();
                }
            }

            private void ParseMotdBlock()
            {
                string? vhost = null;
                var motd = new MotdOptions();

                while (!Eof && !IsPunct("}"))
                {
                    if (TryConsumeIdent("include"))
                    {
                        var inc = ParseValue();
                        ConsumeOptionalPunct(";");
                        Include(inc);
                        continue;
                    }

                    if (TryConsumeIdent("vhost"))
                    {
                        ConsumePunct("=");
                        vhost = ParseValue();
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("file"))
                    {
                        ConsumePunct("=");
                        var fp = ParseValue();
                        ConsumeOptionalPunct(";");
                        motd.FilePath = fp;
                        continue;
                    }

                    ConsumeUnknownStatement();
                }

                if (string.IsNullOrWhiteSpace(vhost))
                {
                    if (!string.IsNullOrWhiteSpace(motd.FilePath))
                    {
                        _o.Motd.FilePath = motd.FilePath;
                    }

                    if (motd.Lines is { Length: > 0 })
                    {
                        _o.Motd.Lines = motd.Lines;
                    }

                    return;
                }

                var list = new List<MotdVhostOptions>(_o.MotdByVhost);
                list.Add(new MotdVhostOptions { Vhost = vhost!, Motd = motd });
                _o.MotdByVhost = list.ToArray();
            }

            private void ParsePingBlock()
            {
                while (!Eof && !IsPunct("}"))
                {
                    if (TryConsumeIdent("enabled"))
                    {
                        ConsumePunct("=");
                        var v = ParseValue();
                        _o.Ping.Enabled = ParseBoolValue(v);
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("idle"))
                    {
                        ConsumePunct("=");
                        if (int.TryParse(ParseValue(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s))
                        {
                            _o.Ping.IdleSecondsBeforePing = s;
                        }

                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("timeout"))
                    {
                        ConsumePunct("=");
                        if (int.TryParse(ParseValue(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s))
                        {
                            _o.Ping.DisconnectSecondsAfterPing = s;
                        }

                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("quit"))
                    {
                        ConsumePunct("=");
                        _o.Ping.QuitMessage = ParseValue();
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    ConsumeUnknownStatement();
                }
            }

            private void ParseObservabilityBlock()
            {
                while (!Eof && !IsPunct("}"))
                {
                    if (TryConsumeIdent("enabled"))
                    {
                        ConsumePunct("=");
                        _o.Observability.Enabled = ParseBoolValue(ParseValue());
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("bind"))
                    {
                        ConsumePunct("=");
                        _o.Observability.BindIp = ParseValue();
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("port"))
                    {
                        ConsumePunct("=");
                        if (int.TryParse(ParseValue(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) && p > 0 && p <= 65535)
                        {
                            _o.Observability.Port = p;
                        }

                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    ConsumeUnknownStatement();
                }
            }

            private void ParseAuditBlock()
            {
                while (!Eof && !IsPunct("}"))
                {
                    if (TryConsumeIdent("enabled"))
                    {
                        ConsumePunct("=");
                        _o.Audit.Enabled = ParseBoolValue(ParseValue());
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("file"))
                    {
                        ConsumePunct("=");
                        _o.Audit.FilePath = ParseValue();
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    ConsumeUnknownStatement();
                }
            }

            private void ParseServicesBlock()
            {
                while (!Eof && !IsPunct("}"))
                {
                    if (TryConsumeIdent("persistence"))
                    {
                        ConsumePunct("{");
                        ParseServicesPersistenceBlock();
                        ConsumePunct("}");
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("nickserv"))
                    {
                        ConsumePunct("{");
                        ParseNickServBlock();
                        ConsumePunct("}");
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("chanserv"))
                    {
                        ConsumePunct("{");
                        ParseChanServBlock();
                        ConsumePunct("}");
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("memoserv"))
                    {
                        ConsumePunct("{");
                        ParseMemoServBlock();
                        ConsumePunct("}");
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("seenserv"))
                    {
                        ConsumePunct("{");
                        ParseSeenServBlock();
                        ConsumePunct("}");
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    ConsumeUnknownStatement();
                }
            }

            private void ParseServicesPersistenceBlock()
            {
                while (!Eof && !IsPunct("}"))
                {
                    if (TryConsumeIdent("saveintervalseconds"))
                    {
                        ConsumePunct("=");
                        if (int.TryParse(ParseValue(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                        {
                            _o.Services.Persistence.SaveIntervalSeconds = v;
                        }

                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("backupcount"))
                    {
                        ConsumePunct("=");
                        if (int.TryParse(ParseValue(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                        {
                            _o.Services.Persistence.BackupCount = v;
                        }

                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("recovertmponstartup"))
                    {
                        ConsumePunct("=");
                        var v = ParseValue();
                        _o.Services.Persistence.RecoverTmpOnStartup = string.Equals(v, "true", StringComparison.OrdinalIgnoreCase) || v == "1";
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("locktimeoutms"))
                    {
                        ConsumePunct("=");
                        if (int.TryParse(ParseValue(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                        {
                            _o.Services.Persistence.LockTimeoutMs = v;
                        }

                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    ConsumeUnknownStatement();
                }
            }

            private void ParseSaslBlock()
            {
                while (!Eof && !IsPunct("}"))
                {
                    if (TryConsumeIdent("external"))
                    {
                        ConsumePunct("{");
                        ParseSaslExternalBlock(_o.Sasl.External);
                        ConsumePunct("}");
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    ConsumeUnknownStatement();
                }
            }

            private void ParseSaslExternalBlock(SaslExternalOptions target)
            {
                while (!Eof && !IsPunct("}"))
                {
                    if (TryConsumeIdent("enabled"))
                    {
                        ConsumePunct("=");
                        var v = ParseValue();
                        target.Enabled = string.Equals(v, "true", StringComparison.OrdinalIgnoreCase) || v == "1";
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("fingerprints"))
                    {
                        ConsumePunct("{");
                        ParseSaslMappingBlock(target.FingerprintToAccount, normalizeFingerprint: true);
                        ConsumePunct("}");
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("subjects"))
                    {
                        ConsumePunct("{");
                        ParseSaslMappingBlock(target.SubjectToAccount, normalizeFingerprint: false);
                        ConsumePunct("}");
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    ConsumeUnknownStatement();
                }
            }

            private void ParseSaslMappingBlock(Dictionary<string, string> target, bool normalizeFingerprint)
            {
                while (!Eof && !IsPunct("}"))
                {
                    var key = ParseValue();
                    ConsumePunct("=");
                    var value = ParseValue();
                    ConsumeOptionalPunct(";");

                    if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
                    {
                        continue;
                    }

                    if (normalizeFingerprint)
                    {
                        key = NormalizeHexFingerprint(key);
                        if (string.IsNullOrWhiteSpace(key))
                        {
                            continue;
                        }
                    }
                    else
                    {
                        key = key.Trim();
                    }

                    target[key] = value;
                }
            }

            private static string NormalizeHexFingerprint(string input)
            {
                if (string.IsNullOrWhiteSpace(input))
                {
                    return string.Empty;
                }

                Span<char> buf = stackalloc char[input.Length];
                var n = 0;

                foreach (var ch in input)
                {
                    var c = ch;
                    if (c is >= '0' and <= '9')
                    {
                        buf[n++] = c;
                        continue;
                    }

                    if (c is >= 'a' and <= 'f')
                    {
                        buf[n++] = (char)(c - 32);
                        continue;
                    }

                    if (c is >= 'A' and <= 'F')
                    {
                        buf[n++] = c;
                        continue;
                    }
                }

                return n == 0 ? string.Empty : new string(buf[..n]);
            }

            private void ParseNickServBlock()
            {
                while (!Eof && !IsPunct("}"))
                {
                    if (TryConsumeIdent("accounts"))
                    {
                        ConsumePunct("=");
                        _o.Services.NickServ.AccountsFilePath = ParseValue();
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("enforce"))
                    {
                        ConsumePunct("=");
                        var v = ParseValue();
                        _o.Services.NickServ.EnforceRegisteredNicks = string.Equals(v, "true", StringComparison.OrdinalIgnoreCase) || v == "1";
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("delay"))
                    {
                        ConsumePunct("=");
                        if (int.TryParse(ParseValue(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s))
                        {
                            _o.Services.NickServ.EnforceDelaySeconds = s;
                        }

                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("confirm_email"))
                    {
                        ConsumePunct("=");
                        var v = ParseValue();
                        _o.Services.NickServ.RequireEmailConfirmation = string.Equals(v, "true", StringComparison.OrdinalIgnoreCase) || v == "1";
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("confirm_expire_hours"))
                    {
                        ConsumePunct("=");
                        if (int.TryParse(ParseValue(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var h))
                        {
                            _o.Services.NickServ.PendingRegistrationExpiryHours = h;
                        }

                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("expire_days"))
                    {
                        ConsumePunct("=");
                        if (int.TryParse(ParseValue(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var d))
                        {
                            _o.Services.NickServ.AccountExpiryDays = d;
                        }

                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("smtp_host"))
                    {
                        ConsumePunct("=");
                        _o.Services.NickServ.Smtp.Host = ParseValue();
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("smtp_port"))
                    {
                        ConsumePunct("=");
                        if (int.TryParse(ParseValue(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var p))
                        {
                            _o.Services.NickServ.Smtp.Port = p;
                        }

                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("smtp_ssl"))
                    {
                        ConsumePunct("=");
                        var v = ParseValue();
                        _o.Services.NickServ.Smtp.UseSsl = string.Equals(v, "true", StringComparison.OrdinalIgnoreCase) || v == "1";
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("smtp_user"))
                    {
                        ConsumePunct("=");
                        _o.Services.NickServ.Smtp.Username = ParseValue();
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("smtp_pass"))
                    {
                        ConsumePunct("=");
                        _o.Services.NickServ.Smtp.Password = ParseValue();
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("smtp_from"))
                    {
                        ConsumePunct("=");
                        _o.Services.NickServ.Smtp.FromAddress = ParseValue();
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("smtp_from_name"))
                    {
                        ConsumePunct("=");
                        _o.Services.NickServ.Smtp.FromName = ParseValue();
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    ConsumeUnknownStatement();
                }
            }

            private void ParseChanServBlock()
            {
                while (!Eof && !IsPunct("}"))
                {
                    if (TryConsumeIdent("channels"))
                    {
                        ConsumePunct("=");
                        _o.Services.ChanServ.ChannelsFilePath = ParseValue();
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("autojoin") || TryConsumeIdent("autojoin_registered"))
                    {
                        ConsumePunct("=");
                        var v = ParseValue();
                        _o.Services.ChanServ.AutoJoinRegisteredChannels = string.Equals(v, "true", StringComparison.OrdinalIgnoreCase) || v == "1";
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    ConsumeUnknownStatement();
                }
            }

            private void ParseMemoServBlock()
            {
                while (!Eof && !IsPunct("}"))
                {
                    if (TryConsumeIdent("memos"))
                    {
                        ConsumePunct("=");
                        _o.Services.MemoServ.MemosFilePath = ParseValue();
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    ConsumeUnknownStatement();
                }
            }

            private void ParseSeenServBlock()
            {
                while (!Eof && !IsPunct("}"))
                {
                    if (TryConsumeIdent("seen"))
                    {
                        ConsumePunct("=");
                        _o.Services.SeenServ.SeenFilePath = ParseValue();
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    ConsumeUnknownStatement();
                }
            }

            private void ParseIsupportBlock()
            {
                while (!Eof && !IsPunct("}"))
                {
                    if (TryConsumeIdent("prefix"))
                    {
                        ConsumePunct("=");
                        _o.Isupport.Prefix = ParseValue();
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("statusmsg"))
                    {
                        ConsumePunct("=");
                        _o.Isupport.StatusMsg = ParseValue();
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("chantypes"))
                    {
                        ConsumePunct("=");
                        _o.Isupport.ChanTypes = ParseValue();
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("chanmodes"))
                    {
                        ConsumePunct("=");
                        _o.Isupport.ChanModes = ParseValue();
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("casemapping"))
                    {
                        ConsumePunct("=");
                        _o.Isupport.CaseMapping = ParseValue();
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("nicklen"))
                    {
                        ConsumePunct("=");
                        if (int.TryParse(ParseValue(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                        {
                            _o.Isupport.NickLen = v;
                        }

                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("channellen"))
                    {
                        ConsumePunct("=");
                        if (int.TryParse(ParseValue(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                        {
                            _o.Isupport.ChanLen = v;
                        }

                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("topiclen"))
                    {
                        ConsumePunct("=");
                        if (int.TryParse(ParseValue(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                        {
                            _o.Isupport.TopicLen = v;
                        }

                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("maxmodes"))
                    {
                        ConsumePunct("=");
                        if (int.TryParse(ParseValue(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                        {
                            _o.Isupport.MaxModes = v;
                        }

                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("awaylen"))
                    {
                        ConsumePunct("=");
                        if (int.TryParse(ParseValue(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                        {
                            _o.Isupport.AwayLen = v;
                        }

                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("elist"))
                    {
                        ConsumePunct("=");
                        _o.Isupport.EList = ParseValue();
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("kicklen"))
                    {
                        ConsumePunct("=");
                        if (int.TryParse(ParseValue(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                        {
                            _o.Isupport.KickLen = v;
                        }

                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    ConsumeUnknownStatement();
                }
            }

            private void ParseTransportBlock()
            {
                while (!Eof && !IsPunct("}"))
                {
                    if (TryConsumeIdent("client_max_line_chars"))
                    {
                        ConsumePunct("=");
                        if (int.TryParse(ParseValue(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                        {
                            _o.Transport.ClientMaxLineChars = v;
                        }

                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("tcp"))
                    {
                        ConsumePunct("{");
                        ParseTransportTcpBlock();
                        ConsumePunct("}");
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("s2s"))
                    {
                        ConsumePunct("{");
                        ParseTransportS2SBlock();
                        ConsumePunct("}");
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("queues"))
                    {
                        ConsumePunct("{");
                        ParseTransportQueuesBlock();
                        ConsumePunct("}");
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    ConsumeUnknownStatement();
                }
            }

            private void ParseSecurityBlock()
            {
                while (!Eof && !IsPunct("}"))
                {
                    if (TryConsumeIdent("profile"))
                    {
                        ConsumePunct("=");
                        _o.Security.Profile = ParseValue();
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    ConsumeUnknownStatement();
                }
            }

            private void ParseTransportTcpBlock()
            {
                while (!Eof && !IsPunct("}"))
                {
                    if (TryConsumeIdent("keepalive_enabled"))
                    {
                        ConsumePunct("=");
                        var v = ParseValue();
                        _o.Transport.Tcp.KeepAliveEnabled = string.Equals(v, "true", StringComparison.OrdinalIgnoreCase) || v == "1";
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("keepalive_time_ms"))
                    {
                        ConsumePunct("=");
                        if (int.TryParse(ParseValue(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                        {
                            _o.Transport.Tcp.KeepAliveTimeMs = v;
                        }

                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("keepalive_interval_ms"))
                    {
                        ConsumePunct("=");
                        if (int.TryParse(ParseValue(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                        {
                            _o.Transport.Tcp.KeepAliveIntervalMs = v;
                        }

                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    ConsumeUnknownStatement();
                }
            }

            private void ParseTransportS2SBlock()
            {
                while (!Eof && !IsPunct("}"))
                {
                    if (TryConsumeIdent("inbound_handshake_timeout"))
                    {
                        ConsumePunct("=");
                        if (int.TryParse(ParseValue(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                        {
                            _o.Transport.S2S.InboundHandshakeTimeoutSeconds = v;
                        }

                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("outbound_scan_interval"))
                    {
                        ConsumePunct("=");
                        if (int.TryParse(ParseValue(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                        {
                            _o.Transport.S2S.OutboundScanIntervalSeconds = v;
                        }

                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("msgid_cache_ttl"))
                    {
                        ConsumePunct("=");
                        if (int.TryParse(ParseValue(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                        {
                            _o.Transport.S2S.MsgIdCacheTtlSeconds = v;
                        }

                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("msgid_cache_max_entries"))
                    {
                        ConsumePunct("=");
                        if (int.TryParse(ParseValue(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                        {
                            _o.Transport.S2S.MsgIdCacheMaxEntries = v;
                        }

                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("outbound_backoff_max"))
                    {
                        ConsumePunct("=");
                        if (int.TryParse(ParseValue(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                        {
                            _o.Transport.S2S.OutboundBackoffMaxSeconds = v;
                        }

                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("outbound_backoff_max_exponent"))
                    {
                        ConsumePunct("=");
                        if (int.TryParse(ParseValue(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                        {
                            _o.Transport.S2S.OutboundBackoffMaxExponent = v;
                        }

                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("outbound_failure_limit"))
                    {
                        ConsumePunct("=");
                        if (int.TryParse(ParseValue(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                        {
                            _o.Transport.S2S.OutboundFailureLimit = v;
                        }

                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    ConsumeUnknownStatement();
                }
            }

            private void ParseTransportQueuesBlock()
            {
                while (!Eof && !IsPunct("}"))
                {
                    if (TryConsumeIdent("client_sendq"))
                    {
                        ConsumePunct("=");
                        if (int.TryParse(ParseValue(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                        {
                            _o.Transport.Queues.ClientSendQueueCapacity = v;
                        }

                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("serverlink_sendq"))
                    {
                        ConsumePunct("=");
                        if (int.TryParse(ParseValue(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                        {
                            _o.Transport.Queues.ServerLinkSendQueueCapacity = v;
                        }

                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    ConsumeUnknownStatement();
                }
            }

            private void ParseFloodBlock()
            {
                while (!Eof && !IsPunct("}"))
                {
                    if (TryConsumeIdent("client"))
                    {
                        ConsumePunct("{");
                        ParseFloodGateBlock(_o.Flood.Client);
                        ConsumePunct("}");
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("commands"))
                    {
                        ConsumePunct("{");
                        ParseFloodCommandsBlock(_o.Flood.Commands);
                        ConsumePunct("}");
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("tlsclient"))
                    {
                        ConsumePunct("{");
                        ParseFloodGateBlock(_o.Flood.TlsClient);
                        ConsumePunct("}");
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("serverlink"))
                    {
                        ConsumePunct("{");
                        ParseFloodGateBlock(_o.Flood.ServerLink);
                        ConsumePunct("}");
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    ConsumeUnknownStatement();
                }
            }

            private void ParseFloodCommandsBlock(CommandFloodOptions target)
            {
                while (!Eof && !IsPunct("}"))
                {
                    if (TryConsumeIdent("enabled"))
                    {
                        ConsumePunct("=");
                        var v = ParseValue();
                        target.Enabled = string.Equals(v, "true", StringComparison.OrdinalIgnoreCase) || v == "1";
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("violations_before_disconnect"))
                    {
                        ConsumePunct("=");
                        if (int.TryParse(ParseValue(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                        {
                            target.ViolationsBeforeDisconnect = v;
                        }

                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("warning_cooldown"))
                    {
                        ConsumePunct("=");
                        if (int.TryParse(ParseValue(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                        {
                            target.WarningCooldownSeconds = v;
                        }

                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("violation_reset_seconds"))
                    {
                        ConsumePunct("=");
                        if (int.TryParse(ParseValue(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                        {
                            target.ViolationResetSeconds = v;
                        }

                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("exempt_opers"))
                    {
                        ConsumePunct("=");
                        var v = ParseValue();
                        target.ExemptOpers = string.Equals(v, "true", StringComparison.OrdinalIgnoreCase) || v == "1";
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("oper_multiplier"))
                    {
                        ConsumePunct("=");
                        if (int.TryParse(ParseValue(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                        {
                            target.OperMultiplier = v;
                        }

                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("temp_dline_enabled"))
                    {
                        ConsumePunct("=");
                        var v = ParseValue();
                        target.TempDlineEnabled = string.Equals(v, "true", StringComparison.OrdinalIgnoreCase) || v == "1";
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("temp_dline_minutes"))
                    {
                        ConsumePunct("=");
                        if (int.TryParse(ParseValue(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                        {
                            target.TempDlineMinutes = v;
                        }

                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("messages"))
                    {
                        ConsumePunct("{");
                        ParseFloodBucketBlock(target.Messages);
                        ConsumePunct("}");
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("joinpart"))
                    {
                        ConsumePunct("{");
                        ParseFloodBucketBlock(target.JoinPart);
                        ConsumePunct("}");
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("whowhois"))
                    {
                        ConsumePunct("{");
                        ParseFloodBucketBlock(target.WhoWhois);
                        ConsumePunct("}");
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("mode"))
                    {
                        ConsumePunct("{");
                        ParseFloodBucketBlock(target.Mode);
                        ConsumePunct("}");
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("nick"))
                    {
                        ConsumePunct("{");
                        ParseFloodBucketBlock(target.Nick);
                        ConsumePunct("}");
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    ConsumeUnknownStatement();
                }
            }

            private void ParseFloodBucketBlock(CommandFloodBucketOptions target)
            {
                while (!Eof && !IsPunct("}"))
                {
                    if (TryConsumeIdent("enabled"))
                    {
                        ConsumePunct("=");
                        var v = ParseValue();
                        target.Enabled = string.Equals(v, "true", StringComparison.OrdinalIgnoreCase) || v == "1";
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("maxevents"))
                    {
                        ConsumePunct("=");
                        if (int.TryParse(ParseValue(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                        {
                            target.MaxEvents = v;
                        }

                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("window"))
                    {
                        ConsumePunct("=");
                        if (int.TryParse(ParseValue(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                        {
                            target.WindowSeconds = v;
                        }

                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("per_target"))
                    {
                        ConsumePunct("=");
                        var v = ParseValue();
                        target.PerTarget = string.Equals(v, "true", StringComparison.OrdinalIgnoreCase) || v == "1";
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    ConsumeUnknownStatement();
                }
            }

            private void ParseFloodGateBlock(FloodGateOptions target)
            {
                while (!Eof && !IsPunct("}"))
                {
                    if (TryConsumeIdent("maxlines"))
                    {
                        ConsumePunct("=");
                        if (int.TryParse(ParseValue(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                        {
                            target.MaxLines = v;
                        }

                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("window"))
                    {
                        ConsumePunct("=");
                        if (int.TryParse(ParseValue(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                        {
                            target.WindowSeconds = v;
                        }

                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    ConsumeUnknownStatement();
                }
            }

            private void ParseAuthBlock()
            {
                while (!Eof && !IsPunct("}"))
                {
                    if (TryConsumeIdent("enabled"))
                    {
                        ConsumePunct("=");
                        var v = ParseValue();
                        _o.Auth.Enabled = string.Equals(v, "true", StringComparison.OrdinalIgnoreCase) || v == "1";
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("rdns"))
                    {
                        ConsumePunct("=");
                        var v = ParseValue();
                        _o.Auth.ReverseDnsEnabled = string.Equals(v, "true", StringComparison.OrdinalIgnoreCase) || v == "1";
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("rdns_timeout"))
                    {
                        ConsumePunct("=");
                        if (int.TryParse(ParseValue(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                        {
                            _o.Auth.ReverseDnsTimeoutSeconds = v;
                        }

                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("ident"))
                    {
                        ConsumePunct("=");
                        var v = ParseValue();
                        _o.Auth.IdentEnabled = string.Equals(v, "true", StringComparison.OrdinalIgnoreCase) || v == "1";
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("ident_timeout"))
                    {
                        ConsumePunct("=");
                        if (int.TryParse(ParseValue(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                            _o.Auth.IdentTimeoutSeconds = v;
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("delay_ms"))
                    {
                        ConsumePunct("=");
                        if (int.TryParse(ParseValue(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                        {
                            _o.Auth.AuthNoticeDelayMs = v;
                        }

                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    ConsumeUnknownStatement();
                }
            }

            private void ConsumeUnknownStatement()
            {
                while (!Eof)
                {
                    if (IsPunct(";"))
                    {
                        _i++;
                        return;
                    }

                    if (IsPunct("}"))
                    {
                        return;
                    }

                    _i++;
                }
            }

            private void Include(string includePath)
            {
                var inc = Path.IsPathRooted(includePath)
                    ? includePath
                    : Path.GetFullPath(Path.Combine(_baseDir, includePath));

                if (Directory.Exists(inc))
                {
                    foreach (var f in Directory.EnumerateFiles(inc, "*.conf", SearchOption.TopDirectoryOnly).Order(StringComparer.OrdinalIgnoreCase))
                    {
                        ApplyInternal(_o, f, _visited);
                    }

                    return;
                }

                if (inc.Contains('*') || inc.Contains('?'))
                {
                    var dir = Path.GetDirectoryName(inc);
                    if (string.IsNullOrWhiteSpace(dir))
                    {
                        dir = _baseDir;
                    }

                    var pattern = Path.GetFileName(inc);
                    foreach (var f in Directory.EnumerateFiles(dir, pattern, SearchOption.TopDirectoryOnly).Order(StringComparer.OrdinalIgnoreCase))
                    {
                        ApplyInternal(_o, f, _visited);
                    }

                    return;
                }

                ApplyInternal(_o, inc, _visited);
            }

            private string ParseIdent()
            {
                if (Eof)
                {
                    throw new InvalidOperationException("Unexpected end of config");
                }

                var t = _t[_i];
                if (t.Kind is not TokenKind.Ident)
                {
                    throw new InvalidOperationException($"Expected identifier, got '{t.Text}'");
                }

                _i++;
                return t.Text;
            }

            private string ParseValue()
            {
                if (Eof)
                {
                    throw new InvalidOperationException("Unexpected end of config");
                }

                var t = _t[_i];
                if (t.Kind is TokenKind.String or TokenKind.Ident)
                {
                    _i++;
                    return t.Text;
                }

                throw new InvalidOperationException($"Expected value, got '{t.Text}'");
            }

            private void ConsumePunct(string punct)
            {
                if (Eof)
                {
                    throw new InvalidOperationException($"Expected '{punct}' but reached end of config");
                }

                var t = _t[_i];
                if (t.Kind != TokenKind.Punct || t.Text != punct)
                {
                    throw new InvalidOperationException($"Expected '{punct}', got '{t.Text}'");
                }

                _i++;
            }

            private void ConsumeOptionalPunct(string punct)
            {
                if (!Eof && _t[_i].Kind == TokenKind.Punct && _t[_i].Text == punct)
                {
                    _i++;
                }
            }

            private bool TryConsumeIdent(string ident)
            {
                if (Eof)
                {
                    return false;
                }

                var t = _t[_i];
                if (t.Kind == TokenKind.Ident && string.Equals(t.Text, ident, StringComparison.OrdinalIgnoreCase))
                {
                    _i++;
                    return true;
                }

                return false;
            }

            private bool IsPunct(string punct)
            {
                if (Eof)
                {
                    return false;
                }

                var t = _t[_i];
                return t.Kind == TokenKind.Punct && t.Text == punct;
            }

            private bool Eof => _i >= _t.Count;
        }

        private static void ApplyKeyValue(IrcOptions target, string key, string value)
        {
            switch (key.ToLowerInvariant())
            {
                case "server.port":
                case "irc.port":
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port))
                    {
                        target.IrcPort = port;
                    }

                    return;

                default:
                    return;
            }
        }
    }
}
