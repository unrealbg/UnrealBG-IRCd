namespace IRCd.Services.Agent
{
    internal static class AgentMessages
    {
        public const string ServiceName = "Agent";

        public static string HelpIntro => "Agent commands: HELP, INFO, UPDATE, BADWORDS, LOGON";

        public static string HelpInfo => "INFO  - Show current agent settings";

        public static string HelpUpdate => "UPDATE  - Reload Agent settings from config (requires agent capability)";

        public static string HelpBadwords => "BADWORDS [LIST|ADD <word>|DEL <word>|CLEAR]  - Manage badwords list (requires agent capability for changes)";

        public static string HelpLogon => "LOGON [SET <message>|CLEAR]  - Manage network logon message (requires agent capability for changes)";
    }
}
