namespace IRCd.Core.Commands
{
    using IRCd.Core.Abstractions;

    public static class CommandGuards
    {
        public static bool EnsureRegistered(IClientSession session, CancellationToken ct, out ValueTask errorTask)
        {
            if (session.IsRegistered)
            {
                errorTask = default;
                return true;
            }

            var nick = session.Nick ?? "*";
            errorTask = session.SendAsync($":server 451 {nick} :You have not registered", ct);
            return false;
        }
    }
}
