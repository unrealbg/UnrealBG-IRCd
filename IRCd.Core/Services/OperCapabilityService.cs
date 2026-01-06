namespace IRCd.Core.Services
{
    using System;
    using System.Linq;

    using IRCd.Core.State;
    using IRCd.Shared.Options;

    public static class OperCapabilityService
    {
        public static bool HasCapability(IrcOptions options, User user, string capability)
        {
            if (user is null)
            {
                return false;
            }

            if (!user.Modes.HasFlag(UserModes.Operator))
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(user.OperClass))
            {
                return true;
            }

            var cls = options.Classes.FirstOrDefault(c =>
                c is not null
                && !string.IsNullOrWhiteSpace(c.Name)
                && string.Equals(c.Name, user.OperClass, StringComparison.OrdinalIgnoreCase));

            if (cls is null)
            {
                return false;
            }

            var caps = cls.Capabilities ?? Array.Empty<string>();
            if (caps.Length == 0)
            {
                return false;
            }

            if (caps.Any(c => string.Equals(c, "netadmin", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            return caps.Any(c => string.Equals(c, capability, StringComparison.OrdinalIgnoreCase));
        }
    }
}
