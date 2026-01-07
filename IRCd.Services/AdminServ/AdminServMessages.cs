namespace IRCd.Services.AdminServ
{
    internal static class AdminServMessages
    {
        public const string ServiceName = "AdminServ";

        public static string HelpIntro => "AdminServ commands: HELP, OPER, FLAGS, OPERSET, WHOIS";

        public static string HelpOper => "OPER: OPER ADD <account> [operclass] | OPER DEL <account> | OPER LIST";

        public static string HelpFlags => "FLAGS: FLAGS ADD <account> <flag> [flag...] | FLAGS DEL <account> <flag> [flag...]";

        public static string HelpOperset => "OPERSET: OPERSET <account> <operclass> | OPERSET <account> -";

        public static string HelpWhois => "WHOIS: WHOIS <nick|account>";
    }
}
