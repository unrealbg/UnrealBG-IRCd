namespace IRCd.Services.NickServ
{
    internal static class NickServMessages
    {
        public const string ServiceName = "NickServ";

        public static string HelpIntro => "NickServ commands: HELP, REGISTER, CONFIRM, IDENTIFY, LOGOUT, INFO, SET, LIST, ACCESS, GROUP, LINK, LINKS, UNGROUP, UNLINK, LISTCHANS, GHOST, RECOVER, RELEASE, STATUS, ACC, DROP";

        public static string HelpRegister => "REGISTER <email> <password>  - Register your current nickname";

        public static string HelpRegister2 => "REGISTER <nick> <email> <password>  - Register a nickname without using it";

        public static string HelpConfirm => "CONFIRM <code>  - Confirm a pending registration (if required by the network)";

        public static string HelpIdentify => "IDENTIFY <password>  - Identify to your nickname";

        public static string HelpIdentify2 => "IDENTIFY <nick> <password>  - Identify to a nickname without using it";

        public static string HelpLogout => "LOGOUT  - Clear your identified state";

        public static string HelpInfo => "INFO [nick]  - Show registration status";

        public static string HelpList => "LIST [mask]  - List registered nicknames";

        public static string HelpAccess => "ACCESS ADD|DEL|LIST|CLEAR  - Manage your access list (requires IDENTIFY)";

        public static string HelpSetPassword => "SET PASSWORD <newpassword>  - Change password (requires IDENTIFY)";

        public static string HelpSetEnforce => "SET ENFORCE ON|OFF  - Toggle nick enforcement for your account (requires IDENTIFY)";

        public static string HelpSetKill => "SET KILL ON|OFF  - Toggle KILL protection (ON: disconnect unidentified users after 30 seconds; requires IDENTIFY)";

        public static string HelpSetEmail => "SET EMAIL <email>|NONE  - Set your account email (requires IDENTIFY)";

        public static string HelpSetHideEmail => "SET HIDEMAIL ON|OFF  - Hide your email in INFO (requires IDENTIFY)";

        public static string HelpSetSecure => "SET SECURE ON|OFF  - Toggle SECURE option (requires IDENTIFY)";

        public static string HelpSetAllowMemos => "SET ALLOWMEMOS ON|OFF  - Allow or disallow receiving memos (requires IDENTIFY)";

        public static string HelpSetMemoNotify => "SET MEMONOTIFY ON|OFF  - Notify you when a memo arrives while online (requires IDENTIFY)";

        public static string HelpSetMemoSignon => "SET MEMOSIGNON ON|OFF  - Notify you about unread memos on IDENTIFY (requires IDENTIFY)";

        public static string HelpSetNoLink => "SET NOLINK ON|OFF  - Toggle NOLINK option (requires IDENTIFY)";

        public static string HelpGroup => "GROUP <nick> <password>  - Group another registered nick under your account";

        public static string HelpLink => "LINK <nick> <password>  - Alias of GROUP";

        public static string HelpLinks => "LINKS [account]  - List grouped nicks for an account";

        public static string HelpUngroup => "UNGROUP <nick>  - Remove a nick from your account group (requires IDENTIFY)";

        public static string HelpUnlink => "UNLINK <nick>  - Alias of UNGROUP";

        public static string HelpListChans => "LISTCHANS [account]  - List channels registered by an account";

        public static string HelpGhost => "GHOST <nick> <password>  - Disconnect a user holding your nick";

        public static string HelpRecover => "RECOVER <nick> <password>  - Disconnect a user holding a registered nick and identify you";

        public static string HelpRelease => "RELEASE <nick> <password>  - Identify for a registered nick without using it";

        public static string HelpStatus => "STATUS <nick>  - Show status: 0=offline, 1=online+unregistered, 2=online+registered, 3=online+identified, 4=online+recognized";

        public static string HelpAcc => "ACC <nick>  - Show access: 0=offline, 1=online+unregistered, 2=online+registered, 3=online+identified, 4=online+recognized";

        public static string HelpDrop => "DROP <password>  - Drop your current nickname account";
    }
}
