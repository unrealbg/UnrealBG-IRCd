namespace IRCd.Services.HelpServ
{
    internal static class HelpServMessages
    {
        public const string ServiceName = "HelpServ";

        public static string HelpIntro => "HelpServ usage: HELP [service]. Example: /msg HelpServ HELP NickServ";

        public static string HelpList => "Services: NickServ, ChanServ, OperServ, MemoServ, SeenServ, InfoServ, StatServ, AdminServ, DevServ, RootServ, HostServ, BotServ, Agent";
    }
}
