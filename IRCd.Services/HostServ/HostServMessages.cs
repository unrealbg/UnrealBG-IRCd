namespace IRCd.Services.HostServ
{
    internal static class HostServMessages
    {
        public const string ServiceName = "HostServ";

        public static string HelpIntro => "HostServ commands: HELP, SETHOST, ADD, DEL, CHANGE";

        public static string HelpSetHost => "SETHOST <vhost>  - Apply an assigned vHost to your current nick";

        public static string HelpSetHost2 => "SETHOST <nick> <vhost>  - Assign+apply a vHost (opers with hostserv capability)";

        public static string HelpAdd => "ADD <nick> <vhost>  - Assign a vHost to a nick (opers with hostserv capability)";

        public static string HelpDel => "DEL <nick>  - Remove an assigned vHost from a nick (opers with hostserv capability)";

        public static string HelpChange => "CHANGE <nick> <vhost>  - Change assigned vHost for a nick (opers with hostserv capability)";
    }
}
