namespace IRCd.Services.MemoServ
{
    internal static class MemoServMessages
    {
        public const string ServiceName = "MemoServ";

        public static string HelpIntro => "MemoServ commands: HELP, SEND, LIST, READ, DEL, CLEAR";

        public static string HelpSend => "SEND <nick> <text>  - Send a memo to a registered nick";

        public static string HelpList => "LIST  - List your memos";

        public static string HelpRead => "READ <num>  - Read a memo";

        public static string HelpDel => "DEL <num>  - Delete a memo";

        public static string HelpClear => "CLEAR  - Delete all memos";
    }
}
