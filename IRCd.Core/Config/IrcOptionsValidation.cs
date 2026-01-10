namespace IRCd.Core.Config;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;

using IRCd.Shared.Options;

public static class IrcOptionsValidation
{
    public static IReadOnlyList<string> Validate(IrcOptions o, string contentRoot)
    {
        var errors = new List<string>();

        if (o.ServerInfo is null)
        {
            errors.Add("serverinfo is missing");
            return errors;
        }

        if (string.IsNullOrWhiteSpace(o.ServerInfo.Name))
            errors.Add("serverinfo.name is required");

        if (string.IsNullOrWhiteSpace(o.ServerInfo.Sid))
        {
            errors.Add("serverinfo.sid is required");
        }
        else
        {
            var sid = o.ServerInfo.Sid.Trim();
            if (sid.Length != 3 || sid.Any(c => !char.IsDigit(c)))
                errors.Add("serverinfo.sid must be 3 digits (e.g. 001)");
            else if (sid == "000")
                errors.Add("serverinfo.sid cannot be 000");
        }

        ValidatePorts(o, errors);
        ValidateOpers(o, errors);
        ValidateLinks(o, errors);
        ValidateTls(o, contentRoot, errors);

        ValidateRanges(o, errors);

        return errors;
    }

    private static void ValidatePorts(IrcOptions o, List<string> errors)
    {
        static bool IsValidPort(int p) => p is > 0 and <= 65535;

        var endpoints = new List<(string ip, int port)>();

        if (o.ListenEndpoints is not null)
        {
            foreach (var ep in o.ListenEndpoints)
            {
                if (ep is null) continue;

                if (!IsValidPort(ep.Port))
                    errors.Add($"listen endpoint port invalid: {ep.BindIp}:{ep.Port}");

                endpoints.Add((NormalizeIp(ep.BindIp), ep.Port));
            }
        }

        if (o.Listen is not null)
        {
            if (o.Listen.ClientPort != 0 && !IsValidPort(o.Listen.ClientPort))
                errors.Add($"listen.clientport must be 1..65535 (got {o.Listen.ClientPort.ToString(CultureInfo.InvariantCulture)})");

            if (o.Listen.TlsClientPort != 0 && !IsValidPort(o.Listen.TlsClientPort))
                errors.Add($"listen.tlsclientport must be 1..65535 (got {o.Listen.TlsClientPort.ToString(CultureInfo.InvariantCulture)})");

            if (o.Listen.ServerPort != 0 && !IsValidPort(o.Listen.ServerPort))
                errors.Add($"listen.serverport must be 1..65535 (got {o.Listen.ServerPort.ToString(CultureInfo.InvariantCulture)})");

            var bind = NormalizeIp(o.Listen.BindIp);
            if (o.Listen.ClientPort > 0)
            {
                endpoints.Add((bind, o.Listen.ClientPort));
            }

            if (o.Listen.TlsClientPort > 0)
            {
                endpoints.Add((bind, o.Listen.TlsClientPort));
            }

            if (o.Listen.ServerPort > 0)
            {
                endpoints.Add((bind, o.Listen.ServerPort));
            }
        }

        for (var i = 0; i < endpoints.Count; i++)
        {
            for (var j = i + 1; j < endpoints.Count; j++)
            {
                var a = endpoints[i];
                var b = endpoints[j];
                if (a.port != b.port)
                {
                    continue;
                }

                if (IpsOverlap(a.ip, b.ip))
                {
                    errors.Add($"port conflict: {a.ip}:{a.port} overlaps {b.ip}:{b.port}");
                }
            }
        }
    }

    private static void ValidateOpers(IrcOptions o, List<string> errors)
    {
        var classes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (o.Classes is not null)
        {
            foreach (var c in o.Classes)
            {
                if (c is null)
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(c.Name))
                {
                    classes.Add(c.Name.Trim());
                }
            }
        }

        if (o.Opers is null)
        {
            return;
        }

        foreach (var oper in o.Opers)
        {
            if (oper is null)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(oper.Name))
            {
                errors.Add("oper.name is required");
            }

            if (string.IsNullOrWhiteSpace(oper.Password))
            {
                errors.Add($"oper.password is required for {oper.Name ?? "<unknown>"}");
            }

