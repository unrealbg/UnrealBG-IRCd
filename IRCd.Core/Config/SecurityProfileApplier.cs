namespace IRCd.Core.Config;

using System;

using IRCd.Shared.Options;

public static class SecurityProfileApplier
{
    public static void Apply(IrcOptions o)
    {
        if (o is null)
        {
            return;
        }

        var profile = (o.Security?.Profile ?? "default").Trim();
        if (profile.Length == 0)
        {
            profile = "default";
        }

        if (profile.Equals("default", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (profile.Equals("public", StringComparison.OrdinalIgnoreCase))
        {
            ApplyPublic(o);
            return;
        }

        if (profile.Equals("trusted-lan", StringComparison.OrdinalIgnoreCase))
        {
            ApplyTrustedLan(o);
            return;
        }
    }

    private static void ApplyPublic(IrcOptions o)
    {
        var baseline = new IrcOptions();

        if (o.ConnectionGuard.Enabled == baseline.ConnectionGuard.Enabled)
        {
            o.ConnectionGuard.Enabled = true;
        }

        if (o.ConnectionGuard.MaxConnectionsPerWindowPerIp == baseline.ConnectionGuard.MaxConnectionsPerWindowPerIp)
        {
            o.ConnectionGuard.MaxConnectionsPerWindowPerIp = 10;
        }

        if (o.ConnectionGuard.MaxConnectionsPerWindowPerIpTls == baseline.ConnectionGuard.MaxConnectionsPerWindowPerIpTls)
        {
            o.ConnectionGuard.MaxConnectionsPerWindowPerIpTls = 10;
        }

        if (o.ConnectionGuard.MaxTlsHandshakesPerWindowPerIp == baseline.ConnectionGuard.MaxTlsHandshakesPerWindowPerIp)
        {
            o.ConnectionGuard.MaxTlsHandshakesPerWindowPerIp = 10;
        }

        if (o.ConnectionGuard.MaxUnregisteredPerIp == baseline.ConnectionGuard.MaxUnregisteredPerIp)
        {
            o.ConnectionGuard.MaxUnregisteredPerIp = 2;
        }

        if (o.ConnectionGuard.MaxActiveConnectionsPerIp == baseline.ConnectionGuard.MaxActiveConnectionsPerIp)
        {
            o.ConnectionGuard.MaxActiveConnectionsPerIp = 5;
        }

        if (o.ConnectionGuard.RegistrationTimeoutSeconds == baseline.ConnectionGuard.RegistrationTimeoutSeconds)
        {
            o.ConnectionGuard.RegistrationTimeoutSeconds = 20;
        }

        if (o.Flood.Client.MaxLines == baseline.Flood.Client.MaxLines)
        {
            o.Flood.Client.MaxLines = 10;
        }

        if (o.Flood.TlsClient.MaxLines == baseline.Flood.TlsClient.MaxLines)
        {
            o.Flood.TlsClient.MaxLines = 10;
        }

        if (o.Flood.Commands.ViolationsBeforeDisconnect == baseline.Flood.Commands.ViolationsBeforeDisconnect)
        {
            o.Flood.Commands.ViolationsBeforeDisconnect = 2;
        }

        if (o.Flood.Commands.Messages.MaxEvents == baseline.Flood.Commands.Messages.MaxEvents)
        {
            o.Flood.Commands.Messages.MaxEvents = 6;
        }

        if (o.Flood.Commands.JoinPart.MaxEvents == baseline.Flood.Commands.JoinPart.MaxEvents)
        {
            o.Flood.Commands.JoinPart.MaxEvents = 6;
        }

        if (o.Flood.Commands.WhoWhois.MaxEvents == baseline.Flood.Commands.WhoWhois.MaxEvents)
        {
            o.Flood.Commands.WhoWhois.MaxEvents = 6;
        }

        if (o.Flood.Commands.Mode.MaxEvents == baseline.Flood.Commands.Mode.MaxEvents)
        {
            o.Flood.Commands.Mode.MaxEvents = 6;
        }

        if (o.Flood.Commands.Nick.MaxEvents == baseline.Flood.Commands.Nick.MaxEvents)
        {
            o.Flood.Commands.Nick.MaxEvents = 3;
        }

        if (o.RateLimit.Enabled == baseline.RateLimit.Enabled)
        {
            o.RateLimit.Enabled = true;
        }

        if (o.RateLimit.PrivMsg.Capacity == baseline.RateLimit.PrivMsg.Capacity)
        {
            o.RateLimit.PrivMsg.Capacity = 3;
        }

        if (o.RateLimit.PrivMsg.RefillPeriodSeconds == baseline.RateLimit.PrivMsg.RefillPeriodSeconds)
        {
            o.RateLimit.PrivMsg.RefillPeriodSeconds = 2;
        }

        if (o.RateLimit.Notice.Capacity == baseline.RateLimit.Notice.Capacity)
        {
            o.RateLimit.Notice.Capacity = 3;
        }

        if (o.RateLimit.Notice.RefillPeriodSeconds == baseline.RateLimit.Notice.RefillPeriodSeconds)
        {
            o.RateLimit.Notice.RefillPeriodSeconds = 2;
        }

        if (o.RateLimit.Join.Capacity == baseline.RateLimit.Join.Capacity)
        {
            o.RateLimit.Join.Capacity = 2;
        }

        if (o.RateLimit.Join.RefillPeriodSeconds == baseline.RateLimit.Join.RefillPeriodSeconds)
        {
            o.RateLimit.Join.RefillPeriodSeconds = 5;
        }

        if (o.RateLimit.Disconnect.MaxViolations == baseline.RateLimit.Disconnect.MaxViolations)
        {
            o.RateLimit.Disconnect.MaxViolations = 5;
        }

        if (o.OperSecurity.RequireHashedPasswords == baseline.OperSecurity.RequireHashedPasswords)
        {
            o.OperSecurity.RequireHashedPasswords = true;
        }

        if (o.Transport.ClientMaxLineChars == baseline.Transport.ClientMaxLineChars)
        {
            o.Transport.ClientMaxLineChars = 400;
        }
    }

    private static void ApplyTrustedLan(IrcOptions o)
    {
        var baseline = new IrcOptions();

        if (o.ConnectionGuard.Enabled == baseline.ConnectionGuard.Enabled)
        {
            o.ConnectionGuard.Enabled = true;
        }

        if (o.ConnectionGuard.MaxConnectionsPerWindowPerIp == baseline.ConnectionGuard.MaxConnectionsPerWindowPerIp)
        {
            o.ConnectionGuard.MaxConnectionsPerWindowPerIp = 100;
        }

        if (o.ConnectionGuard.MaxConnectionsPerWindowPerIpTls == baseline.ConnectionGuard.MaxConnectionsPerWindowPerIpTls)
        {
            o.ConnectionGuard.MaxConnectionsPerWindowPerIpTls = 100;
        }

        if (o.ConnectionGuard.MaxActiveConnectionsPerIp == baseline.ConnectionGuard.MaxActiveConnectionsPerIp)
        {
            o.ConnectionGuard.MaxActiveConnectionsPerIp = 50;
        }

        if (o.ConnectionGuard.RegistrationTimeoutSeconds == baseline.ConnectionGuard.RegistrationTimeoutSeconds)
        {
            o.ConnectionGuard.RegistrationTimeoutSeconds = 60;
        }

        if (o.Flood.Client.MaxLines == baseline.Flood.Client.MaxLines)
        {
            o.Flood.Client.MaxLines = 60;
        }

        if (o.Flood.TlsClient.MaxLines == baseline.Flood.TlsClient.MaxLines)
        {
            o.Flood.TlsClient.MaxLines = 60;
        }

        if (o.RateLimit.Enabled == baseline.RateLimit.Enabled)
        {
            o.RateLimit.Enabled = false;
        }
    }
}
