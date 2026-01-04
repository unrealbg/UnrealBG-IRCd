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
                        i++;
                    continue;
                }

                if (char.IsWhiteSpace(c))
                    continue;

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
                            break;

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
                            break;
                        if (c == '#' || c == ';')
                            break;
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

                    if (TryConsumeIdent("link"))
                    {
                        ConsumePunct("{");
                        ParseLinkBlock();
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

                    ConsumeUnknownStatement();
                }
            }

            private void ParseListenBlock()
            {
                while (!Eof && !IsPunct("}"))
                {
                    if (TryConsumeIdent("bind"))
                    {
                        ConsumePunct("=");
                        _o.Listen.BindIp = ParseValue();
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("clientport"))
                    {
                        ConsumePunct("=");
                        if (int.TryParse(ParseValue(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var p))
                            _o.Listen.ClientPort = p;
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    if (TryConsumeIdent("serverport"))
                    {
                        ConsumePunct("=");
                        if (int.TryParse(ParseValue(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var p))
                            _o.Listen.ServerPort = p;
                        ConsumeOptionalPunct(";");
                        continue;
                    }

                    ConsumeUnknownStatement();
                }
            }

            private void ParseLinkBlock()
            {
                var link = new LinkOptions();

                while (!Eof && !IsPunct("}"))
                {
                    if (TryConsumeIdent("name"))
                    {
                        ConsumePunct("=");
                        link.Name = ParseValue();
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

            private void ParseMotdBlock()
            {
                while (!Eof && !IsPunct("}"))
                {
                    if (TryConsumeIdent("include"))
                    {
                        var inc = ParseValue();
                        ConsumeOptionalPunct(";");
                        Include(inc);
                        continue;
                    }

                    if (TryConsumeIdent("file"))
                    {
                        ConsumePunct("=");
                        var fp = ParseValue();
                        ConsumeOptionalPunct(";");
                        _o.Motd.FilePath = fp;
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
                        return;

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
                        dir = _baseDir;

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
                    throw new InvalidOperationException("Unexpected end of config");

                var t = _t[_i];
                if (t.Kind is not TokenKind.Ident)
                    throw new InvalidOperationException($"Expected identifier, got '{t.Text}'");
                _i++;
                return t.Text;
            }

            private string ParseValue()
            {
                if (Eof)
                    throw new InvalidOperationException("Unexpected end of config");
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
                    throw new InvalidOperationException($"Expected '{punct}' but reached end of config");
                var t = _t[_i];
                if (t.Kind != TokenKind.Punct || t.Text != punct)
                    throw new InvalidOperationException($"Expected '{punct}', got '{t.Text}'");
                _i++;
            }

            private void ConsumeOptionalPunct(string punct)
            {
                if (!Eof && _t[_i].Kind == TokenKind.Punct && _t[_i].Text == punct)
                    _i++;
            }

            private bool TryConsumeIdent(string ident)
            {
                if (Eof)
                    return false;
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
                    return false;
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
                        target.IrcPort = port;
                    return;

                default:
                    return;
            }
        }
    }
}
