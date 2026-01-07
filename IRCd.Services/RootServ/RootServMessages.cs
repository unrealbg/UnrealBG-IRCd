namespace IRCd.Services.RootServ
{
    internal static class RootServMessages
    {
        public const string ServiceName = "RootServ";

        public static string HelpIntro => "RootServ (root control): HELP, REFERENCE, SHUTDOWN, RESTART, RAW, INJECT, QUIT, CHANSNOOP";

        public static string HelpShutdown => "SHUTDOWN: stops the server/services host.";

        public static string HelpRestart => "RESTART: requests host restart (requires external supervisor).";

        public static string HelpRaw => "RAW <line>: sends a raw IRC line to all connected sessions (dangerous).";

        public static string HelpInject => "INJECT <line>: sends a raw IRC line prefixed as the local server (dangerous).";

        public static string HelpQuit => "QUIT [serviceNick]: removes a services pseudo-user from ServerState.";

        public static string HelpChanSnoop => "CHANSNOOP: CHANSNOOP ON <#chan> | OFF <#chan> | LIST";

        public static string HelpReference => "REFERENCE: prints a command index.";

        public static string ReferenceIndex => "Commands: SHUTDOWN, RESTART, RAW, INJECT, QUIT, CHANSNOOP, REFERENCE";
    }
}
