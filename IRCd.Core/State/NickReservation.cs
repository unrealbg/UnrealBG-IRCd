namespace IRCd.Core.State
{
    using System;
    using System.Collections.Generic;

    public class NickReservation
    {
        private static readonly HashSet<string> ReservedServiceNicks = new(StringComparer.OrdinalIgnoreCase)
        {
            "ChanServ",
            "CS",
            "NickServ",
            "NS",
            "OperServ",
            "OS",
            "MemoServ",
            "MS",
            "BotServ",
            "BS",
            "HostServ",
            "HS",
            "HelpServ",
            "RootServ",
            "RS",
            "Global",
            "GlobalServ",
            "GS",
            "InfoServ",
            "IS",
            "StatServ",
            "AdminServ",
            "DevServ",
            "Services",
            "SeenServ",
            "SS",

            "Agent",
            "AG",
        };

        public static bool IsReservedServiceNick(string nick)
        {
            if (string.IsNullOrWhiteSpace(nick))
            {
                return false;
            }

            nick = nick.Trim();

            if (ReservedServiceNicks.Contains(nick))
            {
                return true;
            }

            return nick.EndsWith("Serv", StringComparison.OrdinalIgnoreCase)
                || nick.EndsWith("Service", StringComparison.OrdinalIgnoreCase);
        }
    }
}
