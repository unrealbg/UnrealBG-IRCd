namespace IRCd.Services.ChanServ
{
    internal static class ChanServMessages
    {
        public const string ServiceName = "ChanServ";

        public static string HelpIntro => "ChanServ commands: HELP, LIST, IDENTIFY, REGISTER, DROP, UNREGISTER, INFO, SET, FLAGS, ACCESS, SOP, AOP, VOP, STATUS, ENFORCE, OP, DEOP, VOICE, DEVOICE, INVITE, KICK, AKICK, RECOVER, TOPIC, BAN, UNBAN, SUSPEND, UNSUSPEND, CLEAR";

        public static string HelpList => "LIST [mask]  - List registered channels (e.g. LIST #test*)";
        public static string HelpIdentify => "IDENTIFY <#channel> <password>  - Identify to a channel (founder access)";

        public static string HelpRegister => "REGISTER <#channel> <password> [description]  - Register a channel";
        public static string HelpDrop => "DROP <#channel> <password>  - Drop a channel (founder only); aliases: UNREGISTER, UNREG";
        public static string HelpInfo => "INFO <#channel>  - Show channel registration info";
        public static string HelpSet => "SET <#channel> DESC <text> | SET <#channel> PASSWORD <newpass> | SET <#channel> MLOCK <modes> | SET <#channel> KEY <key|OFF> | SET <#channel> LIMIT <n|OFF> | SET <#channel> TOPICLOCK ON|OFF [topic] | SET <#channel> GUARD ON|OFF | SET <#channel> SEENSERV ON|OFF | SET <#channel> PRIVATE ON|OFF | SET <#channel> SECRET ON|OFF | SET <#channel> MODERATED ON|OFF | SET <#channel> NOEXTERNAL ON|OFF | SET <#channel> TOPICOPS ON|OFF | SET <#channel> INVITEONLY ON|OFF | SET <#channel> RESTRICTED ON|OFF | SET <#channel> URL <url> | SET <#channel> EMAIL <email> | SET <#channel> SUCCESSOR <account|OFF> | SET <#channel> ENTRYMSG <text|OFF> | SET <#channel> FOUNDER <account>";
        public static string HelpSetMlock => "SET <#channel> MLOCK <modes>  - Lock channel modes (e.g. SET #c MLOCK +nt +i)";
        public static string HelpSetTopicLock => "SET <#channel> TOPICLOCK ON|OFF [topic]  - Lock the channel topic to a fixed value";
        public static string HelpFlags => "FLAGS <#channel> [account] [flags]  - View/set access flags (e.g. FLAGS #c bob +OP +KICK)";
        public static string HelpAccess => "ACCESS <#channel> LIST | ADD <account> <flags> | DEL <account> | CLEAR  - Manage access list (alias for FLAGS)";
        public static string HelpSop => "SOP <#channel> LIST | ADD <account> | DEL <account> | CLEAR  - Manage SOP list (wrapper around FLAGS)";
        public static string HelpAop => "AOP <#channel> LIST | ADD <account> | DEL <account> | CLEAR  - Manage AOP list (wrapper around FLAGS)";
        public static string HelpVop => "VOP <#channel> LIST | ADD <account> | DEL <account> | CLEAR  - Manage VOP list (wrapper around FLAGS)";
        public static string HelpStatus => "STATUS <#channel> [nick]  - Show access status (0 none, 1 voice, 2 op, 3 founder)";
        public static string HelpEnforce => "ENFORCE <#channel>  - Re-apply locks and access (MLOCK/TOPICLOCK and auto +o/+v)";
        public static string HelpOp => "OP <#channel> [nick]  - Give +o";
        public static string HelpDeop => "DEOP <#channel> [nick]  - Remove +o";
        public static string HelpVoice => "VOICE <#channel> [nick]  - Give +v";
        public static string HelpDevoice => "DEVOICE <#channel> [nick]  - Remove +v";
        public static string HelpInvite => "INVITE <#channel> <nick>  - Invite nick to channel";
        public static string HelpKick => "KICK <#channel> <nick> [reason]  - Kick nick from channel";
        public static string HelpAkick => "AKICK <#channel> ADD <account> [reason] | DEL <account> | LIST  - Auto-kick by account";
        public static string HelpRecover => "RECOVER <#channel> [nick]  - Restore +o to nick (requires OP access)";
        public static string HelpTopic => "TOPIC <#channel> [topic]  - View/set channel topic (requires OP access)";
        public static string HelpBan => "BAN <#channel> <nick|mask>  - Set +b (requires BAN flag)";
        public static string HelpUnban => "UNBAN <#channel> [nick|mask]  - Remove -b (requires BAN flag); no mask clears bans";
        public static string HelpSuspend => "SUSPEND <#channel> [reason]  - Suspend a channel (requires oper capability cs_suspend)";
        public static string HelpUnsuspend => "UNSUSPEND <#channel>  - Unsuspend a channel (requires oper capability cs_suspend)";
        public static string HelpClear => "CLEAR <#channel> BANS|OPS|VOICES|TOPIC|MODES|USERS  - Clear bans/ops/voices/topic/modes/users (requires matching flag)";
    }
}
