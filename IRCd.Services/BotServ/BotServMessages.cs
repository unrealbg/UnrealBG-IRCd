namespace IRCd.Services.BotServ
{
    internal static class BotServMessages
    {
        public const string ServiceName = "BotServ";

        public static string HelpIntro => "BotServ commands: HELP, LIST, INFO, ASSIGN, UNASSIGN, JOIN, PART, SAY, ACT";

        public static string HelpList => "LIST  - List configured bots";

        public static string HelpInfo => "INFO <botnick>  - Show bot info";

        public static string HelpAssign => "ASSIGN <#channel> <botnick>  - Assign a bot to a channel (requires botserv capability)";

        public static string HelpUnassign => "UNASSIGN <#channel>  - Remove channel bot assignment (requires botserv capability)";

        public static string HelpJoin => "JOIN <#channel> [botnick]  - Join with assigned/explicit bot (requires botserv capability)";

        public static string HelpPart => "PART <#channel> [botnick]  - Part with assigned/explicit bot (requires botserv capability)";

        public static string HelpSay => "SAY <#channel> <text> [botnick]  - Bot says text to channel (requires botserv capability)";

        public static string HelpAct => "ACT <#channel> <action> [botnick]  - Bot sends /me action (requires botserv capability)";
    }
}
