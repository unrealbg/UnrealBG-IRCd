namespace IRCd.Services.OperServ
{
    internal static class OperServMessages
    {
        public const string ServiceName = "OperServ";

        public static string HelpIntro => "OperServ commands: HELP, KLINE, AKILL, DLINE, ZLINE, DENY, WARN, TRIGGER, GLOBAL, FJOIN, FPART, UINFO, STATS, CLEAR, REHASH, DIE, RESTART";

        public static string HelpKline => "KLINE <mask> [reason]  - Add a K-line; use KLINE -<mask> to remove";

        public static string HelpAkill => "AKILL <mask> [reason]  - Alias for KLINE";

        public static string HelpDline => "DLINE <mask> [reason]  - Add a D-line; use DLINE -<mask> to remove";

        public static string HelpZline => "ZLINE <mask> [reason]  - Alias for DLINE";

        public static string HelpDeny => "DENY <mask> [reason]  - Deny nick/hostmask; use DENY -<mask> to remove; DENY LIST";

        public static string HelpWarn => "WARN <mask> [message]  - Add a warning entry; use WARN -<mask> to remove; WARN LIST";

        public static string HelpTrigger => "TRIGGER <pattern> [response]  - Manage triggers; use TRIGGER -<pattern> to remove; TRIGGER LIST";

        public static string HelpGlobal => "GLOBAL <message>  - Send a global notice to all local users";

        public static string HelpFjoin => "FJOIN <nick> <#channel>  - Force a local user to join a channel";

        public static string HelpFpart => "FPART <nick> <#channel> [reason]  - Force a local user to part a channel";

        public static string HelpUinfo => "UINFO <nick>  - Show user info";

        public static string HelpStats => "STATS  - Show server/service stats";

        public static string HelpClear => "CLEAR [ALL|KLINE|DLINE|DENY|WARN|TRIGGER]  - Clear runtime lists";

        public static string HelpRehash => "REHASH  - Reload ircd.conf (requires oper capability rehash)";

        public static string HelpDie => "DIE  - Stop the server (requires oper capability die)";

        public static string HelpRestart => "RESTART  - Restart the server (requires oper capability restart)";
    }
}