            if (!string.IsNullOrWhiteSpace(oper.Class) && !classes.Contains(oper.Class.Trim()))
            {
                errors.Add($"oper.class '{oper.Class}' not found for oper {oper.Name}");
            }
        }
    }

    private static void ValidateLinks(IrcOptions o, List<string> errors)
    {
        if (o.Links is null)
        {
            return;
        }

        var sids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in o.Links)
        {
            if (l is null)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(l.Name))
            {
                errors.Add("link.name is required");
            }

            if (string.IsNullOrWhiteSpace(l.Sid) || l.Sid.Trim().Length != 3)
            {
                errors.Add($"link.sid must be 3 chars for link {l.Name}");
            }
            else if (!sids.Add(l.Sid.Trim()))
            {
                errors.Add($"duplicate link.sid: {l.Sid.Trim()}");
            }

            if (string.IsNullOrWhiteSpace(l.Host))
            {
                errors.Add($"link.host is required for link {l.Name}");
            }

            if (l.Port is <= 0 or > 65535)
            {
                errors.Add($"link.port must be 1..65535 for link {l.Name} (got {l.Port})");
            }

            if (string.IsNullOrWhiteSpace(l.Password))
            {
                errors.Add($"link.password is required for link {l.Name}");
            }
        }
    }

    private static void ValidateTls(IrcOptions o, string contentRoot, List<string> errors)
    {
        var listen = o.Listen;
        if (listen is null)
        {
            return;
        }

        if (!listen.EnableTls)
        {
            return;
        }

        if (!listen.AutoGenerateSelfSignedCertificate)
        {
            if (string.IsNullOrWhiteSpace(listen.TlsCertificatePath))
            {
                errors.Add("listen.enabletls=true but listen.tlscertificatepath is empty");
            }
            else
            {
                var p = ResolvePath(contentRoot, listen.TlsCertificatePath);
                if (!File.Exists(p))
                {
                    errors.Add($"TLS certificate not found: {p}");
                }
            }
        }

        if (listen.TlsCertificates is not null)
        {
            foreach (var kv in listen.TlsCertificates)
            {
                var name = kv.Key;
                var cfg = kv.Value;
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                if (cfg is null || string.IsNullOrWhiteSpace(cfg.Path))
                {
                    errors.Add($"listen.tlscertificates['{name}'].path is required");
                    continue;
                }

                var p = ResolvePath(contentRoot, cfg.Path);
                if (!File.Exists(p))
                {
                    errors.Add($"SNI TLS certificate not found for '{name}': {p}");
                }
            }
        }
    }

    private static void ValidateRanges(IrcOptions o, List<string> errors)
    {
        if (o.Transport is not null)
        {
            if (o.Transport.ClientMaxLineChars is < 64 or > 510)
            {
                errors.Add("transport.client_max_line_chars must be 64..510");
            }
        }

        if (o.Security is not null)
        {
            var p = (o.Security.Profile ?? "default").Trim();
            if (!string.Equals(p, "default", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(p, "public", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(p, "trusted-lan", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add("security.profile must be one of: default | public | trusted-lan");
            }
        }

        if (o.Transport?.S2S is not null)
        {
            if (o.Transport.S2S.InboundHandshakeTimeoutSeconds <= 0)
            {
                errors.Add("transport.s2s.inbound_handshake_timeout must be > 0");
            }

            if (o.Transport.S2S.OutboundScanIntervalSeconds <= 0)
            {
                errors.Add("transport.s2s.outbound_scan_interval must be > 0");
            }
        }

        if (o.Transport is not null)
        {
            if (o.Transport.ClientMaxLineChars is < 128 or > 510)
            {
                errors.Add("transport.client_max_line_chars must be 128..510");
            }
        }

        if (o.Ping is not null)
        {
            if (o.Ping.IdleSecondsBeforePing <= 0)
            {
                errors.Add("ping.idle must be > 0");
            }

            if (o.Ping.DisconnectSecondsAfterPing <= 0)
            {
                errors.Add("ping.timeout must be > 0");
            }
        }

        if (o.Flood?.Commands is not null)
        {
            if (o.Flood.Commands.WarningCooldownSeconds < 0)
            {
                errors.Add("flood.commands.warning_cooldown must be >= 0");
            }

            if (o.Flood.Commands.ViolationResetSeconds <= 0)
            {
                errors.Add("flood.commands.violation_reset_seconds must be > 0");
            }
        }

        if (o.Bans is not null && o.Bans.EnforcementCheckIntervalSeconds <= 0)
        {
            errors.Add("bans.enforcement_check_interval must be > 0");
        }
    }

    private static string ResolvePath(string contentRoot, string p)
        => Path.IsPathRooted(p) ? p : Path.Combine(contentRoot, p);

    private static string NormalizeIp(string? ip)
    {
        if (string.IsNullOrWhiteSpace(ip))
        {
            return "0.0.0.0";
        }

        ip = ip.Trim();
        if (ip == "*" || ip == "0" || ip == "0.0.0.0")
        {
            return "0.0.0.0";
        }

        if (IPAddress.TryParse(ip, out var parsed))
        {
            return parsed.ToString();
        }

        return ip;
    }

    private static bool IpsOverlap(string a, string b)
    {
        if (string.Equals(a, "0.0.0.0", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(b, "0.0.0.0", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }
}
